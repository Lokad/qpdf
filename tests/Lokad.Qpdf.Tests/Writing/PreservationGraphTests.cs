using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PreservationGraphTests
{
    private static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "preservation-" + name + ".pdf"));

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, bool job, string path, string packing, CancellationToken token)
    {
        List<string> argv = ["input", "--preserve-unreferenced", "--stream-data=preserve", "--object-streams=" + packing];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["preserveUnreferenced"] = true, ["streamData"] = "preserve", ["objectStreams"] = packing };
        if (path is "select" or "merge")
        {
            argv.AddRange(["--pages", ".", "1"]); List<object> pages = [new { file = ".", range = "1" }];
            if (path == "merge") { argv.AddRange(["other", "1"]); pages.Add(new { file = "other", range = "1" }); }
            argv.Add("--"); settings["pages"] = pages;
        }
        if (path == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), token).AsTask()
            : Qpdf.ExecuteAsync(host, new(argv), token).AsTask();
    }

    private static async Task<Dictionary<string, (PdfReference Reference, QpdfObject Value)>> ReadTags(byte[] output)
    {
        Dictionary<string, (PdfReference, QpdfObject)> tags = [];
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            foreach (var entry in document.Engine.XRefEntries)
            {
                if (entry.Key <= 0 || entry.Value.Kind == XrefEntryKind.Free) { continue; }
                QpdfObject value = document.GetObject(entry.Key);
                PdfDictionary? dictionary = value is PdfStream stream ? stream.Dictionary : value as PdfDictionary;
                if (dictionary is null || !dictionary.TryGet(PdfName.FromAscii("Tag"), out QpdfObject raw)) { continue; }
                string tag = Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(raw).ValueSpan);
                Assert.True(tags.TryAdd(tag, (new PdfReference(entry.Key, 0), value)), "Duplicated source identity: " + tag);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount); return tags;
    }

    private static PdfDictionary Dictionary(QpdfObject value) => value is PdfStream stream ? stream.Dictionary : Assert.IsType<PdfDictionary>(value);
    private static QpdfObject Get(QpdfObject node, string key)
    { Assert.True(Dictionary(node).TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (string path in new[] { "whole", "select", "merge", "split" })
        foreach (string packing in new[] { "disable", "generate" })
        foreach (string name in new[] { "forward-sharing", "backward-sharing", "cycle", "self-cycle", "shared-stream", "nested-unpreservable", "simple-unpreservable", "unloadable" })
        { yield return [job, path, packing, name]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PreservationKeepsOneCopyAndRetiresFailedCandidates(bool job, string path, string packing, string name)
    {
        byte[] output;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(name), QpdfInputOwnership.Copy);
            host.AddInput(QpdfPath.From("other"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
            QpdfExecutionResult result = await Run(host, job, path, packing, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            bool warning = name is "nested-unpreservable" or "simple-unpreservable" or "unloadable";
            Assert.Equal(warning ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            if (warning) { Assert.NotEmpty(result.Diagnostics); }
            else { Assert.Empty(result.Diagnostics); }
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.HostOperationFailed);
            Assert.True(host.TryGetOutput(QpdfPath.From(path == "split" ? "output-1" : "output"), out output)); Assert.Equal(0L, host.OpenLeaseCount);
        }
        var tags = await ReadTags(output);
        if (name is "nested-unpreservable" or "simple-unpreservable" or "unloadable") { Assert.Empty(tags); return; }
        Assert.Equal(3, tags.Count);
        Assert.Equal(tags["B"].Reference, Assert.IsType<PdfReference>(Get(tags["A"].Value, "Peer")));
        Assert.Equal(tags["B"].Reference, Assert.IsType<PdfReference>(Get(tags["C"].Value, "Peer")));
        if (name == "cycle") { Assert.Equal(tags["A"].Reference, Assert.IsType<PdfReference>(Get(tags["B"].Value, "Peer"))); }
        if (name == "self-cycle") { Assert.Equal(tags["B"].Reference, Assert.IsType<PdfReference>(Get(tags["B"].Value, "Peer"))); }
        if (name == "shared-stream") { Assert.Equal(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Kept shared payload\n", 1024))), Assert.IsType<PdfStream>(tags["B"].Value).Data); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NonzeroAndStaleGenerationsKeepTheirIdentityPolicy(bool job, bool stale)
    {
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Tag (A) /Peer 5 " + (stale ? "0" : "2") + " R >>", null),
            (5, "<< /Tag (B) >>", null), (6, "<< /Tag (C) /Peer 5 2 R >>", null)], 1, new Dictionary<int, int> { [5] = 2 });
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, "split", "generate", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output-1"), out byte[] output)); var tags = await ReadTags(output);
        Assert.Equal(3, tags.Count); Assert.Equal(tags["B"].Reference, Assert.IsType<PdfReference>(Get(tags["C"].Value, "Peer")));
        if (stale) { Assert.IsType<PdfNull>(Get(tags["A"].Value, "Peer")); }
        else { Assert.Equal(tags["B"].Reference, Assert.IsType<PdfReference>(Get(tags["A"].Value, "Peer"))); }
    }

    internal static byte[] RollbackFixture() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Keep 5 0 R >>", null), (4, "null", null),
        (5, "<< /Tag (Prior) /Data (existing) >>", null),
        (6, "<< /Tag (A) /Good 10 0 R /Peer 7 0 R /Prior 5 0 R /Bad 9 0 R >>", null),
        (7, "<< /Tag (B) /Peer 6 0 R >>", null), (8, "<< /Tag (C) /Peer 7 0 R >>", null),
        (9, "<< /Tag (D) /Peer 1 0 R >>", null), (10, "<< /Tag (Good) /Data (independent) >>", null)], 1);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StreamRewritingKeepsPreservedParametersAfterDisconnectingThem(bool job, bool split)
    {
        using MemoryStream encoded = new();
        using (System.IO.Compression.ZLibStream compressor = new(encoded, System.IO.Compression.CompressionLevel.Optimal, true))
        { compressor.Write("q Q"u8); }
        byte[] data = encoded.ToArray();
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "null", null), (5, "<< /Tag (A) /Filter /FlateDecode /DecodeParms 6 0 R /Length " + data.Length + " >>", data),
            (6, "<< /Tag (B) /Predictor 1 >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
        List<string> argv = ["input", "--preserve-unreferenced", "--stream-data=uncompress"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["preserveUnreferenced"] = true, ["streamData"] = "uncompress" };
        if (split) { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; } argv.Add("output");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From(split ? "output-1" : "output"), out byte[] output)); var tags = await ReadTags(output);
        Assert.Equal(2, tags.Count); PdfStream stream = Assert.IsType<PdfStream>(tags["A"].Value);
        Assert.False(stream.Dictionary.TryGet(PdfName.FromAscii("DecodeParms"), out _)); Assert.Equal("q Q"u8.ToArray(), stream.Data);
        Assert.Equal(1L, Assert.IsType<PdfInteger>(Get(tags["B"].Value, "Predictor")).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StrictMalformedLoadsStillFailWithoutReplacingOutput(bool job, bool reachable)
    {
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents " + (reachable ? "5" : "4") + " 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()), (5, "<< /Bad [", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","preserveUnreferenced":true,"suppressRecovery":true}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--preserve-unreferenced", "--suppress-recovery", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.NotEmpty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
    }

    [Theory]
    [InlineData(false, "whole")]
    [InlineData(true, "select")]
    [InlineData(false, "merge")]
    [InlineData(true, "split")]
    public async Task RollbackKeepsEarlierCopiesAndRetriesIndependentDescendants(bool job, string path)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), RollbackFixture(), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("other"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, path, "generate", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.HostOperationFailed);
        Assert.True(host.TryGetOutput(QpdfPath.From(path == "split" ? "output-1" : "output"), out byte[] output));
        var tags = await ReadTags(output); Assert.Equal(2, tags.Count);
        Assert.Equal("existing"u8.ToArray(), Assert.IsType<PdfByteString>(Get(tags["Prior"].Value, "Data")).Value);
        Assert.Equal("independent"u8.ToArray(), Assert.IsType<PdfByteString>(Get(tags["Good"].Value, "Data")).Value);
    }
}
