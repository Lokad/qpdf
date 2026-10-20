using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class LinearizedCollectionOwnershipTests
{
    private static List<NumberedObject> Model(int additional)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        for (int index = 0; index < additional; index++) { model.Add(new NumberedObject(index + 6, PdfNull.Instance)); }
        return model;
    }

    private static IDisposable Classify(string route, List<NumberedObject> model,
        Dictionary<int, QpdfObject> lookup, ParserMemoryScope memory, ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        return route switch
        {
            "graph" => LinearizedParts.ClassifyGraph(lookup, 1, memory, governor, cancellationToken),
            "classic" => LinearizedParts.Classify(model, memory, governor, cancellationToken),
            "streamed" => LinearizedParts.ClassifyStreamed(model, new IReadOnlyList<int>[] { new[] { 5 } },
                memory, governor, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
    }

    [Theory]
    [InlineData("graph", 63L)]
    [InlineData("graph", 255L)]
    [InlineData("graph", 2048L)]
    [InlineData("graph", 8192L)]
    [InlineData("classic", 63L)]
    [InlineData("classic", 255L)]
    [InlineData("classic", 2048L)]
    [InlineData("classic", 8192L)]
    [InlineData("streamed", 63L)]
    [InlineData("streamed", 255L)]
    [InlineData("streamed", 2048L)]
    [InlineData("streamed", 8192L)]
    public void AdmissionFailurePreservesCallerStorageAndCanRetry(string route, long available)
    {
        List<NumberedObject> model = Model(512);
        Dictionary<int, QpdfObject> lookup = model.ToDictionary(item => item.Number, item => item.Value);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2L * 1024L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<int> prior = ParserContainers.CreateList<int>(memory);
        ParserContainers.AddItem(memory, prior, 17, sizeof(int));
        ParserContainers.AddItem(memory, prior, 23, sizeof(int));
        long priorBytes = memory.ReservedBytes;
        long baseline = governor.Snapshot().LiveMemoryBytes;
        long filler = limits.MaxMemoryBytes - baseline - available;
        governor.ReserveMemory(filler);
        QpdfExecutionCounters before = governor.Snapshot();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            Classify(route, model, lookup, memory, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(before.LiveMemoryBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(priorBytes, memory.ReservedBytes);
        Assert.Equal(new[] { 17, 23 }, prior);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, baseline, limits.MaxMemoryBytes);
        if (available == 63L) { }
        governor.ReleaseMemory(filler);
        using (ParserMemoryScope retryMemory = ParserMemoryScope.Create(governor))
        {
            using IDisposable result = Classify(route, model, lookup, retryMemory, governor, CancellationToken.None);
            Assert.True(retryMemory.ReservedBytes > 512L * sizeof(int));
            Assert.Equal(new[] { 17, 23 }, prior);
        }
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(model[3].Value, lookup[4]);
        Assert.Equal("BT (Hi) Tj ET"u8.ToArray(), ((PdfStream)lookup[4]).Data);
    }

    [Fact]
    public void CancellationAfterObservedGroupReadsReturnsPlanningStorage()
    {
        List<NumberedObject> model = Model(512);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(97L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        using CancellationTokenSource cancellation = new();
        CancelingGroups groups = new(cancellation);
        Assert.ThrowsAny<OperationCanceledException>(() => LinearizedParts.ClassifyStreamed(model, groups,
            memory, governor, cancellation.Token));
        Assert.Equal(32, groups.Reads);
        Assert.True(governor.Snapshot().PeakMemoryBytes > baseline + 8192L);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(97L, memory.ReservedBytes);
    }

    private sealed class CancelingGroups(CancellationTokenSource cancellation) : IReadOnlyList<IReadOnlyList<int>>
    {
        internal int Reads { get; private set; }
        public int Count => 512;
        public IReadOnlyList<int> this[int index]
        {
            get
            {
                Reads++;
                if (Reads == 32) { cancellation.Cancel(); }
                return new[] { index + 6 };
            }
        }
        public IEnumerator<IReadOnlyList<int>> GetEnumerator() => throw new InvalidOperationException("Use indexed group reads.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("classic")]
    [InlineData("streamed")]
    public void DisposedWrappersDoNotKeepTheirRetiredCollections(string route)
    {
        List<NumberedObject> model = Model(512);
        Dictionary<int, QpdfObject> lookup = model.ToDictionary(item => item.Number, item => item.Value);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            (IDisposable result, WeakReference[] collections) = PreparedCollections(route, model, lookup, memory, governor);
            CollectFully();
            Assert.All(collections, item => Assert.True(item.IsAlive));
            Assert.True(memory.ReservedBytes > 512L * sizeof(int));
            result.Dispose();
            memory.Dispose();
            CollectFully();
            Assert.All(collections, item => Assert.False(item.IsAlive));
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal("BT (Hi) Tj ET"u8.ToArray(), ((PdfStream)lookup[4]).Data);
            GC.KeepAlive(result);
            GC.KeepAlive(model);
            GC.KeepAlive(lookup);
        }
        finally { memory.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IDisposable Result, WeakReference[] Collections) PreparedCollections(string route,
        List<NumberedObject> model, Dictionary<int, QpdfObject> lookup, ParserMemoryScope memory, ResourceGovernor governor)
    {
        IDisposable result = Classify(route, model, lookup, memory, governor, CancellationToken.None);
        string[] names = ["Part4", "Part6", "Part9", "Pages", "PageUsers", "Renumber", "ContainerMembers", "Members"];
        List<WeakReference> collections = new();
        foreach (string name in names)
        {
            PropertyInfo? property = result.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (property?.GetValue(result) is object value) { collections.Add(new WeakReference(value)); }
        }
        Assert.True(collections.Count >= 5);
        return (result, collections.ToArray());
    }

    private static void CollectFully()
    {
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("classic")]
    [InlineData("streamed")]
    public void ThirtyTwoClassificationsRetireUnderTheOriginalThirtyTwoKiBCeiling(string route)
    {
        List<NumberedObject> model = Model(64);
        Dictionary<int, QpdfObject> lookup = model.ToDictionary(item => item.Number, item => item.Value);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        for (int cycle = 0; cycle < 32; cycle++)
        {
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            {
                using IDisposable result = Classify(route, model, lookup, memory, governor, CancellationToken.None);
                Assert.True(memory.ReservedBytes > 64L * sizeof(int));
            }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        Assert.Equal("BT (Hi) Tj ET"u8.ToArray(), ((PdfStream)lookup[4]).Data);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("classic")]
    [InlineData("streamed")]
    public void ForeignGovernorRejectsBeforeConsumingEitherBudget(string route)
    {
        List<NumberedObject> model = Model(0);
        Dictionary<int, QpdfObject> lookup = model.ToDictionary(item => item.Number, item => item.Value);
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor foreign = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner);
        memory.Reserve(97L);
        Assert.Throws<InvalidOperationException>(() => Classify(route, model, lookup, memory, foreign, CancellationToken.None));
        Assert.Equal(97L, memory.ReservedBytes);
        Assert.Equal(161L, owner.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, foreign.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void BorrowedLiteralLookupDoesNotFormatLongUnknownNames()
    {
        PdfName unknown = new(Enumerable.Repeat((byte)35, 4096).ToArray());
        PdfReference root = new(1, 0);
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject>
        {
            [unknown] = PdfNull.Instance,
            [PdfName.FromAscii("Root")] = root,
        });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.True(GovernedCollections.TryGet(dictionary, "Root"u8, governor, CancellationToken.None, out QpdfObject warm));
        Assert.Same(root, warm);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool found = true;
        for (int index = 0; index < 64; index++)
        {
            found &= GovernedCollections.TryGet(dictionary, "Root"u8, governor, CancellationToken.None, out QpdfObject value)
                && ReferenceEquals(root, value);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(found);
        Assert.Equal(0L, allocated);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        PdfDictionary empty = new(new Dictionary<PdfName, QpdfObject>());
        Assert.ThrowsAny<OperationCanceledException>(() =>
            GovernedCollections.TryGet(empty, "Root"u8, governor, cancellation.Token, out _));
    }
}
