using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Security;

public sealed class OutputEncryptionCommandTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Modes()
    {
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "copy", "select", "merge", "split", "streams", "linear", "linear-streams", "qdf", "qdf-streams", "attachment", "reencrypt", "empty", "empty-merge", "empty-split" })
        { yield return [job, mode]; }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task CommandsAndJobsCreateAes256ThroughSharedWriters(bool job, string mode)
    {
        using InMemoryQpdfHost host = new(); Add(host, mode == "reencrypt" ? "security-r4-ascii.pdf" : "meta-1page.pdf");
        Dictionary<string, object> fields = new() { ["outputFile"] = Output.Value };
        fields["encrypt"] = new Dictionary<string, object> { ["userPassword"] = "new-reader", ["ownerPassword"] = "new-owner",
            ["256bit"] = new { print = "low", modify = "none", extract = "n", cleartextMetadata = "" } };
        List<string> argv = [];
        if (mode.StartsWith("empty", StringComparison.Ordinal)) { fields["empty"] = ""; argv.Add("--empty"); }
        else { fields["inputFile"] = Input.Value; argv.Add(Input.Value); }
        argv.AddRange([Output.Value, "--encrypt", "--user-password=new-reader", "--owner-password=new-owner", "--bits=256", "--print=low", "--modify=none", "--extract=n", "--cleartext-metadata", "--"]);
        if (mode == "reencrypt") { fields["password"] = "reader"; argv.Add("--password=reader"); }
        bool split = mode.EndsWith("split", StringComparison.Ordinal);
        if (mode is "select" or "merge" or "split" or "empty-merge" or "empty-split")
        {
            string range = mode is "merge" or "empty-merge" || split ? "1,1" : "1";
            fields["pages"] = new[] { new { file = Input.Value, range } }; argv.AddRange(["--pages", Input.Value, range, "--"]);
        }
        if (split) { fields["splitPages"] = "1"; argv.Add("--split-pages=1"); }
        if (mode.Contains("streams", StringComparison.Ordinal)) { fields["objectStreams"] = "generate"; argv.Add("--object-streams=generate"); }
        if (mode.StartsWith("linear", StringComparison.Ordinal)) { fields["linearize"] = ""; argv.Add("--linearize"); }
        if (mode.StartsWith("qdf", StringComparison.Ordinal)) { fields["qdf"] = ""; argv.Add("--qdf"); }
        if (mode == "attachment")
        {
            host.AddInput(QpdfPath.From("payload.bin"), "private attachment"u8.ToArray(), QpdfInputOwnership.Copy);
            fields["addAttachment"] = new[] { new { file = "payload.bin" } }; argv.AddRange(["--add-attachment", "payload.bin", "--"]);
        }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        QpdfPath[] outputs = split ? [QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf")] : [Output];
        List<byte[]> credentials = [];
        foreach (QpdfPath path in outputs)
        {
            Assert.True(host.TryGetOutput(path, out byte[] bytes)); Assert.DoesNotContain("TitleText", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
            QpdfPath copy = QpdfPath.From("copy-" + path.Value); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
            foreach (string password in new[] { "new-reader", "new-owner" })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(mode == "empty" ? 0 : mode is "merge" or "empty-merge" ? 2 : 1, document.PageCount);
                Assert.Equal(6, document.Encryption?.Revision); Assert.Equal(-3388, document.Encryption?.Permissions); Assert.False(document.Encryption?.EncryptMetadata);
                PdfReference reference = Assert.IsType<PdfReference>(Get(document.Engine.Trailer, "Encrypt"));
                PdfDictionary encryption = Assert.IsType<PdfDictionary>(document.GetObject(reference.ObjectNumber));
                if (password == "new-reader") { credentials.Add(Assert.IsType<PdfByteString>(Get(encryption, "U")).Value); }
            }
            await Assert.ThrowsAsync<QpdfSecurityException>(async () =>
            { await using QpdfDocument ignored = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText("wrong"), QpdfLimits.Default, CancellationToken.None); });
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = QpdfPassword.FromText("new-reader") }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed && !(mode == "empty" && item.Name == "page-tree"));
            if (mode == "attachment")
            {
                QpdfExecutionResult payload = await Qpdf.ExecuteAsync(host, new([copy.Value, "--password=new-reader", "--show-attachment=payload.bin"]), CancellationToken.None);
                Assert.NotNull(payload.OutputBytes); Assert.Equal("private attachment"u8.ToArray(), payload.OutputBytes.Value.ToArray());
            }
        }
        if (split) { Assert.False(credentials[0].AsSpan().SequenceEqual(credentials[1])); }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "bytes", "café•", "café•")]
    [InlineData(true, "auto", "café•", "café•")]
    [InlineData(false, "unicode", "café•", "café•")]
    [InlineData(true, "hex-bytes", "636166c3a9e280a2", "café•")]
    public async Task OutputPasswordsHonorTheSelectedRepresentation(bool job, string mode, string user, string decoded)
    {
        using InMemoryQpdfHost host = new(); Add(host, "meta-1page.pdf");
        string owner = mode == "hex-bytes" ? "6f776e6572" : "owner";
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object>
        { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["passwordMode"] = mode,
            ["encrypt"] = new Dictionary<string, object> { ["userPassword"] = user, ["ownerPassword"] = owner, ["256bit"] = new { } } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--encrypt", user, owner, "256", "--", "--password-mode=" + mode]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        await Verify(host, decoded, -4, true);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--decrypt")]
    [InlineData("--allow-weak-crypto")]
    [InlineData("--")]
    public async Task PositionalOwnerPasswordsAreNeverInterpretedAsOptions(string owner)
    {
        using InMemoryQpdfHost host = new(); Add(host, "meta-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--encrypt", "user", owner, "256", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await Verify(host, owner, -4, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheLastOutputPolicySetterWins(bool decryptLast)
    {
        using InMemoryQpdfHost host = new(); Add(host, "meta-1page.pdf");
        List<string> argv = [Input.Value, Output.Value];
        if (!decryptLast) { argv.Add("--decrypt"); }
        argv.AddRange(["--encrypt", "user", "owner", "256", "--"]);
        if (decryptLast) { argv.Add("--decrypt"); }
        Assert.Equal(QpdfExecutionStatus.Completed, (await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)).Status);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), decryptLast ? null : QpdfPassword.FromText("user"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(!decryptLast, document.Encryption is not null);
    }

    [Theory]
    [InlineData("--modify=none", "--form=y", -1068)]
    [InlineData("--form=y", "--modify=none", -1324)]
    [InlineData("--print=none", "--print=low", -2052)]
    public async Task ArgvPermissionSettersRetainTheirOrder(string first, string second, int permissions)
    {
        using InMemoryQpdfHost host = new(); Add(host, "meta-1page.pdf");
        Assert.Equal(QpdfExecutionStatus.Completed, (await Qpdf.ExecuteAsync(host,
            new([Input.Value, Output.Value, "--encrypt", "user", "owner", "256", first, second, "--"]), CancellationToken.None)).Status);
        await Verify(host, "user", permissions, true);
    }

    [Theory]
    [InlineData("{\"userPassword\":\"secret\",\"256bit\":{\"print\":\"secret\"}}", QpdfDiagnosticCode.InvalidArguments)]
    [InlineData("{\"userPassword\":\"secret\"}", QpdfDiagnosticCode.InvalidArguments)]
    [InlineData("{\"userPassword\":\"secret\",\"128bit\":{}}", QpdfDiagnosticCode.UnsupportedCryptoOperation)]
    [InlineData("{\"userPassword\":\"secret\",\"256bit\":{\"forceR5\":\"\"}}", QpdfDiagnosticCode.UnsupportedCryptoOperation)]
    public async Task UnsupportedOrMalformedPoliciesNeverExposeSecretsOrOpenInputs(string policy, QpdfDiagnosticCode code)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"missing\",\"outputFile\":\"output.pdf\",\"encrypt\":" + policy + "}", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("secret", result.Diagnostics[0].Message, StringComparison.Ordinal); Assert.Equal(0, result.Counters.HostOperations);
        Assert.False(host.TryGetOutput(Output, out _));
    }

    [Fact]
    public async Task JobPermissionKeysUseNativeLexicalOrderAndEncryptionWinsOverDecryption()
    {
        using InMemoryQpdfHost host = new(); Add(host, "meta-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","outputFile":"output.pdf","encrypt":{"userPassword":"user","ownerPassword":"owner","256bit":{"modify":"none","form":"y","modifyOther":"y"}},"decrypt":""}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await Verify(host, "user", -1316, true);
    }

    [Theory]
    [InlineData("modify", "all", -4)]
    [InlineData("modify", "annotate", -12)]
    [InlineData("modify", "form", -44)]
    [InlineData("modify", "assembly", -300)]
    [InlineData("modify", "none", -1324)]
    [InlineData("print", "none", -2056)]
    [InlineData("print", "low", -2052)]
    [InlineData("print", "full", -4)]
    [InlineData("accessibility", "n", -4)]
    [InlineData("accessibility", "y", -4)]
    [InlineData("extract", "n", -20)]
    [InlineData("extract", "y", -4)]
    [InlineData("annotate", "n", -36)]
    [InlineData("annotate", "y", -4)]
    [InlineData("assemble", "n", -1028)]
    [InlineData("assemble", "y", -4)]
    [InlineData("form", "n", -260)]
    [InlineData("form", "y", -4)]
    [InlineData("modify-other", "n", -12)]
    [InlineData("modify-other", "y", -4)]
    public async Task PermissionChoicesReachTheAuthenticatedDictionary(string flag, string value, int permissions)
    {
        using InMemoryQpdfHost host = new(); Add(host, "meta-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([Input.Value, Output.Value, "--encrypt", "user", "owner", "256", "--" + flag + "=" + value, "--"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await Verify(host, "user", permissions, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeterministicIdsRefuseNewEncryptionBeforeInput(bool encryptionFirst)
    {
        using InMemoryQpdfHost host = new();
        List<string> argv = [Input.Value, Output.Value];
        if (!encryptionFirst) { argv.Add("--deterministic-id"); }
        argv.AddRange(["--encrypt", "user", "owner", "256", "--"]);
        if (encryptionFirst) { argv.Add("--deterministic-id"); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code); Assert.Equal(0, result.Counters.HostOperations);
    }

    [Theory]
    [InlineData("wrong-input")]
    [InlineData("owner-empty")]
    [InlineData("version")]
    [InlineData("canceled")]
    public async Task FailedEncryptionPreservesPriorOutput(string failure)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r4-ascii.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        List<string> argv = [Input.Value, Output.Value, "--password=" + (failure == "wrong-input" ? "private-invalid" : "reader"),
            "--encrypt", "new-user", failure == "owner-empty" ? "" : "new-owner", "256", "--"];
        if (failure == "version") { argv.Add("--force-version=1.4"); }
        using CancellationTokenSource cancellation = new(); if (failure == "canceled") { cancellation.Cancel(); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), cancellation.Token);
        Assert.Equal(failure == "canceled" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.DoesNotContain(result.Diagnostics, item => item.Message.Contains("private-invalid", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NamedPasswordsCanBeOmittedAndInspectionDoesNotWrite()
    {
        using InMemoryQpdfHost host = new(); Add(host, "meta-1page.pdf");
        Assert.Equal(QpdfExecutionStatus.Completed, (await Qpdf.ExecuteAsync(host,
            new([Input.Value, Output.Value, "--encrypt", "--bits=256", "--"]), CancellationToken.None)).Status);
        await Verify(host, "", -4, true);
        QpdfExecutionResult inspected = await Qpdf.ExecuteAsync(host, new([Input.Value, "--encrypt", "user", "owner", "256", "--", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, inspected.Status); Assert.Contains(inspected.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageCount);
    }

    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);

    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }

    private static async Task Verify(InMemoryQpdfHost host, string password, int permissions, bool metadata)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(6, document.Encryption?.Revision); Assert.Equal(permissions, document.Encryption?.Permissions); Assert.Equal(metadata, document.Encryption?.EncryptMetadata);
    }
}
