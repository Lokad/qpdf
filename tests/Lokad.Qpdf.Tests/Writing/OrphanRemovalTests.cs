using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(NameTableCollection))]
public sealed class OrphanRemovalTests
{
    private delegate void RemoveCall(GraphCopier copier, IReadOnlyList<int>? candidates, CancellationToken token);
    private delegate void PrecedingCall(GraphCopier copier, IReadOnlyList<int>? candidates);
    private static readonly MethodInfo Method = typeof(GraphCopier).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Where(method => method.Name == "DropPoolObjectsIfUnreferenced").OrderByDescending(method => method.GetParameters().Length).First();
    private static readonly RemoveCall? Remove = Method.GetParameters().Length == 2 ? Method.CreateDelegate<RemoveCall>() : null;
    private static readonly PrecedingCall? Preceding = Method.GetParameters().Length == 1 ? Method.CreateDelegate<PrecedingCall>() : null;

    // Only the new private overload's caller-token binding differs. Prepared
    // inputs, scopes, limits and assertions also run on the preceding runtime.
    private static void Invoke(GraphCopier copier, IReadOnlyList<int>? candidates)
    {
        if (Remove is not null) { Remove(copier, candidates, CancellationToken.None); return; }
        (Preceding ?? throw new InvalidOperationException())(copier, candidates);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("ignored")]
    [InlineData("orphan")]
    [InlineData("shared")]
    [InlineData("nested")]
    [InlineData("self")]
    [InlineData("candidate-chain")]
    public void PreserveInboundRuleAliasesOrderAndStaleMappingCleanup(string shape)
    {
        PdfStream borrowed = OrphanFixture.Stream(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using OrphanFixture fixture = new(governor, borrowed);
        int[] candidates = shape switch { "empty" => [], "ignored" => [-1, 0, 404, 404], "orphan" => [2, 2, 0, -2, 404], _ => [1, 2] };
        if (shape == "shared") { fixture.Pool[90] = new PdfReference(1, 99); }
        if (shape == "self") { fixture.Pool[1] = new PdfReference(1, 99); }
        if (shape == "candidate-chain") { fixture.Pool[1] = new PdfReference(2, 99); }
        if (shape == "nested")
        {
            QpdfObject[] values = new QpdfObject[3]; values[0] = new PdfReference(1, 99); values[2] = PdfNull.Instance;
            PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Nested")] = PdfArray.FromOwnedItems(values), [PdfName.FromAscii("Null")] = values[1] });
            fixture.Pool[90] = new PdfStream(dictionary, []);
        }
        int[] originalCandidates = candidates.ToArray(); var originals = fixture.Pool.ToArray();
        int[] removed = shape is "empty" or "ignored" ? [] : shape == "candidate-chain" ? [1] : [2];
        Invoke(fixture.Copier, candidates);
        Assert.Equal(originalCandidates, candidates);
        Assert.Equal(originals.Where(entry => !removed.Contains(entry.Key)).Select(entry => entry.Key), fixture.Pool.Keys);
        foreach (var entry in originals.Where(entry => !removed.Contains(entry.Key))) { Assert.Same(entry.Value, fixture.Pool[entry.Key]); }
        Assert.Equal(new[] { 11, 12, 21, 31, 91 }.Where(source => !removed.Contains(source / 10)), fixture.Mapping.Keys);
        Assert.Equal(2 * 1024 * 1024, borrowed.RawMemory.Length); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScratchMustBeAdmittedEvenForEmptyOrIgnoredCandidates(bool ignored)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1L }, CancellationToken.None);
        using OrphanFixture fixture = new(governor, PdfNull.Instance);
        var before = fixture.Pool.ToArray(); var mapping = fixture.Mapping.ToArray();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Invoke(fixture.Copier, ignored ? [0, -1, 404] : []));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        OrphanFixture.AssertUnchanged(fixture.Pool, fixture.Mapping, before, mapping); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappingScanHasFiniteWorkBeforeEitherDictionaryChanges(bool matching)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = 128L }, CancellationToken.None);
        using OrphanFixture fixture = new(governor, PdfNull.Instance);
        for (int index = 0; index < 1024; index++) { fixture.Mapping.Add(10000 + index, matching ? 2 : 90); }
        var before = fixture.Pool.ToArray(); var mapping = fixture.Mapping.ToArray();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Invoke(fixture.Copier, [2]));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        OrphanFixture.AssertUnchanged(fixture.Pool, fixture.Mapping, before, mapping); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void WideTraversalRejectsCapacityBeforeChangingBorrowedGraphs()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using OrphanFixture fixture = new(governor, PdfNull.Instance);
        fixture.Pool[90] = new PdfArray(Enumerable.Repeat<QpdfObject>(new PdfReference(1, 99), 2048).ToArray());
        var before = fixture.Pool.ToArray(); var mapping = fixture.Mapping.ToArray();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Invoke(fixture.Copier, [1, 2]));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        OrphanFixture.AssertUnchanged(fixture.Pool, fixture.Mapping, before, mapping); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EverySmallWorkBudgetPreservesBothDictionariesOnFailure()
    {
        int failures = 0, successes = 0;
        for (int budget = 1; budget < 512; budget++)
        {
            ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = budget }, CancellationToken.None);
            using OrphanFixture fixture = new(governor, PdfNull.Instance);
            var before = fixture.Pool.ToArray(); var mapping = fixture.Mapping.ToArray();
            try
            {
                Invoke(fixture.Copier, [1, 2, 1]); Assert.Equal(new[] { 3, 90 }, fixture.Pool.Keys);
                Assert.Equal(new[] { 31, 91 }, fixture.Mapping.Keys); successes++;
            }
            catch (QpdfLimitException failure)
            {
                Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
                OrphanFixture.AssertUnchanged(fixture.Pool, fixture.Mapping, before, mapping); failures++;
            }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.True(failures > 0); Assert.True(successes > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GovernorCancellationPrecedesAdmissionAndWork(bool empty)
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1L }, cancellation.Token);
        using OrphanFixture fixture = new(governor, PdfNull.Instance);
        Assert.Throws<OperationCanceledException>(() => Invoke(fixture.Copier, empty ? [] : [2]));
        Assert.Equal(0L, governor.Snapshot().TotalWork); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void NullCandidatesRetainArgumentFailurePrecedence()
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using OrphanFixture fixture = new(governor, PdfNull.Instance);
        ArgumentNullException failure = Assert.Throws<ArgumentNullException>(() => Invoke(fixture.Copier, null));
        Assert.Equal("candidates", failure.ParamName); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

