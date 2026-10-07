using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SharedResourceHeuristicTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
    { Dictionary<PdfName, QpdfObject> values = new(); foreach (var entry in entries) { values.Add(PdfName.FromAscii(entry.Key), entry.Value); } return new(values); }
    private static DocumentEngine Engine(PdfDictionary trailer, ResourceGovernor governor, IReadOnlyDictionary<int, QpdfObject> objects)
    {
        XRefTable table = new(0L, new Dictionary<int, XrefEntry>(), trailer, new List<PdfDictionary> { trailer });
        DocumentEngine engine = new(ReadOnlyMemory<byte>.Empty, table, QpdfLimits.Default, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict);
        engine.SetInspectionObjects(objects); return engine;
    }
    private static bool Decide(DocumentEngine engine, ResourceGovernor governor, CancellationToken token)
        => ContentTransforms.SharedResourcesHeuristic(engine, QpdfLimits.Default, governor, token);

    [Theory]
    [InlineData("missing-root", false)]
    [InlineData("null-root", false)]
    [InlineData("wrong-root", false)]
    [InlineData("missing-pages", false)]
    [InlineData("null-pages", false)]
    [InlineData("wrong-pages", false)]
    [InlineData("leaf-empty", false)]
    [InlineData("branch-resources", true)]
    [InlineData("indirect-kids", false)]
    [InlineData("shared-resource", true)]
    [InlineData("shared-xobjects", true)]
    [InlineData("repeated-direct-resource", false)]
    [InlineData("generation-duplicate-resource", true)]
    [InlineData("generation-node-order", false)]
    public void SharedResourceDecisionPreservesShapesIdentityAndGenerationTreatment(string shape, bool expected)
    {
        PdfDictionary empty = Dictionary(); PdfReference resources = new(20, 0), xobjects = new(21, 0);
        Dictionary<int, QpdfObject> objects = new() { [20] = empty, [21] = empty };
        QpdfObject page = Dictionary();
        if (shape == "branch-resources") { page = Dictionary(("Kids", new PdfArray([])), ("Resources", empty)); }
        if (shape == "indirect-kids") { page = Dictionary(("Kids", new PdfReference(30, 0))); objects[30] = new PdfArray([Dictionary(("Resources", empty))]); }
        if (shape is "shared-resource" or "generation-duplicate-resource")
        { page = Dictionary(("Kids", new PdfArray([Dictionary(("Resources", resources)), Dictionary(("Resources", shape == "shared-resource" ? resources : new PdfReference(20, 7)))]))); }
        if (shape == "shared-xobjects")
        { page = Dictionary(("Kids", new PdfArray([Dictionary(("Resources", Dictionary(("XObject", xobjects)))), Dictionary(("Resources", Dictionary(("XObject", xobjects))))]))); }
        if (shape == "repeated-direct-resource")
        { page = Dictionary(("Kids", new PdfArray([Dictionary(("Resources", empty)), Dictionary(("Resources", empty))]))); }
        if (shape == "generation-node-order")
        { objects[31] = Dictionary(("Kids", new PdfArray([])), ("Resources", empty)); page = Dictionary(("Kids", new PdfArray([new PdfReference(31, 0), new PdfReference(31, 7)]))); }
        PdfDictionary catalog = shape == "missing-pages" ? empty : Dictionary(("Pages", shape == "null-pages" ? PdfNull.Instance : shape == "wrong-pages" ? PdfBoolean.True : page));
        PdfDictionary trailer = shape == "missing-root" ? empty : Dictionary(("Root", shape == "null-root" ? PdfNull.Instance : shape == "wrong-root" ? PdfBoolean.True : catalog));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = Engine(trailer, governor, objects); long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Equal(expected, Decide(engine, governor, CancellationToken.None)); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectFormResourcesAreTraversedAndIndirectNodeCyclesStop(bool cycle)
    {
        PdfReference form = new(30, 0); PdfDictionary resources = Dictionary(("XObject", Dictionary(("Fm", form))));
        PdfDictionary page = Dictionary(("Resources", resources));
        PdfDictionary formDictionary = cycle ? Dictionary(("Subtype", PdfName.FromAscii("Form")), ("Resources", resources))
            : Dictionary(("Subtype", PdfName.FromAscii("Form")), ("Kids", new PdfArray([])), ("Resources", Dictionary()));
        PdfStream stream = new(formDictionary, new byte[2 * 1024 * 1024]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = Engine(Dictionary(("Root", Dictionary(("Pages", page)))), governor, new Dictionary<int, QpdfObject> { [30] = stream });
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Equal(!cycle, Decide(engine, governor, CancellationToken.None)); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(2 * 1024 * 1024, stream.Data.Length);
    }

    [Theory]
    [InlineData(63L)]
    [InlineData(127L)]
    [InlineData(511L)]
    public void InitialWalkAdmissionFailurePreservesEnginePriorReceiptAndRetry(long available)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using DocumentEngine engine = Engine(Dictionary(("Root", Dictionary(("Pages", Dictionary())))), governor, new Dictionary<int, QpdfObject>());
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor); filler.Reserve(4096L - governor.Snapshot().LiveMemoryBytes - available);
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Decide(engine, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
        filler.Dispose(); Assert.False(Decide(engine, governor, CancellationToken.None)); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WideRootOrPageMetadataExhaustsWorkWithoutChangingBorrowedValues(bool pageMetadata)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int index = 0; index < 512; index++) { entries.Add(PdfName.FromAscii("other" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)), PdfNull.Instance); }
        PdfDictionary wide = new(entries), trailer = pageMetadata ? Dictionary(("Root", Dictionary(("Pages", wide)))) : wide;
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = 64L }, CancellationToken.None);
        using DocumentEngine engine = Engine(trailer, governor, new Dictionary<int, QpdfObject>()); long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Decide(engine, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, wide.Count);
    }

    [Fact]
    public void WideChildrenRequireAdmittedQueueCapacityBeforeCompletingTheWalk()
    {
        PdfArray children = new(Enumerable.Repeat<QpdfObject>(PdfNull.Instance, 1024).ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using DocumentEngine engine = Engine(Dictionary(("Root", Dictionary(("Pages", Dictionary(("Kids", children)))))), governor, new Dictionary<int, QpdfObject>());
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Decide(engine, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(1024, children.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherPreCanceledContextStopsBeforeMetadataAndOwnerAllocation(bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using DocumentEngine engine = Engine(Dictionary(), governor, new Dictionary<int, QpdfObject>());
        long live = governor.Snapshot().LiveMemoryBytes, peak = governor.Snapshot().PeakMemoryBytes; source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Decide(engine, governor, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(peak, governor.Snapshot().PeakMemoryBytes); Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    private sealed class ObservedObjects(IReadOnlyDictionary<int, QpdfObject> inner, Action observe) : IReadOnlyDictionary<int, QpdfObject>
    {
        public int Count => inner.Count; public IEnumerable<int> Keys => inner.Keys; public IEnumerable<QpdfObject> Values => inner.Values;
        public QpdfObject this[int key] => inner[key]; public bool ContainsKey(int key) => inner.ContainsKey(key);
        public bool TryGetValue(int key, [MaybeNullWhen(false)] out QpdfObject value) { observe(); return inner.TryGetValue(key, out value); }
        public IEnumerator<KeyValuePair<int, QpdfObject>> GetEnumerator() => inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolverReturnCancellationPreventsAnEarlyPositiveDecision(bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        int calls = 0; ObservedObjects objects = new(new Dictionary<int, QpdfObject> { [30] = Dictionary(("Kids", new PdfArray([])), ("Resources", Dictionary())) }, () => { calls++; source.Cancel(); });
        using DocumentEngine engine = Engine(Dictionary(("Root", Dictionary(("Pages", Dictionary(("Kids", new PdfArray([new PdfReference(30, 0)]))))))), governor, objects);
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => Decide(engine, governor, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(1, calls); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.True(governor.Snapshot().TotalWork > 0L);
    }

    [Fact]
    public void RepeatedCompletedWalksReleaseScratchUnderTheSameFiniteMemoryCeiling()
    {
        PdfDictionary leaf = Dictionary(("Resources", new PdfReference(20, 0)));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using DocumentEngine engine = Engine(Dictionary(("Root", Dictionary(("Pages", Dictionary(("Kids", new PdfArray([leaf, leaf]))))))), governor, new Dictionary<int, QpdfObject> { [20] = Dictionary() });
        long live = governor.Snapshot().LiveMemoryBytes;
        for (int index = 0; index < 32; index++) { Assert.True(Decide(engine, governor, CancellationToken.None)); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); }
    }

    private delegate bool FormPredicate(PdfStream stream, ResourceGovernor governor, CancellationToken token);
    private static FormPredicate BindForm()
    {
        MethodInfo method = typeof(ContentTransforms).GetMethod("IsFormXObject", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing form classifier.");
        if (method.GetParameters().Length == 1)
        { Func<PdfStream, bool> legacy = method.CreateDelegate<Func<PdfStream, bool>>(); return (stream, governor, token) => legacy(stream); }
        return method.CreateDelegate<FormPredicate>();
    }
    private static readonly FormPredicate IsForm = BindForm();

    [Theory]
    [InlineData("Form", true)]
    [InlineData("form", false)]
    [InlineData("Form#00", false)]
    [InlineData("binary", false)]
    [InlineData("missing", false)]
    public void FormClassificationPreservesRawNameTruth(string shape, bool expected)
    {
        PdfName subtype = shape == "binary" ? new PdfName([0, 255, 35]) : PdfName.FromAscii(shape);
        PdfStream stream = new(shape == "missing" ? Dictionary() : Dictionary(("Subtype", subtype)), new byte[2 * 1024 * 1024]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(expected, IsForm(stream, governor, CancellationToken.None)); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void WarmedFormClassificationCreatesNoLookupNameDisplayStringOrPayloadCopy()
    {
        PdfStream stream = new(Dictionary(("Subtype", PdfName.FromAscii("Form"))), new byte[2 * 1024 * 1024]); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        for (int index = 0; index < 32; index++) { IsForm(stream, governor, CancellationToken.None); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 128; index++) { IsForm(stream, governor, CancellationToken.None); }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(stream);
    }
}
