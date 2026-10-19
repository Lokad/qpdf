namespace Lokad.Qpdf.Runtime;

/// <summary>A seekable codec output admitting every capacity and returned copy before allocation.</summary>
internal sealed class GovernedBufferStream : Stream
{
    internal const long ArrayOverheadBytes = 32L;

    private readonly ResourceGovernor _governor;
    private readonly long _maximumBytes;
    private readonly string _limitName;
    private readonly CancellationToken _cancellationToken;
    private readonly int _initialCapacity;
    private byte[] _buffer = Array.Empty<byte>();
    private int _length;
    private int _position;
    private bool _disposed;

    internal GovernedBufferStream(ResourceGovernor governor, long maximumBytes, string limitName)
        : this(governor, maximumBytes, limitName, CancellationToken.None) { }

    internal GovernedBufferStream(ResourceGovernor governor, long maximumBytes, string limitName, CancellationToken cancellationToken)
        : this(governor, maximumBytes, limitName, cancellationToken, 8192) { }

    internal GovernedBufferStream(ResourceGovernor governor, long maximumBytes, string limitName, CancellationToken cancellationToken,
        int initialCapacity)
    {
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        if (maximumBytes < 1L)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _maximumBytes = Math.Min(maximumBytes, Array.MaxLength);
        _limitName = limitName ?? throw new ArgumentNullException(nameof(limitName));
        _cancellationToken = cancellationToken;
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _initialCapacity = initialCapacity;
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

    internal ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer.AsMemory(0, _length);
        }
    }

    // Scratch reset retains admitted capacity but drops its logical contents.
    // It is constant-time cleanup after a completed copy, so it cannot cancel.
    internal void ResetWrittenBytes()
        => TruncateWrittenBytes(0);

    // Roll back a failed append without cancellation or changing admitted capacity.
    internal void TruncateWrittenBytes(int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _length);
        _length = length;
        _position = length;
    }

    internal void PrepareCapacity(int count, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckCancellation(token);
        CheckLength(count);
        EnsureCapacity(count, token);
    }

    // Internal writers fill this bounded region synchronously before any
    // snapshot or publication. A failed fill abandons the owning operation.
    internal Span<byte> AppendRegion(int count, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckCancellation(token);
        int next = checked(_length + count);
        CheckLength(next);
        EnsureCapacity(next, token);
        Span<byte> region = _buffer.AsSpan(_length, count);
        _length = next; _position = next;
        return region;
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
            if (memory is not null) { _governor.NoteOperation(); }
            CheckCancellation(cancellationToken);
            byte[] result = GC.AllocateUninitializedArray<byte>(_length);
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

        long growth = _buffer.Length == 0 ? _initialCapacity : 2L * _buffer.Length;
        int capacity = (int)Math.Min(_maximumBytes, Math.Max(required, growth));
        long reserved = capacity + ArrayOverheadBytes;
        _governor.NoteOperation();
        _governor.ReserveMemory(reserved);
        try
        {
            CheckCancellation(cancellationToken);
            // Only written bytes are copied. Write and SetLength explicitly
            // clear gaps before exposing them; unused capacity stays private.
            byte[] next = GC.AllocateUninitializedArray<byte>(capacity);
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
        => CopyBytes(source, destination, _governor, cancellationToken, _cancellationToken);

    internal static void CopyBytes(ReadOnlySpan<byte> source, Span<byte> destination, ResourceGovernor governor,
        CancellationToken firstToken, CancellationToken secondToken)
    {
        for (int position = 0; position < source.Length;)
        {
            firstToken.ThrowIfCancellationRequested();
            secondToken.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            int count = Math.Min(8192, source.Length - position);
            governor.NoteOperation();
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
            _governor.NoteOperation();
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
