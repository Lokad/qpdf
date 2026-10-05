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
}
