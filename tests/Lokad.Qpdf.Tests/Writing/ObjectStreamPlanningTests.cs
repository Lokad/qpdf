using System.Collections;
using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamPlanningTests
{
    internal static byte[] Fixture(int count)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] /Resources << /Extra ["
                + string.Join(' ', Enumerable.Range(4, count).Select(i => i + " 0 R")) + "] >> >>", null) ];
        for (int number = 4; number < count + 4; number++) { objects.Add((number, "<< /V " + number + " >>", null)); }
        return CheckTests.Assemble(objects, 1);
    }

    private delegate HashSet<int> ResolveCall(string mode, IReadOnlyDictionary<int, QpdfObject> pool,
        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> sources,
        ParserMemoryScope owner, ResourceGovernor governor, CancellationToken token);
    private delegate List<List<int>> GroupCall(HashSet<int> numbers, ParserMemoryScope owner, ResourceGovernor governor, CancellationToken token);
    private delegate HashSet<int> RemapCall(HashSet<int> numbers, Dictionary<int, int> map, ParserMemoryScope owner, ResourceGovernor governor, CancellationToken token);
    private delegate HashSet<int> CollectCall(DocumentEngine engine, bool preserve, ParserMemoryScope owner, ResourceGovernor governor, CancellationToken token);
    private static T Live<T>(string name) where T : Delegate => (typeof(Qpdf).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(name)).CreateDelegate<T>();
    private static readonly ResolveCall Resolve = Live<ResolveCall>("ResolveCompressedSet");
    private static readonly GroupCall Group = Live<GroupCall>("PlanObjectStreamGroups");
    private static readonly RemapCall Remap = Live<RemapCall>("RemapCompressedSet");
    private static readonly CollectCall Collect = Live<CollectCall>("CollectCompressedOriginals");

    private static DocumentEngine Engine(int count)
    {
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        Dictionary<int, XrefEntry> entries = Enumerable.Range(3, count).ToDictionary(i => i,
            i => i % 2 == 0 ? XrefEntry.Compressed(2, i) : XrefEntry.Uncompressed(i, 0));
        return new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, entries, trailer, [trailer]),
            QpdfLimits.Default, construction, new(construction), QpdfRecoveryMode.Strict);
    }

    [Theory]
    [InlineData("resolve")]
    [InlineData("collect")]
    [InlineData("group")]
    [InlineData("remap")]
    public void LargePreparationRefusesBeforeAllocatingAndRefundsItsCandidate(string phase)
    {
        HashSet<int> numbers = Enumerable.Range(3, 262144).ToHashSet();
        Dictionary<int, QpdfObject> pool = numbers.ToDictionary(i => i, _ => (QpdfObject)PdfNull.Instance);
        Dictionary<int, int> map = numbers.ToDictionary(i => i, i => i + 10);
        using DocumentEngine engine = Engine(262144);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() =>
        {
            switch (phase)
            {
                case "resolve": Resolve("generate", pool, [], owner, governor, CancellationToken.None); break;
                case "collect": Collect(engine, true, owner, governor, CancellationToken.None); break;
                case "group": Group(numbers, owner, governor, CancellationToken.None); break;
                case "remap": Remap(numbers, map, owner, governor, CancellationToken.None); break;
            }
        });
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0L, 65536L);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, owner.ReservedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(201)]
    [InlineData(1001)]
    public void GroupingPreservesSortedBalancedRowsAndRetainsOnlyReturnedCapacity(int count)
    {
        HashSet<int> numbers = Enumerable.Range(3, count).Reverse().ToHashSet();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        List<List<int>> groups;
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            groups = Group(numbers, owner, governor, CancellationToken.None);
            int perStream = count == 0 ? 0 : 1 + (count - 1) / (1 + (count - 1) / 100);
            Assert.Equal(Enumerable.Range(3, count), groups.SelectMany(row => row));
            Assert.All(groups, row => Assert.InRange(row.Count, 1, perStream));
            Assert.All(groups.SkipLast(1), row => Assert.Equal(perStream, row.Count));
            long expected = 32L + (groups.Capacity == 0 ? 0 : 32L + IntPtr.Size * (long)groups.Capacity)
                + groups.Sum(row => 64L + sizeof(int) * (long)row.Capacity);
            Assert.Equal(expected, owner.ReservedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(Enumerable.Range(3, count), groups.SelectMany(row => row));
    }

    [Fact]
    public void ModesKeepMappedCandidatesAndExcludeStreamsAndSignedDictionaries()
    {
        PdfDictionary signed = new(new Dictionary<PdfName, QpdfObject> {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Sig"),
            [PdfName.FromAscii("ByteRange")] = new PdfArray([]),
            [PdfName.FromAscii("Contents")] = new PdfByteString([1], true) });
        Dictionary<int, QpdfObject> pool = new() {
            [1] = PdfNull.Instance, [2] = PdfNull.Instance, [3] = PdfNull.Instance,
            [4] = new PdfStream(new(new Dictionary<PdfName, QpdfObject>()), []),
            [5] = signed, [6] = new PdfInteger("6", 6), [7] = PdfNull.Instance };
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            Assert.Empty(Resolve("disable", pool, [], owner, governor, CancellationToken.None));
            Assert.Equal(new[] { 3, 6, 7 }, Resolve("generate", pool, [], owner, governor, CancellationToken.None).Order());
            List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> sources = [
                (new Dictionary<int, int> { [10] = 3, [11] = 5, [12] = 6, [13] = 99 }, [10, 11, 12, 13, 14]),
                (new Dictionary<int, int> { [20] = 3 }, [20]) ];
            HashSet<int> preserved = Resolve("preserve", pool, sources, owner, governor, CancellationToken.None);
            Assert.Equal(new[] { 3, 6 }, preserved.Order());
            Assert.Equal(new[] { 42 }, Remap(preserved, new() { [6] = 42 }, owner, governor, CancellationToken.None));
            long before = owner.ReservedBytes;
            Assert.Throws<QpdfParseException>(() => Resolve("broken", pool, [], owner, governor, CancellationToken.None));
            Assert.Equal(before, owner.ReservedBytes);
            using DocumentEngine engine = Engine(8);
            Assert.Empty(Collect(engine, false, owner, governor, CancellationToken.None));
            Assert.Equal(new[] { 4, 6, 8, 10 }, Collect(engine, true, owner, governor, CancellationToken.None).Order());
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingPool(Action cancel) : IReadOnlyDictionary<int, QpdfObject>
    {
        public int Count => 128;
        public IEnumerable<int> Keys => throw new NotSupportedException();
        public IEnumerable<QpdfObject> Values => throw new NotSupportedException();
        public QpdfObject this[int key] => throw new NotSupportedException();
        public bool ContainsKey(int key) => throw new NotSupportedException();
        public bool TryGetValue(int key, out QpdfObject value) => throw new NotSupportedException();
        public IEnumerator<KeyValuePair<int, QpdfObject>> GetEnumerator()
        {
            for (int number = 3; number < Count + 3; number++)
            {
                if (number == 70) { cancel(); }
                yield return new(number, PdfNull.Instance);
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringCandidateConstructionRefundsWithoutTransferring(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<OperationCanceledException>(() => Resolve("generate", new CancelingPool(cancellation.Cancel), [], owner,
            governor, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, owner.ReservedBytes);
    }
}
