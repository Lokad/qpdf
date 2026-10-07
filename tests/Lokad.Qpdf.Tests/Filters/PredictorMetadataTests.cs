using System.Runtime.CompilerServices;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Filters;

[Collection(nameof(NameTableCollection))]
public sealed class PredictorMetadataTests
{
    internal static PdfDictionary UnknownEntries()
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int index = 0; index < 512; index++)
        {
            byte[] raw = new byte[32]; Array.Fill(raw, (byte)255);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(28), index);
            entries.Add(new PdfName(raw), PdfNull.Instance);
        }
        return new PdfDictionary(entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentAndEmptyParametersPreserveDefaultsAndAdmitOnlyTheResult(bool absent)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PredictorParameters? result = PredictorParameters.FromDictionary(absent ? null : new PdfDictionary(new Dictionary<PdfName, QpdfObject>()),
            FlateFilter.FilterName, null, memory, governor, CancellationToken.None);
        if (absent) { Assert.Null(result); Assert.Equal(0L, memory.ReservedBytes); }
        else { Assert.Equal(new PredictorParameters(1, 1, 1, 8), result); Assert.Equal(32L, memory.ReservedBytes); }
    }

    [Fact]
    public void UnknownBinaryKeysRequireNoDisplayStringsKeyViewsOrRepeatedLookup()
    {
        PdfDictionary dictionary = UnknownEntries(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        for (int index = 0; index < 32; index++) { Warm(dictionary, memory, governor); memory.Release(32L); }
        long work = governor.Snapshot().TotalWork; long allocated = GC.GetAllocatedBytesForCurrentThread();
        PredictorParameters? result = PredictorParameters.FromDictionary(dictionary, FlateFilter.FilterName, null, memory, governor, CancellationToken.None);
        long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(new PredictorParameters(1, 1, 1, 8), result); Assert.Equal(32L, delta); Assert.Equal(32L, memory.ReservedBytes);
        Assert.Equal(2562L, governor.Snapshot().TotalWork - work); Assert.Equal(512, dictionary.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Warm(PdfDictionary dictionary, ParserMemoryScope memory, ResourceGovernor governor)
    { GC.KeepAlive(PredictorParameters.FromDictionary(dictionary, FlateFilter.FilterName, null, memory, governor, CancellationToken.None)); }

    [Fact]
    public void KnownFieldsAndIndirectValuesPreserveTheBorrowedGraph()
    {
        PdfInteger columns = new("17", 17L); StubResolver resolver = new(); resolver.Add(2, columns);
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Predictor")] = new PdfInteger("12", 12L), [PdfName.FromAscii("Columns")] = new PdfReference(2, 0),
            [PdfName.FromAscii("Colors")] = new PdfInteger("3", 3L), [PdfName.FromAscii("BitsPerComponent")] = new PdfInteger("16", 16L) });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.Equal(new PredictorParameters(12, 17, 3, 16), PredictorParameters.FromDictionary(dictionary, FlateFilter.FilterName, resolver, memory, governor, CancellationToken.None));
        Assert.True(resolver.TryResolve(new PdfReference(2, 0), out QpdfObject? value)); Assert.Same(columns, value); Assert.Equal(4, dictionary.Count);
    }

    [Fact]
    public void FailedResultAdmissionPreservesPriorStorageAndAllowsRetry()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 128L }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        ParserMemoryScope blocker = ParserMemoryScope.Create(governor); PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject>());
        try
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PredictorParameters.FromDictionary(dictionary, FlateFilter.FilterName, null, memory, governor, CancellationToken.None));
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(128L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, memory.ReservedBytes);
        }
        finally { blocker.Dispose(); }
        Assert.Equal(new PredictorParameters(1, 1, 1, 8), PredictorParameters.FromDictionary(dictionary, FlateFilter.FilterName, null, memory, governor, CancellationToken.None));
        Assert.Equal(96L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void VisitedUnknownEntriesHonorWorkCeilingAndPreservePriorFunding()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = 64L }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L); PdfDictionary dictionary = UnknownEntries();
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PredictorParameters.FromDictionary(dictionary, FlateFilter.FilterName, null, memory, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, memory.ReservedBytes); Assert.Equal(512, dictionary.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EitherPreCanceledRouteRejectsBeforeWorkOrResultAdmission(bool governorToken, bool absent)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); PdfDictionary dictionary = UnknownEntries(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PredictorParameters.FromDictionary(absent ? null : dictionary, FlateFilter.FilterName, null, memory,
            governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(0L, governor.Snapshot().TotalWork); Assert.Equal(0L, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherCancellationRouteStopsAfterCompletedEntryVisits(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L); PdfDictionary dictionary = UnknownEntries();
        using ManualResetEventSlim reached = new(), resume = new();
        Task worker = Task.Factory.StartNew(() => PredictorParameters.FromDictionaryObserved(dictionary, FlateFilter.FilterName, null, memory, governor,
            governorToken ? CancellationToken.None : cancellation.Token,
            () => { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Predictor observation did not resume."); } }),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10))); Assert.True(governor.Snapshot().TotalWork >= 1280L);
            cancellation.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(97L, memory.ReservedBytes); Assert.Equal(512, dictionary.Count);
        }
        finally { cancellation.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    private sealed class CancelingResolver(Action cancel) : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { value = new PdfInteger("1", 1L); cancel(); return true; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterResolverWorkRejectsBeforePublishingTheResult(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Columns")] = new PdfReference(1, 0) });
        Assert.ThrowsAny<OperationCanceledException>(() => PredictorParameters.FromDictionary(dictionary, FlateFilter.FilterName, new CancelingResolver(cancellation.Cancel),
            memory, governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.True(governor.Snapshot().TotalWork > 0L); Assert.Equal(0L, memory.ReservedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PredictorParameters Result, WeakReference Governor, WeakReference Observer) Complete()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        object state = new(); Action observer = () => GC.KeepAlive(state);
        PredictorParameters result = PredictorParameters.FromDictionaryObserved(UnknownEntries(), FlateFilter.FilterName, null, memory, governor, CancellationToken.None, observer)
            ?? throw new InvalidOperationException("Predictor result missing.");
        return (result, new WeakReference(governor), new WeakReference(observer));
    }

    [Fact]
    public void ImmutableResultRetainsNoControllerOrObservationState()
    {
        var state = Complete(); for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(state.Governor.IsAlive); Assert.False(state.Observer.IsAlive); Assert.Equal(new PredictorParameters(1, 1, 1, 8), state.Result); GC.KeepAlive(state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedFieldFailureOrderFollowsBorrowedEntryOrder(bool reverse)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        string first = reverse ? "Columns" : "Colors", second = reverse ? "Colors" : "Columns";
        entries.Add(PdfName.FromAscii(first), PdfNull.Instance); entries.Add(PdfName.FromAscii(second), PdfNull.Instance);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        QpdfFilterException failure = Assert.Throws<QpdfFilterException>(() => PredictorParameters.FromDictionary(new PdfDictionary(entries), FlateFilter.FilterName, null, memory, governor, CancellationToken.None));
        Assert.Contains("/" + first + " must be an integer", failure.Message, StringComparison.Ordinal); Assert.Equal(0L, memory.ReservedBytes);
    }

    [Fact]
    public void ForeignAdmissionScopeRejectsBeforeReadingTheBorrowedDictionary()
    {
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None), reader = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner);
        Assert.Throws<InvalidOperationException>(() => PredictorParameters.FromDictionary(UnknownEntries(), FlateFilter.FilterName, null, memory, reader, CancellationToken.None));
        Assert.Equal(0L, reader.Snapshot().TotalWork); Assert.Equal(0L, memory.ReservedBytes);
    }
}
