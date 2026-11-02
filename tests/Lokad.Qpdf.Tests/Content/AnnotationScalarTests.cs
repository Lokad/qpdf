using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationScalarTests
{
    private static byte[] Fixture(string subtype, string need, string state, bool stateDictionary)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances " + need
                + " /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype " + subtype + " /FT /Tx /T (field) /V (LIVE) /Rect [10 10 150 30] /F 4 /AS "
                + state + (stateDictionary ? " /AP << /N << /On 8 0 R >> >>" : "") + " >>", null),
            (5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (6, "/Widget", null), (7, "true", null),
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 140 20] /Length 16 >>", "0 0 140 20 re f\n"u8.ToArray()),
            (9, "/On", null), (10, "null", null), (11, "false", null), (12, "42", null)], 1);

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject raw)
        => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;

    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    {
        Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject raw));
        return Resolve(document, raw);
    }

    public static IEnumerable<object[]> InspectionCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach ((string subtype, string need, bool widget, bool flag) in new[]
        {
            ("/Widget", "true", true, true), ("6 0 R", "true", true, true),
            ("/Widget", "7 0 R", true, true), ("6 0 R", "7 0 R", true, true),
            ("6 0 R", "11 0 R", true, false), ("6 0 R", "10 0 R", true, false),
            ("10 0 R", "7 0 R", false, true), ("12 0 R", "12 0 R", false, false)
        }) { yield return [route, subtype, need, widget, flag]; }
    }

    [Theory]
    [MemberData(nameof(InspectionCases))]
    public async Task InspectionResolvesWidgetAndNeedAppearances(string route, string subtype, string need, bool widget, bool flag)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(subtype, need, "/On", true), QpdfInputOwnership.Copy);
        if (route == "typed")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None);
            Assert.Empty(result.Warnings);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=acroform", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
                { inputFile = "input", outputFile = "output", json = "2", jsonKey = new[] { "acroform" } }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement form = json.RootElement.GetProperty("acroform");
        Assert.True(form.GetProperty("hasacroform").GetBoolean());
        Assert.Equal(flag, form.GetProperty("needappearances").GetBoolean());
        Assert.Equal(widget ? 1 : 0, form.GetProperty("fields").GetArrayLength());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GenerationResolvesWidgetSubtype(bool job, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(indirect ? "6 0 R" : "/Widget", "7 0 R", "/On", false), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"generateAppearances\":true,\"streamData\":\"uncompress\"}", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--generate-appearances", "--stream-data=uncompress", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfArray annots = Assert.IsType<PdfArray>(Get(document, document.PageReferences[0].Page, "Annots"));
        PdfDictionary annot = Assert.IsType<PdfDictionary>(Resolve(document, annots[0]));
        PdfDictionary ap = Assert.IsType<PdfDictionary>(Get(document, annot, "AP"));
        PdfStream normal = Assert.IsType<PdfStream>(Get(document, ap, "N"));
        Assert.Contains("(LIVE)", Encoding.ASCII.GetString(normal.Data));
    }

    public static IEnumerable<object[]> FlattenCases()
    {
        foreach (bool job in new[] { false, true })
        foreach ((string subtype, string need, string state, bool keep, bool stamp) in new[]
        {
            ("/Widget", "true", "/On", true, false), ("6 0 R", "7 0 R", "/On", true, false),
            ("/Stamp", "false", "/On", false, true), ("/Stamp", "false", "9 0 R", false, true),
            ("/Stamp", "false", "10 0 R", false, false), ("/Stamp", "false", "12 0 R", false, false)
        }) { yield return [job, subtype, need, state, keep, stamp]; }
    }

    [Theory]
    [MemberData(nameof(FlattenCases))]
    public async Task FlatteningResolvesAppearanceStateAndRetainsWidgetsNeedingAppearance(
        bool job, string subtype, string need, string state, bool keep, bool stamp)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(subtype, need, state, true), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"flattenAnnotations\":\"all\",\"streamData\":\"uncompress\"}", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-annotations=all", "--stream-data=uncompress", "output"]), CancellationToken.None);
        Assert.Equal(keep ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        int count = page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw)
            ? Assert.IsType<PdfArray>(Resolve(document, raw)).Count : 0;
        Assert.Equal(keep ? 1 : 0, count);
        bool stamped = page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resources)
            && Resolve(document, resources) is PdfDictionary dictionary
            && dictionary.TryGet(PdfName.FromAscii("XObject"), out QpdfObject forms)
            && Resolve(document, forms) is PdfDictionary xobjects && xobjects.Count != 0;
        Assert.Equal(stamp, stamped);
        if (keep) { Assert.NotEmpty(result.Diagnostics); }
        else { Assert.Empty(result.Diagnostics); }
    }
}
