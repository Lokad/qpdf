namespace Lokad.Qpdf.Runtime;

/// <summary>A seekable codec output admitting every capacity and returned copy before allocation.</summary>
internal sealed class GovernedBufferStream : Stream
{
    internal const long ArrayOverheadBytes = 32L;

    private readonly ResourceGovernor _governor;
    private readonly long _maximumBytes;
    private readonly string _limitName;
    private byte[] _buffer = Array.Empty<byte>();
    private int _length;
    private int _position;
    private bool _disposed;

    internal GovernedBufferStream(ResourceGovernor governor, long maximumBytes, string limitName)
    {
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        if (maximumBytes < 1L)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _maximumBytes = Math.Min(maximumBytes, Array.MaxLength);
        _limitName = limitName ?? throw new ArgumentNullException(nameof(limitName));
    }

    public override bool CanRead => false;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;

    internal ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _governor.CheckCancellation();
            return _buffer.AsSpan(0, _length);
        }
    }

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set
        {
            CheckLength(value);
            _position = (int)value;
        }
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _governor.CheckCancellation();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _governor.CheckCancellation();
        long end = (long)_position + buffer.Length;
        if (end > _maximumBytes)
        {
            throw new QpdfLimitException(_limitName, end, _maximumBytes);
        }

        if (buffer.Length == 0)
        {
            return;
        }

        EnsureCapacity((int)end);
        if (_position > _length)
        {
            Clear(_length, _position - _length);
        }

        Copy(buffer, _buffer.AsSpan(_position, buffer.Length));
        _position = (int)end;
        _length = Math.Max(_length, _position);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    internal byte[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _governor.CheckCancellation();
        if (_length == 0)
        {
            return Array.Empty<byte>();
        }

        long reserved = _length + ArrayOverheadBytes;
        _governor.ReserveMemory(reserved);
        try
        {
            byte[] result = new byte[_length];
            Copy(_buffer.AsSpan(0, _length), result);
            return result;
        }
        catch
        {
            _governor.ReleaseMemory(reserved);
            throw;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        long growth = _buffer.Length == 0 ? 8192L : 2L * _buffer.Length;
        int capacity = (int)Math.Min(_maximumBytes, Math.Max(required, growth));
        long reserved = capacity + ArrayOverheadBytes;
        _governor.ChargeWork(capacity);
        _governor.ReserveMemory(reserved);
        try
        {
            byte[] next = new byte[capacity];
            Copy(_buffer.AsSpan(0, _length), next);
            long previous = _buffer.Length == 0 ? 0L : _buffer.Length + ArrayOverheadBytes;
            _buffer = next;
            _governor.ReleaseMemory(previous);
        }
        catch
        {
            _governor.ReleaseMemory(reserved);
            throw;
        }
    }

    private void Copy(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (int position = 0; position < source.Length;)
        {
            _governor.CheckCancellation();
            int count = Math.Min(8192, source.Length - position);
            _governor.ChargeWork(count);
            source.Slice(position, count).CopyTo(destination.Slice(position, count));
            position += count;
        }
    }

    private void CheckLength(long value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _governor.CheckCancellation();
        if (value < 0L)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (value > _maximumBytes)
        {
            throw new QpdfLimitException(_limitName, value, _maximumBytes);
        }
    }

    private void Clear(int start, int length)
    {
        for (int position = 0; position < length;)
        {
            _governor.CheckCancellation();
            int count = Math.Min(8192, length - position);
            _governor.ChargeWork(count);
            _buffer.AsSpan(start + position, count).Clear();
            position += count;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            long reserved = _buffer.Length == 0 ? 0L : _buffer.Length + ArrayOverheadBytes;
            _buffer = Array.Empty<byte>();
            _length = 0;
            _position = 0;
            _disposed = true;
            _governor.ReleaseMemory(reserved);
        }

        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)
    {
        long basis = origin switch
        {
            SeekOrigin.Begin => 0L,
            SeekOrigin.Current => Position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = checked(basis + offset);
        return Position;
    }

    public override void SetLength(long value)
    {
        CheckLength(value);
        EnsureCapacity((int)value);
        if (value > _length)
        {
            Clear(_length, (int)value - _length);
        }

        _length = (int)value;
        _position = Math.Min(_position, _length);
    }
}
