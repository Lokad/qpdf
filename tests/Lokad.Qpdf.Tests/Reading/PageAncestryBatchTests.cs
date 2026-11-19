using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class PageAncestryBatchTests(ITestOutputHelper output)
{
    private static readonly PdfName[] Keys = [PdfName.FromAscii("Resources"), PdfName.FromAscii("Rotate")];
    private static readonly FieldInfo WorkCounter = typeof(ResourceGovernor).GetField("_operationsSinceCheck",
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing work counter.");

    [Theory]
    [InlineData(1, 64)]
    [InlineData(24, 1)]
    [InlineData(24, 64)]
    public void SharedParentResolutionsFollowDepthAndKeepRawValues(int pages, int depth)
    {
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = long.MaxValue };
        ResourceGovernor individual = new(limits, CancellationToken.None);
        ResourceGovernor shared = new(limits, CancellationToken.None);
        Dictionary<(int, int, bool), QpdfObject> objects = new();
        PdfReference raw = new(500, 2);
        PdfInteger rotation = new("90", 90);
        for (int id = 1; id < depth; id++) { objects[(id, 0, false)] = Dict(("Parent", new PdfReference(id + 1, 0))); }
        objects[(depth, 0, false)] = Dict(("Resources", raw), ("Rotate", rotation));
        objects[(500, 2, false)] = Dict();
        CountingResolver ordinary = new(objects);
        CountingResolver reused = new(objects);
        using (PageInheritance.Batch? batch = PageInheritance.Batch.TryCreate(pages > 1, reused, Keys,
            limits, shared, CancellationToken.None))
        {
            for (int page = 0; page < pages; page++)
            {
                PageLocation leaf = Leaf(100 + page, new PdfReference(1, 0));
                var expected = PageInheritance.FindAncestors(ordinary, leaf, Keys, limits, individual, CancellationToken.None);
                var actual = batch?.Find(leaf) ?? PageInheritance.FindAncestors(reused, leaf, Keys, limits, shared, CancellationToken.None);
                Assert.Same(raw, expected[0]); Assert.Same(expected[0], actual[0]);
                Assert.Same(rotation, expected[1]); Assert.Same(expected[1], actual[1]);
            }
        }
        Assert.Equal(pages * (depth + 1), ordinary.Resolutions);
        Assert.Equal(depth + 1, reused.Resolutions);
        long before = ReadWork(individual), after = ReadWork(shared);
        if (pages > 1 && depth > 1) { Assert.InRange(after, 1L, before / 4L); }
        Assert.Equal(0L, shared.CounterValues.LiveMemoryBytes);
        output.WriteLine($"Pages {pages}, depth {depth}, parent resolutions {pages * depth} -> {depth}, work {before} -> {after}, shared peak {shared.CounterValues.PeakMemoryBytes}.");
    }

    [Fact]
    public void NearValuesOverlayAReusedSuffixAndAnAbsentValueExtendsToItsEnd()
    {
        PdfDictionary resources = Dict(), local = Dict(("Private", PdfBoolean.True));
        PdfInteger rotation = new("180", 180);
        PdfReference tail = new(8, 0);
        CountingResolver resolver = new(new Dictionary<(int, int, bool), QpdfObject>
        {
            [(8, 0, false)] = Dict(("Resources", resources)),
            [(9, 0, false)] = Dict(("Resources", PdfNull.Instance), ("Rotate", rotation), ("Parent", tail)),
            [(10, 0, false)] = Dict(("Resources", local), ("Parent", tail)),
        });
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using PageInheritance.Batch batch = Create(resolver, limits, governor, CancellationToken.None);
        var warm = batch.Find(Leaf(1, tail));
        Assert.Same(resources, warm[0]); Assert.Null(warm[1]);
        var inherited = batch.Find(Leaf(2, new PdfReference(9, 0)));
        Assert.Same(resources, inherited[0]); Assert.Same(rotation, inherited[1]);
        inherited = batch.Find(Leaf(3, new PdfReference(10, 0)));
        Assert.Same(local, inherited[0]); Assert.Null(inherited[1]);
        Assert.Equal(3, resolver.Resolutions);
    }

    [Fact]
    public void EqualNumbersWithDifferentGenerationsOrOriginsKeepTheirResolverMeaning()
    {
        PdfDictionary older = Dict(), live = Dict(("New", PdfBoolean.True)), original = Dict(("Physical", PdfBoolean.True));
        CountingResolver resolver = new(new Dictionary<(int, int, bool), QpdfObject>
        {
            [(7, 2, false)] = Dict(("Resources", older)),
            [(7, 3, false)] = Dict(("Resources", live)),
            [(7, 3, true)] = Dict(("Resources", original)),
        });
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using PageInheritance.Batch batch = Create(resolver, limits, governor, CancellationToken.None);
        Assert.Same(older, batch.Find(Leaf(1, new PdfReference(7, 2)))[0]);
        Assert.Same(live, batch.Find(Leaf(2, new PdfReference(7, 3)))[0]);
        Assert.Same(original, batch.Find(Leaf(3, new PdfReference(7, 3, true)))[0]);
        QpdfParseException failure = Assert.Throws<QpdfParseException>(() => batch.Find(Leaf(4, new PdfReference(7, 4))));
        Assert.Equal(7, failure.ObjectNumber);
        Assert.StartsWith("Unresolvable page parent.", failure.Message, StringComparison.Ordinal);
        Assert.Same(live, batch.Find(Leaf(5, new PdfReference(7, 3)))[0]);
        Assert.Equal(4, resolver.Resolutions);
    }

    [Fact]
    public void WarmSuffixCannotHideALeafCycleOrForceItOnAnEarlierHit()
    {
        PdfInteger rotation = new("90", 90);
        CountingResolver resolver = new(new Dictionary<(int, int, bool), QpdfObject>
        {
            [(7, 2, false)] = Dict(("Parent", new PdfReference(8, 1)), ("Rotate", rotation)),
            [(8, 1, false)] = Dict(("Resources", Dict())),
        });
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (PageInheritance.Batch batch = Create(resolver, limits, governor, CancellationToken.None))
        {
            PdfReference parent = new(7, 2);
            Assert.NotNull(batch.Find(Leaf(1, parent))[0]);
            PageLocation collision = new(new PdfReference(8, 1, true), Dict(("Parent", parent)));
            QpdfParseException failure = Assert.Throws<QpdfParseException>(() => batch.Find(collision));
            Assert.Equal(8, failure.ObjectNumber);
            Assert.StartsWith("Page ancestry contains a cycle.", failure.Message, StringComparison.Ordinal);
            PageLocation early = new(collision.Source, Dict(("Parent", parent), ("Resources", Dict())));
            Assert.Same(rotation, batch.Find(early)[1]);
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void WarmMissStillEnforcesDepthWithTheOriginalRequestedDepth()
    {
        PdfDictionary resources = Dict();
        CountingResolver resolver = new(new Dictionary<(int, int, bool), QpdfObject>
        {
            [(7, 0, false)] = Dict(("Parent", new PdfReference(8, 0)), ("Resources", resources)),
            [(8, 0, false)] = Dict(),
        });
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 2L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using PageInheritance.Batch batch = Create(resolver, limits, governor, CancellationToken.None);
        PdfReference parent = new(7, 0);
        Assert.Null(batch.Find(Leaf(1, parent))[1]);
        PdfDictionary indirectPrefix = Dict(("Parent", parent));
        PageLocation deeper = Leaf(2, indirectPrefix);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => batch.Find(deeper));
        Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), failure.LimitName);
        Assert.Equal(3L, failure.Requested);
        Assert.Equal(2L, failure.Allowed);
        var earlier = batch.Find(new(new PdfReference(3, 0), Dict(("Parent", indirectPrefix), ("Rotate", new PdfInteger("0", 0)))));
        Assert.Same(resources, earlier[0]);
    }

    [Fact]
    public void OptionalCacheRefusalFallsBackAndRefundsItsScratch()
    {
        PdfDictionary resources = Dict();
        CountingResolver resolver = new(new Dictionary<(int, int, bool), QpdfObject> { [(7, 0, false)] = Dict(("Resources", resources)) });
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 160L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (PageInheritance.Batch batch = Create(resolver, limits, governor, CancellationToken.None))
        {
            Assert.Same(resources, batch.Find(Leaf(1, new PdfReference(7, 0)))[0]);
            Assert.Same(resources, batch.Find(Leaf(2, new PdfReference(7, 0)))[0]);
            Assert.False(batch.Knows(7, 0));
        }
        Assert.Equal(2, resolver.Resolutions);
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothCancellationTokensInterruptWarmLookupAndRefundScratch(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, governorToken ? cancellation.Token : CancellationToken.None);
        CountingResolver resolver = new(new Dictionary<(int, int, bool), QpdfObject> { [(7, 0, false)] = Dict() });
        PdfReference parent = new(7, 0);
        using (PageInheritance.Batch batch = Create(resolver, limits, governor, governorToken ? CancellationToken.None : cancellation.Token))
        {
            Assert.Null(batch.Find(Leaf(1, parent))[0]);
            Assert.True(batch.Knows(7, 0));
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => batch.Find(Leaf(2, parent)));
        }
        Assert.Equal(1, resolver.Resolutions);
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private static PageLocation Leaf(int number, QpdfObject parent) => new(new PdfReference(number, 0), Dict(("Parent", parent)));

    private static PageInheritance.Batch Create(CountingResolver resolver, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
        => Assert.IsType<PageInheritance.Batch>(PageInheritance.Batch.TryCreate(true, resolver, Keys, limits, governor, token));

    private static long ReadWork(ResourceGovernor governor) => (long)(WorkCounter.GetValue(governor) ?? throw new InvalidOperationException());

    private sealed class CountingResolver(IReadOnlyDictionary<(int Number, int Generation, bool FromSource), QpdfObject> objects) : IIndirectResolver
    {
        internal int Resolutions { get; private set; }

        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            Resolutions++;
            return objects.TryGetValue((reference.ObjectNumber, reference.Generation, reference.IsFromSource), out value);
        }
    }
}
