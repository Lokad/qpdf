using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class SharedResourceWalkOwnershipTests
{
    [Fact]
    public void WalkAdmitsWrappersAndBorrowedReferencesThroughTheirLastUse()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        using SharedResourceWalk walk = SharedResourceWalk.Create(governor, CancellationToken.None);
        Assert.Equal(512L, governor.Snapshot().LiveMemoryBytes); walk.Push(borrowed, CancellationToken.None);
        Assert.Equal(576L, governor.Snapshot().LiveMemoryBytes); Assert.True(walk.TryPop(CancellationToken.None, out QpdfObject found)); Assert.Same(borrowed, found);
        Assert.False(walk.TryPop(CancellationToken.None, out _)); Assert.Equal(576L, governor.Snapshot().LiveMemoryBytes);
        walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Throws<ObjectDisposedException>(() => walk.Count);
        Assert.Equal(2 * 1024 * 1024, borrowed.ValueSpan.Length);
    }

    [Theory]
    [InlineData(63L)]
    [InlineData(127L)]
    [InlineData(303L)]
    [InlineData(479L)]
    [InlineData(511L)]
    public void WalkAdmissionFailuresDropAllPartialReceipts(long maximum)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => SharedResourceWalk.Create(governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void QueueGrowthFailurePreservesOrderPriorAdmissionAndSameOwnerRetry()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using SharedResourceWalk walk = SharedResourceWalk.Create(governor, CancellationToken.None);
        QpdfObject[] nodes = Enumerable.Range(0, 17).Select(number => (QpdfObject)new PdfInteger(number.ToString(System.Globalization.CultureInfo.InvariantCulture), number)).ToArray();
        for (int index = 0; index < 16; index++) { walk.Push(nodes[index], CancellationToken.None); }
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor); filler.Reserve(8192L - governor.Snapshot().LiveMemoryBytes - 1L);
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => walk.Push(nodes[16], CancellationToken.None)); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(16, walk.Count);
        filler.Dispose(); walk.Push(nodes[16], CancellationToken.None);
        for (int index = 16; index >= 0; index--) { Assert.True(walk.TryPop(CancellationToken.None, out QpdfObject found)); Assert.Same(nodes[index], found); }
        Assert.False(walk.TryPop(CancellationToken.None, out _)); walk.Dispose(); Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexGrowthFailurePreservesEarlierNumbersAndSameOwnerRetry(bool resources)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using SharedResourceWalk walk = SharedResourceWalk.Create(governor, CancellationToken.None);
        for (int number = 0; number < 4; number++) { Assert.True(resources ? walk.VisitResource(number, CancellationToken.None) : walk.VisitNode(number, CancellationToken.None)); }
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor); filler.Reserve(8192L - governor.Snapshot().LiveMemoryBytes - 1L); long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => resources ? walk.VisitResource(4, CancellationToken.None) : walk.VisitNode(4, CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); filler.Dispose();
        for (int number = 0; number < 4; number++) { Assert.False(resources ? walk.VisitResource(number, CancellationToken.None) : walk.VisitNode(number, CancellationToken.None)); }
        Assert.True(resources ? walk.VisitResource(4, CancellationToken.None) : walk.VisitNode(4, CancellationToken.None));
        walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void QueueGrowthRequiresWorkBeforeAllocatingItsFirstArray()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = 4L }, CancellationToken.None);
        using SharedResourceWalk walk = SharedResourceWalk.Create(governor, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => walk.Push(PdfNull.Instance, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(0, walk.Count); Assert.Equal(512L, governor.Snapshot().LiveMemoryBytes);
        walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherCancellationContextPreservesExistingOwnerThenDisposalReleasesIt(bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using SharedResourceWalk walk = SharedResourceWalk.Create(governor, governorToken ? CancellationToken.None : source.Token);
        walk.Push(PdfNull.Instance, CancellationToken.None); long live = governor.Snapshot().LiveMemoryBytes, work = governor.Snapshot().TotalWork; source.Cancel();
        CancellationToken token = governorToken ? CancellationToken.None : source.Token;
        Assert.ThrowsAny<OperationCanceledException>(() => walk.TryPop(token, out _)); Assert.ThrowsAny<OperationCanceledException>(() => walk.VisitNode(3, token));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(work, governor.Snapshot().TotalWork); Assert.Equal(1, walk.Count);
        walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed record Completed(object Owner, QpdfObject Borrowed, WeakReference[] Witnesses);
    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value) ?? throw new InvalidOperationException("Missing walk storage.");
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Completed Complete()
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        SharedResourceWalk walk = SharedResourceWalk.Create(governor, source.Token); PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        for (int number = 0; number < 17; number++) { walk.Push(borrowed, source.Token); walk.VisitNode(number, source.Token); walk.VisitResource(number, source.Token); }
        object pending = Field(walk, "_pending"), nodes = Field(walk, "_nodes"), resources = Field(walk, "_resources");
        object nodeIndex = Field(nodes, "_index"), resourceIndex = Field(resources, "_index");
        WeakReference[] witnesses = [new(governor), new(source), new(Field(walk, "_memory")), new(pending), new(Field(pending, "_items")),
            new(nodes), new(resources), new(nodeIndex), new(resourceIndex), new(Field(nodeIndex, "_entries")), new(Field(nodeIndex, "_buckets")), new(Field(resourceIndex, "_entries")), new(Field(resourceIndex, "_buckets"))];
        walk.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return new(walk, borrowed, witnesses);
    }
    [Fact]
    public void RootedDisposedWalkAndBorrowedPayloadRetainNoExecutionIndexesOrQueue()
    {
        Completed completed = Complete();
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(completed.Witnesses, witness => Assert.False(witness.IsAlive));
        Assert.Equal(2 * 1024 * 1024, ((PdfByteString)completed.Borrowed).ValueSpan.Length); GC.KeepAlive(completed);
    }
}
