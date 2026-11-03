using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ChoiceAppearanceTests
{
    private const string Pairs = "[[(EX) (DISPLAY)] [(OTHER) (SECOND)]]";

    private static byte[] Fixture(string value, int flags, string options, string indices, bool reversed)
    {
        byte[] stale = "/Tx BMC BT /F1 10 Tf (STALE) Tj ET EMC\n"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /FT /Ch /T (Choice) /Ff " + flags + " /Rect [10 20 150 100] /DA (/F1 10 Tf 0 g) "
                + (value.Length == 0 ? "" : "/V " + value) + " /Opt " + options + " " + (indices.Length == 0 ? "" : "/I " + indices) + " /AP << /N 5 0 R >> >>", null),
            (5, "<< /Subtype /Form /BBox " + (reversed ? "[0 80 140 0]" : "[0 0 140 80]") + " /Resources << /Font << /F1 6 0 R >> >> /Length " + stale.Length + " >>", stale),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
            (7, "null", null), (8, "(EX)", null), (9, "(EX)", null), (10, "(DISPLAY)", null),
            (11, "[12 0 R (SECOND)]", null), (12, "[9 0 R 10 0 R]", null),
            (13, "[14 0 R]", null), (14, "1", null)], 1);
    }

    private static IEnumerable<object[]> Inputs()
    {
        yield return ["plain", "(B)", 0, "[(A) (B) (C)]", "", new[] { "A", "B", "C" }, new[] { 1 }, false];
        yield return ["pair", "(EX)", 0, Pairs, "", new[] { "DISPLAY", "SECOND" }, new[] { 0 }, false];
        yield return ["combo", "(EX)", 131072, Pairs, "", new[] { "DISPLAY" }, Array.Empty<int>(), false];
        yield return ["editable", "(CUSTOM)", 393216, Pairs, "", new[] { "CUSTOM" }, Array.Empty<int>(), false];
        yield return ["empty-export", "()", 0, "[[() (EMPTY)] (SECOND)]", "", new[] { "EMPTY", "SECOND" }, new[] { 0 }, false];
        yield return ["empty-options", "", 0, "[]", "", Array.Empty<string>(), Array.Empty<int>(), false];
        yield return ["empty-options-reversed", "", 0, "[]", "", Array.Empty<string>(), Array.Empty<int>(), false];
        yield return ["cleared-selection", "()", 0, Pairs, "[]", new[] { "DISPLAY", "SECOND" }, Array.Empty<int>(), false];
        foreach (string value in new[] { "", "null", "7 0 R" })
        { yield return ["absent-" + value, value, 0, Pairs, "", new[] { "DISPLAY", "SECOND" }, Array.Empty<int>(), false]; }
        yield return ["indirect", "8 0 R", 0, "11 0 R", "", new[] { "DISPLAY", "SECOND" }, new[] { 0 }, false];
        yield return ["multi", "[(EX) (OTHER)]", 2097152, Pairs, "[0 1]", new[] { "DISPLAY", "SECOND" }, new[] { 0, 1 }, false];
        yield return ["multi-order", "[(OTHER) (EX)]", 2097152, Pairs, "[0 1]", new[] { "DISPLAY", "SECOND" }, new[] { 0, 1 }, false];
        yield return ["multi-empty", "[]", 2097152, Pairs, "[]", new[] { "DISPLAY", "SECOND" }, Array.Empty<int>(), false];
        yield return ["duplicate-export", "(EX)", 0, "[[(EX) (FIRST)] [(EX) (SECOND)] (OTHER)]", "[1]", new[] { "FIRST", "SECOND", "OTHER" }, new[] { 1 }, false];
        yield return ["indirect-index", "(EX)", 0, "[[(EX) (FIRST)] [(EX) (SECOND)] (OTHER)]", "13 0 R", new[] { "FIRST", "SECOND", "OTHER" }, new[] { 1 }, false];
        yield return ["repeated-export", "[(EX) (EX)]", 2097152, "[[(EX) (FIRST)] [(EX) (SECOND)]]", "[0 1]", new[] { "FIRST", "SECOND" }, new[] { 0, 1 }, false];
        yield return ["unicode-identity", "<feff0392>", 0, "[[<feff0391> (ALPHA)] [<feff0392> (BETA)]]", "", new[] { "ALPHA", "BETA" }, new[] { 1 }, false];
        foreach ((string name, string value, int flags, string options, string indices) in new[]
        {
            ("unknown-list", "(MISSING)", 0, Pairs, ""), ("unknown-combo", "(MISSING)", 131072, Pairs, ""),
            ("wrong-value", "42", 0, Pairs, ""), ("wrong-options", "(EX)", 0, "42", ""),
            ("wrong-pair", "(EX)", 0, "[[(EX) (DISPLAY) (EXTRA)]]", ""),
            ("multi-no-indices", "[(EX)]", 2097152, Pairs, ""), ("multi-no-flag", "[(EX)]", 0, Pairs, "[0]"),
            ("multi-count", "[(EX)]", 2097152, Pairs, "[0 1]"), ("multi-value", "[42]", 2097152, Pairs, "[0]"),
            ("index-negative", "[(EX)]", 2097152, Pairs, "[-1]"), ("index-range", "[(EX)]", 2097152, Pairs, "[2]"),
            ("index-order", "[(EX) (OTHER)]", 2097152, Pairs, "[1 0]"),
            ("index-duplicate", "[(EX) (EX)]", 2097152, Pairs, "[0 0]"),
            ("index-mismatch", "[(OTHER)]", 2097152, Pairs, "[0]"),
            ("ambiguous", "(EX)", 0, "[[(EX) (FIRST)] [(EX) (SECOND)]]", ""),
        }) { yield return [name, value, flags, options, indices, new[] { "STALE" }, Array.Empty<int>(), true]; }
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (object[] row in Inputs())
        foreach (bool job in new[] { false, true })
        { yield return row.Concat(new object[] { job, false }).ToArray(); }
        foreach (object[] row in Inputs().Where(r => (string)r[0] is "pair" or "combo" or "multi" or "index-mismatch"))
        { yield return row.Concat(new object[] { true, true }).ToArray(); }
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RegenerationPreservesChoiceIdentity(string name, string value, int flags, string options, string indices,
        string[] labels, int[] selected, bool skipped, bool job, bool flatten)
    {
        Assert.NotEmpty(name);
        byte[] output;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(value, flags, options, indices, name.EndsWith("reversed", StringComparison.Ordinal)), QpdfInputOwnership.Copy);
            List<string> argv = ["input", "--generate-appearances", "--compress-streams=n"];
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true, ["compressStreams"] = "n" };
            if (flatten) { argv.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; }
            argv.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(skipped ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            if (skipped) { Assert.Contains(result.Diagnostics, d => d.Message.Contains("choice", StringComparison.Ordinal)); }
            Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out output));
        }
        PdfStream appearance;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
            {
                PdfDictionary page = document.PageReferences[0].Page;
                if (flatten && !skipped)
                {
                    PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
                    appearance = Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(Get(document, resources, "XObject")), "Fxo1"));
                }
                else
                {
                    PdfArray annots = Assert.IsType<PdfArray>(Get(document, page, "Annots"));
                    PdfDictionary widget = Assert.IsType<PdfDictionary>(annots[0] is PdfReference reference ? document.GetObject(reference.ObjectNumber) : annots[0]);
                    appearance = Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(Get(document, widget, "AP")), "N"));
                    PdfDictionary form = Assert.IsType<PdfDictionary>(Get(document, Assert.IsType<PdfDictionary>(document.GetObject(1)), "AcroForm"));
                    bool needs = form.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject raw) && raw is PdfBoolean flag && flag.Value;
                    Assert.Equal(skipped, needs);
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        string program = Encoding.Latin1.GetString(appearance.RawMemory.Span);
        string[] painted = Regex.Matches(program, @"\(([^()]*)\) Tj").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(labels, painted);
        double[] highlightY = Regex.Matches(program, @"(?m)^0 ([0-9.+-]+) 140 12 re f$")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(selected.Select(index => 80 - (80 - labels.Length * 12) / 2.0 - 12 * (index + 1)), highlightY);
    }
}
