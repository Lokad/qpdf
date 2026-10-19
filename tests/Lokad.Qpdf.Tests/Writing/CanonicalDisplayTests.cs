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
    public void NullReferenceFilteringObservesCancellationAfterLookupStarts()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        NullResolver resolver = new(cancellation.Cancel);
        Assert.Throws<OperationCanceledException>(() =>
            CanonicalDisplay.Render(References(), QpdfLimits.Default, governor, resolver));
        Assert.Equal(1, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongStringDisplayObservesCancellationAfterBufferWorkStarts(bool binary)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        byte[] raw = new byte[16 * 1024 * 1024];
        if (!binary) { Array.Fill(raw, (byte)'A'); }
        PdfByteString value = new(raw, false);
        Task<GovernedText> display = Task.Factory.StartNew(() =>
            CanonicalDisplay.Render(value, QpdfLimits.Default, governor),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool observed = SpinWait.SpinUntil(() => governor.Snapshot().PeakMemoryBytes >= 8192L || display.IsCompleted, TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.True(observed);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await display.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(governor.Snapshot().PeakMemoryBytes >= 8192L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
