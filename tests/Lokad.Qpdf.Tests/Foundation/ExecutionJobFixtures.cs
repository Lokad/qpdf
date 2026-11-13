using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

/// <summary>Explicit fixtures and public route adapters for execution observations.</summary>
internal static class ExecutionJobFixtures
{
    internal static readonly QpdfPath InputPath = QpdfPath.From("input.pdf");
    internal static readonly QpdfPath OutputPath = QpdfPath.From("output.pdf");
    internal static readonly string[] TypedRoutes = ["typed-page", "typed-page-rotation", "typed-pages", "typed-pages-rotation",
        "typed-pages-options", "typed-check", "typed-json", "typed-import", "typed-update", "typed-extract",
        "typed-add", "typed-remove", "typed-copy"];

    private static byte[] Deflate(byte[] bytes)
    {
        using MemoryStream output = new();
        using (ZLibStream encoder = new(output, CompressionLevel.Optimal, true)) { encoder.Write(bytes); }
        return output.ToArray();
    }

    private static QpdfAttachmentSpec Attachment()
        => new("attachment.bin", "payload", "payload.bin", null, null, null, null, false);

    internal static async Task<RecordingJobHost> PrepareAsync(string route, bool compressed, string boundary, string outcome)
    {
        byte[] content = "q Q\n"u8.ToArray();
        byte[] source = DocumentBytes(compressed, "none");
        InMemoryQpdfHost setup = new();
        setup.AddInput(InputPath, source, QpdfInputOwnership.Copy);
        setup.AddInput(QpdfPath.From("attachment.bin"), content, QpdfInputOwnership.Copy);
        if (route is "typed-extract" or "typed-remove" or "typed-copy")
        {
            await Qpdf.AddAttachmentsAsync(setup, InputPath, QpdfPath.From("attached.pdf"), [Attachment()], CancellationToken.None);
            Assert.True(setup.TryGetOutput(QpdfPath.From("attached.pdf"), out source));
        }
        if (route == "qdf")
        {
            QpdfExecutionResult qdf = await Qpdf.ExecuteAsync(setup,
                new QpdfCommandInvocation(["input.pdf", "--qdf", "--object-streams=disable", "qdf.pdf"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, qdf.Status);
            Assert.True(setup.TryGetOutput(QpdfPath.From("qdf.pdf"), out source));
        }
        RecordingJobHost host = new(boundary, null, outcome, true);
        host.Inner.AddInput(InputPath, source, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("second.pdf"), source, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("attachment.bin"), content, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("bytes.bin"), route == "zlib-uncompress" ? Deflate(content) : content, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("args.txt"), "input.pdf\noutput.pdf\n"u8.ToArray(), QpdfInputOwnership.Copy);
        if (route is "typed-import" or "typed-update")
        {
            QpdfJsonResult json = await Qpdf.GetDocumentJsonAsync(setup, InputPath, QpdfPath.From("document.json"),
                new QpdfJsonOptions(2, [], "inline", null, [], false), CancellationToken.None);
            host.Inner.AddInput(QpdfPath.From("input.json"), Encoding.UTF8.GetBytes(json.Json), QpdfInputOwnership.Copy);
        }
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(OutputPath, CancellationToken.None);
        await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
        await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    internal static async Task<object> RunAsync(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        QpdfExecutionOptions options = new(limits);
        if (route == "job-whole")
        { return await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input.pdf\",\"outputFile\":\"output.pdf\"}", options, token); }
        if (route == "job-check")
        { return await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input.pdf\",\"check\":\"\"}", options, token); }
        if (route.StartsWith("argv-", StringComparison.Ordinal) || route is "mixed-whole" or "response-whole")
        {
            string[] argv = route switch
            {
                "argv-whole" => ["input.pdf", "output.pdf"],
                "argv-linearized" => ["input.pdf", "--linearize", "output.pdf"],
                "argv-pages" => ["input.pdf", "--pages", ".", "1", "--", "output.pdf"],
                "argv-merge" => ["input.pdf", "--pages", ".", "1", "second.pdf", "1", "--", "output.pdf"],
                "argv-json" => ["input.pdf", "--json", "--json-stream-data=inline"],
                "argv-filtered" => ["input.pdf", "--show-object=4", "--filtered-stream-data"],
                "mixed-whole" => ["input.pdf", "--job-json-file=settings.json", "output.pdf"],
                "response-whole" => ["@args.txt"],
                _ => throw new InvalidOperationException(route),
            };
            return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, token);
        }
        if (route.StartsWith("typed-", StringComparison.Ordinal))
        { return await RunTypedAsync(host, route, limits, token); }
        if (route == "qdf") { return await Qpdf.FixQdfAsync(host, InputPath, OutputPath, limits, token); }
        return await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("bytes.bin"), OutputPath,
            new QpdfZlibFlateOptions(route == "zlib-compress" ? QpdfZlibFlateMode.Compress : QpdfZlibFlateMode.Uncompress, null), limits, token);
    }

    private static async Task<object> RunTypedAsync(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        PdfPageSource[] sources = [new(InputPath, new PdfPageSelectionOptions(1)), new(QpdfPath.From("second.pdf"), new PdfPageSelectionOptions(1))];
        if (route == "typed-page") { return await Qpdf.SelectPageAsync(host, InputPath, OutputPath, new PdfPageSelectionOptions(1), limits, token); }
        if (route == "typed-page-rotation") { return await Qpdf.SelectPageAsync(host, InputPath, OutputPath, new PdfPageSelectionOptions(1), [new PdfPageRotation(PdfRotationMode.Set, 90, [1])], limits, token); }
        if (route == "typed-pages") { return await Qpdf.SelectPagesAsync(host, sources, OutputPath, limits, token); }
        if (route == "typed-pages-rotation") { return await Qpdf.SelectPagesAsync(host, sources, OutputPath, [new PdfPageRotation(PdfRotationMode.Set, 90, [1])], limits, token); }
        if (route == "typed-pages-options") { return await Qpdf.SelectPagesAsync(host, sources, OutputPath, new PdfPageMergeOptions(), limits, token); }
        if (route == "typed-check") { return await Qpdf.CheckAsync(host, InputPath, new QpdfCheckOptions(), limits, token); }
        if (route == "typed-json") { return await Qpdf.GetDocumentJsonAsync(host, InputPath, OutputPath, new QpdfJsonOptions(2, []), limits, token); }
        if (route == "typed-import") { return await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("input.json"), OutputPath, limits, token); }
        if (route == "typed-update") { return await Qpdf.UpdatePdfFromJsonAsync(host, InputPath, QpdfPath.From("input.json"), OutputPath, limits, token); }
        if (route == "typed-extract") { return await Qpdf.ExtractAttachmentAsync(host, InputPath, "payload", OutputPath, limits, token); }
        if (route == "typed-add") { return await Qpdf.AddAttachmentsAsync(host, InputPath, OutputPath, [Attachment()], limits, token); }
        if (route == "typed-remove") { return await Qpdf.RemoveAttachmentsAsync(host, InputPath, OutputPath, ["payload"], limits, token); }
        return await Qpdf.CopyAttachmentsAsync(host, InputPath, OutputPath, [new QpdfAttachmentCopy(QpdfPath.From("second.pdf"), "copy-")], limits, token);
    }

    internal static void AssertClosed(RecordingJobHost host, object? result)
    {
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        QpdfExecutionCounters? counters = result is null ? null : Counters(result);
        QpdfExecutionCounters? snapshot = counters is null ? null : counters with { };
        if (counters is not null)
        { Assert.Equal(0L, counters.OpenHandles); Assert.Equal(host.HostCalls, counters.HostOperations); }
        host.Dispose();
        Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
        Assert.Equal(snapshot, counters);
    }

    internal static QpdfExecutionCounters Counters(object result) => result switch
    {
        PdfPageSelectionResult r => r.Counters,
        PdfMultiPageSelectionResult r => r.Counters,
        QpdfCheckResult r => r.Counters,
        QpdfPageCountResult r => r.Counters,
        QpdfJsonResult r => r.Counters,
        QpdfJsonImportResult r => r.Counters,
        QpdfAttachmentExtraction r => r.Counters,
        QpdfAttachmentEditResult r => r.Counters,
        QpdfExecutionResult r => r.Counters,
        _ => throw new InvalidOperationException("Unknown public result family."),
    };
    internal static byte[] DocumentBytes(bool compressed, string catalog)
    {
        byte[] content = "q Q\n"u8.ToArray();
        if (compressed)
        {
            using MemoryStream output = new();
            using (ZLibStream encoder = new(output, CompressionLevel.Optimal, true)) { encoder.Write(content); }
            content = output.ToArray();
        }
        List<(int, string, byte[]?)> objects = CheckTests.SinglePage(
            "<< /Length " + content.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>", content);
        string extra = catalog == "direct" ? " /Version /1.7" : catalog == "indirect" ? " /Version 6 0 R" : "";
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R" + extra + " >>", null);
        if (catalog == "indirect") { objects.Add((6, "/1.7", null)); }
        return CheckTests.Assemble(objects, 1);
    }

}
