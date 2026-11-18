using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class TypedRangeSelectionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    [Fact]
    public async Task MergeResolvesRelativeEndpointsAndAppliesExclusionsBeforePositions()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf")),
            QpdfInputOwnership.Transfer);
        PdfPageSelectionOptions selection = PdfPageSelectionOptions.FromRange("z-r2,2-1,x2:odd");
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host, [new PdfPageSource(Input, selection)],
            Output, CancellationToken.None);
        Assert.Equal(new[] { 2, 1 }, result.Mapping.Select(page => page.SourcePage));
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Fact]
    public async Task EndpointFreeRangeWorksWithAnEmptyPageTree()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [] /Count 0 >>", null)], 1), QpdfInputOwnership.Transfer);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, Input, Output,
            PdfPageSelectionOptions.FromRange(":even"), CancellationToken.None);
        Assert.Equal(0, result.SourcePageCount);
        Assert.Empty(result.SourcePages);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(Output, bytes, QpdfInputOwnership.Transfer);
        QpdfPageCountResult count = await Qpdf.CountPagesAsync(host, Output, CancellationToken.None);
        Assert.Equal(0, count.PageCount);
    }

    [Fact]
    public void InvalidSyntaxIsRejectedByTheFactory()
        => Assert.Throws<QpdfParseException>(() => PdfPageSelectionOptions.FromRange("z--1"));

    [Theory]
    [InlineData("z-3", false, false)]
    [InlineData("z-1", true, false)]
    [InlineData("1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1", false, true)]
    public async Task RangeFailureHappensBeforePublication(string expression, bool canceled, bool limited)
    {
        using RecordingJobHost host = new(null, null, "success", true);
        host.Inner.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf")),
            QpdfInputOwnership.Transfer);
        PdfPageSelectionOptions selection = PdfPageSelectionOptions.FromRange(expression);
        CancellationToken token = new(canceled);
        QpdfLimits limits = limited ? QpdfLimits.Default with { MaxContainerSize = 16 } : QpdfLimits.Default;
        Func<Task> select = () => Qpdf.SelectPageAsync(host, Input, Output, selection, limits, token).AsTask();
        if (canceled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(select);
            Assert.Empty(host.OpenedPaths);
        }
        else if (limited)
        {
            QpdfLimitException exception = await Assert.ThrowsAsync<QpdfLimitException>(select);
            Assert.Equal(nameof(QpdfLimits.MaxContainerSize), exception.LimitName);
        }
        else
        {
            ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(select);
            Assert.Equal("range", exception.ParamName);
        }
        Assert.Empty(host.CreatedPaths);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }
}
