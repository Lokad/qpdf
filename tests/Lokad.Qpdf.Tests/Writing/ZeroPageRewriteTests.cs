using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ZeroPageRewriteTests
{
    private static readonly byte[] Payload = [19, 23, 41];
    private static readonly byte[] Prior = [17, 29, 43];
    private static readonly byte[] Metadata = "<metadata>zero-page document</metadata>"u8.ToArray();
    private static readonly string[] Targets = ["output.pdf", "out-%d.pdf", "out-1.pdf", "pass1.pdf"];

    private static byte[] Fixture(bool onePage)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
        (1, "<< /Type /Catalog /Pages 2 0 R /Lang (fr-FR) /ViewerPreferences << /HideToolbar true >> /Metadata 3 0 R /Names << /EmbeddedFiles << /Names [(proof) 4 0 R] >> >> >>", null),
        (2, onePage ? "<< /Type /Pages /Kids [6 0 R] /Count 1 >>" : "<< /Type /Pages /Kids [] /Count 0 >>", null),
        (3, "<< /Type /Metadata /Subtype /XML /Length " + Metadata.Length + " >>", Metadata),
        (4, "<< /Type /Filespec /F (proof.bin) /UF (proof.bin) /Desc (zero-page attachment) /EF << /F 5 0 R >> >>", null),
        (5, "<< /Type /EmbeddedFile /Length 3 /Params << /Size 3 >> >>", Payload),
        ];
        if (onePage) { objects.Add((6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>", null)); }
        return CheckTests.Assemble(objects, 1);
    }

    private static async Task<ObservedHost> Prepare()
    {
        ObservedHost host = new();
        host.Inner.AddInput(QpdfPath.From("input.pdf"), Fixture(false), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("source.pdf"), Fixture(false), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("payload.bin"), Payload, QpdfInputOwnership.Copy);
        foreach (string path in Targets)
        {
            await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(QpdfPath.From(path), CancellationToken.None);
            await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        }
        return host;
    }

    private static async Task<QpdfExecutionResult> Run(IQpdfHost host, string route, bool synthetic,
        string operation, string mode, bool split, QpdfLimits limits, CancellationToken token)
    {
        string target = split ? "out-%d.pdf" : "output.pdf";
        List<string> argv = [synthetic ? "--empty" : "input.pdf"];
        Dictionary<string, object> job = new(StringComparer.Ordinal) { ["outputFile"] = target };
        if (synthetic) { job.Add("empty", ""); } else { job.Add("inputFile", "input.pdf"); }
        switch (operation)
        {
            case "add": case "missing-add":
                string payload = operation == "add" ? "payload.bin" : "missing.bin";
                argv.AddRange(["--add-attachment", payload, "--key=added", "--"]);
                job.Add("addAttachment", new[] { new { file = payload, key = "added" } }); break;
            case "copy": case "missing-copy":
                string source = operation == "copy" ? "source.pdf" : "missing.pdf";
                argv.AddRange(["--copy-attachments-from", source, "--prefix=copy-", "--"]);
                job.Add("copyAttachmentsFrom", new[] { new { file = source, prefix = "copy-" } }); break;
            case "remove": argv.Add("--remove-attachment=proof"); job.Add("removeAttachment", new[] { "proof" }); break;
        }
        if (mode == "qdf") { argv.Add("--qdf"); job.Add("qdf", ""); }
        if (mode == "streamed") { argv.Add("--object-streams=generate"); job.Add("objectStreams", "generate"); }
        if (mode == "active-pass1") { argv.Add("--linearize"); job.Add("linearize", ""); }
        if (mode is "active-pass1" or "inactive-pass1")
        { argv.Add("--linearize-pass1=pass1.pdf"); job.Add("linearizePass1", "pass1.pdf"); }
        if (split) { argv.Add("--split-pages=1"); job.Add("splitPages", "1"); }
        argv.AddRange(["--", target]);
        return route == "argv"
            ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), token)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), token);
    }

    private static async Task CheckOutput(ObservedHost host, bool preserveMetadata, string[] keys, int pages)
    {
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes));
        host.Inner.AddInput(QpdfPath.From("readback.pdf"), bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host.Inner, QpdfPath.From("readback.pdf"), CancellationToken.None))
        {
            Assert.Equal(pages, document.PageCount);
            if (preserveMetadata)
            {
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
                Assert.True(catalog.TryGet(PdfName.FromAscii("Lang"), out QpdfObject lang));
                Assert.Equal("fr-FR", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(lang).Value));
                Assert.True(catalog.TryGet(PdfName.FromAscii("ViewerPreferences"), out QpdfObject preferences));
                Assert.True(Assert.IsType<PdfDictionary>(preferences).TryGet(PdfName.FromAscii("HideToolbar"), out QpdfObject hide));
                Assert.True(Assert.IsType<PdfBoolean>(hide).Value);
                Assert.True(catalog.TryGet(PdfName.FromAscii("Metadata"), out QpdfObject metadata));
                PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(metadata).ObjectNumber));
                Assert.Equal(Metadata, stream.Data);
            }
        }
        IReadOnlyList<QpdfAttachmentInfo> attachments = await Qpdf.ListAttachmentsAsync(host.Inner, QpdfPath.From("readback.pdf"), CancellationToken.None);
        Assert.Equal(keys, attachments.Select(item => item.Key));
        foreach (string key in keys)
        {
            QpdfAttachmentExtraction extracted = await Qpdf.ExtractAttachmentAsync(host.Inner, QpdfPath.From("readback.pdf"), key,
                QpdfPath.From("extracted.bin"), CancellationToken.None);
            Assert.Equal(Payload.Length, extracted.ByteCount);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("extracted.bin"), out byte[] payload)); Assert.Equal(Payload, payload);
        }
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", "plain")]
    [InlineData("job", "plain")]
    [InlineData("argv", "qdf")]
    [InlineData("job", "qdf")]
    [InlineData("argv", "streamed")]
    [InlineData("job", "streamed")]
    public async Task WholeFileRewritePreservesZeroPageCatalogAndPayloads(string route, string mode)
    {
        using ObservedHost host = await Prepare();
        QpdfExecutionResult result = await Run(host, route, false, "rewrite", mode, false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        await CheckOutput(host, true, ["proof"], 0);
    }

    [Theory]
    [InlineData("job", true, "add")]
    [InlineData("job", true, "copy")]
    [InlineData("argv", true, "add")]
    [InlineData("argv", true, "copy")]
    [InlineData("argv", false, "add")]
    [InlineData("job", false, "add")]
    [InlineData("argv", false, "copy")]
    [InlineData("job", false, "copy")]
    [InlineData("argv", false, "remove")]
    [InlineData("job", false, "remove")]
    public async Task ZeroPageEditsApplyThroughSharedAttachmentPipeline(string route, bool synthetic, string operation)
    {
        using ObservedHost host = await Prepare();
        QpdfExecutionResult result = await Run(host, route, synthetic, operation, "plain", false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        string[] keys = operation == "remove" ? [] : synthetic ? [operation == "add" ? "added" : "copy-proof"]
            : [operation == "add" ? "added" : "copy-proof", "proof"];
        await CheckOutput(host, !synthetic, keys, 0);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("copy")]
    [InlineData("remove")]
    public async Task TypedAttachmentEditsAcceptRealZeroPageInput(string operation)
    {
        using ObservedHost host = await Prepare();
        QpdfPath input = QpdfPath.From("input.pdf"), output = QpdfPath.From("output.pdf");
        QpdfAttachmentEditResult result = operation switch
        {
            "add" => await Qpdf.AddAttachmentsAsync(host, input, output,
                [new QpdfAttachmentSpec("payload.bin", "added", null, null, null, null, null, false)], CancellationToken.None),
            "copy" => await Qpdf.CopyAttachmentsAsync(host, input, output,
                [new QpdfAttachmentCopy(QpdfPath.From("source.pdf"), "copy-")], CancellationToken.None),
            _ => await Qpdf.RemoveAttachmentsAsync(host, input, output, ["proof"], CancellationToken.None),
        };
        Assert.Equal(operation == "remove" ? 0 : 2, result.AttachmentCount); Assert.Equal(0L, result.Counters.OpenHandles);
        await CheckOutput(host, true, operation == "remove" ? [] : [operation == "add" ? "added" : "copy-proof", "proof"], 0);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task OrdinaryWholeFileRewriteAlsoPreservesExistingAttachments(string route)
    {
        using ObservedHost host = await Prepare();
        host.Inner.AddInput(QpdfPath.From("input.pdf"), Fixture(true), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, route, false, "rewrite", "plain", false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await CheckOutput(host, true, ["proof"], 1);
    }

    [Theory]
    [InlineData("job", "missing-add")]
    [InlineData("job", "missing-copy")]
    [InlineData("argv", "missing-add")]
    [InlineData("argv", "missing-copy")]
    public async Task EmptyJobMissingAttachmentSourcePreservesPriorOutput(string route, string operation)
    {
        using ObservedHost host = await Prepare();
        QpdfExecutionResult result = await Run(host, route, true, operation, "plain", false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Empty(host.Created);
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(Prior, bytes);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    public static IEnumerable<object[]> EmptySplits()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (bool synthetic in new[] { false, true })
        foreach (string mode in new[] { "plain", "inactive-pass1", "active-pass1" })
        { yield return [route, synthetic, mode]; }
    }

    [Theory]
    [MemberData(nameof(EmptySplits))]
    public async Task ZeroSplitGroupsPreserveEveryTarget(string route, bool synthetic, string mode)
    {
        using ObservedHost host = await Prepare();
        QpdfExecutionResult result = await Run(host, route, synthetic, "rewrite", mode, true, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(host.Created);
        foreach (string target in Targets)
        { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] bytes)); Assert.Equal(Prior, bytes); }
        Assert.Equal(0L, result.Counters.TotalOutputBytes); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", "pages")]
    [InlineData("job", "pages")]
    [InlineData("argv", "overlay")]
    [InlineData("job", "overlay")]
    [InlineData("argv", "replace")]
    [InlineData("job", "replace")]
    public async Task InvalidPageSelectionAndEmptyBaseRestrictionsPreservePriorOutput(string route, string operation)
    {
        using ObservedHost host = await Prepare();
        string[] argv = operation switch
        {
            "pages" => ["input.pdf", "--pages", ".", "1", "--", "output.pdf"],
            "overlay" => ["--empty", "--overlay", "source.pdf", "--", "--", "output.pdf"],
            _ => ["--empty", "--replace-input"],
        };
        string job = operation switch
        {
            "pages" => """{"inputFile":"input.pdf","outputFile":"output.pdf","pages":[{"file":".","range":"1"}]}""",
            "overlay" => """{"empty":"","outputFile":"output.pdf","overlay":{"file":"source.pdf"}}""",
            _ => """{"empty":"","replaceInput":""}""",
        };
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Empty(host.Created);
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(Prior, bytes);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BareEmptyOutputCanBeRewritten(bool separator)
    {
        using ObservedHost host = await Prepare();
        string[] argv = separator ? ["--empty", "--", "output.pdf"] : ["--empty", "output.pdf"];
        QpdfExecutionResult created = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, created.ExitCode);
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes));
        host.Inner.AddInput(QpdfPath.From("input.pdf"), bytes, QpdfInputOwnership.Copy);
        QpdfExecutionResult rewritten = await Run(host, "job", false, "rewrite", "plain", false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, rewritten.ExitCode);
        await CheckOutput(host, false, [], 0);
    }

    public static IEnumerable<object[]> Faults()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string operation in new[] { "add", "copy" })
        foreach (string phase in new[] { "read", "input-close", "write", "commit", "output-close" })
        foreach (bool cancel in new[] { false, true })
        { yield return [route, operation, phase, cancel]; }
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task EmptyEditsCleanUpAtHostFailureAndCancellationBoundaries(string route, string operation, string phase, bool cancel)
    {
        using ObservedHost host = await Prepare();
        using CancellationTokenSource cancellation = new();
        bool triggered = false;
        host.Boundary = (actual, path) =>
        {
            if (triggered || actual != phase) { return; }
            triggered = true;
            if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed);
        };
        QpdfExecutionResult result = await Run(host, route, true, operation, "plain", false, QpdfLimits.Default, cancellation.Token);
        Assert.True(triggered); Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        if (phase == "output-close")
        {
            Assert.Equal(new[] { "output.pdf" }, host.Committed);
            await CheckOutput(host, false, [operation == "add" ? "added" : "copy-proof"], 0);
        }
        else
        {
            Assert.Empty(host.Committed);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(Prior, bytes);
        }
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task EmptyCopyRejectsMalformedSourceWithoutPublishing(string route)
    {
        using ObservedHost host = await Prepare();
        host.Inner.AddInput(QpdfPath.From("source.pdf"), "not a PDF"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, route, true, "copy", "plain", false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Empty(host.Created);
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(Prior, bytes);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task EmptyAddHonorsLocalMemoryRefusalBeforePublication(string route)
    {
        using ObservedHost host = await Prepare();
        host.Inner.AddInput(QpdfPath.From("payload.bin"), new byte[131072], QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, route, true, "add", "plain", false,
            QpdfLimits.Default with { MaxMemoryBytes = 65536 }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Empty(host.Created);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(Prior, bytes);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", "add")]
    [InlineData("job", "add")]
    [InlineData("argv", "copy")]
    [InlineData("job", "copy")]
    public async Task EmptyEditsRefundEverySharedAdmissionRefusal(string route, string operation)
    {
        RecordingMemoryBudget baseline = new(8388608L, false);
        using (IMemoryScope caller = baseline.CreateScope())
        using (ObservedHost host = await Prepare())
        {
            QpdfExecutionResult result = await Run(new MemoryHost(host, caller), route, true, operation, "plain", false, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.Equal(0L, baseline.ReservedBytes); Assert.True(baseline.GrowthCalls > 0);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        {
            RecordingMemoryBudget root = new(8388608L, false) { RefuseOnGrowth = growth };
            using (IMemoryScope caller = root.CreateScope())
            using (ObservedHost host = await Prepare())
            {
                QpdfExecutionResult result = await Run(new MemoryHost(host, caller), route, true, operation, "plain", false, QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Empty(host.Committed);
                Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(Prior, bytes);
                Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
    }

    [Fact]
    public async Task RewrittenZeroPageMetadataSurvivesDocumentAndHostCleanup()
    {
        RecordingMemoryBudget root = new(8388608L, false);
        using (IMemoryScope caller = root.CreateScope())
        using (ObservedHost host = await Prepare())
        {
            MemoryHost governed = new(host, caller);
            QpdfExecutionResult result = await Run(governed, "job", false, "rewrite", "plain", false, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes));
            host.Inner.AddInput(QpdfPath.From("readback.pdf"), bytes, QpdfInputOwnership.Copy);
            QpdfAttachmentInfo info = Assert.Single(await Qpdf.ListAttachmentsAsync(governed, QpdfPath.From("readback.pdf"), CancellationToken.None));
            PdfDictionary catalog;
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(governed, QpdfPath.From("readback.pdf"), CancellationToken.None))
            { catalog = Assert.IsType<PdfDictionary>(document.GetObject(1)); }
            Assert.Equal(0L, host.Inner.OpenLeaseCount); host.Dispose();
            Assert.Equal("proof", info.Key); Assert.Equal("zero-page attachment", info.Description);
            Assert.True(catalog.TryGet(PdfName.FromAscii("Lang"), out QpdfObject lang));
            Assert.Equal("fr-FR", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(lang).Value));
            Assert.True(root.ReservedBytes > 0);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class MemoryHost(IQpdfHost inner, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => inner.CreateOutputAsync(path, token);
    }

    private sealed class ObservedHost : IQpdfHost, IDisposable
    {
        internal InMemoryQpdfHost Inner { get; } = new();
        internal List<string> Created { get; } = [];
        internal List<string> Committed { get; } = [];
        internal Action<string, QpdfPath>? Boundary;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(this, await Inner.OpenInputAsync(path, token));
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created.Add(path.Value); return new Output(this, await Inner.CreateOutputAsync(path, token)); }
        public void Dispose() => Inner.Dispose();

        private sealed class Input(ObservedHost host, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            { host.Boundary?.Invoke("read", Path); return inner.ReadAsync(offset, destination, token); }
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); host.Boundary?.Invoke("input-close", Path); }
        }

        private sealed class Output(ObservedHost host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            { host.Boundary?.Invoke("write", Path); return inner.WriteAsync(bytes, token); }
            public async ValueTask CommitAsync(CancellationToken token)
            { host.Boundary?.Invoke("commit", Path); await inner.CommitAsync(token); host.Committed.Add(Path.Value); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); host.Boundary?.Invoke("output-close", Path); }
        }
    }
}
