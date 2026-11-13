using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordTransformationTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Cases()
    {
        // Physical writer/handler combinations belong to EncryptedSerializerTests.
        // Keep each public transformation and both parsers, with targeted pairs.
        yield return ["security-r4-ascii.pdf", false, "copy"];
        yield return ["security-r6-empty.pdf", true, "copy"];
        yield return ["security-r4-ascii.pdf", true, "select"];
        yield return ["security-r6-empty.pdf", false, "merge"];
        yield return ["security-r6-empty.pdf", true, "split"];
        yield return ["security-r4-ascii.pdf", false, "streams"];
        yield return ["security-r6-empty.pdf", true, "linear"];
        yield return ["security-r4-ascii.pdf", true, "linear-streams"];
        yield return ["security-r6-empty.pdf", false, "qdf"];
        yield return ["security-r4-ascii.pdf", false, "qdf-streams"];
        yield return ["security-r6-empty.pdf", true, "attachment"];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CommandsAndJobsPreservePrimaryProtectionThroughSharedWriters(string file, bool job, string mode)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, file); Add(host, QpdfPath.From("other.pdf"), "meta-1page.pdf");
        string password = file.Contains("empty", StringComparison.Ordinal) ? "" : "reader";
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["password"] = password };
        List<string> argv = [Input.Value, Output.Value, "--password=" + password];
        switch (mode)
        {
            case "select":
                fields["pages"] = new[] { new { file = ".", range = "1" } }; argv.AddRange(["--pages", ".", "1", "--"]); break;
            case "merge":
                fields["pages"] = new[] { new { file = ".", range = "1" }, new { file = "other.pdf", range = "1" } };
                argv.AddRange(["--pages", ".", "1", "other.pdf", "1", "--"]); break;
            case "split":
                fields["pages"] = new[] { new { file = ".", range = "1,1" } }; fields["splitPages"] = "1";
                argv.AddRange(["--split-pages=1", "--pages", ".", "1,1", "--"]); break;
            case "attachment":
                host.AddInput(QpdfPath.From("payload.bin"), "private attachment"u8.ToArray(), QpdfInputOwnership.Copy);
                fields["addAttachment"] = new[] { new { file = "payload.bin" } }; argv.AddRange(["--add-attachment", "payload.bin", "--"]); break;
        }
        if (mode.Contains("streams", StringComparison.Ordinal)) { fields["objectStreams"] = "generate"; argv.Add("--object-streams=generate"); }
        if (mode.StartsWith("linear", StringComparison.Ordinal)) { fields["linearize"] = ""; argv.Add("--linearize"); }
        if (mode.StartsWith("qdf", StringComparison.Ordinal)) { fields["qdf"] = ""; argv.Add("--qdf"); }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        QpdfPath[] outputs = mode == "split" ? [QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf")] : [Output];
        foreach (QpdfPath path in outputs)
        {
            Assert.True(host.TryGetOutput(path, out byte[] bytes));
            Assert.DoesNotContain("TitleText", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
            QpdfPath copy = QpdfPath.From("copy-" + path.Value); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
            foreach (string credential in new[] { password, "fixture-owner" })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(credential), QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(mode == "merge" ? 2 : 1, document.PageCount);
                Assert.Equal(file.Contains("r4", StringComparison.Ordinal) ? 4 : 6, document.Encryption?.Revision);
                Assert.Equal(-3388, document.Encryption?.Permissions); Assert.Equal(password.Length != 0, document.Encryption?.EncryptMetadata);
                PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Info")));
                Assert.Equal("TitleText"u8.ToArray(), Assert.IsType<PdfByteString>(Get(info, "Title")).Value);
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = QpdfPassword.FromText(password) }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
            if (mode == "attachment")
            {
                QpdfExecutionResult extracted = await Qpdf.ExecuteAsync(host, new([copy.Value, "--password=" + password, "--show-attachment=payload.bin"]), CancellationToken.None);
                Assert.Equal(QpdfExecutionStatus.Completed, extracted.Status); Assert.NotNull(extracted.OutputBytes);
                Assert.Equal("private attachment"u8.ToArray(), extracted.OutputBytes.Value.ToArray());
            }
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordFileAndHexModeRemainOwnedThroughTransformPublication(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-pdfdoc.pdf");
        host.AddInput(QpdfPath.From("password"), "636166e980\n"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","outputFile":"output.pdf","passwordFile":"password","passwordMode":"hex-bytes","suppressPasswordRecovery":"","objectStreams":"generate"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password-file=password", "--password-mode=hex-bytes", "--suppress-password-recovery", "--object-streams=generate"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(0, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), QpdfPassword.FromText("café•"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(4, document.Encryption?.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnselectedPrimaryStillControlsProtection(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-ascii.pdf"); Add(host, QpdfPath.From("other.pdf"), "meta-1page.pdf");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","outputFile":"output.pdf","password":"reader","pages":[{"file":"other.pdf","range":"1"}]}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password=reader", "--pages", "other.pdf", "1", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, document.PageCount); Assert.Equal(4, document.Encryption?.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongPasswordAndIncompatibleVersionNeverPublish(bool wrongPassword)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value,
            "--password=" + (wrongPassword ? "private-wrong" : "reader"), "--force-version=1.4"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(wrongPassword ? QpdfDiagnosticCode.InvalidPassword : QpdfDiagnosticCode.UnsupportedFeature, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("private-wrong", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("security-r4-empty.pdf")]
    [InlineData("security-r6-empty.pdf")]
    public async Task SharedTypedAttachmentEditingPreservesEmptyPasswordProtection(string file)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, file);
        host.AddInput(QpdfPath.From("payload.bin"), "private attachment"u8.ToArray(), QpdfInputOwnership.Copy);
        _ = await Qpdf.AddAttachmentsAsync(host, Input, Output,
            [new("payload.bin", null, null, null, null, null, null, false)], CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.DoesNotContain("private attachment", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        QpdfExecutionResult extracted = await Qpdf.ExecuteAsync(host, new([copy.Value, "--show-attachment=payload.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, extracted.Status); Assert.NotNull(extracted.OutputBytes);
        Assert.Equal("private attachment"u8.ToArray(), extracted.OutputBytes.Value.ToArray());
        _ = await Qpdf.RemoveAttachmentsAsync(host, copy, Output, ["payload.bin"], CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] removed)); host.AddInput(QpdfPath.From("removed.pdf"), removed, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("removed.pdf"), CancellationToken.None);
        Assert.Equal(file.Contains("r4", StringComparison.Ordinal) ? 4 : 6, document.Encryption?.Revision);
        Assert.Equal(1, document.PageCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EveryTransformAdmissionRefusalRefundsBeforePublication(bool newEncryption, bool emptyPrimary)
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false); Assert.True(baseline.GrowthCalls > 0);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf");
            host.AddInput(QpdfPath.From("password"), "reader\n"u8.ToArray(), QpdfInputOwnership.Copy);
            using IMemoryScope caller = budget.CreateScope();
            List<string> argv = [Input.Value, Output.Value, "--password-file=password", "--linearize", "--object-streams=generate"];
            if (emptyPrimary)
            {
                Add(host, QpdfPath.From("other.pdf"), "meta-1page.pdf");
                argv = ["--empty", Output.Value, "--pages", "other.pdf", "1,1", "--", "--split-pages=1", "--object-streams=generate"];
            }
            if (newEncryption) { argv.AddRange(["--encrypt", "new-user", "new-owner", "256", "--modify=none", "--"]); }
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new BudgetHost(host, caller), new(argv), CancellationToken.None);
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses)
            {
                Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.False(host.TryGetOutput(Output, out _));
                Assert.False(host.TryGetOutput(QpdfPath.From("output-1.pdf"), out _));
                Assert.False(host.TryGetOutput(QpdfPath.From("output-2.pdf"), out _));
            }
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
            caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    private static void Add(InMemoryQpdfHost host, QpdfPath path, string file)
        => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
}
