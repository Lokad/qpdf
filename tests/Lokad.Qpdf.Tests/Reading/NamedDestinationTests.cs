using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class NamedDestinationTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));
    private static PdfByteString Text(string text) => new(Encoding.ASCII.GetBytes(text), false);
    private static PdfReference Reference(int number) => new(number, 0);
    private static PdfArray Destination(int marker) => new([new PdfInteger(marker.ToString(System.Globalization.CultureInfo.InvariantCulture), marker), PdfName.FromAscii("Fit")]);
    private static QpdfLimits Limits => QpdfLimits.Default with { MaxMemoryBytes = 32768, MaxNestingDepth = 32, MaxContainerSize = 32 };

    private sealed class Resolver : IIndirectResolver
    {
        internal readonly Dictionary<(int Number, int Generation), QpdfObject> Values = [];
        internal int Reads;
        internal int MaximumReads = 256;
        internal Action<int>? Observe;
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            Reads++; Assert.InRange(Reads, 1, MaximumReads); Observe?.Invoke(Reads);
            return Values.TryGetValue((reference.ObjectNumber, reference.Generation), out value);
        }
    }

    private static (PdfDictionary Catalog, Resolver Resolver) Tree(int depth, bool shared, bool laterHit)
    {
        Resolver resolver = new();
        for (int level = 0; level <= depth; level++)
        {
            QpdfObject[] kids = shared ? [Reference(level + 2), Reference(level + 2)] : [Reference(level + 2)];
            resolver.Values[(level + 1, 0)] = level == depth
                ? Dictionary(("Names", new PdfArray([Text("Other"), Destination(99)])))
                : Dictionary(("Kids", new PdfArray(kids)));
        }
        if (laterHit)
        {
            resolver.Values[(0, 0)] = Dictionary(("Kids", new PdfArray([Reference(1), Reference(1), Reference(201), Reference(202)])));
            resolver.Values[(201, 0)] = Dictionary(("Names", new PdfArray([Text("A"), Destination(11)])));
            resolver.Values[(202, 0)] = Dictionary(("Names", new PdfArray([Text("B"), Destination(22)])));
        }
        return (Dictionary(("Names", Dictionary(("Dests", Reference(laterHit ? 0 : 1))))), resolver);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(16)]
    public void SharedMissesStayProportionalToTheGraphAndRepeatedQueriesReuseThem(int depth)
    {
        int chain = Measure(depth, false), shared = Measure(depth, true);
        Assert.InRange(shared, 1, chain * 3);
    }

    private static int Measure(int depth, bool shared)
    {
        var tree = Tree(depth, shared, false); ResourceGovernor governor = new(Limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            NamedDestinations lookup = NamedDestinations.Create(tree.Resolver, tree.Catalog, memory, governor, CancellationToken.None);
            Assert.Null(lookup.Find(Text("Missing"))); int reads = tree.Resolver.Reads;
            Assert.Null(lookup.Find(Text("Missing"))); Assert.Equal(reads, tree.Resolver.Reads);
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes); return tree.Resolver.Reads;
    }

    [Fact]
    public void MissSummariesKeepLaterHitsAndDifferentKeysIndependent()
    {
        var tree = Tree(12, true, true); ResourceGovernor governor = new(Limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        NamedDestinations lookup = NamedDestinations.Create(tree.Resolver, tree.Catalog, memory, governor, CancellationToken.None);
        Assert.Null(lookup.Find(Text("Missing")));
        Assert.Equal(11L, Assert.IsType<PdfInteger>(Assert.IsType<PdfArray>(lookup.Find(Text("A")))[0]).Value);
        Assert.Equal(22L, Assert.IsType<PdfInteger>(Assert.IsType<PdfArray>(lookup.Find(Text("B")))[0]).Value);
        int reads = tree.Resolver.Reads;
        Assert.NotNull(lookup.Find(Text("A"))); Assert.NotNull(lookup.Find(Text("B")));
        Assert.Equal(reads, tree.Resolver.Reads);
    }

    [Fact]
    public void FirstMatchesAndExplicitNullsPrecedeUnneededChildren()
    {
        PdfArray first = Destination(11), second = Destination(22);
        PdfDictionary Leaf(QpdfObject value) => Dictionary(("Names", new PdfArray([Text("A"), value])));
        PdfDictionary Catalog(PdfDictionary tree) => Dictionary(("Names", Dictionary(("Dests", tree))));
        ResourceGovernor governor = new(Limits, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        NamedDestinations siblings = NamedDestinations.Create(new Resolver(), Catalog(Dictionary(("Kids", new PdfArray([Leaf(first), Leaf(second)])))), memory, governor, CancellationToken.None);
        Assert.Same(first, siblings.Find(Text("A")));
        NamedDestinations nulls = NamedDestinations.Create(new Resolver(), Catalog(Dictionary(("Names", new PdfArray([Text("A"), PdfNull.Instance])),
            ("Kids", new PdfArray([new PdfInteger("42", 42)])))), memory, governor, CancellationToken.None);
        Assert.Same(PdfNull.Instance, nulls.Find(Text("A")));
    }

    [Fact]
    public void QueryCacheIsBoundedAndEvictionDoesNotChangeResults()
    {
        var tree = Tree(0, false, false); ResourceGovernor governor = new(Limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            NamedDestinations lookup = NamedDestinations.Create(tree.Resolver, tree.Catalog, memory, governor, CancellationToken.None);
            for (int index = 0; index < 32; index++) { Assert.Null(lookup.Find(Text("Missing" + index))); }
            long before = governor.CounterValues.LiveMemoryBytes;
            Assert.Null(lookup.Find(Text("Missing32"))); Assert.Equal(before, governor.CounterValues.LiveMemoryBytes);
            int reads = tree.Resolver.Reads;
            Assert.Null(lookup.Find(Text("Missing32"))); Assert.Equal(reads, tree.Resolver.Reads);
            Assert.Null(lookup.Find(Text("Missing0"))); Assert.True(tree.Resolver.Reads > reads);
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void KindsExactBytesWrappersAndGenerationPairsKeepTheirMeaning()
    {
        Resolver resolver = new(); PdfArray first = Destination(11), second = Destination(22);
        resolver.Values[(1, 0)] = Dictionary(("Names", new PdfArray([Text("A"), new PdfReference(70, 3)])));
        resolver.Values[(70, 3)] = Dictionary(("D", new PdfReference(71, 4))); resolver.Values[(71, 4)] = second;
        resolver.Values[(72, 2)] = first;
        PdfDictionary catalog = Dictionary(("Dests", Dictionary(("A", new PdfReference(72, 2)), ("Stale", new PdfReference(72, 1)))),
            ("Names", Dictionary(("Dests", Reference(1)))));
        ResourceGovernor governor = new(Limits, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        NamedDestinations lookup = NamedDestinations.Create(resolver, catalog, memory, governor, CancellationToken.None);
        Assert.Same(first, lookup.Find(PdfName.FromAscii("A"))); Assert.Same(second, lookup.Find(Text("A")));
        Assert.Null(lookup.Find(new PdfByteString([65, 0], false))); Assert.Null(lookup.Find(PdfName.FromAscii("Stale")));
        int reads = resolver.Reads;
        Assert.Same(first, lookup.Find(PdfName.FromAscii("A"))); Assert.Same(second, lookup.Find(Text("A")));
        Assert.Equal(reads, resolver.Reads);
    }

    private sealed class EqualityGuard(Action observe) : QpdfObject
    {
        public override bool Equals(object? other) { observe(); return other is EqualityGuard; }
        public override int GetHashCode() => 0;
    }

    [Fact]
    public void IdentityIndexesNeverCompareUnneededPrivateValues()
    {
        int comparisons = 0;
        PdfDictionary Leaf() => Dictionary(("Names", new PdfArray([])), ("Audit", new EqualityGuard(() => comparisons++)));
        PdfDictionary first = Leaf(), second = Leaf();
        PdfDictionary catalog = Dictionary(("Names", Dictionary(("Dests", Dictionary(("Kids", new PdfArray([first, second])))))));
        ResourceGovernor governor = new(Limits, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        NamedDestinations lookup = NamedDestinations.Create(new Resolver(), catalog, memory, governor, CancellationToken.None);
        Assert.Null(lookup.Find(Text("Missing"))); Assert.Equal(0, comparisons);
        Assert.True(first.Equals(second)); Assert.True(comparisons > 0);
    }

    [Fact]
    public void ReusedMissesStillEnforceDepthOnADeeperPath()
    {
        Resolver resolver = new();
        resolver.Values[(1, 0)] = Dictionary(("Kids", new PdfArray([Reference(2), Reference(3)])));
        resolver.Values[(2, 0)] = Dictionary(("Kids", new PdfArray([Reference(5)])));
        resolver.Values[(3, 0)] = Dictionary(("Kids", new PdfArray([Reference(4)])));
        resolver.Values[(4, 0)] = Dictionary(("Kids", new PdfArray([Reference(2)])));
        resolver.Values[(5, 0)] = Dictionary(("Kids", new PdfArray([Reference(6)])));
        resolver.Values[(6, 0)] = Dictionary(("Names", new PdfArray([])));
        ResourceGovernor governor = new(Limits with { MaxNestingDepth = 4 }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        NamedDestinations lookup = NamedDestinations.Create(resolver, Dictionary(("Names", Dictionary(("Dests", Reference(1))))), memory, governor, CancellationToken.None);
        long before = governor.CounterValues.LiveMemoryBytes;
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => lookup.Find(Text("Missing")));
        Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), error.LimitName); Assert.Equal(before, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void ActiveCyclesRemainErrorsAndRefundTraversalStorage()
    {
        Resolver resolver = new(); resolver.Values[(1, 0)] = Dictionary(("Kids", new PdfArray([Reference(1)])));
        ResourceGovernor governor = new(Limits, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        NamedDestinations lookup = NamedDestinations.Create(resolver, Dictionary(("Names", Dictionary(("Dests", Reference(1))))), memory, governor, CancellationToken.None);
        long before = governor.CounterValues.LiveMemoryBytes;
        Assert.Contains("cycle", Assert.Throws<QpdfParseException>(() => lookup.Find(Text("Missing"))).Message, StringComparison.Ordinal);
        Assert.Equal(before, governor.CounterValues.LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherTokenCancelsAfterTraversalStartsAndRefundsItsIndex(bool caller)
    {
        var tree = Tree(16, true, false); using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(Limits, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        NamedDestinations lookup = NamedDestinations.Create(tree.Resolver, tree.Catalog, memory, governor, caller ? cancellation.Token : CancellationToken.None);
        tree.Resolver.Observe = reads => { if (reads == 5) { cancellation.Cancel(); } };
        long before = governor.CounterValues.LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => lookup.Find(Text("Missing")));
        Assert.Equal(5, tree.Resolver.Reads); Assert.Equal(before, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void EveryAdmissionRefusalRefundsLookupAndTraversalStorage()
    {
        int Run(RecordingMemoryBudget root)
        {
            using IMemoryScope caller = root.CreateScope(); using InMemoryQpdfHost storage = new();
            ResourceGovernor governor = new(Limits, CancellationToken.None, new MemoryBudgetHost(storage, caller));
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            var tree = Tree(16, true, true); NamedDestinations lookup = NamedDestinations.Create(tree.Resolver, tree.Catalog, memory, governor, CancellationToken.None);
            Assert.Null(lookup.Find(Text("Missing"))); Assert.NotNull(lookup.Find(Text("A")));
            return root.GrowthCalls;
        }
        RecordingMemoryBudget baseline = new(1L << 20); int growths = Run(baseline); Assert.InRange(growths, 1, 128);
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int point = 1; point <= growths; point++)
        {
            RecordingMemoryBudget root = new(1L << 20) { RefuseOnGrowth = point };
            Assert.Throws<QpdfMemoryAdmissionException>(() => Run(root)); Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
        }
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("placement")]
    public async Task LaterDestinationsSurviveSelectionAndPlacement(string route)
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output"), source = QpdfPath.From("source");
        host.AddInput(input, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        host.AddInput(source, SharedSource(), QpdfInputOwnership.Transfer);
        if (route == "typed") { await Qpdf.SelectPagesAsync(host, [new(input, new(1)), new(source, new(1))], output, CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(route == "argv"
                ? ["--empty", "--pages", "source", "1", "--", "output"] : ["input", "--overlay", "source", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(output, out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        var page = document.PageReferences[route == "typed" ? 1 : 0];
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Read(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
        PdfArray annotations = Assert.IsType<PdfArray>(Read(page.Page, "Annots")); PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(Assert.Single(annotations.Items)));
        PdfDictionary action = Assert.IsType<PdfDictionary>(Read(annotation, "A")); PdfArray destination = Assert.IsType<PdfArray>(Read(action, "D"));
        if (route == "placement") { Assert.IsType<PdfNull>(destination[0]); } else { Assert.Equal(page.Source, destination[0]); }
        Assert.Equal("Fit", Assert.IsType<PdfName>(destination[1]).DisplayText);
    }

    private static byte[] SharedSource()
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 5 0 R >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Link /Border [0 0 0] /Rect [0 0 10 10] /A << /S /GoTo /D (Hit) >> >>", null)];
        for (int level = 0; level <= 16; level++)
        {
            int next = 6 + level;
            objects.Add((5 + level, level == 16 ? "<< /Names [(Other) [3 0 R /Fit]] >>"
                : "<< /Kids [" + next + " 0 R " + next + " 0 R" + (level == 0 ? " 22 0 R" : "") + "] >>", null));
        }
        objects.Add((22, "<< /Names [(Hit) [3 0 R /Fit]] >>", null));
        return PdfTestDocument.Assemble(objects, 1);
    }
}
