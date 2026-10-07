using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns an exact cross-reference body and its optional compressed result.</summary>
internal sealed class WriterXref : IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private CancellationToken _token;
    private byte[]? _raw;
    private byte[]? _stored;
    private readonly int _field1;
    private readonly int _field2;
    private int _position;

    private WriterXref(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token,
        byte[] raw, int field1, int field2)
    { _memory = memory; _governor = governor; _token = token; _raw = raw; _field1 = field1; _field2 = field2; }

    internal static WriterXref Create(int count, int field1, int field2, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(field1); ArgumentOutOfRangeException.ThrowIfGreaterThan(field1, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(field2); ArgumentOutOfRangeException.ThrowIfGreaterThan(field2, 8);
        ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(governor);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        long length = checked((long)count * (1L + field1 + field2));
        if (length > limits.MaxOutputBytes)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, limits.MaxOutputBytes); }
        if (length > Array.MaxLength) { throw new QpdfException("Cross-reference body exceeds the supported capacity."); }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(96L + (length == 0 ? 0L : 32L + length));
            governor.ChargeWork(1L + length);
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            return new WriterXref(memory, governor, token, length == 0 ? Array.Empty<byte>() : new byte[(int)length], field1, field2);
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(WriterXref));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(WriterXref));
    private byte[] Raw => _raw ?? throw new InvalidOperationException("Cross-reference body has been completed.");
    private void Check()
    { _ = Memory; _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); }

    internal void Append(byte type, long field1, long field2)
    {
        Check();
        if (_stored is not null) { throw new InvalidOperationException("Cross-reference body has been completed."); }
        byte[] raw = Raw;
        int width = 1 + _field1 + _field2;
        if (_position == raw.Length) { throw new QpdfException("Cross-reference body exceeded its admitted capacity."); }
        Governor.ChargeWork(1L + width);
        raw[_position] = type;
        WriterNumber.WriteUInt(raw.AsSpan(_position + 1, _field1), field1);
        WriterNumber.WriteUInt(raw.AsSpan(_position + 1 + _field1, _field2), field2);
        _position += width;
    }

    internal void Finish(bool compress, int? level, QpdfLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Check();
        if (_stored is not null) { throw new InvalidOperationException("Cross-reference body has been completed."); }
        if (_position != Raw.Length) { throw new QpdfException("Cross-reference body is incomplete."); }
        long rawReservation = Raw.Length == 0 ? 0L : 32L + Raw.Length;
        Governor.ChargeWork(1L);
        byte[] stored = compress ? FlateFilter.Encode(Raw, level, limits, Governor, Memory, _token) : Raw;
        _stored = stored;
        if (compress) { _raw = null; Memory.Release(rawReservation); }
    }

    internal ReadOnlyMemory<byte> Data
    {
        get { _ = Memory; return _stored ?? throw new InvalidOperationException("Cross-reference body is incomplete."); }
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _raw = null; _stored = null; _memory = null; _governor = null; _token = CancellationToken.None;
        memory?.Dispose();
    }
}
