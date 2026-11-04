using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PreservationAncestorTests
{
    private static byte[] Fixture(int generation, string shape, bool unpreservable)
    {
        string parent = shape switch
        {
            "stale-private" => "5 " + (generation == 0 ? 1 : 0) + " R",
            "live-private" => "5 " + generation + " R",
            "null" => "null", "indirect-null" => "6 0 R",
            _ => "2 " + generation + " R",
        };
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 " + generation + " R >>", null),
            (2, "<< /Type " + (generation == 0 ? "/Pages" : "7 0 R") + " /Kids [3 0 R] /Count 1 /OldTreeTag (obsolete) >>", null),
            (3, "<< /Type /Page /Parent " + parent + " /MediaBox [0 0 100 100] /CropBox [0 0 100 100] /Rotate 0 /Resources << >> >>", null),
            (4, "(unreferenced-control)", null),
            (5, "<< /Tag (private-sentinel) /Payload <00ff4142> " + (unpreservable ? "/Outside 1 0 R" : "") + " >>", null),
            (6, "null", null), (7, "/Pages", null),
        ], 1, new Dictionary<int, int> { [2] = generation, [5] = generation });
    }

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, bool job, bool selected, bool preserve, int generation)
    {
        List<string> argv = ["input"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (preserve) { argv.Add("--preserve-unreferenced"); settings["preserveUnreferenced"] = true; }
        if (selected)
        {
            argv.AddRange(["--pages", ".", "1", "--"]);
            settings["pages"] = new[] { new { file = ".", range = "1" } };
        }
        if (generation != 0) { argv.Add("--object-streams=generate"); settings["objectStreams"] = "generate"; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None).AsTask()
            : Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None).AsTask();
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (int generation in new[] { 0, 17 })
        foreach (string shape in new[] { "real-parent", "stale-private", "live-private", "null", "indirect-null" })
        foreach (bool job in new[] { false, true })
        {
            yield return [generation, shape, job, false, false];
            yield return [generation, shape, job, false, true];
            yield return [generation, shape, job, true, true];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PreservationExcludesOnlyVerifiedPageTreeAncestors(int generation, string shape, bool job, bool selected, bool preserve)
    {
        PdfDictionary? escaped = null;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(generation, shape, false), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = await Run(host, job, selected, preserve, generation);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
            {
                int pageTrees = 0, pages = 0, looseTrees = 0;
                foreach (var entry in document.Engine.XRefEntries)
                {
                    if (entry.Key <= 0 || entry.Value.Kind == XrefEntryKind.Free) { continue; }
                    if (document.GetObject(entry.Key) is not PdfDictionary dictionary) { continue; }
                    if (dictionary.TryGet(PdfName.FromAscii("OldTreeTag"), out _)) { looseTrees++; }
                    if (dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject type))
                    {
                        if (type is PdfReference reference) { type = document.GetObject(reference.ObjectNumber); }
                        if (type is PdfName name && name.RawSpan.SequenceEqual("Pages"u8)) { pageTrees++; }
                        if (type is PdfName pageName && pageName.RawSpan.SequenceEqual("Page"u8)) { pages++; }
                    }
                    if (!dictionary.TryGet(PdfName.FromAscii("Tag"), out QpdfObject tag)) { continue; }
                    Assert.Equal("private-sentinel", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(tag).ValueSpan));
                    Assert.Null(escaped); escaped = dictionary;
                }
                // Explicit preservation retains loose trees outside the verified
                // selected ancestor chain, but never a second selected Page.
                Assert.Equal(preserve && shape != "real-parent" ? 1 : 0, looseTrees);
                Assert.Equal(1 + looseTrees, pageTrees); Assert.Equal(1, pages);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(preserve, escaped is not null);
        if (escaped is not null)
        {
            Assert.True(escaped.TryGet(PdfName.FromAscii("Payload"), out QpdfObject raw));
            Assert.True(Assert.IsType<PdfByteString>(raw).ValueSpan.SequenceEqual(new byte[] { 0, 255, 65, 66 }));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitPreservationReportsUncopyablePrivateAncestors(bool job, bool preserve)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(0, "stale-private", true), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction previous = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await previous.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await previous.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Run(host, job, false, preserve, 0);
        Assert.Equal(preserve ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.False(output.AsSpan().SequenceEqual("prior"u8));
        if (preserve)
        { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature && d.Message == "Skipped 1 unpreservable object."); }
    }
}
