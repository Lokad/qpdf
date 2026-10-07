namespace Lokad.Qpdf.Writing;

/// <summary>A borrowed field consumed synchronously by the shared byte formatter.</summary>
internal readonly struct WriterPart
{
    internal enum Kind { Empty, Text, Number, ZeroPaddedNumber, RightPaddedNumber, Spaces, Hex }

    internal Kind Type { get; }
    internal string? Value { get; }
    internal ReadOnlyMemory<byte> Bytes { get; }
    internal long Integer { get; }
    internal int Width { get; }

    private WriterPart(Kind type, string? value, ReadOnlyMemory<byte> bytes, long integer, int width)
    {
        Type = type;
        Value = value;
        Bytes = bytes;
        Integer = integer;
        Width = width;
    }

    internal static WriterPart Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(Kind.Text, value, ReadOnlyMemory<byte>.Empty, 0L, 0);
    }

    internal static WriterPart Number(long value) => new(Kind.Number, null, ReadOnlyMemory<byte>.Empty, value, 0);

    internal static WriterPart ZeroPaddedNumber(long value, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        return new(Kind.ZeroPaddedNumber, null, ReadOnlyMemory<byte>.Empty, value, digits);
    }

    internal static WriterPart RightPaddedNumber(long value, int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        return new(Kind.RightPaddedNumber, null, ReadOnlyMemory<byte>.Empty, value, width);
    }

    internal static WriterPart Spaces(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new(Kind.Spaces, null, ReadOnlyMemory<byte>.Empty, 0L, count);
    }

    internal static WriterPart Hex(ReadOnlyMemory<byte> bytes) => new(Kind.Hex, null, bytes, 0L, 0);
}
