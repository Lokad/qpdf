using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Objects;

public sealed class GraphReachabilityTests
{
    private sealed class Resolver(Func<PdfReference, QpdfObject?> resolve) : IIndirectResolver
    {
        internal int Calls;
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { Calls++; value = resolve(reference); return value is not null; }
    }

    private static PdfDictionary Dictionary(bool frozen, params (string Key, QpdfObject Value)[] entries)
    {
        if (!frozen) { return new(entries.ToDictionary(e => PdfName.FromAscii(e.Key), e => e.Value)); }
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(construction);
        PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, construction, IntPtr.Size, CancellationToken.None);
        foreach (var entry in entries) { names.Assign(memory, construction, PdfName.FromAscii(entry.Key), entry.Value, CancellationToken.None); }
        names.Freeze(memory, construction, CancellationToken.None);
        return PdfDictionary.FromOwnedEntries(names);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WideBorrowedContainersResolveRepeatedReferenceWithinFourKiB(bool dictionary)
    {
        const int count = 262144;
        PdfReference reference = new(7, 0);
        QpdfObject root;
        if (dictionary)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope memory = ParserMemoryScope.Create(construction);
            PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, construction, IntPtr.Size, CancellationToken.None);
            for (int index = 0; index < count; index++)
            { names.Assign(memory, construction, PdfName.FromAscii("K" + index), reference, CancellationToken.None); }
            names.Freeze(memory, construction, CancellationToken.None); root = PdfDictionary.FromOwnedEntries(names);
        }
        else { root = new PdfArray(Enumerable.Repeat<QpdfObject>(reference, count).ToArray()); }
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        Resolver resolver = new(_ => PdfNull.Instance); HashSet<int> found;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            found = GraphReachability.Find(resolver, root, memory, governor);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0L, 65536L);
            Assert.Equal(new[] { 7 }, found); Assert.Equal(1, resolver.Calls);
            Assert.Equal(96L + 64L + 16L * found.EnsureCapacity(0), memory.ReservedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(new[] { 7 }, found);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReverseDepthFirstOrderAndGenerationAwareCyclesRemainStable(bool frozen)
    {
        List<(int, int)> visits = [];
        Dictionary<(int, int), QpdfObject> graph = new() {
            [(1, 0)] = Dictionary(frozen, ("A", new PdfReference(3, 0)), ("B", new PdfReference(4, 0))),
            [(2, 0)] = new PdfReference(1, 0),
            [(2, 1)] = new PdfStream(Dictionary(frozen, ("Nested", new PdfReference(5, 0))), []),
            [(3, 0)] = new PdfReference(1, 0), [(4, 0)] = PdfNull.Instance,
            [(5, 0)] = new PdfArray([new PdfReference(2, 1)]) };
        Resolver resolver = new(reference => {
            var key = (reference.ObjectNumber, reference.Generation); visits.Add(key);
            return graph.GetValueOrDefault(key); });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            HashSet<int> found = GraphReachability.Find(resolver, new PdfArray([
                new PdfReference(1, 0), new PdfReference(2, 0), new PdfReference(2, 1), new PdfReference(9, 0)]), memory, governor);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, found.Order());
            Assert.Equal(new[] { (9, 0), (2, 1), (5, 0), (2, 0), (1, 0), (4, 0), (3, 0) }, visits);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void DistinctReferencesRefuseWithoutTransferringPartialResults()
    {
        PdfArray root = new(Enumerable.Range(1, 262144).Select(i => (QpdfObject)new PdfReference(i, 0)).ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => GraphReachability.Find(new Resolver(_ => PdfNull.Instance), root, memory, governor));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0L, 65536L);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAndMalformedResolutionRefundScratchAndPartialResults(bool malformed)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes;
        Resolver resolver = new(reference => {
            if (reference.ObjectNumber == 1) { return new PdfArray([new PdfReference(2, 0)]); }
            if (malformed) { throw new QpdfParseException("Malformed referenced object.", null, null); }
            cancellation.Cancel(); return PdfNull.Instance; });
        Action traverse = () => GraphReachability.Find(resolver, new PdfReference(1, 0), memory, governor);
        if (malformed) { Assert.Throws<QpdfParseException>(traverse); }
        else { Assert.Throws<OperationCanceledException>(traverse); }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, memory.ReservedBytes);
    }
}
