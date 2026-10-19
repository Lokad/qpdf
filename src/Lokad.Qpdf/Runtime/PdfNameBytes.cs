namespace Lokad.Qpdf.Runtime;

/// <summary>Borrowed raw or hash-escape-decoded name bytes.</summary>
internal readonly ref struct PdfNameBytes
{
    private readonly ReadOnlySpan<byte> _raw;
    private readonly bool _decode;
    internal int Length { get; }
    internal bool IsDecoded => _decode;
    internal ReadOnlySpan<byte> Raw => _raw;
    internal PdfNameBytes(ReadOnlySpan<byte> raw) { _raw = raw; _decode = false; Length = raw.Length; }
    private PdfNameBytes(ReadOnlySpan<byte> raw, int length) { _raw = raw; _decode = true; Length = length; }

    internal static PdfNameBytes Decode(ReadOnlySpan<byte> raw, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        Cursor cursor = new(raw, true); int length = 0;
        while (cursor.TryRead(out _))
        { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); length++; }
        return new(raw, length);
    }
    internal Cursor GetCursor() => new(_raw, _decode);
    internal void CopyTo(Span<byte> target, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (target.Length != Length) { throw new ArgumentException("Name destination length differs.", nameof(target)); }
        Cursor cursor = GetCursor(); int index = 0;
        while (cursor.TryRead(out byte value))
        { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); target[index++] = value; }
    }

    internal ref struct Cursor(ReadOnlySpan<byte> raw, bool decode)
    {
        private readonly ReadOnlySpan<byte> _raw = raw;
        private readonly bool _decode = decode;
        private int _index;
        internal bool TryRead(out byte value)
        {
            if (_index == _raw.Length) { value = 0; return false; }
            value = _raw[_index++];
            if (_decode && value == (byte)'#' && _raw.Length - _index >= 2)
            {
                int high = Hex(_raw[_index]), low = Hex(_raw[_index + 1]);
                if (high >= 0 && low >= 0) { value = (byte)((high << 4) | low); _index += 2; }
            }
            return true;
        }
        private static int Hex(byte value) => value is >= (byte)'0' and <= (byte)'9' ? value - (byte)'0'
            : value is >= (byte)'a' and <= (byte)'f' ? value - (byte)'a' + 10
            : value is >= (byte)'A' and <= (byte)'F' ? value - (byte)'A' + 10 : -1;
    }
}
