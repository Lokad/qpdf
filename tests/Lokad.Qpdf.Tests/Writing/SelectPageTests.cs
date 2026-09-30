using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SelectPageTests
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

    private static async Task<QpdfDocument> Reopen(InMemoryQpdfHost host, string outputName)
    {
        QpdfPath outputPath = QpdfPath.From(outputName);
        if (!host.TryGetOutput(outputPath, out byte[] published))
        {
            throw new Xunit.Sdk.XunitException($"Output was not published: {outputName}.");
        }

        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, published, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
    }

    [Fact]
    public async Task SelectsSinglePageWithPreservedStreams()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-1page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-1page.pdf"),
            QpdfPath.From("outputs/page.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);
        Assert.Equal(1, result.SourcePageNumber);
        Assert.Equal(1, result.PageCount);
        Assert.True(result.Counters.TotalOutputBytes > 0L);

        await using QpdfDocument output = await Reopen(host, "outputs/page.pdf");
        Assert.Equal(1, output.PageCount);
        await using QpdfDocument source = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/minimal-1page.pdf"), CancellationToken.None);
        PdfStream sourceStream = Assert.IsType<PdfStream>(source.GetObject(4));
        PdfStream outputStream = Assert.IsType<PdfStream>(output.GetObject(4));
        Assert.Equal(sourceStream.Data, outputStream.Data);

        bool found = host.TryGetOutput(QpdfPath.From("outputs/page.pdf"), out byte[] published);
        Assert.True(found);
        using MemoryStream buffer = new(published);
        using PdfDocument reread = PdfDocument.Open(buffer);
        Assert.Equal(1, reread.NumberOfPages);
    }

    [Fact]
    public async Task SelectsSecondPageOfTwo()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-2page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/second.pdf"),
            new PdfPageSelectionOptions(2),
            CancellationToken.None);
        Assert.Equal(2, result.SourcePageNumber);

        await using QpdfDocument output = await Reopen(host, "outputs/second.pdf");
        Assert.Equal(1, output.PageCount);
        bool hasSecond = false;
        foreach (int number in new[] { 3, 4, 5, 6, 7 })
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

    [Fact]
    public async Task MaterializesInheritedAttributes()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("inherited-attrs.pdf");
        await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/inherited-attrs.pdf"),
            QpdfPath.From("outputs/inherited.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);

        await using QpdfDocument output = await Reopen(host, "outputs/inherited.pdf");
        Assert.Equal(1, output.PageCount);
        PdfDictionary? page = null;
        foreach (int number in new[] { 3, 4, 5, 6 })
        {
            try
            {
                if (output.GetObject(number) is PdfDictionary candidate
                    && candidate.TryGet(new PdfName(Encoding.ASCII.GetBytes("Type")), out QpdfObject type)
                    && type is PdfName name && name.DisplayText == "Page")
                {
                    page = candidate;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.NotNull(page);
        Assert.True(page.TryGet(new PdfName(Encoding.ASCII.GetBytes("MediaBox")), out QpdfObject box));
        PdfArray mediaBox = Assert.IsType<PdfArray>(box);
        Assert.Equal(new PdfInteger("612", 612L), mediaBox[2]);
        Assert.True(page.TryGet(new PdfName(Encoding.ASCII.GetBytes("Resources")), out QpdfObject resources));
        Assert.IsType<PdfDictionary>(resources);
    }

    [Fact]
    public async Task PreservesStreamsWithIndirectLengths()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("indirect-length.pdf");
        await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/indirect-length.pdf"),
            QpdfPath.From("outputs/indirect.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);

        await using QpdfDocument output = await Reopen(host, "outputs/indirect.pdf");
        Assert.Equal(1, output.PageCount);
        await using QpdfDocument source = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/indirect-length.pdf"), CancellationToken.None);
        PdfStream sourceStream = Assert.IsType<PdfStream>(source.GetObject(4));
        bool preserved = false;
        foreach (int number in new[] { 3, 4, 5, 6, 7 })
        {
            try
            {
                if (output.GetObject(number) is PdfStream candidate
                    && candidate.Data.AsSpan().SequenceEqual(sourceStream.Data))
                {
                    preserved = true;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.True(preserved);
    }

    [Fact]
    public async Task PreservesStreamsWithUnknownFiltersUndecoded()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("bogus-filter.pdf");
        await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/bogus-filter.pdf"),
            QpdfPath.From("outputs/bogus.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);

        await using QpdfDocument output = await Reopen(host, "outputs/bogus.pdf");
        Assert.Equal(1, output.PageCount);
        await using QpdfDocument source = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/bogus-filter.pdf"), CancellationToken.None);
        PdfStream sourceStream = Assert.IsType<PdfStream>(source.GetObject(4));
        bool preserved = false;
        foreach (int number in new[] { 3, 4, 5, 6 })
        {
            try
            {
                if (output.GetObject(number) is PdfStream candidate
                    && candidate.Data.AsSpan().SequenceEqual(sourceStream.Data))
                {
                    preserved = true;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.True(preserved);
    }

    [Fact]
    public async Task RepeatedSelectionsAreByteIdentical()
    {
        (InMemoryQpdfHost first, byte[] _) = await LoadFixture("minimal-2page.pdf");
        (InMemoryQpdfHost second, byte[] _) = await LoadFixture("minimal-2page.pdf");
        await Qpdf.SelectPageAsync(
            first,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/a.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);
        await Qpdf.SelectPageAsync(
            second,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/a.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);
        Assert.True(first.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        Assert.True(second.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task InvalidSelectionsFailExplicitly()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-2page.pdf");
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageSelectionOptions(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/x.pdf"),
            new PdfPageSelectionOptions(3),
            CancellationToken.None).AsTask());
        bool published = host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _);
        Assert.False(published);
    }

    [Fact]
    public async Task ConflictingOutputLeavesTargetUntouched()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-1page.pdf");
        QpdfPath input = QpdfPath.From("inputs/minimal-1page.pdf");
        await using QpdfDocument guard = await QpdfDocument.OpenAsync(host, input, CancellationToken.None);
        await Assert.ThrowsAsync<QpdfHostException>(() => Qpdf.SelectPageAsync(
            host,
            input,
            input,
            new PdfPageSelectionOptions(1),
            CancellationToken.None).AsTask());
        bool published = host.TryGetOutput(input, out byte[] _);
        Assert.False(published);
    }

    [Fact]
    public async Task OutputBudgetIsEnforcedBeforeStaging()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-1page.pdf"),
            QpdfPath.From("outputs/x.pdf"),
            new PdfPageSelectionOptions(1),
            limits,
            CancellationToken.None).AsTask());
        bool published = host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _);
        Assert.False(published);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationAbortsSelection()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-1page.pdf");
        using CancellationTokenSource source = new();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-1page.pdf"),
            QpdfPath.From("outputs/x.pdf"),
            new PdfPageSelectionOptions(1),
            source.Token).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
