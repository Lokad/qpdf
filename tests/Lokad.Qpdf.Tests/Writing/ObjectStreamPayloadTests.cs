using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamPayloadTests
{
    private static async Task<ObjectStreamCapture> Capture(ResourceGovernor governor, QpdfLimits limits, params byte[][] values)
    {
        ObjectStreamCapture members = ObjectStreamCapture.Create(values.Length, limits, governor, CancellationToken.None);
        try
        {
            foreach (byte[] value in values)
            {
                using BufferedSink sink = BufferedSink.Create(limits, governor, CancellationToken.None);
                await sink.AppendAsync(value, CancellationToken.None);
                members.Append(sink, CancellationToken.None);
            }
            return members;
        }
        catch { members.Dispose(); throw; }
    }

    private static byte[] Decode(ReadOnlyMemory<byte> bytes)
    {
        using MemoryStream input = new(bytes.ToArray());
        using ZLibStream decoder = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalTableOffsetsAndOpaqueBytesAreExact(bool compress)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ObjectStreamCapture members = await Capture(governor, QpdfLimits.Default, "null"u8.ToArray(), "<00FF>"u8.ToArray());
        long before = governor.Snapshot().LiveMemoryBytes;
        using ObjectStreamPayload payload = ObjectStreamPayload.Create(new[] { 7, 42 }, null, members,
            compress, null, QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal(9L, payload.First);
        Assert.Equal(compress, payload.Compressed);
        Assert.Equal("7 0 42 4\nnull<00FF>"u8.ToArray(), compress ? Decode(payload.Data) : payload.Data.ToArray());
        Assert.Equal("null"u8.ToArray(), members[0]);
        payload.Dispose();
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => payload.Data);
    }

    [Fact]
    public async Task QdfOffsetsPointAtValuesAfterTheirComments()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ObjectStreamCapture headers = await Capture(governor, QpdfLimits.Default, "%% A\n"u8.ToArray(), "%% BBB\n"u8.ToArray());
        using ObjectStreamCapture bodies = await Capture(governor, QpdfLimits.Default, "null\n"u8.ToArray(), "false\n"u8.ToArray());
        using ObjectStreamPayload payload = ObjectStreamPayload.CreateQdf(new[] { 7, 42 }, null, headers, bodies,
            QpdfLimits.Default, governor, CancellationToken.None);
        Assert.False(payload.Compressed);
        Assert.Equal(15L, payload.First);
        Assert.Equal("7 0\n42 12\n%% A\nnull\n%% BBB\nfalse\n"u8.ToArray(), payload.Data.ToArray());
        Assert.Equal("null\n"u8.ToArray(), payload.Data.Slice((int)payload.First, 5).ToArray());
        Assert.Equal("false\n"u8.ToArray(), payload.Data.Slice((int)payload.First + 12, 6).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenumberingSharesTheSameByteLayout(bool governed)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ObjectStreamCapture members = await Capture(governor, QpdfLimits.Default, Array.Empty<byte>(), Array.Empty<byte>());
        using ParserMemoryScope mapMemory = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<int> index = GovernedIntDictionary<int>.Create(mapMemory, governor, sizeof(int), CancellationToken.None);
        index.Assign(mapMemory, 1, int.MaxValue); index.Assign(mapMemory, 1025, int.MaxValue - 1);
        Dictionary<int, int> ordinary = new() { [1] = int.MaxValue, [1025] = int.MaxValue - 1 };
        using ObjectStreamPayload payload = governed
            ? ObjectStreamPayload.CreateLinearized(new[] { 1, 1025 }, index, members, false, null, QpdfLimits.Default, governor, CancellationToken.None)
            : ObjectStreamPayload.Create(new[] { 1, 1025 }, ordinary, members, false, null, QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal("2147483647 0 2147483646 0\n"u8.ToArray(), payload.Data.ToArray());
        Assert.Equal(26L, payload.First);
    }

    [Fact]
    public async Task BinaryMembersAreCopiedWithoutInterpretation()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] source = [0, 255, 123, 10];
        using ObjectStreamCapture members = await Capture(governor, QpdfLimits.Default, source);
        using ObjectStreamPayload payload = ObjectStreamPayload.Create(new[] { 1 }, null, members, false, null,
            QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal(new byte[] { 49, 32, 48, 10, 0, 255, 123, 10 }, payload.Data.ToArray());
        Assert.Equal(new byte[] { 0, 255, 123, 10 }, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawArrayAdmissionRejectsBeforeAllocationAndCanRetry(bool compress)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2L * 1024L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] source = new byte[65536]; new Random(101).NextBytes(source);
        using ObjectStreamCapture members = await Capture(governor, limits, source);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - (128L + 32L + source.Length + 4L - 1L));
        long live = governor.Snapshot().LiveMemoryBytes, allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ObjectStreamPayload.Create(new[] { 1 }, null,
            members, compress, null, limits, governor, CancellationToken.None));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(allocated < source.Length / 2L, $"Rejected raw payload allocated {allocated} bytes.");
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(source, members[0]);
        filler.Dispose();
        using ObjectStreamPayload retry = ObjectStreamPayload.Create(new[] { 1 }, null, members, compress, null,
            limits, governor, CancellationToken.None);
        Assert.Equal(source, (compress ? Decode(retry.Data) : retry.Data.ToArray()).AsSpan(4).ToArray());
    }

    [Fact]
    public async Task CodecAdmissionFailureRetiresTheAlreadyBuiltRawPayload()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2L * 1024L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ObjectStreamCapture members = await Capture(governor, limits, new byte[65536]);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes -
            (128L + 32L + 65540L + Lokad.Qpdf.Filters.FlateFilter.DeflateWorkingSetBytes + 256L - 1L));
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ObjectStreamPayload.Create(new[] { 1 }, null,
            members, true, null, limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes > live + 65540L);
        Assert.Equal(65536, members[0].Length);
    }

    [Fact]
    public void HeaderCountsAgainstOutputQuotaBeforeAllocatingThePayload()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 3L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ObjectStreamCapture members = ObjectStreamCapture.Create(1, limits, governor, CancellationToken.None);
        using BufferedSink empty = BufferedSink.Create(limits, governor, CancellationToken.None);
        members.Append(empty, CancellationToken.None);
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ObjectStreamPayload.Create(new[] { 1 }, null,
            members, false, null, limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("missing")]
    [InlineData("negative")]
    public async Task InvalidMembersRejectWithoutDisturbingPriorAdmission(string cause)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ObjectStreamCapture members = await Capture(governor, QpdfLimits.Default, "null"u8.ToArray());
        long live = governor.Snapshot().LiveMemoryBytes;
        int[] numbers = cause == "partial" ? new[] { 1, 2 } : cause == "negative" ? new[] { -1 } : new[] { 1 };
        IReadOnlyDictionary<int, int>? map = cause == "missing" ? new Dictionary<int, int>() : null;
        Assert.Throws<QpdfException>(() => ObjectStreamPayload.Create(numbers, map, members, false, null,
            QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal("null"u8.ToArray(), members[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledFactoryPreservesItsPreparedMembers(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ObjectStreamCapture members = await Capture(governor, QpdfLimits.Default, "null"u8.ToArray());
        long live = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ObjectStreamPayload.Create(new[] { 1 }, null, members,
            true, null, QpdfLimits.Default, governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ObjectStreamPayload Payload, WeakReference Raw, WeakReference Stored) Owned(ObjectStreamCapture members,
        ResourceGovernor governor, bool compress)
    {
        ObjectStreamPayload payload = ObjectStreamPayload.Create(new[] { 1 }, null, members, compress, null,
            QpdfLimits.Default, governor, CancellationToken.None);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object raw = typeof(ObjectStreamPayload).GetField("_raw", flags)?.GetValue(payload)
            ?? throw new InvalidOperationException("Expected raw payload.");
        object stored = typeof(ObjectStreamPayload).GetField("_stored", flags)?.GetValue(payload)
            ?? throw new InvalidOperationException("Expected stored payload.");
        return (payload, new WeakReference(raw), new WeakReference(stored));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedDisposedOwnerDropsRawAndCompressedBacking(bool compress)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ObjectStreamCapture members = await Capture(governor, QpdfLimits.Default, new byte[65536]);
        long before = governor.Snapshot().LiveMemoryBytes;
        (ObjectStreamPayload payload, WeakReference raw, WeakReference stored) = Owned(members, governor, compress);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(raw.IsAlive); Assert.True(stored.IsAlive);
        payload.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(raw.IsAlive); Assert.False(stored.IsAlive);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(payload);
    }
}
