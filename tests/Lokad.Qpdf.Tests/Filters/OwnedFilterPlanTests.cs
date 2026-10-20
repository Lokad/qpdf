using System.Runtime.CompilerServices;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

// Exposed name batches use caller ownership without retaining their plan.
public sealed class OwnedFilterPlanTests
{
    private sealed class Alias(FilterPlan plan, string value, WeakReference governor, WeakReference observer)
    {
        internal readonly FilterPlan Plan = plan;
        internal string? Value = value;
        internal readonly WeakReference Governor = governor;
        internal readonly WeakReference Observer = observer;
        internal readonly WeakReference Name = new(value);
    }

    private static PdfDictionary Dictionary() => new(new Dictionary<PdfName, QpdfObject>
    { [PdfName.FromAscii("Filter")] = new PdfName(new byte[256]) });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Capture(ResourceGovernor governor)
    {
        object state = new(); Action observe = () => GC.KeepAlive(state);
        FilterPlan plan = FilterChain.ResolveObserved(Dictionary(), null, governor, CancellationToken.None, observe);
        string value = plan[0].Name; plan.Dispose();
        return new(plan, value, new(governor), new(observe));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Complete() => Capture(new(QpdfLimits.Default, CancellationToken.None));

    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    [Fact]
    public void EscapingBinaryNameStaysFundedUntilItsLastAliasDrops()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); Alias alias = Capture(governor);
        Collect(); VerifyAndClear(alias, governor);
        Collect(); Assert.False(alias.Name.IsAlive); Assert.Equal(governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(alias);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyAndClear(Alias alias, ResourceGovernor governor)
    {
        Assert.Equal(768, alias.Value?.Length); Assert.StartsWith("#00#00", alias.Value, StringComparison.Ordinal);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= GovernedText.StringBytes(768)); alias.Value = null;
    }

    [Fact]
    public void EscapingNameAndRootedDisposedPlanRetainNoCompletedJobOrObserver()
    {
        Alias alias = Complete(); Collect();
        Assert.False(alias.Governor.IsAlive); Assert.False(alias.Observer.IsAlive); Assert.Equal(768, alias.Value?.Length); GC.KeepAlive(alias);
    }
}
