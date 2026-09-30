using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ChainTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<(InMemoryQpdfHost Host, byte[] Bytes)> LoadFixture(string file)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), file));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/" + file), bytes, QpdfInputOwnership.Copy);
        return (host, bytes);
    }

    private static async Task<QpdfDocument> ReopenOutput(InMemoryQpdfHost host, string output)
    {
        if (!host.TryGetOutput(QpdfPath.From(output), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException($"Output was not published: {output}.");
        }

        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, published, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
    }

    [Fact]
    public async Task IncrementalReplacementWins()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("incremental-1page.pdf");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/incremental-1page.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        PdfStream contents = Assert.IsType<PdfStream>(document.GetObject(4));
        Assert.Equal(
            "BT /F1 24 Tf 100 700 Td (Second) Tj ET",
            Encoding.ASCII.GetString(contents.Data));
    }

    [Fact]
    public async Task IncrementalDeleteReadsNull()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("incremental-delete.pdf");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/incremental-delete.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        Assert.Equal(PdfNull.Instance, document.GetObject(5));
    }

    [Fact]
    public async Task HybridMergesStreamEntries()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("hybrid-xstm.pdf");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/hybrid-xstm.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        PdfStream contents = Assert.IsType<PdfStream>(document.GetObject(4));
        Assert.Equal(
            "BT /F1 24 Tf 100 700 Td (Third) Tj ET",
            Encoding.ASCII.GetString(contents.Data));
    }

    [Fact]
    public async Task StreamLedChainResolves()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("prev-stream.pdf");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/prev-stream.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        PdfStream contents = Assert.IsType<PdfStream>(document.GetObject(4));
        Assert.Equal(
            "BT /F1 24 Tf 100 700 Td (Third) Tj ET",
            Encoding.ASCII.GetString(contents.Data));
        PdfDictionary font = Assert.IsType<PdfDictionary>(document.GetObject(5));
        Assert.True(font.TryGet(new PdfName(Encoding.ASCII.GetBytes("BaseFont")), out QpdfObject _));
    }

    [Fact]
    public async Task CyclicChainTerminates()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("cyclic-prev.pdf");
        QpdfParseException ex = await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/cyclic-prev.pdf"), CancellationToken.None).AsTask());
        Assert.Contains("cycles", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RevisionCapIsEnforced()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("incremental-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxRevisions = 1L };
        QpdfLimitException ex = await Assert.ThrowsAsync<QpdfLimitException>(() => QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/incremental-1page.pdf"), limits, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxRevisions), ex.LimitName);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SelectsFromIncrementalUpdate()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("incremental-1page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/incremental-1page.pdf"),
            QpdfPath.From("outputs/inc.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        await using QpdfDocument output = await ReopenOutput(host, "outputs/inc.pdf");
        Assert.Equal(1, output.PageCount);
        bool hasSecond = false;
        foreach (int number in new[] { 3, 4, 5, 6 })
        {
            try
            {
                if (output.GetObject(number) is PdfStream stream
                    && Encoding.ASCII.GetString(stream.Data).Contains("Second", StringComparison.Ordinal))
                {
                    hasSecond = true;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.True(hasSecond);
    }
}
