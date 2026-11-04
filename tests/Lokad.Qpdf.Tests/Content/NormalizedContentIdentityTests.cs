using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class NormalizedContentIdentityTests
{
    private static readonly byte[] Original = "% PRESERVED\r\nq \r\nQ\r\n"u8.ToArray();
    private static readonly byte[] Normalized = "% PRESERVED\n\nq \nQ\n"u8.ToArray();

    private static byte[] Fixture(string shape, bool rooted)
    {
        string contents = shape is "indirect" or "repeat" ? "5 0 R" : shape == "array" ? "[4 0 R]" : "4 0 R";
        string roots = rooted ? "/Audit 5 0 R /Metadata 7 0 R /Names << /EmbeddedFiles << /Names [(copy) 8 0 R] >> >>" : "";
        byte[] bytes = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + roots + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 9 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents " + contents + " >>", null),
            (4, "<< /Type /EmbeddedFile /Length " + Original.Length + " >>", Original),
            (5, shape == "repeat" ? "[4 0 R 4 0 R]" : "[4 0 R]", null),
            (6, "<< /Producer (test) /Keep 4 0 R >>", null),
            (7, "<< /Type /Metadata /Subtype /XML /Keep 4 0 R /Length 7 >>", "<root/>"u8.ToArray()),
            (8, "<< /Type /Filespec /F (content.bin) /EF << /F 4 0 R >> >>", null),
            (9, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents " + contents + " >>", null)], 1);
        return rooted ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(bytes).Replace("/Root 1 0 R", "/Root 1 0 R /Info 6 0 R", StringComparison.Ordinal)) : bytes;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "single", "array", "indirect", "repeat" })
        foreach (string mode in new[] { "whole", "select", "duplicate", "split", "packed", "qdf", "linear", "preserve", "disabled" })
        foreach (bool job in new[] { false, true }) { yield return [shape, mode, job, true]; }
        foreach (string shape in new[] { "single", "array", "indirect", "repeat" })
        foreach (bool job in new[] { false, true }) { yield return [shape, "whole", job, false]; }
        foreach (string shape in new[] { "single", "repeat" })
        foreach (string mode in new[] { "coalesce", "externalize", "merge" })
        foreach (bool job in new[] { false, true }) { yield return [shape, mode, job, true]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NormalizationChangesPageEdgesAndPreservesIndependentPayloads(string shape, string mode, bool job, bool rooted)
    {
        List<PdfStream> escaped = [];
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(shape, rooted), QpdfInputOwnership.Copy);
            host.AddInput(QpdfPath.From("other"), CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null)], 1), QpdfInputOwnership.Copy);
            List<string> argv = ["input", "--normalize-content=" + (mode == "disabled" ? "n" : "y"), "--stream-data=uncompress", "--remove-unreferenced-resources=no"];
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["normalizeContent"] = mode == "disabled" ? "n" : "y", ["streamData"] = "uncompress", ["removeUnreferencedResources"] = "no" };
            if (mode is "select" or "duplicate" or "merge")
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
            if (mode == "coalesce") { argv.Add("--coalesce-contents"); settings["coalesceContents"] = true; }
            if (mode == "externalize") { argv.AddRange(["--externalize-inline-images", "--ii-min-bytes=0"]); settings["externalizeInlineImages"] = true; settings["iiMinBytes"] = "0"; }
            argv.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
            for (int group = 1; group <= (mode == "split" ? 2 : 1); group++)
            {
                Assert.True(host.TryGetOutput(QpdfPath.From(mode == "split" ? "output-" + group : "output"), out byte[] output));
                host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
                QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
                QpdfObject Get(PdfDictionary node, string key) { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
                PdfReference? normalizedLink = null;
                foreach (var page in document.PageReferences)
                {
                    if (!page.Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents)) { continue; }
                    QpdfObject target = Resolve(contents);
                    PdfArray? array = target as PdfArray;
                    for (int index = 0; index < (array?.Count ?? 1); index++)
                    {
                        QpdfObject item = array is null ? contents : array[index];
                        PdfReference link = Assert.IsType<PdfReference>(item);
                        if (normalizedLink is not null) { Assert.Equal(normalizedLink, link); }
                        normalizedLink = link;
                        byte[] expected = mode == "disabled" ? Original : mode == "coalesce" && shape == "repeat" ? [.. Normalized, .. Normalized] : Normalized;
                        Assert.Equal(expected, Assert.IsType<PdfStream>(Resolve(item)).RawMemory.ToArray());
                    }
                }
                if (rooted)
                {
                    PdfDictionary names = Assert.IsType<PdfDictionary>(Resolve(Get(catalog, "Names")));
                    PdfArray pairs = Assert.IsType<PdfArray>(Resolve(Get(Assert.IsType<PdfDictionary>(Resolve(Get(names, "EmbeddedFiles"))), "Names")));
                    PdfDictionary spec = Assert.IsType<PdfDictionary>(Resolve(pairs[1]));
                    QpdfObject file = Get(Assert.IsType<PdfDictionary>(Resolve(Get(spec, "EF"))), "F");
                    Assert.Equal(Original, Assert.IsType<PdfStream>(Resolve(file)).RawMemory.ToArray());
                    if (mode != "disabled") { Assert.NotEqual(normalizedLink, file); }
                    Assert.Equal(file, Get(Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Info"))), "Keep"));
                    Assert.Equal(file, Get(Assert.IsType<PdfStream>(Resolve(Get(catalog, "Metadata"))).Dictionary, "Keep"));
                    if (mode != "split")
                    {
                        PdfArray oldArray = Assert.IsType<PdfArray>(Resolve(Get(catalog, "Audit")));
                        for (int index = 0; index < oldArray.Count; index++) { Assert.Equal(file, oldArray[index]); }
                    }
                    escaped.Add(Assert.IsType<PdfStream>(Resolve(file)));
                }
                if (!rooted)
                {
                    IEnumerable<QpdfObject> objects = document.Engine.XRefEntries.Where(e => e.Key > 0 && e.Value.Kind != XrefEntryKind.Free).Select(e => document.GetObject(e.Key));
                    Assert.DoesNotContain(objects.OfType<PdfStream>(), stream => stream.RawMemory.Span.SequenceEqual(Original));
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        foreach (PdfStream stream in escaped) { Assert.Equal(Original, stream.RawMemory.ToArray()); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task SkippedPageProgramsKeepAllPartsAndShareOriginalStreams(bool filter, bool job, bool sharedArray)
    {
        byte[] damaged = "("u8.ToArray(); byte[] good = "q \r\nQ\r\n"u8.ToArray();
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Keep 4 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 5 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> /Contents " + (sharedArray ? "7 0 R" : "[4 0 R 6 0 R]") + " >>", null),
            (4, "<< /Length 1 " + (filter ? "/Filter /Unknown" : "") + " >>", damaged),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> /Contents " + (sharedArray ? "7 0 R" : "[4 0 R 6 0 R]") + " >>", null),
            (6, "<< /Length " + good.Length + " >>", good), (7, "[4 0 R 6 0 R]", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","normalizeContent":"y","streamData":"preserve","compressStreams":"n","removeUnreferencedResources":"no"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--normalize-content=y", "--stream-data=preserve", "--compress-streams=n", "--remove-unreferenced-resources=no", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode); Assert.Equal(sharedArray ? 1 : 2, result.Diagnostics.Count);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfReference? first = null, second = null;
        foreach (var page in document.PageReferences)
        {
            Assert.True(page.Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
            PdfArray entries = Assert.IsType<PdfArray>(raw is PdfReference link ? document.GetObject(link.ObjectNumber) : raw);
            PdfReference bad = Assert.IsType<PdfReference>(entries[0]), normalized = Assert.IsType<PdfReference>(entries[1]);
            if (first is not null) { Assert.Equal(first, bad); Assert.Equal(second, normalized); }
            first = bad; second = normalized;
            Assert.Equal(damaged, Assert.IsType<PdfStream>(document.GetObject(bad.ObjectNumber)).RawMemory.ToArray());
            Assert.Equal(good, Assert.IsType<PdfStream>(document.GetObject(normalized.ObjectNumber)).RawMemory.ToArray());
        }
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
    [InlineData("write", false)]
    [InlineData("commit", false)]
    [InlineData("cancel", false)]
    [InlineData("local", false)]
    [InlineData("shared", false)]
    [InlineData("write", true)]
    [InlineData("commit", true)]
    [InlineData("cancel", true)]
    [InlineData("local", true)]
    [InlineData("shared", true)]
    public async Task NormalizationFailuresPreserveTargetsAndRefundStorage(string phase, bool fragmented)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), fragmented ? LogicalContentPartitionTests.Fixture("dictionary", true, true, true).Bytes : Fixture("repeat", true), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(phase == "shared" ? 8192 : 16777216); using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(new FailureHost(storage, caller, phase, cancellation),
                """{"inputFile":"input","outputFile":"output","normalizeContent":"y","streamData":"uncompress"}""",
                new(phase == "local" ? QpdfLimits.Default with { MaxMemoryBytes = 8192 } : QpdfLimits.Default), cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (phase is "local" or "shared") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
