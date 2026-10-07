using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ResourceNameSetOwnershipTests
{
    [Fact]
    public void DecodedNamesHaveOneFundedCopyAndBorrowedInputStaysImmutable()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ResourceNameSet names = ResourceNameSet.Create(governor, CancellationToken.None);
        Assert.Equal(224L, governor.Snapshot().LiveMemoryBytes);
        byte[] input = "A#23#34#31"u8.ToArray();
        Assert.True(names.AddRaw(input)); input[0] = (byte)'Z';
        Assert.True(names.ContainsDecoded("A#41"u8)); Assert.False(names.ContainsRaw("A#41"u8));
        Assert.False(names.AddDecoded("A#41"u8)); Assert.True(names.AddRaw("A#41"u8));
        using var cursor = names.GetEnumerator();
        Assert.True(cursor.MoveNext(governor, CancellationToken.None));
        PdfName key = cursor.Current.Key; Assert.Equal("A#41"u8.ToArray(), key.RawBytes);
        byte[] copy = key.RawBytes; copy[0] = 0;
        Assert.Equal("A#41"u8.ToArray(), key.RawBytes);
        Assert.True(cursor.MoveNext(governor, CancellationToken.None)); Assert.Equal("AA"u8.ToArray(), cursor.Current.Key.RawBytes);
        names.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(63L)]
    [InlineData(127L)]
    [InlineData(223L)]
    public void PartialConstructionReturnsOnlyItsOwnAdmission(long maximum)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        Assert.Throws<QpdfLimitException>(() => ResourceNameSet.Create(governor, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreCancellationDoesNotAdmitAnOwner(bool governorToken)
    {
        using CancellationTokenSource source = new(); source.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => ResourceNameSet.Create(governor, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes); Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [Fact]
    public void FailedGrowthPreservesPriorNamesAndRetriesWithTheSameOwner()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ResourceNameSet names = ResourceNameSet.Create(governor, CancellationToken.None);
        Assert.True(names.AddRaw("A"u8)); Assert.True(names.AddRaw("B"u8)); Assert.True(names.AddRaw("C"u8));
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(8192L - governor.Snapshot().LiveMemoryBytes - 65L);
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => names.AddRaw("D"u8));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(3, names.Count); Assert.True(names.ContainsRaw("A"u8)); Assert.False(names.ContainsRaw("D"u8));
        filler.Dispose(); Assert.True(names.AddRaw("D"u8)); Assert.Equal(4, names.Count);
        names.Dispose(); Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Fact]
    public void FailedByteCopyDoesNotPublishOrRetainItsFreshKey()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = 128L }, CancellationToken.None);
        using ResourceNameSet names = ResourceNameSet.Create(governor, CancellationToken.None);
        Assert.True(names.AddDecoded("A"u8)); long live = governor.Snapshot().LiveMemoryBytes;
        byte[] longName = new byte[8192]; Array.Fill(longName, (byte)'x');
        Assert.Throws<QpdfLimitException>(() => names.AddDecoded(longName));
        Assert.Equal(1, names.Count); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        names.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterAcceptedNamesPreservesTheirAdmissionUntilDisposal(bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using ResourceNameSet names = ResourceNameSet.Create(governor, governorToken ? CancellationToken.None : source.Token);
        Assert.True(names.AddRaw("A#41"u8)); long live = governor.Snapshot().LiveMemoryBytes; source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => names.ContainsRaw("AA"u8));
        Assert.ThrowsAny<OperationCanceledException>(() => names.AddDecoded("B"u8));
        Assert.Equal(1, names.Count); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        names.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void WarmedDuplicateAndBorrowedQueriesAllocateNothing()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ResourceNameSet names = ResourceNameSet.Create(governor, CancellationToken.None);
        byte[] raw = "A#23#34#31#00#FF"u8.ToArray(); Assert.True(names.AddRaw(raw));
        for (int index = 0; index < 128; index++) { Assert.True(names.ContainsRaw(raw)); Assert.False(names.AddRaw(raw)); }
        long live = governor.Snapshot().LiveMemoryBytes, before = GC.GetAllocatedBytesForCurrentThread();
        bool result = true;
        for (int index = 0; index < 128; index++) { result &= names.ContainsRaw(raw) && !names.AddRaw(raw); }
        long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(result); Assert.Equal(0L, allocation); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void PageUnresolvedNamesOwnTheirCopyAfterTheFormAnalysisEnds()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ResourceNameSet page = ResourceNameSet.Create(governor, CancellationToken.None);
        using (ResourceNameSet form = ResourceNameSet.Create(governor, CancellationToken.None))
        {
            Assert.True(form.AddRaw("A#23#34#31"u8)); using var cursor = form.GetEnumerator(); Assert.True(cursor.MoveNext(governor, CancellationToken.None));
            Assert.True(page.AddDecoded(cursor.Current.Key.RawSpan));
        }
        Assert.True(page.ContainsRaw("A#23#34#31"u8)); Assert.False(page.ContainsRaw("A#41"u8));
        page.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static object Field(object owner, string field) => owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) ?? throw new InvalidOperationException(field);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ResourceNameSet Owner, WeakReference[] Witnesses) Complete()
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        ResourceNameSet names = ResourceNameSet.Create(governor, source.Token);
        for (int index = 0; index < 17; index++) { names.AddRaw(Encoding.ASCII.GetBytes("N" + index)); }
        object table = Field(names, "_names");
        WeakReference[] witnesses = [new(governor), new(source), new(Field(names, "_memory")), new(table), new(Field(table, "_entries")), new(Field(table, "_buckets")), new(Field(table, "_origin"))];
        names.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return (names, witnesses);
    }
    [Fact]
    public void RootedDisposedOwnerKeepsNoExecutionOrBackingState()
    {
        var completed = Complete(); for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(completed.Witnesses, witness => Assert.False(witness.IsAlive));
        Assert.Throws<ObjectDisposedException>(() => completed.Owner.Count); GC.KeepAlive(completed);
    }
}
