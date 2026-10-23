using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceEncodingReferenceTests
{
    private static byte[] Fixture(string encoding, bool indirect, bool local, string target)
    {
        string chosen = indirect ? "9 0 R" : "/" + encoding;
        string formEncoding = local ? "/WinAnsiEncoding" : chosen;
        string resources = local ? "/Font << /Helv 8 0 R >>" : "";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true /DA (/Helv 12 Tf) /DR << /Font << /Helv 7 0 R >> >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Annots [5 0 R] >>", null),
            (4, "null", null),
            (5, "<< /Subtype /Widget /FT /Tx /V <FEFF00E9> /T (Text) /Rect [0 0 100 20] /AP << /N 6 0 R >> >>", null),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << " + resources + " >> /Length 12 >>", "/Tx BMC\nEMC\n"u8.ToArray()),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding " + formEncoding + " >>", null),
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding " + chosen + " >>", null),
            (9, target, null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (bool selection in new[] { false, true })
        foreach (bool local in new[] { false, true })
        foreach ((string encoding, int expected) in new[] { ("WinAnsiEncoding", 233), ("MacRomanEncoding", 142) })
        { yield return [job, selection, local, encoding, expected]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task IndirectEncodingNamesKeepGlyphsAndLocalFontPrecedence(bool job, bool selection, bool local, string encoding, int expected)
    {
        byte[]? baseline = null;
        foreach (bool indirect in new[] { false, true })
        {
            byte[] input = Fixture(encoding, indirect, local, "/" + encoding);
            var output = await AppearanceProgramTests.Generate(input, job, selection);
            Assert.Empty(output.Result.Diagnostics);
            PdfStream stream = await AppearanceProgramTests.ReadAppearance(output.Pdf);
            byte[] program = stream.Data;
            Assert.True(program.AsSpan().IndexOf(new byte[] { 40, (byte)expected, 41, 32, 84, 106 }) >= 0);
            baseline ??= program; Assert.Equal(baseline, program);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("/Unknown")]
    [InlineData("<< >>")]
    [InlineData("9 0 R")]
    public async Task UnsupportedOrCyclicEncodingKeepsBoundedAsciiFallback(string target)
    {
        var output = await AppearanceProgramTests.Generate(Fixture("WinAnsiEncoding", true, false, target), false, false);
        PdfStream stream = await AppearanceProgramTests.ReadAppearance(output.Pdf);
        Assert.True(stream.Data.AsSpan().IndexOf("(?) Tj"u8) >= 0);
    }
}
