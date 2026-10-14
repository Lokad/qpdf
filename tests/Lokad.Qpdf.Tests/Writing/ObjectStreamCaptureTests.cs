using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamCaptureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MixedFormattedAndCopiedMembersPreserveBytesWithinLimits(bool tightMemory, bool tightWork)
    {
        QpdfLimits limits = QpdfLimits.Default with
        {
            MaxMemoryBytes = tightMemory ? 4096L : QpdfLimits.Default.MaxMemoryBytes,
            MaxWork = tightWork ? 4096L : QpdfLimits.Default.MaxWork, MaxOutputBytes = 1024L,
        };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ObjectStreamCapture members = ObjectStreamCapture.Create(3, limits, governor, CancellationToken.None))
        {
            PdfSerializer serializer = new(limits, governor);
            PdfArray first = new([new PdfReference(12345, 0), PdfName.FromAscii("A B"),
                new PdfInteger("123456789", 123456789L), new PdfByteString([0, 255], true)]);
            await members.AppendAsync(serializer, first, false, CancellationToken.None);
            using (BufferedSink external = BufferedSink.Create(limits, governor, CancellationToken.None, 256))
            {
                byte[] bytes = Enumerable.Repeat((byte)'A', 384).ToArray();
                await external.AppendAsync(bytes, CancellationToken.None);
                members.Append(external, CancellationToken.None);
                Array.Fill(bytes, (byte)'X');
            }
            await members.AppendAsync(serializer, new PdfReference(54321, 0), false, CancellationToken.None);
            Assert.Equal("[12345 0 R /A#20B 123456789 <00FF>]"u8.ToArray(), members[0].ToArray());
            Assert.Equal(Enumerable.Repeat((byte)'A', 384).ToArray(), members[1].ToArray());
            Assert.Equal("54321 0 R"u8.ToArray(), members[2].ToArray());
            Assert.InRange(governor.Snapshot().PeakMemoryBytes, 0L, limits.MaxMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task MembersAndOffsetsStayOwnedUntilDisposal()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(97L);
        using ObjectStreamCapture members = ObjectStreamCapture.Create(2, QpdfLimits.Default, governor, CancellationToken.None);
        long tableAdmission = governor.Snapshot().LiveMemoryBytes - 161L;
        PdfSerializer serializer = new(QpdfLimits.Default, governor);
        Assert.Throws<InvalidOperationException>(() => members[0]);
        PdfArray value = new([new PdfInteger("17", 17L), PdfName.FromAscii("A B"), PdfBoolean.True, PdfNull.Instance]);
        await members.AppendAsync(serializer, value, false, CancellationToken.None);
        await members.AppendAsync(serializer, new PdfByteString([0, 255], true), false, CancellationToken.None);
        Assert.Equal("[17 /A#20B true null]"u8.ToArray(), members[0].ToArray());
        Assert.Equal("<00FF>"u8.ToArray(), members[1].ToArray());
        Assert.True(governor.Snapshot().LiveMemoryBytes > 161L + tableAdmission
            + members[0].Length + members[1].Length);
        long live = governor.Snapshot().LiveMemoryBytes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => members.AppendAsync(serializer, PdfNull.Instance, false, CancellationToken.None).AsTask());
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(PdfBoolean.True, value[2]);
        members.Dispose(); members.Dispose();
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => members[0]);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => members.AppendAsync(serializer, value, false, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void InvalidGroupSizeRejectsBeforeAdmission(int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfException>(() => ObjectStreamCapture.Create(count, QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [Fact]
    public void PartialFactoryFailureRetiresItsOwnScope()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 328L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(97L);
        Assert.Throws<QpdfLimitException>(() => ObjectStreamCapture.Create(2, limits, governor, CancellationToken.None));
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
    }

    [Fact]
    public async Task FailedCopyPreservesEarlierMembersAndCanRetryTheSameSlot()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 200000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ObjectStreamCapture members = ObjectStreamCapture.Create(2, limits, governor, CancellationToken.None);
        PdfSerializer serializer = new(limits, governor);
        await members.AppendAsync(serializer, PdfNull.Instance, false, CancellationToken.None);
        using BufferedSink capture = BufferedSink.Create(limits, governor, CancellationToken.None);
        byte[] source = new byte[65536];
        new Random(89).NextBytes(source);
        await capture.AppendAsync(source, CancellationToken.None);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - (source.Length + 31L));
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => members.Append(capture, CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal("null"u8.ToArray(), members[0].ToArray());
        Assert.Throws<InvalidOperationException>(() => members[1]);
        filler.Dispose();
        members.Append(capture, CancellationToken.None);
        Assert.Equal(source, members[1].ToArray());
        capture.Dispose(); members.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(4096, 1024)]
    [InlineData(32768, 8192)]
    public async Task SerializationFailureRollsBackPartialMemberAndPreservesEarlierBytes(int maximum, int count)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = maximum };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ObjectStreamCapture members = ObjectStreamCapture.Create(2, limits, governor, CancellationToken.None);
        PdfSerializer serializer = new(limits, governor);
        await members.AppendAsync(serializer, PdfNull.Instance, false, CancellationToken.None);
        PdfArray oversized = new(Enumerable.Repeat<QpdfObject>(PdfNull.Instance, count).ToArray());
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            members.AppendAsync(serializer, oversized, false, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), failure.LimitName);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 0L, limits.MaxMemoryBytes);
        Assert.Equal("null"u8.ToArray(), members[0].ToArray());
        Assert.Throws<InvalidOperationException>(() => members[1]);
        await members.AppendAsync(serializer, PdfBoolean.False, false, CancellationToken.None);
        Assert.Equal("false"u8.ToArray(), members[1].ToArray());
        members.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledMemberAttemptPreservesPriorCopiesAndRetiresItsWorkingStorage(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ObjectStreamCapture members = ObjectStreamCapture.Create(2, QpdfLimits.Default, governor, CancellationToken.None);
        PdfSerializer serializer = new(QpdfLimits.Default, governor);
        await members.AppendAsync(serializer, PdfNull.Instance, false, CancellationToken.None);
        long live = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => members.AppendAsync(serializer, PdfBoolean.True, false,
            governorToken ? CancellationToken.None : cancellation.Token).AsTask());
        Assert.True(governor.Snapshot().LiveMemoryBytes <= live);
        Assert.Equal("null"u8.ToArray(), members[0].ToArray());
        members.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ObjectStreamCapture Owner, WeakReference Table, WeakReference Bytes)> Filled(ResourceGovernor governor)
    {
        ObjectStreamCapture owner = ObjectStreamCapture.Create(1, QpdfLimits.Default, governor, CancellationToken.None);
        using BufferedSink capture = BufferedSink.Create(QpdfLimits.Default, governor, CancellationToken.None);
        await capture.AppendAsync(new byte[65536], CancellationToken.None);
        owner.Append(capture, CancellationToken.None);
        object table = typeof(ObjectStreamCapture).GetField("_ends", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException("Expected member table.");
        Assert.True(MemoryMarshal.TryGetArray(owner[0], out ArraySegment<byte> backing));
        Assert.NotNull(backing.Array);
        return (owner, new WeakReference(table), new WeakReference(backing.Array));
    }

    [Fact]
    public async Task RetainedDisposedOwnerDropsBothItsTableAndCopies()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        (ObjectStreamCapture owner, WeakReference table, WeakReference bytes) = await Filled(governor);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(table.IsAlive); Assert.True(bytes.IsAlive);
        owner.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(table.IsAlive); Assert.False(bytes.IsAlive);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(owner);
    }
}
