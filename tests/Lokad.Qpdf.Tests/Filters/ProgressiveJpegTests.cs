using Lokad.Qpdf.ImageSharp;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class ProgressiveJpegTests
{
    private static async Task<InMemoryQpdfHost> FixtureHost(string fixture)
    {
        byte[] pdf = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        InMemoryQpdfHost host = new(QpdfLimits.Default, ImageSharpJpegCodec.Instance);
        host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        return host;
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
    {
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }

    private static PdfStream Image(QpdfDocument document)
    {
        PdfDictionary page = Assert.Single(document.PageReferences).Page;
        Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resourcesRaw));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(document, resourcesRaw));
        Assert.True(resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjectsRaw));
        PdfDictionary xobjects = Assert.IsType<PdfDictionary>(Resolve(document, xobjectsRaw));
        Assert.True(xobjects.TryGet(PdfName.FromAscii("Im0"), out QpdfObject imageRaw));
        return Assert.IsType<PdfStream>(Resolve(document, imageRaw));
    }

    [Theory]
    [InlineData("images-progressive-gray-1page.pdf", 13, 9, 1)]
    [InlineData("images-progressive-rgb-1page.pdf", 17, 11, 3)]
    [InlineData("images-progressive-gray-large-1page.pdf", 1024, 1024, 1)]
    [InlineData("images-progressive-rgb-large-1page.pdf", 512, 512, 3)]
    public async Task ProgressiveSamplesAndOwnersMatchAuthoredFrame(string fixture, int width, int height, int components)
    {
        InMemoryQpdfHost host = await FixtureHost(fixture);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        byte[] jpeg = Image(document).Data;
        Assert.True(jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC2 }) >= 0);
        Assert.True(DctFilter.TryParseFrame(jpeg, out int actualWidth, out int actualHeight, out int actualComponents));
        Assert.Equal(width, actualWidth);
        Assert.Equal(height, actualHeight);
        Assert.Equal(components, actualComponents);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { JpegCodec = ImageSharpJpegCodec.Instance };
        byte[] samples = DctFilter.Decode(jpeg, QpdfLimits.Default, governor);
        Assert.Equal(width * height * components, samples.Length);
        Assert.Equal(-1, samples.AsSpan().IndexOfAnyExcept((byte)128));
        Assert.Equal(samples.Length + JpegCodec.SampleArrayOverheadBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(samples.Length, governor.Snapshot().TotalDecodedBytes);
        if (samples.Length >= 512 * 1024)
        {
            Assert.True(governor.Snapshot().PeakMemoryBytes >= 3L * samples.Length);
        }

        GC.KeepAlive(samples);
    }

    [Theory]
    [InlineData("images-progressive-gray-large-1page.pdf", 1024 * 1024)]
    [InlineData("images-progressive-rgb-large-1page.pdf", 512 * 512 * 3)]
    public async Task ProgressiveCoefficientBudgetFailureDisposesOwners(string fixture, int samples)
    {
        InMemoryQpdfHost host = await FixtureHost(fixture);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        byte[] jpeg = Image(document).Data;
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 3L * samples + 256L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None) { JpegCodec = ImageSharpJpegCodec.Instance };
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => DctFilter.Decode(jpeg, limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
    }

    [Theory]
    [InlineData("images-progressive-gray-1page.pdf", 13 * 9)]
    [InlineData("images-progressive-rgb-1page.pdf", 17 * 11 * 3)]
    [InlineData("images-progressive-gray-large-1page.pdf", 1024 * 1024)]
    [InlineData("images-progressive-rgb-large-1page.pdf", 512 * 512 * 3)]
    public async Task PublicRewritePreservesExactProgressiveSamples(string fixture, int samples)
    {
        InMemoryQpdfHost host = await FixtureHost(fixture);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--decode-level=all",                 "--stream-data=uncompress", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        PdfStream image = Image(document);
        Assert.False(image.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
        Assert.Equal(samples, image.Data.Length);
        Assert.Equal(-1, image.Data.AsSpan().IndexOfAnyExcept((byte)128));
    }
}
