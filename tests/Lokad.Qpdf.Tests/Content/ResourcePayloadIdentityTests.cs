using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ResourcePayloadIdentityTests
{
    private static readonly byte[] FormBytes = [.. "% ORIGINAL FORM\r\nq 30 0 0 30 10 10 cm BI /W 1 /H 1 /BPC 8 /CS /RGB ID "u8, 255, 0, 0, .. " EI Q\r\n"u8];
    private static readonly byte[] ImageBytes = Samples();
    private static byte[] Samples()
    {
        byte[] samples = new byte[128 * 128 * 3]; new Random(42).NextBytes(samples); return samples;
    }

    private static byte[] Fixture(bool image, string shape, bool rooted)
    {
        string xobjects = "/Target 6 0 R /Alias 6 0 R" + (shape == "flat" ? "" : " /Outer 7 0 R /OtherOuter 7 0 R");
        string resources = shape == "flat" ? "<< /XObject << " + xobjects + " >> >>" : "8 0 R";
        string roots = rooted ? "/Audit 6 0 R /AuditResources 8 0 R /AuditOuter 7 0 R" : "";
        string catalog = rooted ? "/Original 6 0 R /Names << /EmbeddedFiles << /Names [(copy) 9 0 R] >> >>" : "";
        byte[] payload = image ? ImageBytes : FormBytes;
        string header = image ? "/Subtype /Image /Width 128 /Height 128 /BitsPerComponent 8 /ColorSpace /DeviceRGB"
            : "/Subtype /Form /BBox [0 0 100 200] /Resources << >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + catalog + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources " + resources + " /Contents 5 0 R " + roots + " >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources " + resources + " /Contents 5 0 R " + roots + " >>", null),
            (5, "<< /Length 10 >>", "/Target Do"u8.ToArray()),
            (6, "<< /Length " + payload.Length + " " + header + " >>", payload),
            (7, "<< /Subtype /Form /BBox [0 0 100 200] /Resources << /XObject << /Target 6 0 R /Alias 6 0 R " +
                (shape == "cycle" ? "/Self 7 0 R" : "") + " >> >> /Length 10 >>", "/Target Do"u8.ToArray()),
            (8, "<< /XObject << " + xobjects + " >> >>", null),
            (9, "<< /Type /Filespec /F (original.bin) /EF << /F 6 0 R >> >>", null)], 1);
    }

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, bool image, string mode, bool job, CancellationToken token)
    {
        List<string> argv = ["input", "--stream-data=preserve", "--compress-streams=n", "--decode-level=none", "--normalize-content=n", "--remove-unreferenced-resources=no"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "preserve", ["compressStreams"] = "n", ["decodeLevel"] = "none", ["normalizeContent"] = "n", ["removeUnreferencedResources"] = "no" };
        if (image)
        {
            argv.AddRange(["--optimize-images", "--oi-min-width=0", "--oi-min-height=0", "--oi-min-area=0"]);
            settings["optimizeImages"] = true; settings["oiMinWidth"] = "0"; settings["oiMinHeight"] = "0"; settings["oiMinArea"] = "0";
        }
        else { argv.AddRange(["--externalize-inline-images", "--ii-min-bytes=0"]); settings["externalizeInlineImages"] = true; settings["iiMinBytes"] = "0"; }
        if (mode is "select" or "duplicate" or "merge")
        {
            string range = mode == "duplicate" ? "1,1,2" : "1-2";
            argv.AddRange(["--pages", ".", range]); List<object> pages = [new { file = ".", range }];
            if (mode == "merge") { argv.AddRange(["other", "1-2"]); pages.Add(new { file = "other", range = "1-2" }); }
            argv.Add("--"); settings["pages"] = pages;
        }
        if (mode == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
        if (mode == "packed") { argv.Add("--object-streams=generate"); settings["objectStreams"] = "generate"; }
        if (mode == "qdf") { argv.Add("--qdf"); settings["qdf"] = true; }
        if (mode == "linear") { argv.Add("--linearize"); settings["linearize"] = true; }
        if (mode == "preserve") { argv.Add("--preserve-unreferenced"); settings["preserveUnreferenced"] = true; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), token).AsTask() : Qpdf.ExecuteAsync(host, new(argv), token).AsTask();
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool image in new[] { false, true })
        foreach (string shape in new[] { "flat", "nested", "cycle" })
        foreach (bool job in new[] { false, true })
        foreach (bool rooted in new[] { false, true }) { yield return [image, shape, "whole", job, rooted]; }
        foreach (bool image in new[] { false, true })
        foreach (string mode in new[] { "select", "duplicate", "merge", "split", "packed", "qdf", "linear", "preserve" })
        foreach (bool job in new[] { false, true }) { yield return [image, "cycle", mode, job, true]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ResourceReplacementsKeepIndependentPayloadsAndSharedBindings(bool image, string shape, string mode, bool job, bool rooted)
    {
        List<PdfStream> escaped = [];
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, LokadImageJpegCodec.Instance))
        {
            host.AddInput(QpdfPath.From("input"), Fixture(image, shape, rooted), QpdfInputOwnership.Copy);
            host.AddInput(QpdfPath.From("other"), Fixture(image, shape, rooted), QpdfInputOwnership.Copy);
            QpdfExecutionResult result = await Run(host, image, mode, job, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
            for (int group = 1; group <= (mode == "split" ? 2 : 1); group++)
            {
                Assert.True(host.TryGetOutput(QpdfPath.From(mode == "split" ? "output-" + group : "output"), out byte[] output));
                host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
                QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
                QpdfObject Get(PdfDictionary node, string key) { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
                PdfDictionary Dictionary(QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(value));
                PdfDictionary Objects(PdfDictionary owner) => Dictionary(Get(Dictionary(Get(owner, "Resources")), "XObject"));
                PdfReference? first = null;
                foreach (var location in document.PageReferences)
                {
                    PdfDictionary page = location.Page, bindings = Objects(page);
                    PdfReference transformed = Assert.IsType<PdfReference>(Get(bindings, "Target")); Assert.Equal(transformed, Get(bindings, "Alias"));
                    if (first is not null && mode != "merge") { Assert.Equal(first, transformed); }
                    first = transformed;
                    PdfStream resource = Assert.IsType<PdfStream>(Resolve(transformed));
                    if (image) { Assert.Equal(PdfName.FromAscii("DCTDecode"), Get(resource.Dictionary, "Filter")); }
                    else { Assert.DoesNotContain("BI ", Encoding.Latin1.GetString(resource.RawMemory.Span)); Assert.Contains(" Do", Encoding.Latin1.GetString(resource.RawMemory.Span)); }
                    if (rooted)
                    {
                        QpdfObject original = Get(page, "Audit"); Assert.NotEqual(original, transformed);
                        PdfStream retained = Assert.IsType<PdfStream>(Resolve(original)); Assert.Equal(image ? ImageBytes : FormBytes, retained.RawMemory.ToArray()); escaped.Add(retained);
                        PdfDictionary oldResources = Dictionary(Get(page, "AuditResources")); Assert.Equal(original, Get(Dictionary(Get(oldResources, "XObject")), "Target"));
                        PdfStream oldOuter = Assert.IsType<PdfStream>(Resolve(Get(page, "AuditOuter"))); Assert.Equal(original, Get(Objects(oldOuter.Dictionary), "Target"));
                    }
                    if (shape != "flat")
                    {
                        PdfReference outer = Assert.IsType<PdfReference>(Get(bindings, "Outer")); Assert.Equal(outer, Get(bindings, "OtherOuter"));
                        PdfStream parent = Assert.IsType<PdfStream>(Resolve(outer)); PdfDictionary nested = Objects(parent.Dictionary);
                        Assert.Equal(transformed, Get(nested, "Target")); Assert.Equal(transformed, Get(nested, "Alias"));
                        if (shape == "cycle") { Assert.Equal(outer, Get(nested, "Self")); }
                    }
                }
                if (rooted)
                {
                    QpdfExecutionResult extraction = await Qpdf.ExecuteAsync(host, new(["read", "--show-attachment=copy"]), CancellationToken.None);
                    Assert.Equal(QpdfExitCode.Success, extraction.ExitCode); ReadOnlyMemory<byte> bytes = Assert.IsType<ReadOnlyMemory<byte>>(extraction.OutputBytes);
                    Assert.Equal(image ? ImageBytes : FormBytes, bytes.ToArray());
                }
                int originalPayloads = 0;
                foreach (int number in document.Engine.XRefEntries.Keys)
                {
                    if (number != 0 && document.GetObject(number) is PdfStream stream && stream.RawMemory.Span.SequenceEqual(image ? ImageBytes : FormBytes)) { originalPayloads++; }
                }
                Assert.Equal(rooted ? mode == "merge" ? 2 : 1 : 0, originalPayloads);
            }
        }
        foreach (PdfStream stream in escaped) { Assert.Equal(image ? ImageBytes : FormBytes, stream.RawMemory.ToArray()); }
    }

    private sealed class FailureHost(InMemoryQpdfHost inner, IMemoryBudget budget, string phase, CancellationTokenSource cancellation)
        : IQpdfHost, IQpdfMemoryHost, IQpdfJpegCodecHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public IQpdfJpegCodec JpegCodec => LokadImageJpegCodec.Instance;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(await inner.CreateOutputAsync(path, token), phase, cancellation);
        private sealed class Transaction(IQpdfOutputTransaction inner, string phase, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                if (phase == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (phase == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token) => phase == "commit" ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false, "write")]
    [InlineData(true, "write")]
    [InlineData(false, "commit")]
    [InlineData(true, "commit")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "local")]
    [InlineData(true, "local")]
    [InlineData(false, "shared")]
    [InlineData(true, "shared")]
    public async Task FailurePreservesTheTargetAndRefundsBothResourcePaths(bool image, string phase)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(image, "cycle", true), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(phase == "shared" ? 32768 : 16777216); using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            FailureHost executionHost = new(host, caller, phase, cancellation);
            List<string> argv = ["input", "--stream-data=preserve", "--compress-streams=n", "--remove-unreferenced-resources=no"];
            argv.AddRange(image ? ["--optimize-images", "--oi-min-width=0", "--oi-min-height=0", "--oi-min-area=0"] : ["--externalize-inline-images", "--ii-min-bytes=0"]); argv.Add("output");
            QpdfLimits limits = phase == "local" ? QpdfLimits.Default with { MaxMemoryBytes = 16384 } : QpdfLimits.Default;
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(executionHost, new(argv), new(limits), cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (phase is "local" or "shared") { Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded); }
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
