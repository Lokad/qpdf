using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class JoinedContentOwnershipTests
{
    [Theory]
    [InlineData(63L)]
    [InlineData(95L)]
    [InlineData(1151L)]
    public void PartialConstructionReturnsOnlyItsOwnAdmission(long maximum)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        Assert.Throws<QpdfLimitException>(() => JoinedContent.Create([new byte[1024]], governor, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EmptyAnalysisOwnsNoArrayAndDisposalIsIdempotent()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        JoinedContent joined = JoinedContent.Create(Array.Empty<byte[]>(), governor, CancellationToken.None);
        Assert.True(joined.Bytes.IsEmpty); Assert.Equal(96L, governor.Snapshot().LiveMemoryBytes);
        joined.Dispose(); joined.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => joined.Bytes);
    }

    [Fact]
    public void JoinedStorageIsIndependentOfCallerBuffers()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); byte[] first = "a"u8.ToArray();
        using JoinedContent joined = JoinedContent.Create([first, "b"u8.ToArray()], governor, CancellationToken.None);
        first[0] = (byte)'z'; Assert.Equal("a\nb"u8.ToArray(), joined.Bytes.ToArray());
        Assert.Equal(131L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class ObservedParts : IReadOnlyList<byte[]>
    {
        private readonly byte[][] _parts;
        private readonly Action _observe;
        private int _visits;
        internal ObservedParts(byte[][] parts, Action observe) { _parts = parts; _observe = observe; }
        public int Count => _parts.Length;
        public byte[] this[int index]
        {
            get { if (++_visits == 3) { _observe(); } return _parts[index]; }
        }
        public IEnumerator<byte[]> GetEnumerator() => ((IEnumerable<byte[]>)_parts).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterSizingAndAdmissionReturnsOnlyNewStorage(bool governorToken)
    {
        using CancellationTokenSource source = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long live = governor.Snapshot().LiveMemoryBytes;
        ObservedParts parts = new([new byte[32768], "last"u8.ToArray()], source.Cancel);
        Assert.ThrowsAny<OperationCanceledException>(() => JoinedContent.Create(parts, governor, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes >= live + 32768L);
    }

    private static object Field(object owner, string name)
        => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner) ?? throw new InvalidOperationException(name);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (JoinedContent Owner, byte[] Input, WeakReference[] Witnesses) Complete()
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        byte[] input = new byte[32768]; input[17] = 91;
        JoinedContent joined = JoinedContent.Create([input], governor, source.Token);
        WeakReference[] witnesses = [new(governor), new(source), new(Field(joined, "_memory")), new(Field(joined, "_bytes"))];
        joined.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return (joined, input, witnesses);
    }

    [Fact]
    public void RootedDisposedOwnerDropsBufferAndExecutionStateWhileInputSurvives()
    {
        var completed = Complete();
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(completed.Witnesses, witness => Assert.False(witness.IsAlive)); Assert.Equal(91, completed.Input[17]);
        Assert.Throws<ObjectDisposedException>(() => completed.Owner.Bytes); GC.KeepAlive(completed);
    }
}
