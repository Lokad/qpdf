using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FieldInheritanceTests
{
    private static byte[] Fixture(int depth, bool existingAppearance)
    {
        int widget = 4 + depth, font = widget + 1, appearance = font + 1;
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true"
                + " /Q 1 /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv " + font + " 0 R >> >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [" + widget + " 0 R] >>", null)
        ];
        for (int i = 0; i <= depth; i++)
        {
            int number = 4 + i;
            string entry = i == 0 ? "/FT /Tx /T (field) /V (LIVE) /Q 2 /DA (/Helv 20 Tf 0 g) "
                : "/Parent " + (number - 1) + " 0 R ";
            entry += i < depth ? "/Kids [" + (number + 1) + " 0 R] "
                : "/Type /Annot /Subtype /Widget /P 3 0 R /Rect [10 10 150 30] /F 4 "
                    + (existingAppearance ? "/AP << /N " + appearance + " 0 R >> " : "");
            objects.Add((number, "<< " + entry + ">>", null));
        }
        objects.Add((font, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null));
        objects.Add((appearance, "<< /Type /XObject /Subtype /Form /BBox [0 0 140 20] /Length 4 >>", "q Q\n"u8.ToArray()));
        return CheckTests.Assemble(objects, 1);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    {
        Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return Resolve(document, value);
    }

    public static IEnumerable<object[]> GenerationCases()
    {
        foreach (bool job in new[] { false, true })
        foreach (int depth in new[] { 63, 64, 65, 120 }) { yield return [job, depth]; }
    }

    [Theory]
    [MemberData(nameof(GenerationCases))]
    public async Task GenerationUsesInheritedValuesBeyondLegacyCutoff(bool job, int depth)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(depth, false), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"generateAppearances\":true,\"streamData\":\"uncompress\"}", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--generate-appearances", "--stream-data=uncompress", "output"]), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfArray annots = Assert.IsType<PdfArray>(Get(document, document.PageReferences[0].Page, "Annots"));
        PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, annots[0]));
        PdfDictionary ap = Assert.IsType<PdfDictionary>(Get(document, widget, "AP"));
        PdfStream normal = Assert.IsType<PdfStream>(Get(document, ap, "N"));
        string program = Encoding.ASCII.GetString(normal.Data);
        Assert.Contains("(LIVE)", program); Assert.Contains("20 Tf", program);
    }

    public static IEnumerable<object[]> OverlayCases()
    {
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (int depth in new[] { 31, 32, 33, 70 }) { yield return [job, mode, depth]; }
    }

    [Theory]
    [MemberData(nameof(OverlayCases))]
    public async Task OverlayPreservesInheritedDefaultsBeyondLegacyCutoff(bool job, string mode, int depth)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), Fixture(depth, true), QpdfInputOwnership.Copy);
        Dictionary<string, object> settings = new()
        { ["inputFile"] = "input", ["outputFile"] = "output", [mode] = new { file = "stamp" } };
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--" + mode, "stamp", "--", "output"]), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfArray annots = Assert.IsType<PdfArray>(Get(document, document.PageReferences[0].Page, "Annots"));
        PdfDictionary node = Assert.IsType<PdfDictionary>(Resolve(document, annots[0]));
        PdfByteString? appearance = null; long? quadding = null;
        for (int i = 0; i <= depth; i++)
        {
            if (appearance is null && node.TryGet(PdfName.FromAscii("DA"), out QpdfObject da))
            { appearance = Resolve(document, da) as PdfByteString; }
            if (quadding is null && node.TryGet(PdfName.FromAscii("Q"), out QpdfObject q)
                && Resolve(document, q) is PdfInteger integer) { quadding = integer.Value; }
            if (!node.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parent)) { break; }
            node = Assert.IsType<PdfDictionary>(Resolve(document, parent));
        }
        Assert.NotNull(appearance); Assert.Contains("20 Tf", Encoding.ASCII.GetString(appearance.ValueSpan));
        Assert.Equal(2L, quadding);
    }
}
