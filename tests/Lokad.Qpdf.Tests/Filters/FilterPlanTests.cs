using System.Collections;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FilterPlanTests
{
    private static PdfDictionary Dictionary(QpdfObject filter, QpdfObject parms)
        => new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Filter")] = filter, [PdfName.FromAscii("DecodeParms")] = parms });

    [Theory]
    [InlineData("Fl", "FlateDecode")]
    [InlineData("LZW", "LZWDecode")]
    [InlineData("A85", "ASCII85Decode")]
    [InlineData("AHx", "ASCIIHexDecode")]
    [InlineData("RL", "RunLengthDecode")]
    [InlineData("CCF", "CCITTFaxDecode")]
    [InlineData("DCT", "DCTDecode")]
    [InlineData("FlateDecode", "FlateDecode")]
    [InlineData("LZWDecode", "LZWDecode")]
    [InlineData("ASCII85Decode", "ASCII85Decode")]
    [InlineData("ASCIIHexDecode", "ASCIIHexDecode")]
    [InlineData("RunLengthDecode", "RunLengthDecode")]
    [InlineData("CCITTFaxDecode", "CCITTFaxDecode")]
    [InlineData("DCTDecode", "DCTDecode")]
    [InlineData("JPXDecode", "JPXDecode")]
    [InlineData("JBIG2Decode", "JBIG2Decode")]
    public void KnownNamesBorrowCanonicalStringsAndRetireExclusiveStorage(string spelling, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using FilterPlan plan = FilterChain.Resolve(Dictionary(PdfName.FromAscii(spelling), PdfNull.Instance), null, governor, CancellationToken.None);
        Assert.Equal(expected, Assert.Single(plan).Name);
        using FilterPlan twin = FilterChain.Resolve(Dictionary(PdfName.FromAscii(spelling), PdfNull.Instance), null, governor, CancellationToken.None);
        Assert.Same(plan[0].Name, twin[0].Name); twin.Dispose();
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 3L * IntPtr.Size);
        plan.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => plan[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AbsentNullAndEmptyFiltersPreservePriorAdmission(int kind)
    {
        PdfDictionary dictionary = kind == 0 ? new(new Dictionary<PdfName, QpdfObject>())
            : Dictionary(kind == 1 ? PdfNull.Instance : new PdfArray([]), PdfNull.Instance);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long before = governor.Snapshot().LiveMemoryBytes;
        using FilterPlan plan = FilterChain.Resolve(dictionary, null, governor, CancellationToken.None);
        Assert.Empty(plan); Assert.True(governor.Snapshot().LiveMemoryBytes > before);
        plan.Dispose(); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Fact]
    public void CapacityFailureRollsBackAndSmallRetryUsesTheSameGovernor()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 512L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long before = governor.Snapshot().LiveMemoryBytes;
        PdfArray many = new(Enumerable.Repeat<QpdfObject>(PdfName.FromAscii("Fl"), 1024).ToArray());
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => FilterChain.Resolve(Dictionary(many, PdfNull.Instance), null, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        using FilterPlan retry = FilterChain.Resolve(Dictionary(PdfName.FromAscii("Fl"), PdfNull.Instance), null, governor, CancellationToken.None);
        Assert.Equal("FlateDecode", Assert.Single(retry).Name); retry.Dispose(); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilterValidationPrecedesParametersAndRetiresUnpublishedNames(bool binary)
    {
        PdfName name = binary ? new([0, 255, 35]) : PdfName.FromAscii("UnknownDecode");
        PdfDictionary dictionary = Dictionary(new PdfArray([name, PdfNull.Instance]), new PdfInteger("7", 7L));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfParseException failure = Assert.Throws<QpdfParseException>(() => FilterChain.Resolve(dictionary, null, governor, CancellationToken.None));
        Assert.Contains("/Filter array entries must be names", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(2, dictionary.Count);
    }

    [Fact]
    public void ParametersBorrowTheOriginalDictionaryForEveryStage()
    {
        PdfDictionary parms = new(new Dictionary<PdfName, QpdfObject>());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using FilterPlan plan = FilterChain.Resolve(Dictionary(new PdfArray([PdfName.FromAscii("Fl"), PdfName.FromAscii("LZW")]), parms), null, governor, CancellationToken.None);
        Assert.Same(parms, plan[0].Parms); Assert.Same(parms, plan[1].Parms);
        plan.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Empty(parms.Keys);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("entry")]
    [InlineData("without-filter")]
    public void MalformedParameterShapesRetireAllExclusiveStageStorage(string kind)
    {
        QpdfObject filter = kind == "without-filter" ? new PdfArray([]) : PdfName.FromAscii("Fl");
        QpdfObject parms = kind == "count" ? new PdfArray([]) : kind == "entry" ? new PdfArray([new PdfInteger("7", 7L)]) : new PdfInteger("7", 7L);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfParseException failure = Assert.Throws<QpdfParseException>(() => FilterChain.Resolve(Dictionary(filter, parms), null, governor, CancellationToken.None));
        Assert.Contains("/DecodeParms", failure.Message, StringComparison.Ordinal); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class Resolver(Dictionary<int, QpdfObject> values, Action? observe) : IIndirectResolver
    {
        internal readonly List<int> Visits = new();
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { Visits.Add(reference.ObjectNumber); bool result = values.TryGetValue(reference.ObjectNumber, out value); observe?.Invoke(); return result; }
    }

    [Fact]
    public void IndirectResolutionPreservesPhaseOrderNullsAndBorrowing()
    {
        PdfDictionary parms = new(new Dictionary<PdfName, QpdfObject>());
        Resolver resolver = new(new()
        {
            [1] = new PdfArray([new PdfReference(2, 0), new PdfReference(3, 0)]),
            [2] = PdfName.FromAscii("Fl"), [3] = PdfName.FromAscii("RL"),
            [4] = new PdfArray([new PdfReference(5, 0), new PdfReference(6, 0)]), [5] = parms, [6] = PdfNull.Instance,
        }, null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using FilterPlan plan = FilterChain.Resolve(Dictionary(new PdfReference(1, 0), new PdfReference(4, 0)), resolver, governor, CancellationToken.None);
        Assert.Equal([1, 2, 3, 4, 5, 6], resolver.Visits); Assert.Same(parms, plan[0].Parms); Assert.Null(plan[1].Parms);
        Assert.Same(resolver, plan[0].Resolver); Assert.Equal("RunLengthDecode", plan[1].Name);
        plan.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherPreCanceledRouteRejectsBeforeAdmission(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => FilterChain.Resolve(Dictionary(PdfName.FromAscii("Fl"), PdfNull.Instance), null,
            governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task CancellationAfterNameWorkRollsBackCountingOrStringCopy(bool governorToken, int phase)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        PdfDictionary dictionary = Dictionary(new PdfName(new byte[256]), PdfNull.Instance);
        using ManualResetEventSlim reached = new(), resume = new(); int observations = 0;
        Task worker = Task.Factory.StartNew(() =>
        {
            using FilterPlan plan = FilterChain.ResolveObserved(dictionary, null, governor,
                governorToken ? CancellationToken.None : cancellation.Token, () =>
                {
                    if (++observations != phase) { return; }
                    reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Filter name observation did not resume."); }
                });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10))); Assert.True(governor.Snapshot().TotalWork >= 256L);
            cancellation.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(2, dictionary.Count);
        }
        finally { cancellation.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAtResolverBoundaryPreventsPublication(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        Resolver resolver = new(new() { [1] = PdfName.FromAscii("Fl") }, cancellation.Cancel);
        Assert.ThrowsAny<OperationCanceledException>(() => FilterChain.Resolve(Dictionary(new PdfReference(1, 0), PdfNull.Instance), resolver,
            governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal([1], resolver.Visits); Assert.True(governor.Snapshot().TotalWork > 0L); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void InspectionRetainsItsSemanticsAboveTheDecoderFilterCount()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxStreamFilters = 1 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using FilterPlan plan = FilterChain.Resolve(Dictionary(new PdfArray([PdfName.FromAscii("Fl"), PdfName.FromAscii("RL")]), PdfNull.Instance), null, governor, CancellationToken.None);
        Assert.True(FilterChain.AllStagesInLevel(plan, "specialized")); Assert.False(FilterChain.AllStagesInLevel(plan, "generalized"));
        QpdfFilterException failure = Assert.Throws<QpdfFilterException>(() => FilterChain.Decode([], plan, limits, governor));
        Assert.Equal(QpdfFilterFailureReason.TooManyFilters, failure.Reason); plan.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void InterfaceEnumeratorKeepsOnlyItsOwnAdmissionAfterPlanDisposal()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        FilterPlan plan = FilterChain.Resolve(Dictionary(PdfName.FromAscii("Fl"), PdfNull.Instance), null, governor, CancellationToken.None);
        IEnumerator<FilterStage> iterator = ((IEnumerable<FilterStage>)plan).GetEnumerator();
        Assert.True(iterator.MoveNext()); Assert.Equal("FlateDecode", iterator.Current.Name);
        plan.Dispose(); Assert.True(governor.Snapshot().LiveMemoryBytes > 0L);
        Assert.Throws<ObjectDisposedException>(() => iterator.MoveNext()); iterator.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        iterator.Dispose(); Assert.Throws<ObjectDisposedException>(() => iterator.Current);
    }

    [Fact]
    public void NonGenericStageBoxStaysFundedAfterEnumerationAndPlanRetire()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        object stage = Box(governor);
        for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); governor.ReserveMemory(0L); }
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 40L); Assert.Equal("FlateDecode", Assert.IsType<FilterStage>(stage).Name); GC.KeepAlive(stage);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Box(ResourceGovernor governor)
    {
        using FilterPlan plan = FilterChain.Resolve(Dictionary(PdfName.FromAscii("Fl"), PdfNull.Instance), null, governor, CancellationToken.None);
        IEnumerator iterator = ((IEnumerable)plan).GetEnumerator();
        try { Assert.True(iterator.MoveNext()); return iterator.Current; }
        finally { ((IDisposable)iterator).Dispose(); }
    }

    [Fact]
    public void DecoderRejectsAForeignStageOwnerWithoutRetiringIt()
    {
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None), foreign = new(QpdfLimits.Default, CancellationToken.None);
        using FilterPlan plan = FilterChain.Resolve(Dictionary(PdfName.FromAscii("Fl"), PdfNull.Instance), null, owner, CancellationToken.None);
        long before = owner.Snapshot().LiveMemoryBytes;
        Assert.Throws<InvalidOperationException>(() => FilterChain.Decode([], plan, QpdfLimits.Default, foreign));
        Assert.Equal(before, owner.Snapshot().LiveMemoryBytes); Assert.Equal(0L, foreign.Snapshot().LiveMemoryBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (FilterPlan Plan, WeakReference Governor, WeakReference Observer) Complete()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); object state = new(); Action observe = () => GC.KeepAlive(state);
        PdfArray names = new(Enumerable.Repeat<QpdfObject>(PdfName.FromAscii("Fl"), 256).ToArray());
        FilterPlan plan = FilterChain.ResolveObserved(Dictionary(names, PdfNull.Instance), null, governor, CancellationToken.None, observe);
        plan.Dispose(); return (plan, new(governor), new(observe));
    }

    [Fact]
    public void RootedDisposedPlanRetainsNoControllerOrObserver()
    {
        var state = Complete(); for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(state.Governor.IsAlive); Assert.False(state.Observer.IsAlive); GC.KeepAlive(state);
    }
}
