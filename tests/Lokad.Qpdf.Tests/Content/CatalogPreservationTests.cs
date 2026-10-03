using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Content;

public sealed class CatalogPreservationTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<InMemoryQpdfHost> HostWithFixture(string logical, string fixture)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(logical), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<QpdfDocument> RereadAsync(InMemoryQpdfHost host, string output, string alias)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] pdf));
        host.AddInput(QpdfPath.From(alias), pdf, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, QpdfPath.From(alias), CancellationToken.None);
    }

    private static PdfDictionary RequireCatalog(QpdfDocument output)
    {
        return Assert.IsType<PdfDictionary>(output.GetObject(1));
    }

    [Fact]
    public async Task TypedSelectionDropsOutlinesForContainedPreview()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Equal([1], result.SourcePages);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        Assert.Equal(1, output.PageCount);
        PdfDictionary catalog = RequireCatalog(output);
        Assert.False(catalog.TryGet(PdfName.FromAscii("Outlines"), out QpdfObject _));
    }

    [Fact]
    public async Task ArgvSelectionPreservesOutlinesForOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        Assert.Equal(1, output.PageCount);
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("Outlines"), out QpdfObject? raw));
        PdfReference link = Assert.IsType<PdfReference>(raw);
        PdfDictionary outlines = Assert.IsType<PdfDictionary>(output.GetObject(link.ObjectNumber));
        Assert.True(outlines.TryGet(PdfName.FromAscii("First"), out QpdfObject _));
    }

    [Fact]
    public async Task JobSelectionPreservesOutlinesForOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        string job = "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("Outlines"), out QpdfObject _));
    }

    [Fact]
    public async Task TypedSelectionDropsStructureForContainedPreview()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-preserve-1page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.False(catalog.TryGet(PdfName.FromAscii("StructTreeRoot"), out QpdfObject _));
        Assert.False(catalog.TryGet(PdfName.FromAscii("MarkInfo"), out QpdfObject _));
    }

    [Fact]
    public async Task ArgvSelectionPreservesStructureForOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-preserve-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("StructTreeRoot"), out QpdfObject? treeRaw));
        Assert.IsType<PdfReference>(treeRaw);
        Assert.True(catalog.TryGet(PdfName.FromAscii("MarkInfo"), out QpdfObject? markRaw));
        Assert.IsType<PdfDictionary>(markRaw);
    }

    [Fact]
    public async Task JobSelectionPreservesStructureForOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-preserve-1page.pdf");
        string job = "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("StructTreeRoot"), out QpdfObject _));
        Assert.True(catalog.TryGet(PdfName.FromAscii("MarkInfo"), out QpdfObject _));
    }

    [Fact]
    public async Task DamagedStructureDropsWithoutFailingOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        Assert.Equal(1, output.PageCount);
    }
}
