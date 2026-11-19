using System.Text;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Content;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class ReportRotationTests
{
    [Theory]
    [InlineData(false, "792 612", "0 -1 1 0 0 612 cm")]
    [InlineData(true, "612 792", "-1 0 0 -1 612 792 cm")]
    public async Task ReportsSeeTheBakedBoxAndWrappedProgram(bool compose, string bounds, string matrix)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rotated-ap-1page.pdf")), QpdfInputOwnership.Transfer);
        string[] flags = compose ? ["--rotate=+90:1", "--flatten-rotation"] : ["--flatten-rotation"];
        QpdfExecutionResult listing = await Qpdf.ExecuteAsync(host,
            new(["input", .. flags, "--check", "--show-object=3"]), CancellationToken.None);
        Assert.True(listing.ExitCode == QpdfExitCode.Success, string.Join("\n", listing.Diagnostics.Select(item => item.Message)));
        string page = Assert.Single(listing.Diagnostics, item => item.Code == QpdfDiagnosticCode.ObjectListing).Message;
        Assert.Contains("/MediaBox [ 0 0 " + bounds + " ]", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/Rotate", page, StringComparison.Ordinal);
        Match content = Regex.Match(page, @"/Contents\s+\[\s+(\d+)\s+0\s+R", RegexOptions.CultureInvariant);
        Assert.True(content.Success, page);

        QpdfExecutionResult stream = await Qpdf.ExecuteAsync(host,
            new(["input", .. flags, "--show-object=" + content.Groups[1].Value, "--filtered-stream-data", "program"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, stream.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("program"), out byte[] bytes));
        Assert.Contains(matrix, Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        if (!compose)
        {
            QpdfExecutionResult original = await Qpdf.ExecuteAsync(host, new(["input", "--show-xref"]), CancellationToken.None);
            QpdfExecutionResult baked = await Qpdf.ExecuteAsync(host, new(["input", .. flags, "--show-xref"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, baked.ExitCode);
            Assert.Equal(original.Diagnostics, baked.Diagnostics);
            Assert.Null(baked.OutputBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BakingAnInheritedRotationClearsItFromTheRecreatedTree()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] /Rotate 90 /MediaBox [0 0 200 100] /Resources << >> >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>", null),
            (4, "<< /Length 4 >>", "q Q\n"u8.ToArray())], 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host,
            new(["input", "--flatten-rotation", "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
        await using (QpdfDocument document = await RecreateAsync(host, report))
        {
            PdfDictionary page = Assert.Single(document.PageReferences).Page;
            PdfDictionary parent = RotationDestinationTests.Dict(document, page, "Parent");
            Assert.False(page.TryGet(PdfName.FromAscii("Rotate"), out _));
            Assert.False(parent.TryGet(PdfName.FromAscii("Rotate"), out _));
            PdfArray box = Assert.IsType<PdfArray>(RotationDestinationTests.Value(document, page, "MediaBox"));
            Assert.Equal(new[] { 0d, 0d, 100d, 200d }, box.Items.Select(item => item is PdfReal real ? real.Value : Assert.IsType<PdfInteger>(item).Value));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RecreatedReportKeepsRotatedNavigationAndTheOriginalPrivateDestination()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), RotationDestinationTests.Source("XYZ", 90, true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","jsonOutput":"","jsonStreamData":"inline","flattenRotation":""}""", CancellationToken.None);
        await using QpdfDocument document = await RecreateAsync(host, report);
        PdfDictionary catalog = RotationDestinationTests.Dict(document, document.Engine.Trailer, "Root");
        PdfDictionary page = Assert.Single(document.PageReferences).Page;
        Assert.False(page.TryGet(PdfName.FromAscii("Rotate"), out _));
        PdfArray annotations = Assert.IsType<PdfArray>(RotationDestinationTests.Value(document, page, "Annots"));
        PdfDictionary link = Assert.IsType<PdfDictionary>(RotationDestinationTests.Resolve(document, annotations[0]));
        foreach (QpdfObject dest in new[] { RotationDestinationTests.Value(document, catalog, "OpenAction"), RotationDestinationTests.Value(document, link, "Dest") })
        { RotationDestinationTests.AssertDestination(document, dest, "XYZ", RotationDestinationTests.Expected("XYZ", 90)); }
        RotationDestinationTests.AssertDestination(document, RotationDestinationTests.Value(document, catalog, "Private"), "XYZ", RotationDestinationTests.Expected("XYZ", 0));
        RotationDestinationTests.AssertDestination(document, RotationDestinationTests.Value(document, link, "Private"), "XYZ", RotationDestinationTests.Expected("XYZ", 0));
    }

    [Fact]
    public async Task UnsupportedRetainedDestinationKeepsThePriorReport()
    {
        QpdfPath target = QpdfPath.From("report");
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(RotationDestinationTests.Source("FitH", 90, false), target);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--flatten-rotation", "--json-output", target.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.UnsupportedFeature);
        Assert.True(host.TryGetOutput(target, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async ValueTask<QpdfDocument> RecreateAsync(InMemoryQpdfHost host, QpdfExecutionResult report)
    {
        Assert.True(report.ExitCode == QpdfExitCode.Success, string.Join("\n", report.Diagnostics.Select(item => item.Message)));
        QpdfPath json = QpdfPath.From("json"), pdf = QpdfPath.From("pdf");
        host.AddInput(json, Encoding.UTF8.GetBytes(report.OutputText ?? throw new InvalidOperationException()), QpdfInputOwnership.Transfer);
        QpdfJsonImportResult created = await Qpdf.CreatePdfFromJsonAsync(host, json, pdf, CancellationToken.None);
        Assert.Equal(1, created.PageCount);
        Assert.True(host.TryGetOutput(pdf, out byte[] bytes));
        host.AddInput(pdf, bytes, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, pdf, CancellationToken.None);
    }
}
