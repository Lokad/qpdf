using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class AppearanceTransformInspectionTests
{
    private static readonly byte[] PageContent = "q Q\n"u8.ToArray();
    private static readonly byte[] Appearance = "0 0 5 5 re f\n"u8.ToArray();

    [Fact]
    public async Task CheckFlattensStoredAppearanceAndRetainsSourcePairs()
    {
        using InMemoryQpdfHost host = Open(Fixture(false));
        QpdfExecutionResult physical = await Qpdf.ExecuteAsync(host, new(["input", "--show-xref"]), CancellationToken.None);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--flatten-annotations=all", "--check", "--show-object=3,2", "--show-pages", "--show-xref"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Null(result.OutputBytes);
        Assert.Equal(physical.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.XrefListing),
            result.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.XrefListing));
        string page = Listing(result);
        Assert.DoesNotContain("/Annots", page, StringComparison.Ordinal);
        Assert.Contains("/ProcSet [ /PDF ]", page, StringComparison.Ordinal);
        string appearance = Selector(page, @"/Fxo1 (\d+) (\d+) R");
        Assert.Equal("8,5", appearance);
        string[] contents = result.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("    ", StringComparison.Ordinal))
            .Select(d => Selector(d.Message, @"^    (\d+) (\d+) R$")).ToArray();
        Assert.Equal(3, contents.Length);
        Assert.Equal("4,3", contents[1]);
        Assert.Equal("q\n"u8.ToArray(), await Stream(host, contents[0]));
        Assert.Equal(PageContent, await Stream(host, contents[1]));
        QpdfPath placementPath = QpdfPath.From("placement");
        QpdfExecutionResult published = await Qpdf.ExecuteAsync(host,
            new(["input", "--flatten-annotations=all", "--show-object=" + contents[2], "--filtered-stream-data", placementPath.Value]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, published.ExitCode);
        Assert.True(host.TryGetOutput(placementPath, out byte[] placement));
        Assert.Contains("1 0 0 1 10 20 cm\n/Fxo1 Do\n", Encoding.ASCII.GetString(placement), StringComparison.Ordinal);
        Assert.Null(published.OutputBytes);
        Assert.Equal(placement.Length, published.Counters.TotalOutputBytes);
        Assert.Equal(Appearance, await Stream(host, appearance));
        string catalog = Listing(await Qpdf.ExecuteAsync(host,
            new(["input", "--flatten-annotations=all", "--remove-metadata", "--show-object=1"]), CancellationToken.None));
        Assert.DoesNotContain("/AcroForm", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("/Metadata", catalog, StringComparison.Ordinal);
        Assert.Contains("/AuditForm 5 2 R", catalog, StringComparison.Ordinal);
        Assert.Contains("/Fields [ 6 7 R ]", Listing(await Qpdf.ExecuteAsync(host,
            new(["input", "--flatten-annotations=all", "--show-object=5,2"]), CancellationToken.None)), StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task JobJsonKeepsPrivateAnnotationAndFormWhileTheExportRecreatesTheStamp()
    {
        using InMemoryQpdfHost host = Open(Fixture(false));
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"input","jsonOutput":"2","flattenAnnotations":"all"}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string text = Assert.IsType<string>(result.OutputText);
        using (JsonDocument json = JsonDocument.Parse(text))
        {
            JsonElement table = json.RootElement.GetProperty("qpdf")[1];
            JsonElement catalog = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root").GetString());
            Assert.False(catalog.TryGetProperty("/AcroForm", out _));
            Assert.Equal("5 2 R", catalog.GetProperty("/AuditForm").GetString());
            Assert.Equal("6 7 R", Value(table, "5 2 R").GetProperty("/Fields")[0].GetString());
            Assert.Equal("6 7 R", Value(table, "7 4 R")[0].GetString());
            Assert.Equal("8 5 R", Value(table, "6 7 R").GetProperty("/AP").GetProperty("/N").GetString());
            JsonElement page = Value(table, Value(table, catalog.GetProperty("/Pages").GetString()).GetProperty("/Kids")[0].GetString());
            Assert.False(page.TryGetProperty("/Annots", out _));
            Assert.Equal("8 5 R", page.GetProperty("/Resources").GetProperty("/XObject").GetProperty("/Fxo1").GetString());
        }
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(text), QpdfInputOwnership.Transfer);
        await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("recreated"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("recreated"), out byte[] bytes));
        host.AddInput(QpdfPath.From("reopen"), bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reopen"), CancellationToken.None))
        {
            PdfDictionary page = Assert.Single(document.PageReferences).Page;
            Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
            Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
            PdfArray contents = Assert.IsType<PdfArray>(raw);
            Assert.Equal(PageContent, Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(contents[1]))).RawMemory.ToArray());
            PdfStream placement = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(contents[2])));
            Assert.Contains("/Fxo1 Do", Encoding.ASCII.GetString(placement.RawMemory.Span), StringComparison.Ordinal);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task FlatteningKeepsWidgetsWhenTheFormNeedsAppearanceGeneration()
    {
        using InMemoryQpdfHost host = Open(Fixture(true));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--flatten-annotations=all", "--show-object=3,2"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature && d.Message.Contains("updated appearance streams", StringComparison.Ordinal));
        Assert.Contains("/Annots 7 4 R /Contents 4 3 R", Listing(result), StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static JsonElement Value(JsonElement table, string? pair) => table.GetProperty("obj:" + pair).GetProperty("value");

    private static string Listing(QpdfExecutionResult result)
        => Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message;

    private static string Selector(string listing, string pattern)
    {
        Match match = Regex.Match(listing, pattern);
        Assert.True(match.Success, listing);
        return match.Groups[1].Value + "," + match.Groups[2].Value;
    }

    private static async Task<byte[]> Stream(InMemoryQpdfHost host, string selector)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--flatten-annotations=all", "--show-object=" + selector, "--filtered-stream-data"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        return Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes).ToArray();
    }

    private static InMemoryQpdfHost Open(byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        return host;
    }

    private static byte[] Fixture(bool needAppearances) => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 2 R /AuditForm 5 2 R /AuditAnnots 7 4 R /Metadata 10 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 2 R] /MediaBox [0 0 100 100] /Resources 9 2 R >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /Contents 4 3 R /Annots 7 4 R >>", null),
        (4, "<< /Length " + PageContent.Length + " >>", PageContent),
        (5, "<< /Fields [6 7 R]" + (needAppearances ? " /NeedAppearances true" : "") + " >>", null),
        (6, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /P 3 2 R /Rect [10 20 30 40] /AP << /N 8 5 R >> >>", null),
        (7, "[6 7 R]", null),
        (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length " + Appearance.Length + " >>", Appearance),
        (9, "<< /ProcSet [/PDF] >>", null),
        (10, "<< /Type /Metadata /Subtype /XML /Length 4 >>", "<x/>"u8.ToArray()),
    ], 1, new Dictionary<int, int> { [3] = 2, [4] = 3, [5] = 2, [6] = 7, [7] = 4, [8] = 5, [9] = 2 });
}
