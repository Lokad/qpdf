using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class TypedCounterOwnershipTests
{
    private readonly ITestOutputHelper _output;
    private OwnershipBatch.Observation? _collection;

    public TypedCounterOwnershipTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");
    private static readonly string[] Routes = ["typed-page", "typed-page-rotation", "typed-pages", "typed-pages-rotation",
        "typed-pages-options", "typed-check", "typed-json", "typed-import", "typed-update", "typed-extract",
        "typed-add", "typed-remove", "typed-copy"];

    private readonly record struct Values(long Live, long Peak, long Work, long Decoded, long OutputBytes,
        long Calls, long Handles, long Diagnostics);

    private static Values State(QpdfExecutionCounters c) => new(c.LiveMemoryBytes, c.PeakMemoryBytes, c.TotalWork,
        c.TotalDecodedBytes, c.TotalOutputBytes, c.HostOperations, c.OpenHandles, c.DiagnosticCount);

    private sealed class Alias(ResourceGovernor governor, object? value, WeakReference host, WeakReference result,
        WeakReference counters, WeakReference prepared, Values? snapshot)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
        internal readonly WeakReference Host = host;
        internal readonly WeakReference Result = result;
        internal readonly WeakReference Counters = counters;
        internal readonly WeakReference Prepared = prepared;
        internal readonly Values? Snapshot = snapshot;
    }

    public static IEnumerable<object[]> PublicJobs()
    {
        foreach (object[] row in ExecutionCounterTests.Jobs())
        {
            if (!((string)row[0]).StartsWith("typed-", StringComparison.Ordinal)) { continue; }
            yield return [.. row];
        }
    }

    public static IEnumerable<object[]> SuccessAliases()
    {
        foreach (string route in Routes)
        foreach (bool compressed in new[] { false, true })
        foreach (string kind in new[] { "result", "counters", "copy" }) { yield return [route, compressed, kind]; }
    }

    public static IEnumerable<object[]> BudgetJobs()
    {
        foreach (string route in Routes)
        foreach (bool compressed in new[] { false, true })
        foreach (string budget in new[] { "memory", "work" }) { yield return [route, compressed, budget]; }
    }

    public static IEnumerable<object[]> MetadataJobs()
    {
        foreach (string route in Routes)
        foreach (bool compressed in new[] { false, true }) { yield return [route, compressed]; }
    }

    public static IEnumerable<object[]> TinyJobs()
    {
        foreach (string route in Routes)
        foreach (string budget in new[] { "memory", "work" })
        foreach (bool canceled in new[] { false, true }) { yield return [route, budget, canceled]; }
    }

    private sealed class CountingHost(IQpdfHost inner) : IQpdfHost
    {
        internal int Calls;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Calls++; return inner.OpenInputAsync(path, token); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Calls++; return inner.CreateOutputAsync(path, token); }
    }

    [Theory]
    [MemberData(nameof(TinyJobs))]
    [Trait("Layer", "Contract")]
    public async Task TinyAndPrecanceledTypedJobsRejectBeforeHostAccess(string route, string budget, bool canceled)
    {
        object fixture = await Prepare(route, false, "none", "success");
        CountingHost host = new((IQpdfHost)fixture);
        using CancellationTokenSource cancellation = new();
        if (canceled) { cancellation.Cancel(); }
        QpdfLimits limits = budget == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 143L }
            : QpdfLimits.Default with { MaxWork = 1L };
        if (canceled) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Start(host, route, limits, cancellation.Token)); }
        else { await Assert.ThrowsAsync<QpdfLimitException>(() => Start(host, route, limits, cancellation.Token)); }
        Assert.Equal(0, host.Calls);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(fixture, "Inner");
        Assert.Equal(0L, inner.OpenLeaseCount);
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
    }

    private static object Read(object value, string property) => value.GetType().GetProperty(property, Members)?.GetValue(value)
        ?? throw new InvalidOperationException("Missing complete-job fixture property: " + property);

    private static async Task<object> Prepare(string route, bool compressed, string boundary, string outcome)
    {
        Task task = (Task)(typeof(ExecutionCounterTests).GetMethod("Prepare", Members)?.Invoke(null, [route, compressed, boundary, outcome])
            ?? throw new InvalidOperationException("Missing original complete-job fixture."));
        await task;
        return Read(task, "Result");
    }

    private static ResourceGovernor FindGovernor(Task task) => (ResourceGovernor)(typeof(ExecutionCounterTests)
        .GetMethod("FindGovernor", Members)?.Invoke(null, [task, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
        ?? throw new InvalidOperationException("Missing actual public governor."));

    private static QpdfAttachmentSpec Attachment() => new("attachment.bin", "payload", "payload.bin", null, null, null, null, false);

    private static async Task<object> Start(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        PdfPageSource[] sources = [new(Input, new PdfPageSelectionOptions(1)), new(QpdfPath.From("second.pdf"), new PdfPageSelectionOptions(1))];
        if (route == "typed-page") { return await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), limits, token); }
        if (route == "typed-page-rotation") { return await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), [new PdfPageRotation(PdfRotationMode.Set, 90, [1])], limits, token); }
        if (route == "typed-pages") { return await Qpdf.SelectPagesAsync(host, sources, Output, limits, token); }
        if (route == "typed-pages-rotation") { return await Qpdf.SelectPagesAsync(host, sources, Output, [new PdfPageRotation(PdfRotationMode.Set, 90, [1])], limits, token); }
        if (route == "typed-pages-options") { return await Qpdf.SelectPagesAsync(host, sources, Output, new PdfPageMergeOptions(), limits, token); }
        if (route == "typed-check") { return await Qpdf.CheckAsync(host, Input, new QpdfCheckOptions(), limits, token); }
        if (route == "typed-json") { return await Qpdf.GetDocumentJsonAsync(host, Input, Output, new QpdfJsonOptions(2, []), limits, token); }
        if (route == "typed-import") { return await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("input.json"), Output, limits, token); }
        if (route == "typed-update") { return await Qpdf.UpdatePdfFromJsonAsync(host, Input, QpdfPath.From("input.json"), Output, limits, token); }
        if (route == "typed-extract") { return await Qpdf.ExtractAttachmentAsync(host, Input, "payload", Output, limits, token); }
        if (route == "typed-add") { return await Qpdf.AddAttachmentsAsync(host, Input, Output, [Attachment()], limits, token); }
        if (route == "typed-remove") { return await Qpdf.RemoveAttachmentsAsync(host, Input, Output, ["payload"], limits, token); }
        return await Qpdf.CopyAttachmentsAsync(host, Input, Output, [new QpdfAttachmentCopy(QpdfPath.From("second.pdf"), "copy-")], limits, token);
    }

    private static void Resume(object host) => host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);

    private static async Task<Alias> Complete(string route, bool compressed, string boundary, string outcome, string kind, QpdfLimits limits)
    {
        using CancellationTokenSource cancellation = new();
        object host = await Prepare(route, compressed, boundary, outcome);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(host, "Inner");
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        Assert.True(GC.TryStartNoGCRegion(16L * 1024L * 1024L));
        Task<object> running = Start((IQpdfHost)host, route, limits, cancellation.Token);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = FindGovernor(running);
            object? prepared = typeof(ResourceGovernor).GetField("_preparedTypedCounters", Members)?.GetValue(governor);
            WeakReference admitted = new(prepared);
            prepared = null;
            if (outcome == "cancel") { cancellation.Cancel(); } else { Resume(host); }
            object? result = null;
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
            else if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
            else if (outcome == "budget") { await Assert.ThrowsAsync<QpdfLimitException>(() => running); }
            else { result = await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            Assert.True((long)(host.GetType().GetField("ReadBytes", Members)?.GetValue(host) ?? 0L) > 0L);
            Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
            Assert.True(governor.Snapshot().TotalWork <= limits.MaxWork);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(inner.TryGetOutput(Output, out byte[] current));
            if ((int)(host.GetType().GetField("Commits", Members)?.GetValue(host) ?? 0) == 0) { Assert.Equal(prior, current); }
            QpdfExecutionCounters? counters = result is null ? null : (QpdfExecutionCounters)Read(result, "Counters");
            if (counters is not null)
            {
                Assert.Same(counters, admitted.Target);
                PropertyInfo property = result?.GetType().GetProperty("Counters") ?? throw new InvalidOperationException();
                Assert.False(property.CanWrite);
                Assert.Equal(governor.Snapshot(), counters);
            }
            object? value = kind == "result" ? result : kind == "copy" && counters is not null ? counters with { } : counters;
            if (kind == "copy" && counters is not null)
            {
                Assert.Equal(counters, value);
                Assert.Equal(counters.GetHashCode(), value?.GetHashCode());
            }
            return new(governor, value, new(host), new(result), new(counters), admitted, counters is null ? null : State(counters));
        }
        finally
        {
            Resume(host);
            try
            {
                try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { }
            }
            finally { GC.EndNoGCRegion(); }
        }
    }

    private static void Collect(ResourceGovernor governor)
    {
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        for (int boundary = 0; boundary < 4; boundary++) { governor.ReserveMemory(0L); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckSnapshot(Alias alias)
    {
        object value = alias.Value ?? throw new InvalidOperationException();
        QpdfExecutionCounters counters = value is QpdfExecutionCounters c ? c : (QpdfExecutionCounters)Read(value, "Counters");
        Assert.Equal(alias.Snapshot, State(counters));
    }

    [Theory]
    [MemberData(nameof(PublicJobs))]
    [Trait("Layer", "Contract")]
    public async Task CompletePublicJobsRetireUnreturnedCountersAndFundReturnedAliases(string route, bool compressed, string boundary, string outcome)
    {
        Alias alias = await Complete(route, compressed, boundary, outcome, "counters", QpdfLimits.Default);
        if (_collection is null) { return; } // Contract lane: producer assertions only.
        await Task.Yield();
        await _collection.CollectAsync(alias.Governor, 4);
        Assert.False(alias.Host.IsAlive); Assert.False(alias.Result.IsAlive);
        if (outcome == "success")
        {
            Assert.True(alias.Counters.IsAlive);
            Assert.True(alias.Governor.Snapshot().LiveMemoryBytes >= 88L);
            CheckSnapshot(alias);
        }
        else { Assert.False(alias.Prepared.IsAlive); }
        alias.Value = null;
        await _collection.CollectAsync(alias.Governor, 4);
        Assert.False(alias.Counters.IsAlive); Assert.False(alias.Prepared.IsAlive);
        GC.KeepAlive(alias);
    }

    [Theory]
    [MemberData(nameof(SuccessAliases))]
    [Trait("Layer", "Contract")]
    public async Task EachTypedFamilyKeepsIndependentImmutableCountersAndCallerCopies(string route, bool compressed, string kind)
    {
        Alias alias = await Complete(route, compressed, "read", "success", kind, QpdfLimits.Default);
        if (_collection is null) { return; } // Contract lane: producer assertions only.
        await Task.Yield();
        await _collection.CollectAsync(alias.Governor, 4);
        Assert.False(alias.Host.IsAlive);
        Assert.Equal(kind == "result", alias.Result.IsAlive);
        Assert.Equal(kind != "copy", alias.Counters.IsAlive);
        CheckSnapshot(alias);
        if (kind != "copy") { Assert.True(alias.Governor.Snapshot().LiveMemoryBytes >= 88L); }
        alias.Value = null;
        await _collection.CollectAsync(alias.Governor, 4);
        Assert.False(alias.Prepared.IsAlive); Assert.False(alias.Counters.IsAlive); Assert.False(alias.Result.IsAlive);
        GC.KeepAlive(alias);
    }

    [Theory]
    [MemberData(nameof(BudgetJobs))]
    [Trait("Layer", "Contract")]
    public async Task RealPublicBudgetFailuresRetirePreparedCountersWithoutReplacingPriorOutputs(string route, bool compressed, string budget)
    {
        QpdfLimits limits = budget == "work" ? QpdfLimits.Default with { MaxWork = 128L }
            : QpdfLimits.Default with { MaxMemoryBytes = 2048L };
        Alias alias = await Complete(route, compressed, "read", "budget", "counters", limits);
        if (_collection is null) { return; } // Contract lane: producer assertions only.
        await Task.Yield();
        await _collection.CollectAsync(alias.Governor, 4);
        Assert.False(alias.Host.IsAlive); Assert.False(alias.Prepared.IsAlive);
        Assert.False(alias.Result.IsAlive); Assert.False(alias.Counters.IsAlive);
        GC.KeepAlive(alias);
    }

    private sealed record Detached(object Value, WeakReference[] Metadata);

    private static WeakReference[] Metadata(ResourceGovernor governor, object? document)
    {
        List<WeakReference> result = [new(governor), new(document)];
        object? ledger = typeof(ResourceGovernor).GetField("_valueOwners", Members)?.GetValue(governor);
        if (ledger is not null)
        {
            result.Add(new(ledger));
            foreach (string name in new[] { "_storage", "_heads", "_slots", "_segments" })
            { result.Add(new(ledger.GetType().GetField(name, Members)?.GetValue(ledger))); }
        }
        return result.ToArray();
    }

    private static async Task<Detached> DetachTyped(string route, bool compressed)
    {
        Alias alias = await Complete(route, compressed, "read", "success", "counters", QpdfLimits.Default);
        return new(alias.Value ?? throw new InvalidOperationException(), [alias.Host, alias.Result, .. Metadata(alias.Governor, null)]);
    }

    [Theory]
    [MemberData(nameof(MetadataJobs))]
    [Trait("Layer", "Contract")]
    public async Task TypedCounterAliasesDoNotRootCompletedMetadata(string route, bool compressed)
    {
        Detached alias = await DetachTyped(route, compressed);
        if (_collection is null) { return; } // Contract lane: producer assertions only.
        await Task.Yield();
        await _collection.CollectAsync(null, 0);
        Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive));
        GC.KeepAlive(alias.Value);
    }

    private sealed class DocumentAliases(ResourceGovernor governor, QpdfExecutionCounters[] values, WeakReference document, WeakReference host)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal QpdfExecutionCounters[] Values = values;
        internal readonly WeakReference Document = document;
        internal readonly WeakReference Host = host;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Values KeepFirst(DocumentAliases aliases)
    {
        QpdfExecutionCounters first = aliases.Values[0];
        aliases.Values = [first];
        return State(first);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckFirst(DocumentAliases aliases, Values expected) => Assert.Equal(expected, State(aliases.Values[0]));

    private static async Task<DocumentAliases> RepeatedDocument(bool compressed, int count, bool cancel)
    {
        using CancellationTokenSource cancellation = new();
        object fixture = await Prepare("typed-check", compressed, "none", "success");
        QpdfDocument document = await QpdfDocument.OpenAsync((IQpdfHost)fixture, Input, cancellation.Token);
        ResourceGovernor governor = (ResourceGovernor)(typeof(QpdfDocument).GetField("_governor", Members)?.GetValue(document)
            ?? throw new InvalidOperationException());
        QpdfExecutionCounters[] snapshots = new QpdfExecutionCounters[count];
        Values[] observations = new Values[count];
        bool rejectedCancellation = !cancel;
        try
        {
            for (int index = 0; index < count; index++)
            {
                snapshots[index] = document.Counters;
                observations[index] = State(governor.Snapshot());
            }
            if (cancel)
            {
                cancellation.Cancel();
                try { _ = document.Counters; }
                catch (OperationCanceledException) { rejectedCancellation = true; }
            }
        }
        finally { await document.DisposeAsync(); }
        Assert.Equal(0L, ((InMemoryQpdfHost)Read(fixture, "Inner")).OpenLeaseCount);
        Assert.True(rejectedCancellation);
        // Check ownership only after the complete public job has closed, even
        // when the identical final tests run on an unfunded predecessor.
        for (int index = 0; index < count; index++)
        {
            Assert.Equal(observations[index], State(snapshots[index]));
            if (index != 0)
            {
                Assert.NotSame(snapshots[index-1], snapshots[index]);
                Assert.Equal(snapshots[index-1].LiveMemoryBytes + 120L, snapshots[index].LiveMemoryBytes);
                Assert.Equal(snapshots[index-1].TotalWork + 1L, snapshots[index].TotalWork);
            }
        }
        return new(governor, snapshots, new(document), new(fixture));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ResourceGovernor> ClosedDocumentGovernor(bool compressed)
    {
        object fixture = await Prepare("typed-check", compressed, "none", "success");
        QpdfDocument? document = await QpdfDocument.OpenAsync((IQpdfHost)fixture, Input, CancellationToken.None);
        ResourceGovernor governor = (ResourceGovernor)(typeof(QpdfDocument).GetField("_governor", Members)?.GetValue(document)
            ?? throw new InvalidOperationException());
        await document.DisposeAsync();
        document = null;
        return governor;
    }

    private static async Task<(long Work, long Peak)> ClosedDocumentBaseline(bool compressed)
    {
        ResourceGovernor governor = await ClosedDocumentGovernor(compressed);
        await Task.Yield();
        Collect(governor);
        QpdfExecutionCounters observation = governor.Snapshot();
        Assert.Equal(0L, observation.LiveMemoryBytes);
        return (observation.TotalWork, observation.PeakMemoryBytes);
    }

    private static async Task<DocumentAliases> LimitedDocument(bool compressed, string budget)
    {
        var baseline = await ClosedDocumentBaseline(compressed);
        QpdfLimits limits = budget == "work" ? QpdfLimits.Default with { MaxWork = 2048L }
            : QpdfLimits.Default with { MaxMemoryBytes = baseline.Peak };
        object fixture = await Prepare("typed-check", compressed, "none", "success");
        QpdfDocument document;
        using (OwnershipBatch.CaptureWithoutCollection())
        { document = await QpdfDocument.OpenAsync((IQpdfHost)fixture, Input, limits, CancellationToken.None); }
        ResourceGovernor governor = (ResourceGovernor)(typeof(QpdfDocument).GetField("_governor", Members)?.GetValue(document)
            ?? throw new InvalidOperationException());
        await document.DisposeAsync();
        Collect(governor);
        // The still-usable disposed document retains its public versions. Those
        // carriers have separate ownership from subsequently read counters.
        Assert.Equal("1.4", document.Version.ToString());
        long retainedVersions = governor.Snapshot().LiveMemoryBytes;
        Assert.True(retainedVersions >= PdfVersionOwnershipTests.MeasurePhysicalSize(typeof(PdfVersion)));
        long heldWork = (long)(typeof(ResourceGovernor).GetField("_reservedResultWork", Members)?.GetValue(governor)
            ?? throw new InvalidOperationException("Missing actual reserved carrier work."));
        // Weak reader cleanup has its own observed work; a separate run cannot
        // predict it. Exhaust the actual remaining budget with live snapshots.
        int expected = budget == "work" ? checked((int)((limits.MaxWork - governor.Snapshot().TotalWork - heldWork) / 2L))
            : checked((int)((limits.MaxMemoryBytes - retainedVersions - 24L) / 120L));
        Assert.InRange(expected, 1, 1024);
        QpdfExecutionCounters[] snapshots = new QpdfExecutionCounters[expected];
        for (int index = 0; index < expected; index++) { snapshots[index] = document.Counters; }
        Values last = State(snapshots[^1]);
        Assert.Throws<QpdfLimitException>(() => document.Counters);
        Assert.Equal(last, State(snapshots[^1]));
        Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork <= limits.MaxWork);
        Assert.Equal(0L, ((InMemoryQpdfHost)Read(fixture, "Inner")).OpenLeaseCount);
        return new(governor, snapshots, new(document), new(fixture));
    }

    [Theory]
    [InlineData(false, "work")]
    [InlineData(true, "work")]
    [InlineData(false, "memory")]
    [InlineData(true, "memory")]
    [Trait("Layer", "Ownership")]
    public async Task PublicDocumentSnapshotBudgetsRejectBeforeAllocationAndPrepayRetirement(bool compressed, string budget)
    {
        DocumentAliases aliases = await LimitedDocument(compressed, budget);
        await Task.Yield();
        Collect(aliases.Governor);
        Assert.False(aliases.Host.IsAlive); Assert.False(aliases.Document.IsAlive);
        Assert.Equal(aliases.Values.Length * 120L + 24L, aliases.Governor.Snapshot().LiveMemoryBytes);
        aliases.Values = [];
        Collect(aliases.Governor);
        Assert.Equal(0L, aliases.Governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(aliases);
    }

    [Theory]
    [InlineData(false, 4, false)]
    [InlineData(true, 4, false)]
    [InlineData(false, 32, false)]
    [InlineData(true, 32, false)]
    [InlineData(false, 4, true)]
    [InlineData(true, 4, true)]
    [Trait("Layer", "Ownership")]
    public async Task RepeatedDocumentSnapshotsFundSurvivorsAndRetireDiscardedSiblings(bool compressed, int count, bool cancel)
    {
        DocumentAliases aliases = await RepeatedDocument(compressed, count, cancel);
        await Task.Yield();
        Collect(aliases.Governor);
        Assert.False(aliases.Document.IsAlive); Assert.False(aliases.Host.IsAlive);
        Assert.Equal(count * 120L + 24L, aliases.Governor.Snapshot().LiveMemoryBytes);
        Values first = KeepFirst(aliases);
        Collect(aliases.Governor);
        Assert.Equal(144L, aliases.Governor.Snapshot().LiveMemoryBytes);
        CheckFirst(aliases, first);
        aliases.Values = [];
        Collect(aliases.Governor);
        Assert.Equal(0L, aliases.Governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(aliases);
    }

    private static IEnumerable<(string Method, IEnumerable<object[]> Rows)> OwnershipCases()
        => new (string, IEnumerable<object[]>)[]
        {
            (nameof(CompletePublicJobsRetireUnreturnedCountersAndFundReturnedAliases), PublicJobs()),
            (nameof(EachTypedFamilyKeepsIndependentImmutableCountersAndCallerCopies), SuccessAliases()),
            (nameof(RealPublicBudgetFailuresRetirePreparedCountersWithoutReplacingPriorOutputs), BudgetJobs()),
            (nameof(TypedCounterAliasesDoNotRootCompletedMetadata), MetadataJobs()),
        };

    public static IEnumerable<object[]> OwnershipBatches() => OwnershipMatrix.Batches(OwnershipCases());

    [Theory]
    [MemberData(nameof(OwnershipBatches))]
    [Trait("Layer", "Ownership")]
    public Task BatchedLifetimeObservationsPreserveEveryOriginalPhase(int batch)
        => OwnershipMatrix.RunAsync(_output, OwnershipCases(), batch,
            observation => new TypedCounterOwnershipTests(_output) { _collection = observation });
}
