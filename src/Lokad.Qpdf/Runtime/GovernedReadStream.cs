namespace Lokad.Qpdf.Runtime;

/// <summary>Bounded codec input that rejects exhaustion before the inflater finishes its frame.</summary>
internal sealed class GovernedReadStream : Stream
{
    internal const int MaximumReadBytes = 8192;

    private readonly ResourceGovernor _governor;
    private CancellationToken _token;
    private ReadOnlyMemory<byte> _input;
    private int _position;
    private bool _disposed;

    internal GovernedReadStream(ReadOnlyMemory<byte> input, ResourceGovernor governor, CancellationToken token)
    {
        _input = input;
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _token = token;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _token.ThrowIfCancellationRequested();
        _governor.CheckCancellation();
        int count = Math.Min(Math.Min(buffer.Length, MaximumReadBytes), _input.Length - _position);
        _governor.NoteOperation();
        // A complete frame makes the inflater stop without requesting more
        // input. Returning EOF here would let BCL inflate accept truncation.
        if (count == 0 && buffer.Length != 0)
        { throw new InvalidDataException("Compressed input ended before its frame completed."); }
        _input.Span.Slice(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        _input = ReadOnlyMemory<byte>.Empty;
        _token = default;
        _disposed = true;
        base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
