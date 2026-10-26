using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(NameTableCollection))]
public sealed class OrphanRemovalPlanTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerCancellationPrecedesGraphAdmission(bool empty)
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using OrphanFixture fixture = new(governor, PdfNull.Instance);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(4096L - governor.Snapshot().LiveMemoryBytes - 1L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<OperationCanceledException>(() => fixture.Copier.DropPoolObjectsIfUnreferenced(empty ? [] : [2], cancellation.Token));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateGetterCancellationReleasesPrefixScratch(bool caller)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using OrphanFixture fixture = new(governor, PdfNull.Instance);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        var before = fixture.Pool.ToArray(); var mapping = fixture.Mapping.ToArray();
        Assert.Throws<OperationCanceledException>(() => fixture.Copier.DropPoolObjectsIfUnreferenced(new CancellingCandidates(cancellation), caller ? cancellation.Token : CancellationToken.None));
        OrphanFixture.AssertUnchanged(fixture.Pool, fixture.Mapping, before, mapping); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CancellationDuringMutationRestoresAliasesMappingAndEnumerationOrder(bool caller, bool mappingStage)
    {
        using CancellationTokenSource cancellation = new(); CancellingComparer comparer = new(cancellation);
        Dictionary<int, QpdfObject> pool = mappingStage ? new() : new(comparer);
        Dictionary<int, int> mapping = mappingStage ? new(comparer) : new();
        PdfStream borrowed = OrphanFixture.Stream(); pool.Add(1, borrowed); pool.Add(2, borrowed); pool.Add(90, PdfNull.Instance);
        mapping.Add(11, 1); mapping.Add(12, 1); mapping.Add(21, 2); mapping.Add(91, 90);
        var before = pool.ToArray(); var sources = mapping.ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using (OrphanRemovalPlan plan = OrphanRemovalPlan.Create([1, 2], pool, mapping, governor, caller ? cancellation.Token : CancellationToken.None))
        {
            comparer.Armed = true; Assert.Throws<OperationCanceledException>(plan.Apply);
            OrphanFixture.AssertUnchanged(pool, mapping, before, sources);
            Assert.Equal(2 * 1024 * 1024, borrowed.RawMemory.Length);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void TraversalBackingRetiresBeforePlanApplication()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Dictionary<int, QpdfObject> pool = new() { [1] = PdfNull.Instance, [90] = new PdfArray(Enumerable.Repeat<QpdfObject>(PdfNull.Instance, 1024).ToArray()) };
        Dictionary<int, int> mapping = new() { [11] = 1 };
        using (OrphanRemovalPlan plan = OrphanRemovalPlan.Create([1], pool, mapping, governor, CancellationToken.None))
        {
            Assert.True(governor.Snapshot().PeakMemoryBytes > governor.Snapshot().LiveMemoryBytes + 4096L);
            plan.Apply(); Assert.False(pool.ContainsKey(1)); Assert.Empty(mapping);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RootedDisposedPlanDoesNotKeepContextScratchOrBorrowedContainers()
    {
        var prepared = PrepareDisposed();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        foreach (WeakReference witness in prepared.Witnesses) { Assert.False(witness.IsAlive); }
        Assert.Throws<ObjectDisposedException>(prepared.Plan.Apply); prepared.Plan.Dispose();
        Assert.Equal(2 * 1024 * 1024, prepared.Borrowed.RawMemory.Length); GC.KeepAlive(prepared.Plan);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (OrphanRemovalPlan Plan, PdfStream Borrowed, WeakReference[] Witnesses) PrepareDisposed()
    {
        CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        PdfStream borrowed = OrphanFixture.Stream(); Dictionary<int, QpdfObject> pool = new() { [1] = borrowed };
        Dictionary<int, int> mapping = new() { [11] = 1 };
        OrphanRemovalPlan plan = OrphanRemovalPlan.Create([1], pool, mapping, governor, cancellation.Token);
        List<WeakReference> witnesses = [new(governor), new(cancellation), new(pool), new(mapping)];
        foreach (string field in new[] { "_memory", "_wanted", "_drops", "_sources" })
        {
            object value = typeof(OrphanRemovalPlan).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plan) ?? throw new InvalidOperationException();
            witnesses.Add(new(value));
            if (value is IList)
            { object backing = value.GetType().GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value) ?? throw new InvalidOperationException(); witnesses.Add(new(backing)); }
        }
        plan.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); cancellation.Dispose();
        return (plan, borrowed, witnesses.ToArray());
    }

    private sealed class CancellingComparer(CancellationTokenSource cancellation) : IEqualityComparer<int>
    {
        internal bool Armed;
        public bool Equals(int left, int right) => left == right;
        public int GetHashCode(int value) { if (Armed) { Armed = false; cancellation.Cancel(); } return value; }
    }
    private sealed class CancellingCandidates(CancellationTokenSource cancellation) : IReadOnlyList<int>
    {
        public int Count => 2;
        public int this[int index] { get { if (index == 1) { cancellation.Cancel(); } return index + 1; } }
        public IEnumerator<int> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
