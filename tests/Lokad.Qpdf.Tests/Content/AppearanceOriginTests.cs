using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceOriginTests
{
    private static byte[] Fixture(string field, int x, int y, bool indirect)
    {
        string value = field switch
        {
            "text" => "/FT /Tx /V (HELLO)",
            "list" => "/FT /Ch /V (B) /Opt [(A) (B) (C)]",
            "multiselect" => "/FT /Ch /Ff 2097152 /V [(A) (C)] /I [0 2] /Opt [(A) (B) (C)]",
            _ => "/FT /Ch /Ff 131072 /V (B) /Opt [(A) (B) (C)]",
        };
        string box = FormattableString.Invariant($"[{x} {y} {x + 80} {y + 40}]");
        byte[] old = "/Tx BMC BT /F1 10 Tf (OLD) Tj ET EMC"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OriginalAP 5 0 R /AcroForm << /Fields [6 0 R] /NeedAppearances true /DA (/F1 10 Tf 0 g) /DR << /Font << /F1 7 0 R >> >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [6 0 R] >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Subtype /Form /BBox " + (indirect ? "8 0 R" : box) + " /Resources << /Font << /F1 7 0 R >> >> /Length " + old.Length + " >>", old),
            (6, "<< /Type /Annot /Subtype /Widget /T (field) /Rect [10 20 90 60] /F 4 /AP << /N 5 0 R >> " + value + " >>", null),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null), (8, box, null)], 1);
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value; }
    private static PdfDictionary Dict(QpdfDocument document, PdfDictionary node, string key) => Assert.IsType<PdfDictionary>(Get(document, node, key));

    public static IEnumerable<object[]> Cases()
    {
        foreach (string field in new[] { "text", "list", "multiselect", "combo" })
        foreach ((int x, int y) in new[] { (0, 0), (10, 20), (-10, -20), (20, 0) })
        foreach (bool indirect in new[] { false, true })
        foreach (bool flatten in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [field, x, y, indirect, flatten, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task GeneratedTextAndHighlightsUseTheBBoxOriginOnce(string field, int x, int y, bool indirect, bool flatten, bool job)
    {
        PdfStream escaped;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(field, x, y, indirect), QpdfInputOwnership.Transfer);
            List<string> argv = ["input", "--stream-data=uncompress", "--generate-appearances"];
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress", ["generateAppearances"] = true };
            if (flatten) { argv.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; }
            argv.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            PdfDictionary page = document.PageReferences[0].Page;
            if (flatten)
            {
                PdfDictionary objects = Dict(document, Dict(document, page, "Resources"), "XObject");
                using PdfDictionary.EntryEnumerator entries = objects.GetEntryEnumerator(); Assert.True(entries.MoveNext());
                escaped = Assert.IsType<PdfStream>(Get(document, objects, entries.Current.Key.DisplayText));
                Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
            }
            else
            {
                PdfArray annotations = Assert.IsType<PdfArray>(Get(document, page, "Annots"));
                PdfReference annotation = Assert.IsType<PdfReference>(annotations[0]);
                escaped = Assert.IsType<PdfStream>(Get(document, Dict(document, Assert.IsType<PdfDictionary>(document.GetObject(annotation.ObjectNumber)), "AP"), "N"));
            }
            PdfStream original = Assert.IsType<PdfStream>(Get(document, Dict(document, document.Engine.Trailer, "Root"), "OriginalAP"));
            Assert.Equal("/Tx BMC BT /F1 10 Tf (OLD) Tj ET EMC", Encoding.ASCII.GetString(original.RawMemory.Span));
        }
        string program = Encoding.ASCII.GetString(escaped.RawMemory.Span);
        MatchCollection positions = Regex.Matches(program, @"(?m)^([+-]?[0-9.]+) ([+-]?[0-9.]+) Td$");
        bool list = field is "list" or "multiselect";
        Assert.Equal(list ? 3 : 1, positions.Count);
        Assert.Equal(x + 1, double.Parse(positions[0].Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(y + (list ? 28 : 16), double.Parse(positions[0].Groups[2].Value, CultureInfo.InvariantCulture));
        MatchCollection highlights = Regex.Matches(program, @"(?m)^([+-]?[0-9.]+) ([+-]?[0-9.]+) 80 12 re f$");
        double[] expected = field switch { "list" => [y + 14], "multiselect" => [y + 26, y + 2], _ => [] };
        Assert.Equal(expected.Length, highlights.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(x, double.Parse(highlights[index].Groups[1].Value, CultureInfo.InvariantCulture));
            Assert.Equal(expected[index], double.Parse(highlights[index].Groups[2].Value, CultureInfo.InvariantCulture));
        }
    }
}
