using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class TypedResultOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");
    private static readonly Dictionary<Type, long> Geometry = [];

    private readonly record struct Values(long Live, long Peak, long Work, long Decoded, long OutputBytes,
        long Calls, long Handles, long Diagnostics);
    private static Values State(QpdfExecutionCounters c) => new(c.LiveMemoryBytes, c.PeakMemoryBytes, c.TotalWork,
        c.TotalDecodedBytes, c.TotalOutputBytes, c.HostOperations, c.OpenHandles, c.DiagnosticCount);

    private sealed class Alias(ResourceGovernor governor, object? value, WeakReference host, WeakReference result,
        WeakReference counters, WeakReference receipt, WeakReference preparedReceipt, bool prepared,
        long heldWork, Values? snapshot)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
        internal readonly WeakReference Host = host;
        internal readonly WeakReference Result = result;
        internal readonly WeakReference Counters = counters;
        internal readonly WeakReference Receipt = receipt;
        internal readonly WeakReference PreparedReceipt = preparedReceipt;
        internal readonly bool Prepared = prepared;
        internal readonly long HeldWork = heldWork;
        internal readonly Values? Snapshot = snapshot;
    }

    public static IEnumerable<object[]> PublicJobs() => TypedCounterOwnershipTests.PublicJobs();
    public static IEnumerable<object[]> BudgetJobs() => TypedCounterOwnershipTests.BudgetJobs();
    public static IEnumerable<object[]> MetadataJobs() => TypedCounterOwnershipTests.MetadataJobs();
    public static IEnumerable<object[]> TinyJobs() => TypedCounterOwnershipTests.TinyJobs();
    public static IEnumerable<object[]> SuccessAliases()
    {
        foreach (object[] row in MetadataJobs())
        foreach (string kind in new[] { "result", "counters", "copy", "constructed" })
        { yield return [row[0], row[1], kind]; }
    }

    private static object Read(object value, string property) => value.GetType().GetProperty(property, Members)?.GetValue(value)
        ?? throw new InvalidOperationException("Missing original public-job fixture property: " + property);
    private static object? Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value);
    private static async Task<object> Invoke(string method, object?[] arguments)
    {
        Task task = (Task)(typeof(TypedCounterOwnershipTests).GetMethod(method, Members)?.Invoke(null, arguments)
            ?? throw new InvalidOperationException("Missing original public-job fixture method: " + method));
        await task;
        return Read(task, "Result");
    }

    private static ResourceGovernor FindGovernor(Task task) => (ResourceGovernor)(typeof(ExecutionCounterTests)
        .GetMethod("FindGovernor", Members)?.Invoke(null, [task, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
        ?? throw new InvalidOperationException("Missing actual public governor."));
    private static void Resume(object host) => host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);

    private static long Size(Type type)
    {
        if (Geometry.TryGetValue(type, out long measured)) { return measured; }
        object[] roots = new object[1024];
        for (int i = 0; i < roots.Length; i++) { roots[i] = RuntimeHelpers.GetUninitializedObject(type); }
        Array.Clear(roots);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < roots.Length; i++) { roots[i] = RuntimeHelpers.GetUninitializedObject(type); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, bytes % roots.Length);
        measured = bytes / roots.Length;
        Geometry.Add(type, measured);
        GC.KeepAlive(roots);
        return measured;
    }

    private static object Copy(object result) => result switch
    {
        PdfPageSelectionResult r => r with { },
        PdfMultiPageSelectionResult r => r with { },
        QpdfCheckResult r => r with { },
        QpdfJsonResult r => r with { },
        QpdfJsonImportResult r => r with { },
        QpdfAttachmentExtraction r => r with { },
        QpdfAttachmentEditResult r => r with { },
        _ => throw new InvalidOperationException("Unknown public result family."),
    };

    private static object Construct(object result) => result switch
    {
        PdfPageSelectionResult r => new PdfPageSelectionResult(r.SourcePageCount, r.SourcePages, r.PageCount, r.Warnings, r.Counters),
        PdfMultiPageSelectionResult r => new PdfMultiPageSelectionResult(r.PageCount, r.Mapping, r.SourcePageCounts, r.OutputPaths, r.Warnings, r.Counters),
        QpdfCheckResult r => new QpdfCheckResult(r.PageCount, r.Version, r.Checks, r.Diagnostics, r.Counters),
        QpdfJsonResult r => new QpdfJsonResult(r.Json, r.Warnings, r.Counters),
        QpdfJsonImportResult r => new QpdfJsonImportResult(r.PageCount, r.Warnings, r.Counters),
        QpdfAttachmentExtraction r => new QpdfAttachmentExtraction(r.Key, r.ByteCount, r.Warnings, r.Counters),
        QpdfAttachmentEditResult r => new QpdfAttachmentEditResult(r.AttachmentCount, r.Warnings, r.Counters),
        _ => throw new InvalidOperationException("Unknown public result family."),
    };

    private static async Task<Alias> Complete(string route, bool compressed, string boundary, string outcome, string kind, QpdfLimits limits)
    {
        using CancellationTokenSource cancellation = new();
        object host = await Invoke("Prepare", [route, compressed, boundary, outcome]);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(host, "Inner");
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        Assert.True(GC.TryStartNoGCRegion(16L * 1024L * 1024L));
        Task<object> running = Invoke("Start", [host, route, limits, cancellation.Token]);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = FindGovernor(running);
            object? storage = Field(governor, "_preparedTypedResultStorage");
            bool prepared = storage is not null;
            WeakReference preparedReceipt = new(storage);
            storage = null;
            long held = (long)(Field(governor, "_reservedResultWork") ?? 0L);
            // Observe wrapper credit independently of the versions created by
            // this completed public job; each live version holds one retirement.
            held -= (int)(Field(governor, "_versionCarriers") ?? 0);
            // Selection arrays/mappings share the prepaid channel. They are
            // separate owners from the wrapper/counter credits asserted here.
            held -= PrepaidAllocationWork(governor);
            if (outcome == "cancel") { cancellation.Cancel(); } else { Resume(host); }
            object? result = null;
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
            else if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
            else if (outcome == "budget") { await Assert.ThrowsAsync<QpdfLimitException>(() => running); }
            else { result = await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            Assert.True((long)(Field(host, "ReadBytes") ?? 0L) > 0L);
            Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
            Assert.True(governor.Snapshot().TotalWork <= limits.MaxWork);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(inner.TryGetOutput(Output, out byte[] current));
            if ((int)(Field(host, "Commits") ?? 0) == 0) { Assert.Equal(prior, current); }
            QpdfExecutionCounters? counters = result is null ? null : (QpdfExecutionCounters)Read(result, "Counters");
            if (counters is not null) { Assert.Equal(governor.Snapshot(), counters); }
            object? value = result is null ? null : kind == "result" ? result : kind == "copy" ? Copy(result)
                : kind == "constructed" ? Construct(result) : counters;
            if (kind == "copy" && result is not null)
            {
                Assert.Equal(result, value);
                Assert.Equal(result.GetHashCode(), value?.GetHashCode());
                Assert.Same(counters, Read(value ?? throw new InvalidOperationException(), "Counters"));
            }
            if (value is not null && value is not QpdfExecutionCounters)
            {
                foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                { Assert.False(property.CanWrite); }
                Assert.Same(counters, Read(value, "Counters"));
                if (kind is "copy" or "constructed") { Assert.Null(Field(value, "_storage")); }
            }
            return new(governor, value, new(host), new(result), new(counters), new(result is null ? null : Field(result, "_storage")),
                preparedReceipt, prepared, held, counters is null ? null : State(counters));
        }
        finally
        {
            Resume(host);
            try { try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { } }
            finally { GC.EndNoGCRegion(); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PrepaidAllocationWork(ResourceGovernor governor)
    {
        object? owners = Field(governor, "_valueOwners");
        if (owners is null) { return 0L; }
        long count = 0L;
        for (object? segment = Field(owners, "_segments"); segment is not null; segment = Field(segment, "Next"))
        {
            ConditionalWeakTable<object, RetainedValueOwners.Receipt> values =
                (ConditionalWeakTable<object, RetainedValueOwners.Receipt>)(Field(segment, "Values") ?? throw new InvalidOperationException());
            foreach (KeyValuePair<object, RetainedValueOwners.Receipt> pair in values)
            { if (pair.Value.PrepaidRetirement) { count++; } }
        }
        return count;
    }

    private static void Collect(ResourceGovernor governor)
    {
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        for (int boundary = 0; boundary < 4; boundary++) { governor.ReserveMemory(0L); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckSurvivor(Alias alias, string kind)
    {
        object value = alias.Value ?? throw new InvalidOperationException();
        QpdfExecutionCounters counters = value is QpdfExecutionCounters c ? c : (QpdfExecutionCounters)Read(value, "Counters");
        Assert.Equal(alias.Snapshot, State(counters));
        if (kind == "result")
        {
            object storage = Field(value, "_storage") ?? throw new InvalidOperationException("Public wrapper has no receipt.");
            long wrapper = Size(value.GetType());
            Assert.Equal(wrapper + 32L, Field(storage, "_bytes"));
            Assert.True(alias.Governor.Snapshot().LiveMemoryBytes >= wrapper + Size(typeof(QpdfExecutionCounters)) + 64L + 24L);
        }
        else { Assert.True(alias.Governor.Snapshot().LiveMemoryBytes >= Size(typeof(QpdfExecutionCounters)) + 32L + 24L); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalExtractionJobsMeetTheIndependentCarrierMinimum(bool compressed)
    {
        Alias alias = await Complete("typed-extract", compressed, "read", "success", "result", QpdfLimits.Default);
        await Task.Yield();
        Collect(alias.Governor);
        object value = alias.Value ?? throw new InvalidOperationException();
        // Preserve the original minimum first: on the predecessor this is
        // 48 + 88 + 32 + 24 = 192, independently of any new private fields.
        long originalMinimum = Size(value.GetType()) + Size(typeof(QpdfExecutionCounters)) + 32L + 24L;
        Assert.True(alias.Governor.Snapshot().LiveMemoryBytes >= originalMinimum,
            $"Retained extraction storage {alias.Governor.Snapshot().LiveMemoryBytes} is below measured minimum {originalMinimum}.");
        CheckSurvivor(alias, "result");
        Assert.False(alias.Host.IsAlive);
        GC.KeepAlive(value);
        GC.KeepAlive(alias);
    }

    [Theory]
    [MemberData(nameof(PublicJobs))]
    public async Task CompleteJobsPrepareWrapperOwnershipBeforeIOAndRetireEveryOutcome(string route, bool compressed, string boundary, string outcome)
    {
        Alias alias = await Complete(route, compressed, boundary, outcome, "counters", QpdfLimits.Default);
        await Task.Yield();
        Collect(alias.Governor);
        Assert.True(alias.Prepared); Assert.Equal(3L, alias.HeldWork);
        Assert.False(alias.Host.IsAlive); Assert.False(alias.Result.IsAlive);
        Assert.False(alias.Receipt.IsAlive); Assert.False(alias.PreparedReceipt.IsAlive);
        if (outcome == "success") { CheckSurvivor(alias, "counters"); }
        alias.Value = null;
        Retire(alias);
    }

    private static void Retire(Alias alias)
    {
        Collect(alias.Governor);
        Assert.Null(Field(alias.Governor, "_preparedTypedResultStorage"));
        Assert.Null(Field(alias.Governor, "_preparedTypedCounters"));
        Assert.False(alias.Result.IsAlive); Assert.False(alias.Counters.IsAlive);
        Assert.False(alias.Receipt.IsAlive); Assert.False(alias.PreparedReceipt.IsAlive);
        Assert.Equal(0L, Field(alias.Governor, "_reservedResultWork"));
        Assert.Equal(0, Field(alias.Governor, "_executionResultCarriers"));
        Assert.Null(Field(alias.Governor, "_executionResultLink"));
        GC.KeepAlive(alias);
    }

    [Theory]
    [MemberData(nameof(SuccessAliases))]
    public async Task IndependentResultCounterAndCallerCopiesPreserveTheirOwnReceipts(string route, bool compressed, string kind)
    {
        Alias alias = await Complete(route, compressed, "read", "success", kind, QpdfLimits.Default);
        await Task.Yield();
        Collect(alias.Governor);
        Assert.True(alias.Prepared); Assert.Equal(3L, alias.HeldWork);
        Assert.False(alias.Host.IsAlive);
        Assert.Equal(kind == "result", alias.Result.IsAlive);
        Assert.Equal(kind == "result", alias.Receipt.IsAlive);
        Assert.Equal(kind == "result", alias.PreparedReceipt.IsAlive);
        CheckSurvivor(alias, kind);
        alias.Value = null;
        Retire(alias);
    }

    [Theory]
    [MemberData(nameof(BudgetJobs))]
    public async Task RealMemoryAndWorkFailuresKeepPriorOutputsAndRetireUnconstructedWrappers(string route, bool compressed, string budget)
    {
        QpdfLimits limits = budget == "work" ? QpdfLimits.Default with { MaxWork = 128L }
            : QpdfLimits.Default with { MaxMemoryBytes = 2048L };
        Alias alias = await Complete(route, compressed, "read", "budget", "counters", limits);
        await Task.Yield();
        Collect(alias.Governor);
        Assert.True(alias.Prepared); Assert.Equal(3L, alias.HeldWork);
        Assert.False(alias.Host.IsAlive); Assert.False(alias.PreparedReceipt.IsAlive);
        Assert.Equal(0L, Field(alias.Governor, "_reservedResultWork"));
        Retire(alias);
    }

    private sealed record Detached(object Value, WeakReference[] Metadata);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Detached> Detach(string route, bool compressed)
    {
        Alias alias = await Complete(route, compressed, "read", "success", "result", QpdfLimits.Default);
        List<WeakReference> metadata = [alias.Host, new(alias.Governor)];
        object? ledger = Field(alias.Governor, "_valueOwners");
        metadata.Add(new(ledger));
        if (ledger is not null)
        {
            foreach (string name in new[] { "_storage", "_heads", "_slots", "_segments" }) { metadata.Add(new(Field(ledger, name))); }
        }
        return new(alias.Value ?? throw new InvalidOperationException(), metadata.ToArray());
    }

    [Theory]
    [MemberData(nameof(MetadataJobs))]
    public async Task ReturnedWrappersDoNotRootCompletedJobs(string route, bool compressed)
    {
        Detached alias = await Detach(route, compressed);
        await Task.Yield();
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(alias.Metadata, weak => Assert.False(weak.IsAlive));
        Assert.NotNull(Field(alias.Value, "_storage"));
        GC.KeepAlive(alias);
    }

    private sealed class CountingHost(IQpdfHost inner) : IQpdfHost
    {
        internal int Calls;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Calls++; return inner.OpenInputAsync(path, token); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Calls++; return inner.CreateOutputAsync(path, token); }
    }

    private static Type ResultType(string route) => route switch
    {
        "typed-page" or "typed-page-rotation" => typeof(PdfPageSelectionResult),
        "typed-pages" or "typed-pages-rotation" or "typed-pages-options" => typeof(PdfMultiPageSelectionResult),
        "typed-check" => typeof(QpdfCheckResult), "typed-json" => typeof(QpdfJsonResult),
        "typed-import" or "typed-update" => typeof(QpdfJsonImportResult), "typed-extract" => typeof(QpdfAttachmentExtraction),
        _ => typeof(QpdfAttachmentEditResult),
    };

    [Theory]
    [MemberData(nameof(TinyJobs))]
    public async Task WrapperAdmissionAndPrecancellationRejectBeforeHostAccess(string route, string budget, bool canceled)
    {
        long bytes = Size(ResultType(route)) + 176L;
        object fixture = await Invoke("Prepare", [route, false, "none", "success"]);
        CountingHost host = new((IQpdfHost)fixture);
        using CancellationTokenSource cancellation = new();
        if (canceled) { cancellation.Cancel(); }
        QpdfLimits limits = budget == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = bytes - 1L }
            : QpdfLimits.Default with { MaxWork = 4L };
        if (canceled) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke("Start", [host, route, limits, cancellation.Token])); }
        else { await Assert.ThrowsAsync<QpdfLimitException>(() => Invoke("Start", [host, route, limits, cancellation.Token])); }
        Assert.Equal(0, host.Calls);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(fixture, "Inner");
        Assert.Equal(0L, inner.OpenLeaseCount);
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
    }

    [Theory]
    [InlineData(typeof(PdfPageSelectionResult))]
    [InlineData(typeof(PdfMultiPageSelectionResult))]
    [InlineData(typeof(QpdfCheckResult))]
    [InlineData(typeof(QpdfJsonResult))]
    [InlineData(typeof(QpdfJsonImportResult))]
    [InlineData(typeof(QpdfAttachmentExtraction))]
    [InlineData(typeof(QpdfAttachmentEditResult))]
    public void WrapperAdmissionEqualsIndependentlyMeasuredPhysicalSize(Type type)
    {
        FieldInfo constant = type.GetField("StorageBytes", Members) ?? throw new InvalidOperationException("Missing wrapper admission.");
        Assert.Equal(Size(type), constant.GetRawConstantValue());
    }

    [Theory]
    [InlineData("PageSelectionOutcome")]
    [InlineData("MultiPageSelectionOutcome")]
    [InlineData("AttachmentExtractionOutcome")]
    [InlineData("JsonInspectionOutcome")]
    [InlineData("JsonImportOutcome")]
    [InlineData("TypedCheckOutcome")]
    public void SharedPrivateOutcomesAreReadonlyValues(string name)
    {
        Type type = typeof(Qpdf).Assembly.GetType("Lokad.Qpdf.Runtime." + name)
            ?? throw new InvalidOperationException("Missing private value outcome: " + name);
        Assert.True(type.IsValueType);
        Assert.True(type.IsDefined(typeof(IsReadOnlyAttribute)));
        Assert.False(type.IsPublic);
    }
}