internal sealed class OrphanFixture : IDisposable
{
    private readonly DocumentEngine _engine;
    internal readonly GraphCopier Copier;
    internal Dictionary<int, QpdfObject> Pool => (Dictionary<int, QpdfObject>)Copier.CopiedObjects;
    internal Dictionary<int, int> Mapping => (Dictionary<int, int>)Copier.SourceToDestination;
    internal OrphanFixture(ResourceGovernor governor, QpdfObject borrowed)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
        _engine = new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]),
            QpdfLimits.Default, construction, new DiagnosticBag(construction), QpdfRecoveryMode.Strict);
        Copier = new(_engine, governor, QpdfLimits.Default, 1);
        Pool.Add(1, borrowed); Pool.Add(2, borrowed); Pool.Add(3, PdfNull.Instance); Pool.Add(90, PdfNull.Instance);
        Mapping.Add(11, 1); Mapping.Add(12, 1); Mapping.Add(21, 2); Mapping.Add(31, 3); Mapping.Add(91, 90);
    }
    internal static PdfStream Stream() => new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), new byte[2 * 1024 * 1024]);
    internal static void AssertUnchanged(Dictionary<int, QpdfObject> pool, Dictionary<int, int> mapping,
        KeyValuePair<int, QpdfObject>[] before, KeyValuePair<int, int>[] sources)
    {
        Assert.Equal(before.Select(entry => entry.Key), pool.Keys);
        foreach (var entry in before) { Assert.Same(entry.Value, pool[entry.Key]); }
        Assert.Equal(sources, mapping.ToArray());
    }
    public void Dispose() { _engine.Dispose(); }
}
