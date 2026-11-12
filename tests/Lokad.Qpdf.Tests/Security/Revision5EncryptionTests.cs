using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class Revision5EncryptionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf"), Template = QpdfPath.From("template.pdf");

    public static IEnumerable<object[]> Vectors()
    {
        using JsonDocument vectors = ReadVectors();
        foreach (JsonElement vector in vectors.RootElement.EnumerateArray())
        {
            string Text(string name) => vector.GetProperty(name).GetString() ?? throw new InvalidDataException(name);
            yield return [Text("file"), Text("dictionary"), Text("userHex"), Text("ownerHex"), Text("fileKeyHex")];
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void BothPasswordsRecoverTheNativeKeyAndFailuresClearIt(string file, string dictionary, string userHex, string ownerHex, string keyHex)
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        PdfDictionary encryption = Parse(dictionary, governor, source); long baseline = governor.Snapshot().LiveMemoryBytes;
        foreach ((string hex, PasswordAuthentication expected) in new[]
        { (userHex, PasswordAuthentication.User), (ownerHex, PasswordAuthentication.Owner), ("77726f6e67", PasswordAuthentication.None) })
        {
            byte[] password = Convert.FromHexString(hex), original = (byte[])password.Clone(), key = new byte[32]; key.AsSpan().Fill(0xA5);
            Assert.Equal(expected, Aes256Encryption.Authenticate(encryption, password, key, governor));
            Assert.Equal(expected == PasswordAuthentication.None ? new byte[32] : Convert.FromHexString(keyHex), key);
            Assert.Equal(original, password); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Theory]
    [InlineData("P")]
    [InlineData("EncryptMetadata")]
    [InlineData("Perms")]
    [InlineData("U")]
    [InlineData("O")]
    [InlineData("UE")]
    [InlineData("OE")]
    public void DamagedParametersNeverExposeAKey(string field)
    {
        object[] vector = Vectors().First(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor); PdfDictionary original = Parse((string)vector[1], governor, source);
        Dictionary<PdfName, QpdfObject> entries = original.Keys.ToDictionary(name => name, name =>
        { Assert.True(original.TryGet(name, out QpdfObject value)); return value; });
        entries[PdfName.FromAscii(field)] = field switch
        { "P" => new PdfInteger("-4", -4), "EncryptMetadata" => PdfBoolean.False, "Perms" => new PdfByteString(new byte[16], true), _ => new PdfByteString(new byte[1], true) };
        byte[] key = new byte[32]; key.AsSpan().Fill(0xA5); long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfParseException>(() => Aes256Encryption.Authenticate(new(entries), "reader"u8, key, governor));
        Assert.Equal(new byte[32], key); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EveryAuthenticationRefusalAndCancellationClearsTheKey()
    {
        object[] vector = Vectors().First(); ResourceGovernor parser = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(parser); PdfDictionary encryption = Parse((string)vector[1], parser, source);
        RecordingMemoryBudget baseline = new(1048576); Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { Run(new(1048576) { RefuseOnGrowth = growth }, true); }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel(); ResourceGovernor canceled = new(QpdfLimits.Default, cancellation.Token);
        byte[] canceledKey = new byte[32]; canceledKey.AsSpan().Fill(0xA5);
        Assert.Throws<OperationCanceledException>(() => Aes256Encryption.Authenticate(encryption, "reader"u8, canceledKey, canceled)); Assert.Equal(new byte[32], canceledKey);
        void Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            byte[] key = new byte[32]; key.AsSpan().Fill(0xA5);
            if (refuses) { Assert.Throws<QpdfMemoryAdmissionException>(() => Aes256Encryption.Authenticate(encryption, "reader"u8, key, governor)); Assert.Equal(new byte[32], key); }
            else { Assert.Equal(PasswordAuthentication.User, Aes256Encryption.Authenticate(encryption, "reader"u8, key, governor)); }
            Assert.Equal(0, budget.ReservedBytes);
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task TypedReadersExposeNativePlaintextWithEitherPassword(string file, string dictionary, string userHex, string ownerHex, string keyHex)
    {
        Assert.NotEmpty(dictionary); Assert.NotEmpty(keyHex);
        using InMemoryQpdfHost host = new(); Add(host, Input, file);
        using JsonDocument vectors = ReadVectors(); JsonElement vector = vectors.RootElement.EnumerateArray().Single(value => value.GetProperty("file").GetString() == file);
        foreach (string hex in new[] { userHex, ownerHex })
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromBytes(Convert.FromHexString(hex)), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount); Assert.Equal(5, document.Encryption?.Revision); Assert.Equal(256, document.Encryption?.KeyLengthBits);
            foreach (JsonElement stream in vector.GetProperty("streams").EnumerateArray())
            {
                PdfStream plain = Assert.IsType<PdfStream>(document.GetObject(stream.GetProperty("number").GetInt32()));
                Assert.Equal(Convert.FromBase64String(stream.GetProperty("plainBase64").GetString() ?? throw new InvalidDataException()), plain.Data);
            }
            AssertTitle(document);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "classic")]
    [InlineData(true, "classic")]
    [InlineData(false, "object-streams")]
    [InlineData(true, "object-streams")]
    [InlineData(false, "qdf")]
    [InlineData(true, "qdf")]
    [InlineData(false, "linearized")]
    [InlineData(true, "linearized")]
    public async Task CommandsPreserveRevision5AcrossSharedWriters(bool job, string mode)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r5-ascii.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["password"] = "reader" };
        List<string> argv = [Input.Value, Output.Value, "--password=reader"];
        if (mode == "qdf") { fields["qdf"] = ""; argv.Add("--qdf"); }
        if (mode == "linearized") { fields["linearize"] = ""; argv.Add("--linearize"); }
        fields["objectStreams"] = mode is "object-streams" or "linearized" ? "generate" : "disable";
        argv.Add("--object-streams=" + fields["objectStreams"]);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await VerifyOutput(host, 5, ["reader", "fixture-owner"]);
    }

    [Theory]
    [InlineData("preserve")]
    [InlineData("remove")]
    [InlineData("new")]
    [InlineData("template")]
    public async Task TypedSelectionUsesExistingOutputPolicies(string policy)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r5-ascii.pdf"); Add(host, Template, "security-r5-empty.pdf");
        QpdfOutputEncryption encryption = policy switch
        { "remove" => QpdfOutputEncryption.Remove, "new" => QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner"), -4, false),
            "template" => QpdfOutputEncryption.CopyFrom(Template, null), _ => QpdfOutputEncryption.Preserve };
        _ = await Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FromPages([1]), QpdfPassword.FromText("reader"), encryption, CancellationToken.None);
        await VerifyOutput(host, policy == "remove" ? null : policy == "new" ? 6 : 5,
            policy == "remove" ? [""] : policy == "new" ? ["new-reader", "new-owner"] : policy == "template" ? ["", "fixture-owner"] : ["reader", "fixture-owner"]);
    }

    [Fact]
    public async Task EmptyPasswordsAndAttachmentExtractionUseTheSameReader()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r5-attachment.pdf");
        Assert.Equal("private.bin", Assert.Single(await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None)).Key);
        _ = await Qpdf.ExtractAttachmentAsync(host, Input, "private.bin", Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("Private attachment payload.\n"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task TextPasswordsUseUtf8AndTheSameByteLimit()
    {
        foreach ((string file, string text) in new[] { ("security-r5-utf8.pdf", "café-π-密碼"), ("security-r5-truncated.pdf", new string('x', 126) + "é-ending") })
        {
            using InMemoryQpdfHost host = new(); Add(host, Input, file);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromText(text), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount); AssertTitle(document);
        }
    }

    [Fact]
    public async Task PagePlacementAndAttachmentSourcesKeepIndependentRevision5Credentials()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Template, "security-r5-ascii.pdf");
        Add(host, QpdfPath.From("source.pdf"), "security-r5-attachment.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--pages", ".", "1", "source.pdf", "1", "--",
            "--overlay", Template.Value, "--password=reader", "--", "--copy-attachments-from", "source.pdf", "--prefix=copy-", "--",
            "--encrypt", "new-reader", "new-owner", "256", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy); QpdfPassword password = QpdfPassword.FromText("new-reader");
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, password, QpdfLimits.Default, CancellationToken.None))
        { Assert.Equal(2, document.PageCount); Assert.Equal(6, document.Encryption?.Revision); }
        QpdfPath payload = QpdfPath.From("payload.bin");
        _ = await Qpdf.ExtractAttachmentAsync(host, copy, "copy-private.bin", payload, password, QpdfLimits.Default, CancellationToken.None);
        Assert.True(host.TryGetOutput(payload, out byte[] data)); Assert.Equal("Private attachment payload.\n"u8.ToArray(), data); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CopiedTemplateRaisesTheVersionToExtensionThree()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Template, "security-r5-ascii.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--copy-encryption=template.pdf", "--encryption-file-password=reader", "--force-version=1.7.3"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await VerifyOutput(host, 5, ["reader", "fixture-owner"]);
    }

    [Theory]
    [InlineData("1.6")]
    [InlineData("1.7.2")]
    public async Task IncompatibleForcedVersionsNeverDiscardProtection(string version)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r5-ascii.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password=reader", "--force-version=" + version]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task FullJsonUpdatesPreserveTheAuthenticatedRevision5Handler()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r5-ascii.pdf");
        QpdfExecutionResult exported = await Qpdf.ExecuteAsync(host, new([Input.Value, "--password=reader", "--json-output=2", "--json-stream-data=inline", "--", "update.json"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, exported.Status); Assert.True(host.TryGetOutput(QpdfPath.From("update.json"), out byte[] bytes));
        host.AddInput(QpdfPath.From("update.json"), bytes, QpdfInputOwnership.Copy);
        _ = await Qpdf.UpdatePdfFromJsonAsync(host, Input, QpdfPath.From("update.json"), Output, QpdfPassword.FromText("reader"), QpdfOutputEncryption.Preserve, CancellationToken.None);
        await VerifyOutput(host, 5, ["reader", "fixture-owner"]);
    }

    [Theory]
    [InlineData(null, QpdfSecurityFailure.PasswordRequired)]
    [InlineData("private-wrong", QpdfSecurityFailure.InvalidPassword)]
    public async Task MissingAndWrongCredentialsRemainDistinct(string? password, QpdfSecurityFailure expected)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r5-ascii.pdf");
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(async () =>
        { await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, password is null ? null : QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None); });
        Assert.Equal(expected, error.Reason); Assert.DoesNotContain("private-wrong", error.Message, StringComparison.Ordinal); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("select")]
    [InlineData("template")]
    public async Task EveryPublicAdmissionRefusalClosesInputsAndRefundsSecurity(string operation)
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost storage = new(); Add(storage, Input, "security-r5-ascii.pdf"); Add(storage, Template, "security-r5-empty.pdf");
            using IMemoryScope caller = budget.CreateScope(); BudgetHost host = new(storage, caller);
            async Task Operate()
            {
                if (operation == "open")
                { await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromText("fixture-owner"), QpdfLimits.Default, CancellationToken.None); Assert.Equal(1, document.PageCount); }
                else
                {
                    _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), QpdfPassword.FromText("fixture-owner"),
                        operation == "template" ? QpdfOutputEncryption.CopyFrom(Template, null) : QpdfOutputEncryption.Preserve, CancellationToken.None);
                }
            }
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Operate); Assert.False(storage.TryGetOutput(Output, out _)); }
            else { await Operate(); }
            Assert.Equal(0, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static async Task VerifyOutput(InMemoryQpdfHost host, int? revision, string[] passwords)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        foreach (string password in passwords)
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision); AssertTitle(document);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
    private static void AssertTitle(QpdfDocument document)
    {
        Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject raw));
        PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(raw).ObjectNumber));
        Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title)); Assert.Equal("TitleText"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
    }
    private static PdfDictionary Parse(string text, ResourceGovernor governor, ParserMemoryScope source)
        => Assert.IsType<PdfDictionary>(new PdfParser(QpdfLimits.Default, governor, new DiagnosticBag(governor), null).ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, source));
    private static JsonDocument ReadVectors() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r5-vectors.json")));
    private static void Add(InMemoryQpdfHost host, QpdfPath path, string fixture)
        => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), QpdfInputOwnership.Copy);
    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
}
