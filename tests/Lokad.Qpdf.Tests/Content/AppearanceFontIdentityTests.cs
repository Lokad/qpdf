using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceFontIdentityTests
{
    private static byte[] Fixture(string local, string fallback, bool indirect)
    {
        string localEncoding = local is "default" or "missing" ? "" : "/Encoding " + (indirect ? "9 0 R" : "/" + local + "Encoding");
        string localFont = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " + localEncoding + " >>";
        string fallbackFont = "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding " + (indirect ? "10 0 R" : "/" + fallback + "Encoding") + " >>";
        string resources = "<< /Font << " + (local == "missing" ? "" : "/F1 " + (indirect ? "6 0 R" : localFont)) + " >> >>";
        string defaults = "<< /Font << /F1 " + (indirect ? "7 0 R" : fallbackFont) + " >> >>";
        byte[] original = "/Tx BMC BT /F1 12 Tf (OLD) Tj ET EMC\n"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true " + (fallback == "absent" ? "" : "/DR " + (indirect ? "11 0 R" : defaults)) + " >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (F) /V <e9> /Rect [10 20 150 60] /DA (/F1 12 Tf 0 g) /AP << /N 5 0 R >> >>", null),
            (5, "<< /Subtype /Form /BBox [0 0 140 40] /Resources " + (indirect ? "8 0 R" : resources) + " /Length " + original.Length + " >>", original),
            (6, localFont, null), (7, fallbackFont, null), (8, resources, null),
            (9, "/" + local + "Encoding", null), (10, "/" + fallback + "Encoding", null), (11, defaults, null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (string local in new[] { "default", "Standard", "WinAnsi", "MacRoman", "missing" })
        foreach (string fallback in new[] { "absent", "WinAnsi", "MacRoman" })
        {
            if (local == "missing" && fallback == "absent") { continue; }
            yield return [job, local, fallback, false, false];
        }
        yield return [false, "default", "WinAnsi", true, true];
        yield return [true, "default", "MacRoman", true, true];
        yield return [false, "MacRoman", "WinAnsi", true, true];
        yield return [true, "missing", "WinAnsi", true, true];
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TextEncodingBelongsToTheSelectedFont(bool job, string local, string fallback, bool indirect, bool flatten)
    {
        byte[] output;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(local, fallback, indirect), QpdfInputOwnership.Copy);
            List<string> argv = ["input", "--generate-appearances", "--compress-streams=n"];
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true, ["compressStreams"] = "n" };
            if (indirect) { argv.AddRange(["--pages", ".", "1", "--"]); settings["pages"] = new[] { new { file = ".", range = "1" } }; }
            if (flatten) { argv.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; }
            argv.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out output));
        }
        PdfStream program;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
            {
                PdfDictionary page = document.PageReferences[0].Page;
                if (flatten)
                {
                    PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
                    program = Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(Get(document, resources, "XObject")), "Fxo1"));
                }
                else
                {
                    PdfArray annots = Assert.IsType<PdfArray>(Get(document, page, "Annots"));
                    PdfDictionary widget = Assert.IsType<PdfDictionary>(annots[0] is PdfReference reference ? document.GetObject(reference.ObjectNumber) : annots[0]);
                    program = Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(Get(document, widget, "AP")), "N"));
                }
                PdfDictionary resourcesFont = Assert.IsType<PdfDictionary>(Get(document, Assert.IsType<PdfDictionary>(Get(document, program.Dictionary, "Resources")), "Font"));
                PdfDictionary font = Assert.IsType<PdfDictionary>(Get(document, resourcesFont, "F1"));
                Assert.Equal(local == "missing" ? "Courier" : "Helvetica", Assert.IsType<PdfName>(Get(document, font, "BaseFont")).DisplayText);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        string selected = local == "missing" ? fallback : local;
        char expected = selected switch { "WinAnsi" => (char)0xe9, "MacRoman" => (char)0x8e, _ => '?' };
        string text = Encoding.Latin1.GetString(program.RawMemory.Span);
        Assert.Contains("(" + expected + ") Tj", text); Assert.DoesNotContain("(OLD)", text);
    }
}
