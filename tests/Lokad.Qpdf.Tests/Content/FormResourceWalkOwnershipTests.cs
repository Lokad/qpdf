using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class FormResourceWalkOwnershipTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] values)
    { Dictionary<PdfName, QpdfObject> entries = new(); foreach (var value in values) { entries.Add(PdfName.FromAscii(value.Key), value.Value); } return new(entries); }

    [Fact]
    public void OwnerFundsFormsThroughTheirLastReadWithoutOwningBorrowedPayloads()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        PdfStream borrowed = new(Dictionary(), new byte[2 * 1024 * 1024]);
        using FormResourceWalk walk = FormResourceWalk.Create(governor, CancellationToken.None);
        Assert.Equal(480L, governor.Snapshot().LiveMemoryBytes); walk.Add(17, borrowed);
        Assert.Equal(576L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(17, walk.Get(0).Id); Assert.Same(borrowed, walk.Get(0).Stream);
        walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Throws<ObjectDisposedException>(() => walk.Count);
        Assert.Equal(2 * 1024 * 1024, borrowed.RawMemory.Length);
    }

    [Theory]
    [InlineData(63L)]
    [InlineData(143L)]
    [InlineData(207L)]
    [InlineData(239L)]
    [InlineData(479L)]
    public void PartialConstructionFailureReleasesEveryNewReceipt(long maximum)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => FormResourceWalk.Create(governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreCancellationStopsBeforeOwnerAdmission(bool governorToken)
    {
        using CancellationTokenSource source = new(); source.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => FormResourceWalk.Create(governor, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Fact]
    public void FormListGrowthFailureKeepsPriorOrderAndCanRetryAfterReleasingTheFiller()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using FormResourceWalk walk = FormResourceWalk.Create(governor, CancellationToken.None);
        PdfStream[] streams = Enumerable.Range(0, 17).Select(number => new PdfStream(Dictionary(), [(byte)number])).ToArray();
        for (int index = 0; index < 16; index++) { walk.Add(index, streams[index]); }
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor); filler.Reserve(8192L - governor.Snapshot().LiveMemoryBytes - 1L);
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => walk.Add(16, streams[16])); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(16, walk.Count);
        filler.Dispose(); walk.Add(16, streams[16]);
        for (int index = 0; index < 17; index++) { Assert.Equal(index, walk.Get(index).Id); Assert.Same(streams[index], walk.Get(index).Stream); }
        walk.Dispose(); Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Fact]
    public void ScopeInsertionFailureDoesNotPublishAnUnregisteredQueueEntry()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using FormResourceWalk walk = FormResourceWalk.Create(governor, CancellationToken.None);
        PdfDictionary[] scopes = Enumerable.Range(0, 4).Select(number => Dictionary(("V", new PdfReference(number, 0)))).ToArray();
        for (int index = 0; index < 3; index++) { Assert.True(walk.AddScope(scopes[index])); }
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor); filler.Reserve(8192L - governor.Snapshot().LiveMemoryBytes - 1L);
        Assert.Throws<QpdfLimitException>(() => walk.AddScope(scopes[3])); Assert.Equal(3, walk.ScopeCount);
        for (int index = 0; index < 3; index++) { Assert.Same(scopes[index], walk.GetScope(index)); }
        filler.Dispose(); Assert.True(walk.AddScope(scopes[3])); Assert.False(walk.AddScope(scopes[3]));
        walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ScopeVisitsUseIdentityWithoutMergingEqualValues()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using FormResourceWalk walk = FormResourceWalk.Create(governor, CancellationToken.None);
        PdfDictionary first = Dictionary(("XObject", Dictionary(("A", new PdfReference(12, 0)))));
        PdfDictionary equal = Dictionary(("XObject", Dictionary(("A", new PdfReference(12, 0)))));
        PdfDictionary different = Dictionary(("XObject", Dictionary(("A", new PdfReference(12, 1)))));
        Assert.True(walk.AddScope(first)); Assert.True(walk.AddScope(equal)); Assert.True(walk.AddScope(different));
        Assert.False(walk.AddScope(first)); Assert.Equal(3, walk.ScopeCount);
        Assert.Same(first, walk.GetScope(0)); Assert.Same(equal, walk.GetScope(1)); Assert.Same(different, walk.GetScope(2));
        Assert.True(walk.VisitNumber(12)); Assert.False(walk.VisitNumber(12)); walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RevisitingScopesDoesNotTraverseTheirDeepBorrowedValues()
    {
        QpdfObject left = PdfBoolean.True, right = new PdfBoolean(true);
        for (int depth = 0; depth < 4096; depth++)
        { left = new PdfArray([left]); right = new PdfArray([right]); }
        PdfDictionary first = Dictionary(("Extension", left)), equal = Dictionary(("Extension", right));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 2048L }, CancellationToken.None);
        using FormResourceWalk walk = FormResourceWalk.Create(governor, CancellationToken.None);
        Assert.True(walk.AddScope(first)); Assert.True(walk.AddScope(equal));
        Assert.False(walk.AddScope(first)); Assert.False(walk.AddScope(equal));
        Assert.Equal(2, walk.ScopeCount); walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationKeepsAcceptedFormsAndDisposalRetiresScratch(bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using FormResourceWalk walk = FormResourceWalk.Create(governor, governorToken ? CancellationToken.None : source.Token);
        walk.Add(null, new PdfStream(Dictionary(), [])); long live = governor.Snapshot().LiveMemoryBytes; source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => walk.Get(0)); Assert.ThrowsAny<OperationCanceledException>(() => walk.AddScope(Dictionary()));
        Assert.Equal(1, walk.Count); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed record Completed(FormResourceWalk Owner, PdfStream Borrowed, WeakReference[] Witnesses);
    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value) ?? throw new InvalidOperationException("Missing owner field.");
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Completed Complete()
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        FormResourceWalk walk = FormResourceWalk.Create(governor, source.Token); PdfStream borrowed = new(Dictionary(), new byte[2 * 1024 * 1024]);
        for (int index = 0; index < 17; index++) { walk.Add(index, borrowed); walk.VisitNumber(index); walk.AddScope(Dictionary(("V", new PdfReference(index, 0)))); }
        object scopes = Field(walk, "_scopes"), forms = Field(walk, "_forms"), visited = Field(walk, "_visited"), indexStorage = Field(visited, "_index");
        WeakReference[] witnesses = [new(governor), new(source), new(Field(walk, "_memory")), new(scopes), new(forms), new(Field(scopes, "_items")), new(Field(forms, "_items")),
            new(Field(walk, "_seen")), new(visited), new(indexStorage), new(Field(indexStorage, "_entries")), new(Field(indexStorage, "_buckets"))];
        walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return new(walk, borrowed, witnesses);
    }
    [Fact]
    public void RootedDisposedWalkKeepsNoExecutionOrBackingMetadata()
    {
        Completed completed = Complete();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(completed.Witnesses, witness => Assert.False(witness.IsAlive)); Assert.Equal(2 * 1024 * 1024, completed.Borrowed.RawMemory.Length); GC.KeepAlive(completed);
    }
}
