using System.Reflection;
using Xunit.Abstractions;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class SelectionBackingOwnershipTests
{
    private readonly ITestOutputHelper _output;
    private OwnershipBatch.Observation? _collection;

    public SelectionBackingOwnershipTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static IEnumerable<object[]> Aliases()
    {
        foreach (bool compressed in new[] { false, true })
        {
            yield return ["typed-page", compressed, "SourcePages", false];
            yield return ["typed-pages", compressed, "Mapping", false];
            yield return ["typed-pages", compressed, "Mapping", true];
            yield return ["typed-pages", compressed, "SourcePageCounts", false];
            yield return ["typed-pages", compressed, "OutputPaths", false];
        }
    }

    private static object Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value)
        ?? throw new InvalidOperationException(name);

    private static async Task<object> Complete(string route, bool compressed)
        => await Complete(route, compressed, "read", "success", QpdfLimits.Default);

    private static async Task<object> Complete(string route, bool compressed, string boundary, string outcome, QpdfLimits limits)
    {
        Task task = (Task)(typeof(TypedCounterOwnershipTests).GetMethod("Complete", Members)?.Invoke(null,
            [route, compressed, boundary, outcome, "result", limits]) ?? throw new InvalidOperationException());
        await task;
        return task.GetType().GetProperty("Result", Members)?.GetValue(task) ?? throw new InvalidOperationException();
    }

    private sealed record Alias(object Captured, WeakReference Value, WeakReference Backing, long Minimum);

    private static long Size(object value)
    {
        Func<object> create = value is Array array
            ? () => Array.CreateInstance(value.GetType().GetElementType() ?? throw new InvalidOperationException(), array.Length)
            : value is string text ? () => new string('\0', text.Length)
            : () => RuntimeHelpers.GetUninitializedObject(value.GetType());
        object[] roots = new object[1024];
        for (int i = 0; i < roots.Length; i++) { roots[i] = create(); }
        Array.Clear(roots);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < roots.Length; i++) { roots[i] = create(); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(roots);
        return bytes / roots.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Keep(object captured, string property, bool element)
    {
        object result = Field(captured, "Value");
        object value = result.GetType().GetProperty(property)?.GetValue(result) ?? throw new InvalidOperationException();
        if (element) { value = ((IReadOnlyList<PdfPageSelectionMapping>)value)[0]; }
        captured.GetType().GetField("Value", Members)?.SetValue(captured, value);
        object? backing = value.GetType().GetFields(Members).Select(field => field.GetValue(value))
            .OfType<Array>().SingleOrDefault();
        return new(captured, new(value), new(backing), Size(value) + (backing is null ? 0L : Size(backing)));
    }

    private async Task<QpdfExecutionCounters> CollectAsync(object captured)
    {
        ResourceGovernor governor = (ResourceGovernor)Field(captured, "Governor");
        if (_collection is not null) { await _collection.CollectAsync(governor, 4); }
        else
        {
            for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            for (int boundary = 0; boundary < 4; boundary++) { governor.ReserveMemory(0L); }
        }
        return governor.Snapshot();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Drop(Alias alias) => alias.Captured.GetType().GetField("Value", Members)?.SetValue(alias.Captured, null);

    private static RetainedValueOwners.Receipt Receipt(ResourceGovernor governor, object carrier)
    {
        object owners = Field(governor, "_valueOwners");
        for (object? segment = Field(owners, "_segments"); segment is not null;
            segment = segment.GetType().GetField("Next", Members)?.GetValue(segment))
        {
            ConditionalWeakTable<object, RetainedValueOwners.Receipt> values =
                (ConditionalWeakTable<object, RetainedValueOwners.Receipt>)Field(segment, "Values");
            if (values.TryGetValue(carrier, out RetainedValueOwners.Receipt? receipt)) { return receipt; }
        }
        throw new InvalidOperationException("Public alias lacks an independent receipt.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PacketMinimum(Alias alias)
    {
        ResourceGovernor governor = (ResourceGovernor)Field(alias.Captured, "Governor");
        object value = alias.Value.Target ?? throw new InvalidOperationException();
        long bytes = alias.Minimum + Size(Receipt(governor, value));
        if (alias.Backing.Target is object backing) { bytes += Size(Receipt(governor, backing)); }
        return bytes;
    }

    [Theory]
    [MemberData(nameof(Aliases))]
    [Trait("Layer", "Contract")]
    public async Task OriginalPublicAliasesRetireTheirIndependentBodyAndBacking(string route, bool compressed, string property, bool element)
    {
        object captured = await Complete(route, compressed);
        await Task.Yield();
        Alias alias = Keep(captured, property, element);
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        QpdfExecutionCounters held = await CollectAsync(captured);
        Assert.False(((WeakReference)Field(captured, "Host")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Result")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Counters")).IsAlive);
        Assert.True(alias.Value.IsAlive);
        long packetMinimum = PacketMinimum(alias);
        Drop(alias);
        QpdfExecutionCounters dropped = await CollectAsync(captured);
        Assert.False(alias.Value.IsAlive);
        Assert.False(alias.Backing.IsAlive);
        Assert.True(held.LiveMemoryBytes - dropped.LiveMemoryBytes >= packetMinimum,
            $"{property} retired {held.LiveMemoryBytes - dropped.LiveMemoryBytes} bytes; independent packet minimum is {packetMinimum}.");
        Assert.Equal(0L, dropped.OpenHandles);
        Assert.True(dropped.TotalWork <= QpdfLimits.Default.MaxWork);
        GC.KeepAlive(alias);
    }

    public static IEnumerable<object[]> Siblings()
    {
        foreach (string route in new[] { "typed-pages", "typed-pages-rotation", "typed-pages-options" })
        foreach (bool compressed in new[] { false, true })
        foreach (string kind in new[] { "element", "record-copy", "constructed", "result-copy" })
        { yield return [route, compressed, kind]; }
    }

    private sealed record MappingAliases(object Captured, WeakReference Mapping, WeakReference First,
        WeakReference Second, WeakReference Counts, WeakReference Paths, WeakReference Receipt,
        PdfPageSelectionMapping Expected);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MappingAliases KeepMapping(object captured, string kind)
    {
        PdfMultiPageSelectionResult result = (PdfMultiPageSelectionResult)Field(captured, "Value");
        PdfPageSelectionMapping first = result.Mapping[0];
        ResourceGovernor governor = (ResourceGovernor)Field(captured, "Governor");
        object value = kind switch
        {
            "element" => first,
            "record-copy" => first with { },
            "constructed" => new PdfPageSelectionMapping(first.SourceIndex, first.SourcePage),
            _ => result with { },
        };
        if (value is PdfPageSelectionMapping copied && kind != "element")
        { Assert.Equal(first, copied); Assert.Equal(first.GetHashCode(), copied.GetHashCode()); Assert.NotSame(first, copied); }
        if (value is PdfMultiPageSelectionResult resultCopy)
        { Assert.Equal(result, resultCopy); Assert.Same(result.Mapping, resultCopy.Mapping); }
        MappingAliases aliases = new(captured, new(result.Mapping), new(first), new(result.Mapping[1]),
            new(result.SourcePageCounts), new(result.OutputPaths), new(Receipt(governor, first)),
            new(first.SourceIndex, first.SourcePage));
        captured.GetType().GetField("Value", Members)?.SetValue(captured, value);
        return aliases;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckMapping(MappingAliases aliases, string kind)
    {
        object value = Field(aliases.Captured, "Value");
        PdfPageSelectionMapping first = value is PdfMultiPageSelectionResult result ? result.Mapping[0] : (PdfPageSelectionMapping)value;
        Assert.Equal(aliases.Expected, first);
        Assert.False(typeof(PdfPageSelectionMapping).GetProperty(nameof(PdfPageSelectionMapping.SourcePage))?.CanWrite);
        bool resultCopy = kind == "result-copy", borrowedElement = kind == "element" || resultCopy;
        Assert.Equal(resultCopy, aliases.Mapping.IsAlive);
        Assert.Equal(resultCopy, aliases.Second.IsAlive);
        Assert.Equal(resultCopy, aliases.Counts.IsAlive);
        Assert.Equal(resultCopy, aliases.Paths.IsAlive);
        Assert.Equal(borrowedElement, aliases.First.IsAlive);
        Assert.Equal(borrowedElement, aliases.Receipt.IsAlive);
    }

    [Theory]
    [MemberData(nameof(Siblings))]
    [Trait("Layer", "Contract")]
    public async Task MappingAliasesReleaseIndependentSiblingsAndDistinguishCallerCopies(string route, bool compressed, string kind)
    {
        object captured = await Complete(route, compressed);
        await Task.Yield();
        MappingAliases aliases = KeepMapping(captured, kind);
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(captured);
        Assert.False(((WeakReference)Field(captured, "Host")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Result")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Counters")).IsAlive && kind != "result-copy");
        CheckMapping(aliases, kind);
        GC.KeepAlive(aliases);
    }

    public static IEnumerable<object[]> PublicFailures()
        => TypedCounterOwnershipTests.PublicJobs().Where(row => ((string)row[0]).StartsWith("typed-page", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(PublicFailures))]
    [Trait("Layer", "Contract")]
    public async Task CompleteSelectionJobsKeepOriginalFaultCancellationAndPublicationControls(string route, bool compressed, string boundary, string outcome)
    {
        object captured = await Complete(route, compressed, boundary, outcome, QpdfLimits.Default);
        await Task.Yield();
        captured.GetType().GetField("Value", Members)?.SetValue(captured, null);
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        QpdfExecutionCounters counters = await CollectAsync(captured);
        Assert.False(((WeakReference)Field(captured, "Host")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Result")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Counters")).IsAlive);
        Assert.InRange(counters.PeakMemoryBytes, 1L, QpdfLimits.Default.MaxMemoryBytes);
        Assert.InRange(counters.TotalWork, 1L, QpdfLimits.Default.MaxWork);
        Assert.Equal(0L, counters.OpenHandles);
        GC.KeepAlive(captured);
    }

    private sealed class SplitCapture(ResourceGovernor governor, object? value, WeakReference host, WeakReference result, WeakReference counters)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
        internal readonly WeakReference Host = host, Result = result, Counters = counters;
    }

    private static async Task<PdfMultiPageSelectionResult> StartSplit(IQpdfHost host, QpdfLimits limits, CancellationToken token)
        => await Qpdf.SelectPagesAsync(host,
            [new PdfPageSource(QpdfPath.From("input.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("second.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("output.pdf"), new PdfPageMergeOptions { SplitPages = 1 }, limits, token);

    private static async Task<SplitCapture> CompleteSplit(bool compressed, string boundary, string outcome, bool second)
    {
        Task setup = (Task)(typeof(ExecutionCounterTests).GetMethod("Prepare", Members)?.Invoke(null,
            ["typed-pages", compressed, boundary, outcome]) ?? throw new InvalidOperationException());
        await setup;
        object host = setup.GetType().GetProperty("Result")?.GetValue(setup) ?? throw new InvalidOperationException();
        InMemoryQpdfHost inner = (InMemoryQpdfHost)(host.GetType().GetProperty("Inner", Members)?.GetValue(host)
            ?? throw new InvalidOperationException());
        List<byte[]> prior = [];
        foreach (string path in new[] { "output-1.pdf", "output-2.pdf" })
        {
            await using IQpdfOutputTransaction seed = await inner.CreateOutputAsync(QpdfPath.From(path), CancellationToken.None);
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
            Assert.True(inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes)); prior.Add(bytes);
        }
        if (second) { host.GetType().GetField("PausePath", Members)?.SetValue(host, QpdfPath.From("output-2.pdf")); }
        using CancellationTokenSource cancellation = new();
        Assert.True(GC.TryStartNoGCRegion(16L * 1024L * 1024L));
        Task<PdfMultiPageSelectionResult> running = StartSplit((IQpdfHost)host, QpdfLimits.Default, cancellation.Token);
        try
        {
            await ((Task)(host.GetType().GetProperty("Entered", Members)?.GetValue(host) ?? throw new InvalidOperationException()))
                .WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = (ResourceGovernor)(typeof(ExecutionCounterTests).GetMethod("FindGovernor", Members)?.Invoke(null,
                [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)]) ?? throw new InvalidOperationException());
            if (outcome == "cancel") { cancellation.Cancel(); }
            else { host.GetType().GetMethod("Resume", Members)?.Invoke(host, []); }
            PdfMultiPageSelectionResult? result = null;
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
            else if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
            else { result = await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            int commits = outcome == "success" ? 2 : boundary == "output-close" ? second ? 2 : 1 : second ? 1 : 0;
            Assert.Equal(commits, (int)Field(host, "Commits"));
            for (int index = 0; index < prior.Count; index++)
            {
                Assert.Equal(new byte[] { 17, 23, 41 }, prior[index]);
                Assert.True(inner.TryGetOutput(QpdfPath.From($"output-{index + 1}.pdf"), out byte[] bytes));
                if (index < commits) { Assert.NotEqual(prior[index], bytes); }
                else { Assert.Equal(prior[index], bytes); }
            }
            Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            if (result is not null)
            {
                Assert.Equal(new[] { "output-1.pdf", "output-2.pdf" }, result.OutputPaths.Select(path => path.Value));
                Assert.Equal(new[] { 1, 1 }, result.SourcePageCounts);
                Assert.Equal(governor.Snapshot(), result.Counters);
            }
            return new(governor, result, new(host), new(result), new(result?.Counters));
        }
        finally
        {
            host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);
            try { try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { } }
            finally { GC.EndNoGCRegion(); }
        }
    }

    public static IEnumerable<object[]> SplitJobs()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (string boundary in new[] { "read", "input-close", "write", "commit", "output-close" })
        foreach (string outcome in new[] { "success", "fault", "cancel" })
        {
            yield return [compressed, boundary, outcome, false];
            if (boundary is "write" or "commit" or "output-close") { yield return [compressed, boundary, outcome, true]; }
        }
    }

    [Theory]
    [MemberData(nameof(SplitJobs))]
    [Trait("Layer", "Contract")]
    public async Task SplitJobsKeepPublishedAndPriorAliasesAcrossBothOutputs(bool compressed, string boundary, string outcome, bool second)
    {
        SplitCapture captured = await CompleteSplit(compressed, boundary, outcome, second);
        await Task.Yield();
        if (outcome == "success")
        {
            Alias alias = Keep(captured, "OutputPaths", false);
            if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
            QpdfExecutionCounters held = await CollectAsync(captured);
            long minimum = PacketMinimum(alias);
            Assert.False(captured.Host.IsAlive); Assert.False(captured.Result.IsAlive); Assert.False(captured.Counters.IsAlive);
            Drop(alias);
            QpdfExecutionCounters dropped = await CollectAsync(captured);
            Assert.False(alias.Value.IsAlive); Assert.False(alias.Backing.IsAlive);
            Assert.True(held.LiveMemoryBytes - dropped.LiveMemoryBytes >= minimum);
        }
        else
        {
            await CollectAsync(captured);
            Assert.False(captured.Host.IsAlive); Assert.False(captured.Result.IsAlive); Assert.False(captured.Counters.IsAlive);
        }
        Assert.Equal(0L, captured.Governor.Snapshot().OpenHandles);
        GC.KeepAlive(captured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Layer", "Contract")]
    public async Task AGeneratedSplitNameAloneOwnsItsStringAndReleasesSiblingNames(bool compressed)
    {
        SplitCapture captured = await CompleteSplit(compressed, "read", "success", false);
        await Task.Yield();
        Alias alias = KeepName(captured, out WeakReference sibling);
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        QpdfExecutionCounters held = await CollectAsync(captured);
        Assert.False(captured.Host.IsAlive); Assert.False(captured.Result.IsAlive); Assert.False(captured.Counters.IsAlive);
        Assert.False(sibling.IsAlive);
        long minimum = PacketMinimum(alias);
        Drop(alias);
        QpdfExecutionCounters dropped = await CollectAsync(captured);
        Assert.False(alias.Value.IsAlive);
        Assert.True(held.LiveMemoryBytes - dropped.LiveMemoryBytes >= minimum);
        GC.KeepAlive(alias);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias KeepName(SplitCapture captured, out WeakReference sibling)
    {
        PdfMultiPageSelectionResult result = (PdfMultiPageSelectionResult)(captured.Value ?? throw new InvalidOperationException());
        string name = result.OutputPaths[0].Value;
        sibling = new(result.OutputPaths[1].Value);
        captured.Value = name;
        return new(captured, new(name), new(null), Size(name));
    }

    [Theory]
    [InlineData("typed-page", false)]
    [InlineData("typed-page", true)]
    [InlineData("typed-pages", false)]
    [InlineData("typed-pages", true)]
    [Trait("Layer", "Contract")]
    public async Task CallerConstructorsCopyValueBackingWithoutBorrowingTheOriginalView(string route, bool compressed)
    {
        object captured = await Complete(route, compressed);
        await Task.Yield();
        Alias original = KeepCallerBacking(captured, route);
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(captured);
        Assert.False(original.Value.IsAlive); Assert.False(original.Backing.IsAlive);
        Assert.False(((WeakReference)Field(captured, "Host")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Result")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Counters")).IsAlive);
        Assert.Equal(route == "typed-page" ? new[] { 1 } : new[] { 1, 1 }, (IReadOnlyList<int>)Field(captured, "Value"));
        GC.KeepAlive(original);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias KeepCallerBacking(object captured, string route)
    {
        object result = Field(captured, "Value");
        IReadOnlyList<int> source, copy;
        if (result is PdfPageSelectionResult page)
        {
            source = page.SourcePages;
            copy = new PdfPageSelectionResult(page.SourcePageCount, page.SourcePages, page.PageCount, page.Warnings, page.Counters).SourcePages;
        }
        else
        {
            PdfMultiPageSelectionResult pages = (PdfMultiPageSelectionResult)result;
            source = pages.SourcePageCounts;
            copy = new PdfMultiPageSelectionResult(pages.PageCount, pages.Mapping, pages.SourcePageCounts, pages.OutputPaths, pages.Warnings, pages.Counters).SourcePageCounts;
        }
        Assert.NotSame(source, copy); Assert.Equal(source, copy);
        object backing = source.GetType().GetFields(Members).Select(field => field.GetValue(source)).OfType<Array>().Single();
        captured.GetType().GetField("Value", Members)?.SetValue(captured, copy);
        return new(captured, new(source), new(backing), Size(source) + Size(backing));
    }

    public static IEnumerable<object[]> FixedBudgetJobs()
        => TypedCounterOwnershipTests.BudgetJobs().Where(row => ((string)row[0]).StartsWith("typed-page", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(FixedBudgetJobs))]
    [Trait("Layer", "Contract")]
    public async Task OriginalShortBudgetsFailAfterReadingAndKeepPriorTargets(string route, bool compressed, string budget)
    {
        QpdfLimits limits = budget == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 2048L }
            : QpdfLimits.Default with { MaxWork = 128L };
        object captured = await Complete(route, compressed, "read", "budget", limits);
        await Task.Yield();
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        QpdfExecutionCounters counters = await CollectAsync(captured);
        Assert.False(((WeakReference)Field(captured, "Host")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Result")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Counters")).IsAlive);
        Assert.True(counters.PeakMemoryBytes <= limits.MaxMemoryBytes);
        Assert.True(counters.TotalWork <= limits.MaxWork);
        Assert.Equal(0L, counters.OpenHandles);
        GC.KeepAlive(captured);
    }

    private static IEnumerable<(string Method, IEnumerable<object[]> Rows)> OwnershipCases()
        => new (string, IEnumerable<object[]>)[]
        {
            (nameof(OriginalPublicAliasesRetireTheirIndependentBodyAndBacking), Aliases()),
            (nameof(MappingAliasesReleaseIndependentSiblingsAndDistinguishCallerCopies), Siblings()),
            (nameof(CompleteSelectionJobsKeepOriginalFaultCancellationAndPublicationControls), PublicFailures()),
            (nameof(SplitJobsKeepPublishedAndPriorAliasesAcrossBothOutputs), SplitJobs()),
            (nameof(AGeneratedSplitNameAloneOwnsItsStringAndReleasesSiblingNames), new object[][] { [false], [true] }),
            (nameof(CallerConstructorsCopyValueBackingWithoutBorrowingTheOriginalView), new object[][] { ["typed-page", false], ["typed-page", true], ["typed-pages", false], ["typed-pages", true] }),
            (nameof(OriginalShortBudgetsFailAfterReadingAndKeepPriorTargets), FixedBudgetJobs()),
        };

    public static IEnumerable<object[]> OwnershipBatches() => OwnershipMatrix.Batches(OwnershipCases());

    [Theory]
    [MemberData(nameof(OwnershipBatches))]
    [Trait("Layer", "Ownership")]
    public Task BatchedLifetimeObservationsPreserveEveryOriginalPhase(int batch)
        => OwnershipMatrix.RunAsync(_output, OwnershipCases(), batch,
            observation => new SelectionBackingOwnershipTests(_output) { _collection = observation });
}
