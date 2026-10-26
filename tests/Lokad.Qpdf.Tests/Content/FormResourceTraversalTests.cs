using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormResourceTraversalTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] values)
    { Dictionary<PdfName, QpdfObject> entries = new(); foreach (var value in values) { entries.Add(PdfName.FromAscii(value.Key), value.Value); } return new(entries); }
    private static PdfStream Form(PdfDictionary resources) => new(Dictionary(("Subtype", PdfName.FromAscii("Form")), ("Resources", resources)), []);
    private static readonly Func<GraphCopier, PdfDictionary, ResourceGovernor, CancellationToken, FormResourceWalk> Collect =
        (typeof(ContentTransforms).GetMethod("CollectFormStreams", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("Missing form collector."))
        .CreateDelegate<Func<GraphCopier, PdfDictionary, ResourceGovernor, CancellationToken, FormResourceWalk>>();
    private static List<(int? Id, PdfStream Stream)> Read(FormResourceWalk result)
    {
        try
        {
            List<(int?, PdfStream)> items = new();
            for (int index = 0; index < result.Count; index++)
            { FormResourceWalk.Form form = result.Get(index); items.Add((form.Id, form.Stream)); }
            return items;
        }
        finally { result.Dispose(); }
    }
    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        private readonly ParserMemoryScope _copyMemory;
        internal readonly GraphCopier Copier;
        internal Prepared(ResourceGovernor governor, IReadOnlyDictionary<int, QpdfObject> values)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None); PdfDictionary trailer = Dictionary();
            using ParserMemoryScope fixtureStorage = ParserMemoryScope.Create(construction);
            Dictionary<int, XrefEntry> fixtureEntries = ParserContainers.CreateDictionary<int, XrefEntry>(fixtureStorage);
            using ParserMemoryScope fixtureValues = ParserMemoryScope.Create(construction);
            List<PdfDictionary> fixtureTrailers = ParserContainers.CreateList<PdfDictionary>(fixtureStorage, 1, IntPtr.Size);
            fixtureTrailers.Add(trailer);
            XRefTable fixtureTable = XRefTable.CreateOwned(0L, fixtureEntries, trailer, fixtureTrailers,
                construction, fixtureStorage, fixtureValues, false);
            DocumentEngine fixtureEngine;
            try
            {
                fixtureStorage.Reserve(32L);
                fixtureEngine = DocumentEngine.CreateOwned(ReadOnlyMemory<byte>.Empty, fixtureTable, QpdfLimits.Default,
                    construction, new DiagnosticBag(construction), QpdfRecoveryMode.Strict, 32L);
                fixtureStorage.Retain();
            }
            catch { fixtureTable.Dispose(); throw; }
            _engine = fixtureEngine;
            _copyMemory = ParserMemoryScope.Create(governor);
            Copier = GraphCopier.CreateOwned(_engine, governor, QpdfLimits.Default, 1, _copyMemory);
            IDictionary<int, QpdfObject> pool = (IDictionary<int, QpdfObject>)Copier.CopiedObjects;
            foreach (var value in values) { pool.Add(value.Key, value.Value); }
        }
        public void Dispose() { _engine.Dispose(); _copyMemory.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectFormsRepeatWhileIndirectObjectNumbersAreVisitedOnce(bool indirect)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); PdfStream form = Form(Dictionary());
        QpdfObject first = indirect ? new PdfReference(10, 0) : form, second = indirect ? new PdfReference(10, 7) : form;
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject> { [10] = form }); long baseline = governor.Snapshot().LiveMemoryBytes;
        List<(int? Id, PdfStream Stream)> forms = Read(Collect(prepared.Copier, Dictionary(("XObject", Dictionary(("A", first), ("B", second)))), governor, CancellationToken.None));
        Assert.Equal(indirect ? 1 : 2, forms.Count); Assert.All(forms, found => Assert.Same(form, found.Stream));
        Assert.Equal(indirect ? 10 : (int?)null, forms[0].Id); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ScopeOrderIsBreadthFirstAndSharedIndirectFormsAreVisitedOnce()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfStream child = Form(Dictionary()), sibling = Form(Dictionary());
        PdfDictionary firstScope = Dictionary(("XObject", Dictionary(("Child", new PdfReference(30, 0)))));
        PdfDictionary equalScope = Dictionary(("XObject", Dictionary(("Child", new PdfReference(30, 0)))));
        PdfStream first = Form(firstScope), equal = Form(equalScope), last = Form(Dictionary(("XObject", Dictionary(("Sibling", new PdfReference(40, 0))))));
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject> { [30] = child, [40] = sibling }); long baseline = governor.Snapshot().LiveMemoryBytes;
        List<(int? Id, PdfStream Stream)> forms = Read(Collect(prepared.Copier, Dictionary(("XObject", Dictionary(("First", first), ("Equal", equal), ("Last", last)))), governor, CancellationToken.None));
        Assert.Equal(5, forms.Count); Assert.Same(first, forms[0].Stream); Assert.Same(equal, forms[1].Stream); Assert.Same(last, forms[2].Stream);
        Assert.Same(child, forms[3].Stream); Assert.Same(sibling, forms[4].Stream); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void SharedAndCyclicResourcesStopAtTheirGraphIdentities()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary scope = Dictionary(("XObject", Dictionary(("Self", new PdfReference(10, 0)), ("Again", new PdfReference(10, 7)))));
        PdfStream form = Form(scope);
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject> { [10] = form }); long baseline = governor.Snapshot().LiveMemoryBytes;
        List<(int? Id, PdfStream Stream)> forms = Read(Collect(prepared.Copier, scope, governor, CancellationToken.None));
        Assert.Single(forms); Assert.Equal(10, forms[0].Id); Assert.Same(form, forms[0].Stream);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void DistinctEqualScopesRetainTheirDirectFormOccurrences()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfStream child = Form(Dictionary());
        PdfDictionary first = Dictionary(("XObject", Dictionary(("Child", child))));
        PdfDictionary equal = Dictionary(("XObject", Dictionary(("Child", child))));
        PdfStream firstForm = Form(first), equalForm = Form(equal);
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject>()); long baseline = governor.Snapshot().LiveMemoryBytes;
        List<(int? Id, PdfStream Stream)> forms = Read(Collect(prepared.Copier,
            Dictionary(("XObject", Dictionary(("First", firstForm), ("Equal", equalForm)))), governor, CancellationToken.None));
        Assert.Equal(4, forms.Count); Assert.Same(firstForm, forms[0].Stream); Assert.Same(equalForm, forms[1].Stream);
        Assert.Same(child, forms[2].Stream); Assert.Same(child, forms[3].Stream);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("wrong")]
    public void AbsentAndNonDictionaryResourceSectionsStayEmpty(string shape)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor, new Dictionary<int, QpdfObject>()); long baseline = governor.Snapshot().LiveMemoryBytes;
        PdfDictionary resources = shape == "missing" ? Dictionary() : Dictionary(("XObject", shape == "null" ? PdfNull.Instance : PdfBoolean.True));
        Assert.Empty(Read(Collect(prepared.Copier, resources, governor, CancellationToken.None))); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FailedWalkAdmissionPreservesAnEarlierReceiptAndBorrowedResources()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject>()); long baseline = governor.Snapshot().LiveMemoryBytes; PdfDictionary resources = Dictionary();
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor); filler.Reserve(8192L - governor.Snapshot().LiveMemoryBytes - 1L);
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => Collect(prepared.Copier, resources, governor, CancellationToken.None)); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        filler.Dispose(); Assert.Empty(Read(Collect(prepared.Copier, resources, governor, CancellationToken.None))); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothPreCanceledContextsStopBeforeTraversalAllocations(bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject>()); long baseline = governor.Snapshot().LiveMemoryBytes; source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Collect(prepared.Copier, Dictionary(), governor, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(baseline, governor.Snapshot().PeakMemoryBytes);     }

    private delegate QpdfObject? Resolve(PoolResolver resolver, PdfReference reference, ResourceGovernor governor, CancellationToken token);
    private static Resolve BindResolve()
    {
        MethodInfo method = typeof(ContentTransforms).GetMethod("ResolvePool", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException();
        return method.CreateDelegate<Resolve>();
    }
    private static readonly Resolve ResolveBorrowed = BindResolve();
    private sealed class Observed(IReadOnlyDictionary<int, QpdfObject> inner, Action observe) : IReadOnlyDictionary<int, QpdfObject>
    {
        public int Count => inner.Count; public IEnumerable<int> Keys => inner.Keys; public IEnumerable<QpdfObject> Values => inner.Values;
        public QpdfObject this[int key] => inner[key]; public bool ContainsKey(int key) => inner.ContainsKey(key);
        public bool TryGetValue(int key, [MaybeNullWhen(false)] out QpdfObject value) { observe(); return inner.TryGetValue(key, out value); }
        public IEnumerator<KeyValuePair<int, QpdfObject>> GetEnumerator() => inner.GetEnumerator(); IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterResolverReturnPreventsPublishingTheBorrowedValue(bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        QpdfObject value = Dictionary(); int calls = 0;
        PoolResolver resolver = new(new Observed(new Dictionary<int, QpdfObject> { [20] = value }, () => { calls++; source.Cancel(); }));
        Assert.ThrowsAny<OperationCanceledException>(() => ResolveBorrowed(resolver, new PdfReference(20, 7), governor, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(1, calls); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
