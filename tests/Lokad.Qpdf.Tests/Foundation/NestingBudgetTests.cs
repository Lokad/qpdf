using System.Globalization;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class NestingBudgetTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(500L)]
    [InlineData(600L)]
    [InlineData(100000L)]
    [InlineData(long.MaxValue)]
    public async Task UnsupportedBudgetsRejectBeforeHostAccess(long depth)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = depth };
        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => QpdfDocument.OpenAsync(new ForbiddenHost(), Input, limits, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), error.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new QpdfExecutionOptions(limits));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryQpdfHost(limits));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.RunZlibFlateAsync(new ForbiddenHost(),
            Input, Output, new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, null), limits, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task MaximumBudgetPreservesEscapedNestedValues()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, DeepPdf(498), QpdfInputOwnership.Transfer);
        QpdfObject value;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input,
            QpdfLimits.Default with { MaxNestingDepth = 499L }, CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("Deep"), out value));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        for (int depth = 0; depth < 498; depth++)
        {
            PdfArray array = Assert.IsType<PdfArray>(value);
            Assert.Equal(1, array.Count);
            value = array[0];
        }
        Assert.Equal(0L, Assert.IsType<PdfInteger>(value).Value);
    }

    [Theory]
    [InlineData(0, 1L)]
    [InlineData(499, 499L)]
    [InlineData(20000, 499L)]
    public async Task ExcessiveInputDepthFailsWithoutPublishing(int depth, long maximum)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, DeepPdf(depth), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction previous = await host.CreateOutputAsync(Output, CancellationToken.None))
        {
            await previous.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await previous.CommitAsync(CancellationToken.None);
        }
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = maximum };
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(host, Input, limits, CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation([Input.Value, "--suppress-recovery",
                "--parser-max-nesting=" + maximum.ToString(CultureInfo.InvariantCulture), Output.Value]),
            new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments && item.Message.Contains("Nesting depth", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(Output, out byte[] published));
        Assert.Equal(new byte[] { 17, 23, 41 }, published);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static byte[] DeepPdf(int depth) => CheckTests.Assemble(
    [
        (1, "<< /Type /Catalog /Pages 2 0 R /Deep " + new string('[', depth) + "0" + new string(']', depth) + " >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)
    ], 1);

    private sealed class ForbiddenHost : IQpdfHost
    {
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("Invalid budgets must reject before opening input.");
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("Invalid budgets must reject before creating output.");
    }
}
