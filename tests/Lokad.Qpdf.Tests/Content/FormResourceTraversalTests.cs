using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormResourceTraversalTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] values)
    { Dictionary<PdfName, QpdfObject> entries = new(); foreach (var value in values) { entries.Add(PdfName.FromAscii(value.Key), value.Value); } return new(entries); }
    private static PdfStream Form(PdfDictionary resources) => new(Dictionary(("Subtype", PdfName.FromAscii("Form")), ("Resources", resources)), []);
    private static readonly Func<GraphCopier, PdfDictionary, ResourceGovernor, CancellationToken, object> Collect =
        (typeof(ContentTransforms).GetMethod("CollectFormStreams", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("Missing form collector."))
        .CreateDelegate<Func<GraphCopier, PdfDictionary, ResourceGovernor, CancellationToken, object>>();
    private static List<(int? Id, PdfStream Stream)> Read(object result)
    {
        try
        {
            List<(int?, PdfStream)> items = new();
            if (result is IList legacy)
            {
                foreach (object item in legacy) { ITuple tuple = (ITuple)item; items.Add(((int?)tuple[0], (PdfStream)(tuple[1] ?? throw new InvalidOperationException()))); }
            }
            else
            {
                Type type = result.GetType(); BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                int count = (int)(type.GetProperty("Count", flags)?.GetValue(result) ?? throw new InvalidOperationException());
                MethodInfo get = type.GetMethod("Get", flags) ?? throw new InvalidOperationException();
                for (int index = 0; index < count; index++)
                {
                    object item = get.Invoke(result, [index]) ?? throw new InvalidOperationException();
                    items.Add(((int?)item.GetType().GetProperty("Id")?.GetValue(item), (PdfStream)(item.GetType().GetProperty("Stream")?.GetValue(item) ?? throw new InvalidOperationException())));
                }
            }
            return items;
        }
        finally { (result as IDisposable)?.Dispose(); }
    }
    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        internal readonly GraphCopier Copier;
        internal Prepared(ResourceGovernor governor, IReadOnlyDictionary<int, QpdfObject> values)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None); PdfDictionary trailer = Dictionary();
            _engine = new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]), QpdfLimits.Default, construction, new DiagnosticBag(construction), QpdfRecoveryMode.Strict);
            Copier = new(_engine, governor, QpdfLimits.Default, 1);
            IDictionary<int, QpdfObject> pool = (IDictionary<int, QpdfObject>)Copier.CopiedObjects;
            foreach (var value in values) { pool.Add(value.Key, value.Value); }
        }
        public void Dispose() => _engine.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectFormsRepeatWhileIndirectObjectNumbersAreVisitedOnce(bool indirect)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); PdfStream form = Form(Dictionary());
        QpdfObject first = indirect ? new PdfReference(10, 0) : form, second = indirect ? new PdfReference(10, 7) : form;
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject> { [10] = form });
        List<(int? Id, PdfStream Stream)> forms = Read(Collect(prepared.Copier, Dictionary(("XObject", Dictionary(("A", first), ("B", second)))), governor, CancellationToken.None));
        Assert.Equal(indirect ? 1 : 2, forms.Count); Assert.All(forms, found => Assert.Same(form, found.Stream));
        Assert.Equal(indirect ? 10 : (int?)null, forms[0].Id); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ScopeOrderIsBreadthFirstAndDistinctEqualScopesAreDeduplicated()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfStream child = Form(Dictionary()), sibling = Form(Dictionary());
        PdfDictionary firstScope = Dictionary(("XObject", Dictionary(("Child", new PdfReference(30, 0)))));
        PdfDictionary equalScope = Dictionary(("XObject", Dictionary(("Child", new PdfReference(30, 0)))));
        PdfStream first = Form(firstScope), equal = Form(equalScope), last = Form(Dictionary(("XObject", Dictionary(("Sibling", new PdfReference(40, 0))))));
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject> { [30] = child, [40] = sibling });
        List<(int? Id, PdfStream Stream)> forms = Read(Collect(prepared.Copier, Dictionary(("XObject", Dictionary(("First", first), ("Equal", equal), ("Last", last)))), governor, CancellationToken.None));
        Assert.Equal(5, forms.Count); Assert.Same(first, forms[0].Stream); Assert.Same(equal, forms[1].Stream); Assert.Same(last, forms[2].Stream);
        Assert.Same(child, forms[3].Stream); Assert.Same(sibling, forms[4].Stream); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("wrong")]
    public void AbsentAndNonDictionaryResourceSectionsStayEmpty(string shape)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor, new Dictionary<int, QpdfObject>());
        PdfDictionary resources = shape == "missing" ? Dictionary() : Dictionary(("XObject", shape == "null" ? PdfNull.Instance : PdfBoolean.True));
        Assert.Empty(Read(Collect(prepared.Copier, resources, governor, CancellationToken.None))); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FailedWalkAdmissionPreservesAnEarlierReceiptAndBorrowedResources()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject>()); PdfDictionary resources = Dictionary();
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor); filler.Reserve(8192L - governor.Snapshot().LiveMemoryBytes - 1L);
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => Collect(prepared.Copier, resources, governor, CancellationToken.None)); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        filler.Dispose(); Assert.Empty(Read(Collect(prepared.Copier, resources, governor, CancellationToken.None))); Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothPreCanceledContextsStopBeforeTraversalAllocations(bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using Prepared prepared = new(governor, new Dictionary<int, QpdfObject>()); source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Collect(prepared.Copier, Dictionary(), governor, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);     }

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
