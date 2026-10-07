namespace Lokad.Qpdf.Runtime;

/// <summary>A seekable codec output admitting every capacity and returned copy before allocation.</summary>
internal sealed class GovernedBufferStream : Stream
{
    internal const long ArrayOverheadBytes = 32L;

    private readonly ResourceGovernor _governor;
    private readonly long _maximumBytes;
    private readonly string _limitName;
    private readonly CancellationToken _cancellationToken;
    private byte[] _buffer = Array.Empty<byte>();
    private int _length;
    private int _position;
    private bool _disposed;

    internal GovernedBufferStream(ResourceGovernor governor, long maximumBytes, string limitName)
        : this(governor, maximumBytes, limitName, CancellationToken.None) { }

    internal GovernedBufferStream(ResourceGovernor governor, long maximumBytes, string limitName, CancellationToken cancellationToken)
    {
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        if (maximumBytes < 1L)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _maximumBytes = Math.Min(maximumBytes, Array.MaxLength);
        _limitName = limitName ?? throw new ArgumentNullException(nameof(limitName));
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => false;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;

    public override void WriteByte(byte value)
    {
        Span<byte> single = stackalloc byte[1];
        single[0] = value;
        Write(single);
    }

    internal ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CheckCancellation(CancellationToken.None);
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
        CheckCancellation(CancellationToken.None);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
        => Write(buffer, CancellationToken.None);

    internal void Write(ReadOnlySpan<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckCancellation(cancellationToken);
        long end = (long)_position + buffer.Length;
        if (end > _maximumBytes)
        {
            throw new QpdfLimitException(_limitName, end, _maximumBytes);
        }

        if (buffer.Length == 0)
        {
            return;
        }

        EnsureCapacity((int)end, cancellationToken);
        if (_position > _length)
        {
            Clear(_length, _position - _length, cancellationToken);
        }

        Copy(buffer, _buffer.AsSpan(_position, buffer.Length), cancellationToken);
        _position = (int)end;
        _length = Math.Max(_length, _position);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        Write(buffer.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count), cancellationToken);
        return Task.CompletedTask;
    }

    internal byte[] ToArray()
        => ToArrayCore(null, CancellationToken.None);

    internal byte[] ToArray(ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memory);
        memory.CheckOwner(_governor);
        return ToArrayCore(memory, cancellationToken);
    }

    private byte[] ToArrayCore(ParserMemoryScope? memory, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckCancellation(cancellationToken);
        if (_length == 0)
        {
            return Array.Empty<byte>();
        }

        long reserved = _length + ArrayOverheadBytes;
        if (memory is null) { _governor.ReserveMemory(reserved); }
        else { memory.Reserve(reserved); }
        try
        {
            if (memory is not null) { _governor.ChargeWork(_length); }
            CheckCancellation(cancellationToken);
            byte[] result = new byte[_length];
            Copy(_buffer.AsSpan(0, _length), result, cancellationToken);
            return result;
        }
        catch
        {
            if (memory is null) { _governor.ReleaseMemory(reserved); }
            else { memory.Release(reserved); }
            throw;
        }
    }

    private void EnsureCapacity(int required, CancellationToken cancellationToken)
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
            CheckCancellation(cancellationToken);
            byte[] next = new byte[capacity];
            Copy(_buffer.AsSpan(0, _length), next, cancellationToken);
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

    private void Copy(ReadOnlySpan<byte> source, Span<byte> destination, CancellationToken cancellationToken)
    {
        for (int position = 0; position < source.Length;)
        {
            CheckCancellation(cancellationToken);
            int count = Math.Min(8192, source.Length - position);
            _governor.ChargeWork(count);
            source.Slice(position, count).CopyTo(destination.Slice(position, count));
            position += count;
        }
    }

    private void CheckCancellation(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken.ThrowIfCancellationRequested();
        _governor.CheckCancellation();
    }

    private void CheckLength(long value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckCancellation(CancellationToken.None);
        if (value < 0L)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (value > _maximumBytes)
        {
            throw new QpdfLimitException(_limitName, value, _maximumBytes);
        }
    }

    private void Clear(int start, int length, CancellationToken cancellationToken)
    {
        for (int position = 0; position < length;)
        {
            CheckCancellation(cancellationToken);
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
        EnsureCapacity((int)value, CancellationToken.None);
        if (value > _length)
        {
            Clear(_length, (int)value - _length, CancellationToken.None);
        }

        _length = (int)value;
        _position = Math.Min(_position, _length);
    }
}
