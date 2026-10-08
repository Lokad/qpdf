using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class ContentPlanTests
{
    private delegate object? ReadCall(GraphCopier copier, PdfDictionary page, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token);
    private static readonly MethodInfo ReadMethod = typeof(ContentTransforms).GetMethod("ReadPageContents", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException();
    private static readonly ReadCall? Read = ReadMethod.GetParameters().Length == 5 ? ReadMethod.CreateDelegate<ReadCall>() : null;
    private static readonly MethodInfo IdMethod = typeof(ContentTransforms).GetMethod("ReplacedContentIds", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException();

    // The preceding private reader takes copier/page/diagnostics only; its
    // records are read through the same property names. The ID helper takes
    // only the prepared value. Inputs, budgets and assertions stay identical.
    private static object? InvokeRead(GraphCopier copier, PdfDictionary page, ResourceGovernor governor, DiagnosticBag diagnostics)
    {
        if (Read is not null) { return Read(copier, page, governor, diagnostics, CancellationToken.None); }
        return Invoke(ReadMethod, [copier, page, diagnostics]);
    }
    private static object? Invoke(MethodInfo method, object?[] arguments)
    {
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static List<int> InvokeIds(QpdfObject? value, ParserMemoryScope memory, ResourceGovernor governor)
        => (IdMethod.GetParameters().Length == 4 ? Invoke(IdMethod, [value, memory, governor, CancellationToken.None]) : Invoke(IdMethod, [value])) as List<int> ?? throw new InvalidOperationException();
    private static int Count(object entries)
        => (int)(entries.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(entries) ?? throw new InvalidOperationException());
    private static (int? Id, int Index, PdfStream Stream) Get(object entries, int index)
    {
        object entry = entries is IList list ? list[index] ?? throw new InvalidOperationException()
            : Invoke(entries.GetType().GetMethod("Get", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException(), [index], entries) ?? throw new InvalidOperationException();
        Type type = entry.GetType();
        return ((int?)type.GetProperty("ObjectId")?.GetValue(entry), (int)(type.GetProperty("ArrayIndex")?.GetValue(entry) ?? throw new InvalidOperationException()),
            type.GetProperty("Stream")?.GetValue(entry) as PdfStream ?? throw new InvalidOperationException());
    }
    private static object? Invoke(MethodInfo method, object?[] arguments, object receiver)
    {
        try { return method.Invoke(receiver, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Close(object? entries)
    { if (entries is IDisposable owner) { owner.Dispose(); } else if (entries is IList list) { list.Clear(); } }
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        internal readonly GraphCopier Copier;
        internal readonly PdfStream Stream;
        internal readonly ResourceGovernor Construction = new(QpdfLimits.Default, CancellationToken.None);
        internal Prepared()
        {
            PdfDictionary trailer = Dictionary();
            _engine = new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]), QpdfLimits.Default,
                Construction, new DiagnosticBag(Construction), QpdfRecoveryMode.Strict);
            Copier = new(_engine, Construction, QpdfLimits.Default, 1);
            Stream = new(Dictionary(), new byte[2 * 1024 * 1024]);
            ((IDictionary<int, QpdfObject>)Copier.CopiedObjects)[7] = Stream;
            ((IDictionary<int, QpdfObject>)Copier.CopiedObjects)[8] = PdfNull.Instance;
        }
        public void Dispose() { _engine.Dispose(); }
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("reference")]
    [InlineData("mixed")]
    [InlineData("empty")]
    public void PlanBorrowsStreamsAndPreservesIdsIndicesAndGenerationTreatment(string shape)
    {
        using Prepared prepared = new();
        QpdfObject contents = shape switch
        { "direct" => prepared.Stream, "reference" => new PdfReference(7, 11), "mixed" => new PdfArray([prepared.Stream, new PdfReference(7, 11), prepared.Stream]), _ => new PdfArray([]) };
        PdfDictionary page = Dictionary(("Contents", contents));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        object? entries = InvokeRead(prepared.Copier, page, governor, new(prepared.Construction));
        try
        {
            Assert.NotNull(entries); int count = shape == "mixed" ? 3 : shape == "empty" ? 0 : 1;
            Assert.Equal(count, Count(entries)); Assert.True(governor.Snapshot().LiveMemoryBytes >= 160L);
            for (int index = 0; index < count; index++)
            {
                var entry = Get(entries, index); Assert.Same(prepared.Stream, entry.Stream); Assert.Equal(index, entry.Index);
                Assert.Equal(shape == "reference" || shape == "mixed" && index == 1 ? 7 : (int?)null, entry.Id);
            }
            Assert.Equal(1, page.Count);
        }
        finally { Close(entries); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("dictionary")]
    public void AbsentNullAndUnsupportedSingleContentsStaySilentWithoutAnOwner(string shape)
    {
        using Prepared prepared = new();
        PdfDictionary page = shape == "missing" ? Dictionary() : Dictionary(("Contents", shape == "null" ? PdfNull.Instance : Dictionary()));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(prepared.Construction);
        Assert.Null(InvokeRead(prepared.Copier, page, governor, diagnostics)); Assert.Empty(diagnostics.Entries);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("missing-reference")]
    [InlineData("nonstream-reference")]
    [InlineData("array-nonstream")]
    [InlineData("array-reference")]
    public void InvalidContentsDropPrefixScratchAndKeepTheExactWarning(string shape)
    {
        using Prepared prepared = new();
        QpdfObject contents = shape switch
        { "missing-reference" => new PdfReference(99, 3), "nonstream-reference" => new PdfReference(8, 3),
          "array-nonstream" => new PdfArray([prepared.Stream, PdfNull.Instance]), _ => new PdfArray([prepared.Stream, new PdfReference(99, 3)]) };
        PdfDictionary page = Dictionary(("Contents", contents)); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(prepared.Construction);
        Assert.Null(InvokeRead(prepared.Copier, page, governor, diagnostics));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        QpdfDiagnostic warning = Assert.Single(diagnostics.Entries);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code); Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(shape == "array-nonstream" ? "Skipped content operation on a non-stream content entry." : "Skipped content operation on an unresolvable content stream.", warning.Message);
        Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject unchanged));
        Assert.Same(contents, unchanged);
    }

    [Theory]
    [InlineData(256L)]
    [InlineData(512L)]
    [InlineData(2048L)]
    public void PlanGrowthFailurePreservesOriginalInputsAndPriorFunding(long maximum)
    {
        using Prepared prepared = new(); PdfArray contents = new(Enumerable.Repeat<QpdfObject>(prepared.Stream, 512).ToArray());
        PdfDictionary page = Dictionary(("Contents", contents)); ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L); long live = governor.Snapshot().LiveMemoryBytes;
        DiagnosticBag diagnostics = new(prepared.Construction);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => InvokeRead(prepared.Copier, page, governor, diagnostics));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(61L, prior.ReservedBytes); Assert.Equal(512, contents.Count); Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public void EveryValidPlanWorkInterruptionRetiresOnlyItsScratch()
    {
        using Prepared prepared = new(); PdfDictionary page = Dictionary(("Contents", new PdfArray(Enumerable.Repeat<QpdfObject>(prepared.Stream, 17).ToArray())));
        int failures = 0, successes = 0;
        for (int maximum = 1; maximum <= 511; maximum++)
        {
            ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = maximum }, CancellationToken.None);
            using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L); long live = governor.Snapshot().LiveMemoryBytes;
            object? entries = null;
            try { entries = InvokeRead(prepared.Copier, page, governor, new(prepared.Construction)); Assert.NotNull(entries); Assert.Equal(17, Count(entries)); successes++; }
            catch (QpdfLimitException failure) { Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); failures++; }
            finally { Close(entries); }
            Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes);
        }
        Assert.True(failures > 0); Assert.True(successes > 0);
    }

    [Fact]
    public void GovernorCancellationPreventsPlanLookupAndConstruction()
    {
        using Prepared prepared = new(); PdfDictionary page = Dictionary(("Contents", prepared.Stream));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => InvokeRead(prepared.Copier, page, governor, new(prepared.Construction)));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("single")]
    [InlineData("mixed")]
    public void OrphanCollectionPreservesRawOrderDuplicatesAndNonpositiveIds(string shape)
    {
        QpdfObject? value = shape switch { "none" => null, "single" => new PdfReference(7, 3), _ => new PdfArray([new PdfReference(7, 3), PdfNull.Instance, new PdfReference(7, 11), new PdfReference(0, 3), new PdfReference(-2, 3)]) };
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<int>? ids = InvokeIds(value, memory, governor);
        Assert.Equal(shape == "none" ? Array.Empty<int>() : shape == "single" ? new[] { 7 } : new[] { 7, 7, 0, -2 }, ids);
        Assert.True(memory.ReservedBytes >= 32L); ids = null; memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(256L)]
    [InlineData(512L)]
    public void OrphanGrowthFailureKeepsItsOwnScopeUntilDisposal(long maximum)
    {
        PdfArray value = new(Enumerable.Range(0, 512).Select(index => (QpdfObject)new PdfReference(index, 3)).ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L); long live = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => InvokeIds(value, memory, governor));
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.True(memory.ReservedBytes >= 32L);
        }
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, value.Count);
    }
}
