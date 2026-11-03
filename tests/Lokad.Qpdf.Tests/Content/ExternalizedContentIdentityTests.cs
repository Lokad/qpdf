using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ExternalizedContentIdentityTests
{
    private static readonly byte[] Original = [.. "% ORIGINAL\nq 30 0 0 30 10 10 cm BI /W 1 /H 1 /BPC 8 /CS /RGB ID "u8, 255, 0, 0, .. " EI Q\n"u8];

    private static byte[] Fixture(string alias, bool array, bool image)
    {
        byte[] content = image ? Original : "q Q\n"u8.ToArray();
        string catalog = alias switch
        {
            "catalog-stream" => "/Audit 4 0 R", "catalog-array" => "/Audit 5 0 R",
            "nested" => "/Audit << /Original 5 0 R >>", "holder" => "/Audit 6 0 R",
            "metadata" => "/Metadata 10 0 R", "attachment" => "/Names << /EmbeddedFiles << /Names [(copy) 8 0 R] >> >>", _ => ""
        };
        byte[] bytes = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + catalog + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents " + (array ? "5" : "4") + " 0 R " + (alias == "page" ? "/Audit 5 0 R" : "") + " >>", null),
            (4, "<< /Type /EmbeddedFile /Length " + content.Length + " >>", content),
            (5, "[4 0 R]", null), (6, "<< /Original 5 0 R >>", null),
            (7, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> " + (alias == "other-page" ? "/Audit 5 0 R" : "") + " >>", null),
            (8, "<< /Type /Filespec /F (content.bin) /EF << /F 4 0 R >> >>", null),
            (9, "<< /Producer (test) /Keep 5 0 R >>", null),
            (10, "<< /Type /Metadata /Subtype /XML /Keep 5 0 R /Length 7 >>", "<root/>"u8.ToArray())], 1);
        return alias == "info" ? Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace("/Root 1 0 R", "/Root 1 0 R /Info 9 0 R", StringComparison.Ordinal)) : bytes;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string alias in new[] { "none", "catalog-stream", "catalog-array", "nested", "holder", "page", "other-page", "info", "metadata", "attachment" })
        foreach (bool array in new[] { false, true })
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "whole", "select", "preserve" }) { yield return [alias, array, mode, job]; }
        foreach (string alias in new[] { "none", "nested", "info", "metadata", "attachment", "page" })
        foreach (string mode in new[] { "packed", "qdf", "linear", "split", "merge", "duplicate" })
        foreach (bool job in new[] { false, true }) { yield return [alias, true, mode, job]; }
    }

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, string mode, bool job, QpdfLimits limits, CancellationToken token)
    {
        List<string> argv = ["input", "--externalize-inline-images", "--ii-min-bytes=0", "--stream-data=preserve", "--compress-streams=n", "--normalize-content=n", "--remove-unreferenced-resources=no"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["externalizeInlineImages"] = true, ["iiMinBytes"] = "0", ["streamData"] = "preserve", ["compressStreams"] = "n", ["normalizeContent"] = "n", ["removeUnreferencedResources"] = "no" };
        if (mode is "select" or "merge" or "duplicate")
        {
            string range = mode == "duplicate" ? "1,1,2" : "1-2";
            argv.AddRange(["--pages", ".", range]); List<object> pages = [new { file = ".", range }];
            if (mode == "merge") { argv.AddRange(["other", "1"]); pages.Add(new { file = "other", range = "1" }); }
            argv.Add("--"); settings["pages"] = pages;
        }
        if (mode == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
        if (mode == "packed") { argv.Add("--object-streams=generate"); settings["objectStreams"] = "generate"; }
        if (mode == "qdf") { argv.Add("--qdf"); settings["qdf"] = true; }
        if (mode == "linear") { argv.Add("--linearize"); settings["linearize"] = true; }
        if (mode == "preserve") { argv.Add("--preserve-unreferenced"); settings["preserveUnreferenced"] = true; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), new(limits), token).AsTask()
            : Qpdf.ExecuteAsync(host, new(argv), new(limits), token).AsTask();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExternalizationPreservesIndependentRootsAndRetiresOnlyUnreachableOriginals(string alias, bool array, string mode, bool job)
    {
        List<PdfStream> escaped = [];
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(alias, array, true), QpdfInputOwnership.Copy);
            host.AddInput(QpdfPath.From("other"), Fixture("none", false, false), QpdfInputOwnership.Copy);
            QpdfExecutionResult result = await Run(host, mode, job, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
            for (int group = 1; group <= (mode == "split" ? 2 : 1); group++)
            {
                Assert.True(host.TryGetOutput(QpdfPath.From(mode == "split" ? "output-" + group : "output"), out byte[] output));
                host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
                QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
                QpdfObject Get(PdfDictionary node, string key) { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
                bool rooted = alias != "none" && !(mode == "split" && alias is "catalog-stream" or "catalog-array" or "nested" or "holder")
                    && !(mode == "split" && (alias == "page" && group == 2 || alias == "other-page" && group == 1));
                QpdfObject? retained = null;
                if (rooted)
                {
                    if (alias is "catalog-stream" or "catalog-array" or "nested" or "holder")
                    {
                        retained = Get(catalog, "Audit");
                        if (alias is "nested" or "holder") { retained = Get(Assert.IsType<PdfDictionary>(Resolve(retained)), "Original"); }
                    }
                    else if (alias is "page" or "other-page")
                    { retained = Get(document.PageReferences.Select(p => p.Page).First(p => p.TryGet(PdfName.FromAscii("Audit"), out _)), "Audit"); }
                    else if (alias is "info" or "metadata")
                    { retained = Get(Assert.IsType<PdfDictionary>(alias == "info" ? Resolve(Get(document.Engine.Trailer, "Info")) : Assert.IsType<PdfStream>(Resolve(Get(catalog, "Metadata"))).Dictionary), "Keep"); }
                    else
                    {
                        PdfDictionary names = Assert.IsType<PdfDictionary>(Resolve(Get(catalog, "Names")));
                        PdfArray pairs = Assert.IsType<PdfArray>(Resolve(Get(Assert.IsType<PdfDictionary>(Resolve(Get(names, "EmbeddedFiles"))), "Names")));
                        PdfDictionary spec = Assert.IsType<PdfDictionary>(Resolve(pairs[1]));
                        retained = Get(Assert.IsType<PdfDictionary>(Resolve(Get(spec, "EF"))), "F");
                    }
                    QpdfObject target = Resolve(retained);
                    if (target is PdfArray list) { target = Resolve(list[0]); }
                    Assert.Equal(Original, Assert.IsType<PdfStream>(target).RawMemory.ToArray());
                }
                List<PdfStream> originals = document.Engine.XRefEntries.Where(e => e.Key > 0 && e.Value.Kind != XrefEntryKind.Free)
                    .Select(e => document.GetObject(e.Key)).OfType<PdfStream>().Where(s => s.RawMemory.Span.SequenceEqual(Original)).ToList();
                Assert.Equal(rooted || mode == "preserve" ? 1 : 0, originals.Count); escaped.AddRange(originals);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        foreach (PdfStream stream in escaped) { Assert.Equal(Original, stream.RawMemory.ToArray()); }
    }

    private sealed class FailureHost(InMemoryQpdfHost inner, IMemoryBudget budget, string phase, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
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
    [InlineData("write")]
    [InlineData("commit")]
    [InlineData("cancel")]
    [InlineData("local")]
    [InlineData("shared")]
    public async Task FailurePreservesPriorOutputAndRefundsTheCall(string phase)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Fixture("nested", true, true), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(phase == "shared" ? 16384 : 16777216); using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            QpdfExecutionResult result = await Run(new FailureHost(storage, caller, phase, cancellation), "whole", true,
                phase == "local" ? QpdfLimits.Default with { MaxMemoryBytes = 16384 } : QpdfLimits.Default, cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (phase is "local" or "shared") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
