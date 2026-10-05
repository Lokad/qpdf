using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class CanonicalDisplayTests
{
    private sealed class NullResolver(Action onResolve) : IIndirectResolver
    {
        internal int Calls { get; private set; }

        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            Calls++;
            onResolve();
            value = PdfNull.Instance;
            return true;
        }
    }

    private static PdfDictionary References() => new(new Dictionary<PdfName, QpdfObject>
    {
        [PdfName.FromAscii("A")] = new PdfReference(10, 0),
        [PdfName.FromAscii("B")] = new PdfReference(11, 0),
    });

    [Fact]
    public void NullReferenceFilteringHonorsWorkBudget()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        NullResolver resolver = new(() => { });
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            CanonicalDisplay.Render(References(), limits, governor, resolver));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void NullReferenceFilteringObservesCancellationAfterLookupStarts()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        NullResolver resolver = new(cancellation.Cancel);
        Assert.Throws<OperationCanceledException>(() =>
            CanonicalDisplay.Render(References(), QpdfLimits.Default, governor, resolver));
        Assert.Equal(1, resolver.Calls);
        Assert.True(governor.Snapshot().TotalWork > 0L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BinaryStringClassificationHonorsWorkBudget(bool hex)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            CanonicalDisplay.Render(new PdfByteString(new byte[4096], hex), limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
    }

    [Fact]
    public void HexFormattingHonorsWorkBudgetAfterClassification()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 257L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            CanonicalDisplay.Render(new PdfByteString(new byte[4096], false), limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
        Assert.Equal(257L, governor.Snapshot().TotalWork);
    }

    [Fact]
    public void NameFormattingHonorsWorkBudget()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            CanonicalDisplay.Render(new PdfName(new byte[4096]), limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
    }

    [Fact]
    public async Task LongStringDisplayObservesCancellationAfterWorkStarts()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        byte[] raw = new byte[16 * 1024 * 1024];
        Array.Fill(raw, (byte)'A');
        PdfByteString value = new(raw, false);
        Task<string> display = Task.Factory.StartNew(() =>
            CanonicalDisplay.Render(value, QpdfLimits.Default, governor),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool observed = SpinWait.SpinUntil(() => governor.Snapshot().TotalWork >= 4096L || display.IsCompleted, TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.True(observed);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await display.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(governor.Snapshot().TotalWork >= 4096L);
    }
}
