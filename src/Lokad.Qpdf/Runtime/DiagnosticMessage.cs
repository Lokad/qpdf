using System.Globalization;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Runtime;

/// <summary>Borrowed text or a bounded integer, evaluated before formatting begins.</summary>
internal readonly struct DiagnosticPart
{
    private readonly object? _source;
    private readonly long _number;
    private readonly bool _numeric;

    private DiagnosticPart(object? source, long number, bool numeric)
    {
        _source = source;
        _number = number;
        _numeric = numeric;
    }

    public static implicit operator DiagnosticPart(string? text) => new(text, 0L, false);
    public static implicit operator DiagnosticPart(int number) => (DiagnosticPart)(long)number;
    public static implicit operator DiagnosticPart(long number)
        => new(number < 0L ? CultureInfo.CurrentCulture.NumberFormat.NegativeSign : null, number, true);

    internal static DiagnosticPart Invariant(long number) => new(number < 0L ? "-" : null, number, true);
    internal static DiagnosticPart Name(PdfName name) => new(name, 0L, false);
    internal static DiagnosticPart DecodeLevel(QpdfStreamDecodeLevel level) => level switch
    {
        QpdfStreamDecodeLevel.None => nameof(QpdfStreamDecodeLevel.None),
        QpdfStreamDecodeLevel.Generalized => nameof(QpdfStreamDecodeLevel.Generalized),
        QpdfStreamDecodeLevel.Specialized => nameof(QpdfStreamDecodeLevel.Specialized),
        QpdfStreamDecodeLevel.All => nameof(QpdfStreamDecodeLevel.All),
        _ => Invariant((long)level),
    };

    internal ReadOnlySpan<char> Prefix => (_source as string).AsSpan();
    internal ReadOnlySpan<byte> RawName => _source is PdfName name ? name.RawSpan : ReadOnlySpan<byte>.Empty;

    // A sign is borrowed separately, so even a caller's long/custom sign needs
    // no ungoverned temporary string or unbounded numeric formatting buffer.
    internal int WriteNumber(ResourceGovernor governor, Span<char> digits)
    {
        if (!_numeric) { return 0; }
        ulong magnitude = _number < 0L ? (ulong)(-(_number + 1L)) + 1UL : (ulong)_number;
        int written = 0;
        do
        {
            governor.ChargeWork(1L);
            digits[digits.Length - ++written] = (char)('0' + magnitude % 10UL);
            magnitude /= 10UL;
        }
        while (magnitude != 0UL);
        return written;
    }
}

internal readonly record struct DiagnosticTextInfo(int Length, long Utf8Bytes, int Hash, bool Ascii);

/// <summary>Shares quota/hash scanning and admitted copying without formatting intermediates.</summary>
internal static class DiagnosticMessage
{
    private struct Scanner
    {
        internal int Length;
        internal long Bytes;
        internal int Hash;
        internal bool Ascii;
        internal bool HighSurrogate;

        internal void Append(ResourceGovernor governor, ReadOnlySpan<char> text, bool diagnostic)
        {
            if (text.Length > Array.MaxLength - Length)
            {
                throw new QpdfParseException("Diagnostic text exceeds supported capacity.", null, null);
            }
            Length += text.Length;
            for (int offset = 0; offset < text.Length; offset += Math.Min(256, text.Length - offset))
            {
                int count = Math.Min(256, text.Length - offset);
                governor.ChargeWork(count);
                governor.CheckCancellation();
                for (int index = offset; index < offset + count; index++)
                {
                    char value = text[index];
                    Ascii &= value <= 127;
                    Hash = unchecked(Hash * 33 ^ value);
                    Bytes += value <= 127 ? 1 : value <= 2047 ? 2 : HighSurrogate && char.IsLowSurrogate(value) ? 1 : 3;
                    HighSurrogate = char.IsHighSurrogate(value);
                }
                if (diagnostic) { governor.CheckDiagnostic(Bytes); }
            }
        }
    }

    internal static DiagnosticTextInfo Check(ResourceGovernor governor, ReadOnlySpan<DiagnosticPart> parts)
    {
        governor.CheckDiagnostic(0L);
        return Measure(governor, parts, true);
    }

