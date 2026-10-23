using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
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

    private static async Task<QpdfExecutionResult> Run(ObservedHost host, string route, bool synthetic,
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
    [InlineData("missing-add")]
    [InlineData("missing-copy")]
    public async Task EmptyJobMissingAttachmentSourcePreservesPriorOutput(string operation)
    {
        using ObservedHost host = await Prepare();
        QpdfExecutionResult result = await Run(host, "job", true, operation, "plain", false, QpdfLimits.Default, CancellationToken.None);
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

    private sealed class ObservedHost : IQpdfHost, IDisposable
    {
        internal InMemoryQpdfHost Inner { get; } = new();
        internal List<string> Created { get; } = [];
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => Inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created.Add(path.Value); return Inner.CreateOutputAsync(path, token); }
        public void Dispose() => Inner.Dispose();
    }
}
