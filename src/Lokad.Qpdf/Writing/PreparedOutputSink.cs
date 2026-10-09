using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Scopes serialized output without imposing a contiguous-array size ceiling.</summary>
internal sealed class PreparedOutputSink : IByteSink, IDisposable
{
    internal sealed class Segment(byte[] bytes)
    {
        internal byte[] Bytes = bytes;
        internal Segment? Next;
        internal int Used;
        internal ReadOnlyMemory<byte> Written => Bytes.AsMemory(0, Used);
    }

    private const int SegmentCapacity = 32768;
    private readonly ResourceGovernor _governor;
    private readonly CancellationToken _token;
    private readonly long _maximum;
    private ParserMemoryScope? _memory;
    private Segment? _head, _tail;
    private long _length, _segments;

    private PreparedOutputSink(ResourceGovernor governor, CancellationToken token, long maximum, ParserMemoryScope memory)
    { _governor = governor; _token = token; _maximum = maximum; _memory = memory; }

    internal static PreparedOutputSink Create(QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(80L); return new(governor, token, limits.MaxOutputBytes, memory); }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(PreparedOutputSink));
    public long Length { get { _ = Memory; return _length; } }
    internal long SegmentCount { get { _ = Memory; return _segments; } }
    internal Segment? First { get { _ = Memory; return _head; } }

    public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        _ = Memory; CheckCancellation(cancellationToken);
        if (chunk.Length > _maximum - _length)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), checked(_length + chunk.Length), _maximum); }
        ReadOnlySpan<byte> remaining = chunk.Span;
        while (!remaining.IsEmpty)
        {
            CheckCancellation(cancellationToken); _governor.ChargeWork(1L);
            Segment tail = EnsureTail(Math.Min(SegmentCapacity, remaining.Length), cancellationToken);
            int count = Math.Min(remaining.Length, tail.Bytes.Length - tail.Used);
            Copy(remaining[..count], tail.Bytes.AsSpan(tail.Used, count), cancellationToken);
            tail.Used += count; _length += count; remaining = remaining[count..];
        }
        return ValueTask.CompletedTask;
    }

    private Segment EnsureTail(int incoming, CancellationToken token)
    {
        if (_tail is Segment existing && existing.Used < existing.Bytes.Length) { return existing; }
        if (_tail is Segment growing && growing.Bytes.Length < SegmentCapacity)
        {
            int capacity = Math.Min(SegmentCapacity, Math.Max(growing.Used + incoming, growing.Bytes.Length * 2));
            long bytes = ArrayBytes(capacity);
            _governor.ChargeWork(capacity); Memory.Reserve(bytes);
            try
            {
                CheckCancellation(token);
                byte[] next = new byte[capacity]; Copy(growing.Written.Span, next, token);
                long previous = ArrayBytes(growing.Bytes.Length);
                // The segment object survives capacity growth, while the old
                // array becomes unreachable before its admission is released.
                growing.Bytes = next;
                Memory.Release(previous); return growing;
            }
            catch { Memory.Release(bytes); throw; }
        }
        int initial = Math.Max(1, incoming);
        long reservation = 40L + ArrayBytes(initial);
        _governor.ChargeWork(initial); Memory.Reserve(reservation);
        try
        {
            CheckCancellation(token);
            Segment segment = new(new byte[initial]);
            if (_tail is null) { _head = segment; } else { _tail.Next = segment; }
            _tail = segment; _segments++; return segment;
        }
        catch { Memory.Release(reservation); throw; }
    }

    private static long ArrayBytes(int capacity) => (24L + capacity + 7L) & ~7L;

    private void CheckCancellation(CancellationToken token)
    { token.ThrowIfCancellationRequested(); _token.ThrowIfCancellationRequested(); _governor.CheckCancellation(); }

    private void Copy(ReadOnlySpan<byte> source, Span<byte> destination, CancellationToken token)
    {
        for (int offset = 0; offset < source.Length;)
        {
            int count = Math.Min(256, source.Length - offset);
            CheckCancellation(token); _governor.ChargeWork(count);
            source.Slice(offset, count).CopyTo(destination.Slice(offset, count)); offset += count;
        }
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null; _head = null; _tail = null; _length = 0L; _segments = 0L;
        memory?.Dispose();
    }
}