    internal static DiagnosticTextInfo Measure(ResourceGovernor governor, ReadOnlySpan<DiagnosticPart> parts)
        => Measure(governor, parts, false);

    private static DiagnosticTextInfo Measure(ResourceGovernor governor, ReadOnlySpan<DiagnosticPart> parts, bool diagnostic)
    {
        governor.CheckCancellation();
        Scanner scanner = new() { Hash = 5381, Ascii = true };
        Span<char> digits = stackalloc char[20];
        Span<char> escaped = stackalloc char[3];
        foreach (DiagnosticPart part in parts)
        {
            governor.ChargeWork(1L);
            governor.CheckCancellation();
            int written = part.WriteNumber(governor, digits);
            scanner.Append(governor, part.Prefix, diagnostic);
            scanner.Append(governor, digits.Slice(digits.Length - written, written), diagnostic);
            foreach (byte value in part.RawName)
            {
                governor.ChargeWork(1L);
                int count = PdfName.WriteDisplayByte(value, escaped);
                scanner.Append(governor, escaped.Slice(0, count), diagnostic);
            }
        }
        return new DiagnosticTextInfo(scanner.Length, scanner.Bytes, scanner.Hash, scanner.Ascii);
    }

    internal static void Copy(ResourceGovernor governor, ReadOnlySpan<DiagnosticPart> parts, Span<char> target)
    {
        Span<char> digits = stackalloc char[20];
        Span<char> escaped = stackalloc char[3];
        int position = 0;
        foreach (DiagnosticPart part in parts)
        {
            governor.ChargeWork(1L);
            governor.CheckCancellation();
            int written = part.WriteNumber(governor, digits);
            CopyPart(governor, part.Prefix, target, ref position);
            CopyPart(governor, digits.Slice(digits.Length - written, written), target, ref position);
            foreach (byte value in part.RawName)
            {
                governor.ChargeWork(1L);
                int count = PdfName.WriteDisplayByte(value, escaped);
                CopyPart(governor, escaped.Slice(0, count), target, ref position);
            }
        }
        if (position != target.Length) { throw new InvalidOperationException("Diagnostic formatting length changed."); }
    }

    private static void CopyPart(ResourceGovernor governor, ReadOnlySpan<char> text, Span<char> target, ref int position)
    {
        for (int offset = 0; offset < text.Length; offset += Math.Min(256, text.Length - offset))
        {
            int count = Math.Min(256, text.Length - offset);
            governor.ChargeWork(count);
            governor.CheckCancellation();
            text.Slice(offset, count).CopyTo(target.Slice(position, count));
            position += count;
        }
    }

    internal static bool Matches(ResourceGovernor governor, ReadOnlySpan<DiagnosticPart> parts, string candidate)
    {
        Span<char> digits = stackalloc char[20];
        Span<char> escaped = stackalloc char[3];
        int position = 0;
        foreach (DiagnosticPart part in parts)
        {
            governor.ChargeWork(1L);
            governor.CheckCancellation();
            int written = part.WriteNumber(governor, digits);
            if (!MatchPart(governor, part.Prefix, candidate, ref position)
                || !MatchPart(governor, digits.Slice(digits.Length - written, written), candidate, ref position)) { return false; }
            foreach (byte value in part.RawName)
            {
                governor.ChargeWork(1L);
                int count = PdfName.WriteDisplayByte(value, escaped);
                if (!MatchPart(governor, escaped.Slice(0, count), candidate, ref position)) { return false; }
            }
        }
        return position == candidate.Length;
    }

    private static bool MatchPart(ResourceGovernor governor, ReadOnlySpan<char> text, string candidate, ref int position)
    {
        if (text.Length > candidate.Length - position) { return false; }
        for (int offset = 0; offset < text.Length; offset += Math.Min(256, text.Length - offset))
        {
            int count = Math.Min(256, text.Length - offset);
            governor.ChargeWork(count);
            governor.CheckCancellation();
            if (!text.Slice(offset, count).SequenceEqual(candidate.AsSpan(position, count))) { return false; }
            position += count;
        }
        return true;
    }
}
