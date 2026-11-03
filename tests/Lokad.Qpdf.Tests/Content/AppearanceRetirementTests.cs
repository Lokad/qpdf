using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceRetirementTests
{
    private static byte[] Fixture(string alias, string kind, bool needs)
    {
        bool two = kind is "partial" or "shared";
        byte[] first = Encoding.ASCII.GetBytes("%" + new string('x', 4096) + "\n/Tx BMC BT /F1 10 Tf (STALE1) Tj ET EMC\n");
        byte[] second = Encoding.ASCII.GetBytes("/Tx BMC BT /F1 10 Tf (" + (kind == "partial" ? "STALE2" : "ORPHAN") + ") Tj ET EMC\n");
        string catalog = alias switch { "catalog" => "/Keep 5 0 R", "header" => "/Keep 9 0 R", "metadata" => "/Metadata 12 0 R", _ => "" };
        string extra = alias == "rollover" ? "/R 5 0 R" : alias == "down" ? "/D << /On 5 0 R >>" : "";
        string header = "/Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /F1 6 0 R >> >>";
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + catalog + " /AcroForm << /NeedAppearances " + (needs ? "true" : "false") + " /Fields [4 0 R" + (two ? " 7 0 R" : "") + "] >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Annots [4 0 R" + (two ? " 7 0 R" : "") + "] >>", null),
            (4, "<< /Subtype /Widget /FT /Tx /T (First) /V (LIVE1) /DA (" + (kind == "skipped" ? "nonsense" : "/F1 10 Tf") + ") /Rect [10 20 110 40] /AP 9 0 R >>", null),
            (5, "<< " + header + " /Length " + first.Length + " >>", first),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (7, "<< /Subtype /Widget /FT /Tx /T (Second) /V (LIVE2) /DA (" + (kind == "partial" ? "nonsense" : "/F1 10 Tf") + ") /Rect [10 60 110 80] /AP " + (kind == "partial" ? "10" : "9") + " 0 R >>", null),
            (8, "<< " + header + " /Length " + second.Length + " >>", second),
            (9, "<< /Marker /OldAP /N 5 0 R " + extra + " >>", null),
            (10, "<< /Marker /FailedAP /N 8 0 R >>", null),
            (11, "<< /Producer (test) /Keep 5 0 R >>", null),
            (12, "<< /Type /Metadata /Subtype /XML /Keep 5 0 R /Length 7 >>", "<root/>"u8.ToArray())], 1);
        // Only trailer text changes, after every indexed object and the xref offset.
        return alias == "info" ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(input).Replace("/Root 1 0 R", "/Root 1 0 R /Info 11 0 R", StringComparison.Ordinal)) : input;
    }

    private static Task<QpdfExecutionResult> Run(InMemoryQpdfHost host, string mode, bool job, bool preserve)
    {
        List<string> argv = ["input", "--generate-appearances", "--stream-data=preserve"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true, ["streamData"] = "preserve" };
        if (mode is "select" or "merge")
        {
            argv.AddRange(["--pages", ".", "1"]); List<object> pages = [new { file = ".", range = "1" }];
            if (mode == "merge") { argv.AddRange(["other", "1"]); pages.Add(new { file = "other", range = "1" }); }
            argv.Add("--"); settings["pages"] = pages;
        }
        if (mode == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
        if (mode == "packed") { argv.Add("--object-streams=generate"); settings["objectStreams"] = "generate"; }
        if (mode == "qdf") { argv.Add("--qdf"); settings["qdf"] = true; }
        if (mode == "linear") { argv.Add("--linearize"); settings["linearize"] = true; }
        if (preserve) { argv.Add("--preserve-unreferenced"); settings["preserveUnreferenced"] = true; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None).AsTask()
            : Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None).AsTask();
    }

    private static async Task Verify(byte[] bytes, string alias, string mode, bool preserve, string kind, bool needs)
    {
        List<PdfStream> escaped = []; int oldHeaders = 0;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
            {
                foreach (var entry in document.Engine.XRefEntries)
                {
                    if (entry.Key <= 0 || entry.Value.Kind == XrefEntryKind.Free) { continue; }
                    QpdfObject value = document.GetObject(entry.Key);
                    if (value is PdfStream stream && stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype)
                        && subtype is PdfName name && name.DisplayText == "Form") { escaped.Add(stream); }
                    if (value is PdfDictionary dictionary && dictionary.TryGet(PdfName.FromAscii("Marker"), out QpdfObject marker)
                        && marker is PdfName label && label.DisplayText == "OldAP") { oldHeaders++; }
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        string[] programs = escaped.Select(s => Encoding.ASCII.GetString(s.RawMemory.Span)).ToArray();
        int sources = mode == "merge" ? 2 : 1;
        bool rebuilt = needs && kind != "skipped";
        bool retainedAlias = alias != "none" && !(mode == "split" && alias is "catalog" or "header");
        int oldFirst = !rebuilt || preserve ? sources : retainedAlias ? 1 : 0;
        int fresh = rebuilt ? (kind == "shared" ? 2 : 1) + sources - 1 : 0;
        int failed = kind == "partial" ? 1 : 0;
        int orphans = preserve ? sources - failed : 0;
        Assert.Equal(oldFirst, programs.Count(p => p.Contains("(STALE1)", StringComparison.Ordinal)));
        Assert.Equal(fresh, programs.Count(p => p.Contains("(LIVE", StringComparison.Ordinal)));
        Assert.Equal(failed, programs.Count(p => p.Contains("(STALE2)", StringComparison.Ordinal)));
        Assert.Equal(orphans, programs.Count(p => p.Contains("(ORPHAN)", StringComparison.Ordinal)));
        Assert.Equal(oldFirst + fresh + failed + orphans, programs.Length);
        Assert.Equal(!rebuilt || preserve ? sources : alias == "header" && mode != "split" ? 1 : 0, oldHeaders);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "whole", "select" })
        foreach (string alias in new[] { "none", "catalog", "header", "rollover", "down", "info", "metadata" })
        foreach (bool preserve in new[] { false, true }) { yield return [alias, mode, job, preserve]; }
        foreach (bool job in new[] { false, true })
        {
            foreach (string mode in new[] { "packed", "qdf", "linear" })
            foreach (string alias in new[] { "none", "catalog" }) { yield return [alias, mode, job, false]; }
            foreach (string alias in new[] { "none", "catalog", "info", "metadata" }) { yield return [alias, "merge", job, false]; }
            foreach (string alias in new[] { "none", "rollover", "down" }) { yield return [alias, "split", job, false]; }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RegeneratedProgramsRetireOnlyAfterFinalRootsAreKnown(string alias, string mode, bool job, bool preserve)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(alias, "single", true), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("other"), Fixture("none", "single", false), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, mode, job, preserve); Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From(mode == "split" ? "output-1" : "output"), out byte[] output));
        await Verify(output, alias, mode, preserve, "single", true);
    }

    [Theory]
    [InlineData("partial", false)]
    [InlineData("partial", true)]
    [InlineData("shared", false)]
    [InlineData("shared", true)]
    [InlineData("noop", false)]
    [InlineData("noop", true)]
    [InlineData("skipped", false)]
    [InlineData("skipped", true)]
    public async Task PartialSharedAndUnchangedProgramsKeepTheirOwners(string kind, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("none", kind, kind != "noop"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, "whole", job, false);
        Assert.Equal(kind is "partial" or "skipped" ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); await Verify(output, "none", "whole", false, kind, kind != "noop");
    }
}
