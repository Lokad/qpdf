using System.Diagnostics;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class RemappedModelOwnershipTests
{
    private static (GovernedIntDictionary<QpdfObject> Source, GovernedIntDictionary<int> Numbers) Index(
        ParserMemoryScope memory, ResourceGovernor governor, QpdfObject value)
    {
        GovernedIntDictionary<QpdfObject> source = GovernedIntDictionary<QpdfObject>.Create(
            memory, governor, IntPtr.Size, CancellationToken.None);
        GovernedIntDictionary<int> numbers = GovernedIntDictionary<int>.Create(
            memory, governor, sizeof(int), CancellationToken.None);
        source.Assign(memory, 1, value);
        numbers.Assign(memory, 1, 7);
        return (source, numbers);
    }

    [Fact]
    public void RebuiltMetadataBorrowsImmutablePayloadsKeysAndScalars()
    {
        byte[] input = new byte[1024 * 1024];
        input[0] = 73;
        PdfName key = new([0, 35, 255]);
        PdfInteger scalar = new("0042", 42);
        PdfStream stream = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [key] = new PdfArray([new PdfReference(1, 13), scalar])
        }), input);
        input[0] = 99;
        byte[] publicCopy = stream.Data;
        publicCopy[0] = 11;
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        (GovernedIntDictionary<QpdfObject> source, GovernedIntDictionary<int> numbers) = Index(memory, governor, stream);
        long prior = governor.Snapshot().LiveMemoryBytes;
        using RemappedModel mapped = RemappedModel.Create(source, numbers, governor, CancellationToken.None);
        Assert.Equal(1, mapped.Count);
        Assert.True(mapped.TryGetValue(7, out QpdfObject? value));
        PdfStream result = Assert.IsType<PdfStream>(value);
        Assert.NotSame(stream, result);
        Assert.NotSame(stream.Dictionary, result.Dictionary);
        Assert.Same(stream.AllocationCarrier, result.AllocationCarrier);
        Assert.Same(key, Assert.Single(result.Dictionary.Keys));
        Assert.True(result.Dictionary.TryGet(key, out QpdfObject? items));
        PdfArray array = Assert.IsType<PdfArray>(items);
        PdfReference reference = Assert.IsType<PdfReference>(array[0]);
        Assert.Equal(7, reference.ObjectNumber);
        Assert.Equal(0, reference.Generation);
        Assert.Same(scalar, array[1]);
        Assert.Equal(73, result.RawMemory.Span[0]);
        byte[] returned = result.Data;
        returned[0] = 12;
        Assert.Equal(73, stream.RawMemory.Span[0]);
        Assert.Equal(73, result.RawMemory.Span[0]);
        Assert.True(governor.Snapshot().LiveMemoryBytes > prior);
        mapped.Dispose();
        Assert.Equal(prior, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0, mapped.Count);
        Assert.Throws<ObjectDisposedException>(() => mapped.TryGetValue(7, out _));
        Assert.Equal(7, numbers[1]);
        Assert.Same(stream, source[1]);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("depth")]
    public void FailedConstructionPreservesPriorOwnerAndBorrowedGraph(string failure)
    {
        QpdfObject root = new PdfArray(Enumerable.Repeat<QpdfObject>(new PdfReference(1, 0), 2048).ToArray());
        if (failure == "depth")
        {
            root = PdfNull.Instance;
            for (int depth = 0; depth < 32; depth++) { root = new PdfArray([root]); }
        }
        QpdfLimits limits = QpdfLimits.Default with
        {
            MaxMemoryBytes = 32L * 1024L,
            MaxNestingDepth = failure == "depth" ? 16L : QpdfLimits.Default.MaxNestingDepth
        };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(97L);
        (GovernedIntDictionary<QpdfObject> source, GovernedIntDictionary<int> numbers) = Index(memory, governor, root);
        long prior = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            RemappedModel.Create(source, numbers, governor, CancellationToken.None));
        Assert.Equal(failure == "memory" ? nameof(QpdfLimits.MaxMemoryBytes)
            : nameof(QpdfLimits.MaxNestingDepth), error.LimitName);
        Assert.Equal(prior, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, prior + 1L, limits.MaxMemoryBytes);
        Assert.True(source.Count == 1 && numbers.Count == 1);
        if (failure != "work")
        {
            Assert.Same(root, source[1]);
            source.Assign(memory, 1, PdfNull.Instance);
            using RemappedModel retry = RemappedModel.Create(source, numbers, governor, CancellationToken.None);
            Assert.True(retry.TryGetValue(7, out QpdfObject? value));
            Assert.Same(PdfNull.Instance, value);
            retry.Dispose();
        }
        memory.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("missing-object")]
    [InlineData("missing-reference")]
    [InlineData("duplicate-number")]
    public void InvalidMappingRollsBackOnlyItsAttempt(string failure)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        QpdfObject root = failure == "missing-reference" ? new PdfReference(99, 4) : PdfNull.Instance;
        (GovernedIntDictionary<QpdfObject> source, GovernedIntDictionary<int> numbers) = Index(memory, governor, root);
        if (failure == "missing-object") { numbers.Remove(1); }
        if (failure == "duplicate-number")
        {
            source.Assign(memory, 2, PdfBoolean.True);
            numbers.Assign(memory, 2, 7);
        }
        long prior = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfException>(() => RemappedModel.Create(source, numbers, governor, CancellationToken.None));
        Assert.Equal(prior, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(root, source[1]);
        numbers.Assign(memory, 1, 7);
        if (failure == "missing-reference") { numbers.Assign(memory, 99, 99); }
        if (failure == "duplicate-number") { numbers.Assign(memory, 2, 8); }
        using RemappedModel retry = RemappedModel.Create(source, numbers, governor, CancellationToken.None);
        Assert.True(retry.Count > 0);
    }

    [Fact]
    public void ForeignIndexRejectsBeforeWorkOrAdmission()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor foreign = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using ParserMemoryScope otherMemory = ParserMemoryScope.Create(foreign);
        (GovernedIntDictionary<QpdfObject> source, _) = Index(memory, governor, PdfNull.Instance);
        (_, GovernedIntDictionary<int> numbers) = Index(otherMemory, foreign, PdfNull.Instance);
        QpdfExecutionCounters before = governor.Snapshot();
        QpdfExecutionCounters otherBefore = foreign.Snapshot();
        Assert.Throws<InvalidOperationException>(() => RemappedModel.Create(source, numbers, governor, CancellationToken.None));
        Assert.Equal(before, governor.Snapshot());
        Assert.Equal(otherBefore, foreign.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterObservedRebuildingWorkRetiresAttempt(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default,
            governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfArray root = new(Enumerable.Repeat<QpdfObject>(new PdfReference(1, 0), 512).ToArray());
        (GovernedIntDictionary<QpdfObject> source, GovernedIntDictionary<int> numbers) = Index(memory, governor, root);
        long prior = governor.Snapshot().LiveMemoryBytes;
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = Task.Run(() =>
        {
            while (true)
            {
                using RemappedModel mapped = RemappedModel.Create(source, numbers, governor,
                    governorToken ? CancellationToken.None : cancellation.Token);
                observed.TrySetResult();
            }
        });
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(prior, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(512, root.Count);
            Assert.Equal(1, Assert.IsType<PdfReference>(root[0]).ObjectNumber);
        }
        finally
        {
            cancellation.Cancel();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IDisposable Owner, WeakReference Header, WeakReference Items) Rebuild(
        GovernedIntDictionary<QpdfObject> source, GovernedIntDictionary<int> numbers, ResourceGovernor governor)
    {
        RemappedModel owner = RemappedModel.Create(source, numbers, governor, CancellationToken.None);
        Assert.True(owner.TryGetValue(7, out QpdfObject? value));
        PdfStream stream = Assert.IsType<PdfStream>(value);
        Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Items"), out QpdfObject? array));
        return (owner, new WeakReference(stream.Dictionary.AllocationCarrier),
            new WeakReference(Assert.IsType<PdfArray>(array).AllocationCarrier));
    }

    [Fact]
    public void RetainedDisposedOwnerDropsRebuiltStorageAndPreservesBorrowedPayload()
    {
        PdfStream original = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Items")] = new PdfArray([new PdfReference(1, 2)])
        }), new byte[1024 * 1024]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        (GovernedIntDictionary<QpdfObject> source, GovernedIntDictionary<int> numbers) = Index(memory, governor, original);
        long prior = governor.Snapshot().LiveMemoryBytes;
        (IDisposable owner, WeakReference header, WeakReference items) = Rebuild(source, numbers, governor);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.True(header.IsAlive);
        Assert.True(items.IsAlive);
        Assert.True(governor.Snapshot().LiveMemoryBytes > prior);
        owner.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(header.IsAlive);
        Assert.False(items.IsAlive);
        Assert.Equal(prior, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1024 * 1024, original.RawMemory.Length);
        GC.KeepAlive(owner);
        GC.KeepAlive(original);
    }
}
