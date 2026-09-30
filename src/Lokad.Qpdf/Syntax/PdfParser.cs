using System.Globalization;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Syntax;

/// <summary>Resolves indirect references during parsing (wired to xref in 05b).</summary>
internal interface IIndirectResolver
{
    /// <summary>Tries to resolve a reference to its object.</summary>
    bool TryResolve(PdfReference reference, out QpdfObject? value);
}

/// <summary>
/// Strict PDF object parser over bytes. Containers recurse with an explicit
/// depth budget; every value charges work and every copy reserves memory
/// before allocating. Duplicate dictionary keys resolve last-wins with a
/// recorded warning, matching the upstream reference behavior.
/// </summary>
internal sealed class PdfParser
{
    private const int MaxNumberTextLength = 1024;

    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private readonly DiagnosticBag _diagnostics;
    private readonly IIndirectResolver? _resolver;

    internal PdfParser(QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, IIndirectResolver? resolver)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        _limits = limits;
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _resolver = resolver;
    }

    internal QpdfObject ParseValue(PdfTokenizer tokenizer, int depth)
    {
        if (tokenizer is null)
        {
            throw new ArgumentNullException(nameof(tokenizer));
        }

        if (depth > _limits.MaxNestingDepth)
        {
            throw new QpdfParseException($"Nesting depth exceeds {_limits.MaxNestingDepth}.", tokenizer.Position, null);
        }

        _governor.ChargeWork(1L);
        if (!tokenizer.TryReadToken(out PdfToken token))
        {
            throw new QpdfParseException("Unexpected end of input while parsing a value.", tokenizer.Position, null);
        }

        switch (token.Kind)
        {
            case PdfTokenKind.Null:
                return PdfNull.Instance;
            case PdfTokenKind.True:
                return PdfBoolean.True;
            case PdfTokenKind.False:
                return PdfBoolean.False;
            case PdfTokenKind.Integer:
                return ParseIntegerOrReference(tokenizer, token);
            case PdfTokenKind.Real:
                return ParseReal(token);
            case PdfTokenKind.Name:
                return ParseName(token);
            case PdfTokenKind.LiteralString:
                return new PdfByteString(UnescapeLiteral(token.Data, token.Offset), hex: false);
            case PdfTokenKind.HexString:
                return new PdfByteString(DecodeHex(token.Data, token.Offset), hex: true);
            case PdfTokenKind.ArrayOpen:
                return ParseArray(tokenizer, depth);
            case PdfTokenKind.DictionaryOpen:
                return ParseDictionaryOrStream(tokenizer, depth, token.Offset);
            default:
                throw new QpdfParseException($"Unexpected token {token.Kind} while parsing a value.", token.Offset, null);
        }
    }

    internal (int Number, int Generation, QpdfObject Value) ParseIndirectObject(PdfTokenizer tokenizer, int depth)
    {
        if (tokenizer is null)
        {
            throw new ArgumentNullException(nameof(tokenizer));
        }

        if (!tokenizer.TryReadToken(out PdfToken numberToken) || numberToken.Kind != PdfTokenKind.Integer)
        {
            throw new QpdfParseException("Expected an object number.", tokenizer.Position, null);
        }

        if (!tokenizer.TryReadToken(out PdfToken generationToken) || generationToken.Kind != PdfTokenKind.Integer)
        {
            throw new QpdfParseException("Expected an object generation.", numberToken.Offset, null);
        }

        if (!tokenizer.TryReadToken(out PdfToken objToken) || objToken.Kind != PdfTokenKind.Object)
        {
            throw new QpdfParseException("Expected 'obj'.", numberToken.Offset, null);
        }

        int number = ParseObjectNumber(numberToken);
        int generation = ParseGeneration(generationToken);
        QpdfObject value = ParseValue(tokenizer, depth);
        if (!tokenizer.TryReadToken(out PdfToken endToken) || endToken.Kind != PdfTokenKind.EndObject)
        {
            throw new QpdfParseException("Expected 'endobj'.", numberToken.Offset, number);
        }

        return (number, generation, value);
    }

    private QpdfObject ParseIntegerOrReference(PdfTokenizer tokenizer, PdfToken first)
    {
        long saved = tokenizer.Position;
        if (tokenizer.TryReadToken(out PdfToken second) && second.Kind == PdfTokenKind.Integer
            && tokenizer.TryReadToken(out PdfToken third) && third.Kind == PdfTokenKind.Reference)
        {
            int number = ParseObjectNumber(first);
            int generation = ParseGeneration(second);
            return new PdfReference(number, generation);
        }

        tokenizer.SetPosition(saved);
        return ParseInteger(first);
    }

    private static int ParseObjectNumber(PdfToken token)
    {
        long value = ParseIntegerText(token);
        if (value < 0 || value > int.MaxValue)
        {
            throw new QpdfParseException($"Object number {value} is out of range.", token.Offset, null);
        }

        return (int)value;
    }

    private static int ParseGeneration(PdfToken token)
    {
        long value = ParseIntegerText(token);
        if (value < 0 || value > 65535)
        {
            throw new QpdfParseException($"Generation {value} is out of range.", token.Offset, null);
        }

        return (int)value;
    }

    private static PdfInteger ParseInteger(PdfToken token)
    {
        return new PdfInteger(ToAscii(token.Data.Span, token.Offset), ParseIntegerText(token));
    }

    private static long ParseIntegerText(PdfToken token)
    {
        ReadOnlySpan<byte> span = token.Data.Span;
        if (span.Length > MaxNumberTextLength)
        {
            throw new QpdfParseException($"Numeric token exceeds {MaxNumberTextLength} characters.", token.Offset, null);
        }

        int i = 0;
        bool negative = false;
        if (span[0] == (byte)'+' || span[0] == (byte)'-')
        {
            negative = span[0] == (byte)'-';
            i = 1;
        }

        long value = 0L;
        try
        {
            checked
            {
                for (; i < span.Length; i++)
                {
                    value = (value * 10L) + (span[i] - (byte)'0');
                }

                if (negative)
                {
                    value = -value;
                }
            }
        }
        catch (OverflowException ex)
        {
            throw new QpdfParseException("Integer token overflows 64 bits.", token.Offset, null, ex);
        }

        return value;
    }

    private static PdfReal ParseReal(PdfToken token)
    {
        ReadOnlySpan<byte> span = token.Data.Span;
        if (span.Length > MaxNumberTextLength)
        {
            throw new QpdfParseException($"Numeric token exceeds {MaxNumberTextLength} characters.", token.Offset, null);
        }

        string text = ToAscii(span, token.Offset);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new QpdfParseException($"Malformed real number '{text}'.", token.Offset, null);
        }

        return new PdfReal(text, value);
    }

    private QpdfObject ParseName(PdfToken token)
    {
        byte[] raw = UnescapeName(token.Data, token.Offset);
        _governor.ReserveMemory(raw.Length);
        return new PdfName(raw);
    }

    private QpdfObject ParseArray(PdfTokenizer tokenizer, int depth)
    {
        _governor.ReserveMemory(32L);
        List<QpdfObject> items = new();
        while (true)
        {
            if (!tokenizer.TryReadToken(out PdfToken token))
            {
                throw new QpdfParseException("Unterminated array.", tokenizer.Position, null);
            }

            if (token.Kind == PdfTokenKind.ArrayClose)
            {
                return new PdfArray(items);
            }

            tokenizer.SetPosition(token.Offset);
            _governor.ReserveMemory(16L);
            items.Add(ParseValue(tokenizer, depth + 1));
        }
    }

    private QpdfObject ParseDictionaryOrStream(PdfTokenizer tokenizer, int depth, long offset)
    {
        _governor.ReserveMemory(64L);
        Dictionary<PdfName, QpdfObject> entries = new();
        while (true)
        {
            if (!tokenizer.TryReadToken(out PdfToken token))
            {
                throw new QpdfParseException("Unterminated dictionary.", tokenizer.Position, null);
            }

            if (token.Kind == PdfTokenKind.DictionaryClose)
            {
                break;
            }

            if (token.Kind != PdfTokenKind.Name)
            {
                throw new QpdfParseException($"Dictionary key must be a name, not {token.Kind}.", token.Offset, null);
            }

            PdfName key = (PdfName)ParseName(token);
            QpdfObject value = ParseValue(tokenizer, depth + 1);
            if (entries.ContainsKey(key))
            {
                _diagnostics.Add(
                    QpdfDiagnosticCode.DuplicateKey,
                    QpdfDiagnosticSeverity.Warning,
                    $"Duplicate dictionary key '{key.DisplayText}' keeps the last value.");
            }

            entries[key] = value;
            _governor.ReserveMemory(32L);
        }

        PdfDictionary dictionary = new(entries);
        if (!tokenizer.TryReadToken(out PdfToken next))
        {
            return dictionary;
        }

        if (next.Kind != PdfTokenKind.Stream)
        {
            tokenizer.SetPosition(next.Offset);
            return dictionary;
        }

        return ParseStream(tokenizer, dictionary, next.Offset);
    }
    private QpdfObject ParseStream(PdfTokenizer tokenizer, PdfDictionary dictionary, long offset)
    {
        long length = ResolveStreamLength(dictionary, offset);
        if (length > int.MaxValue)
        {
            throw new QpdfParseException($"Stream length {length} exceeds the supported maximum.", offset, null);
        }

        ReadOnlyMemory<byte> data = tokenizer.Data;
        long cursor = tokenizer.Position;
        if (cursor < data.Length && data.Span[(int)cursor] == (byte)'\r')
        {
            cursor += 1;
            if (cursor < data.Length && data.Span[(int)cursor] == (byte)'\n')
            {
                cursor += 1;
            }
        }
        else if (cursor < data.Length && data.Span[(int)cursor] == (byte)'\n')
        {
            cursor += 1;
        }
        else
        {
            throw new QpdfParseException("The 'stream' keyword must be followed by an end-of-line marker.", offset, null);
        }

        checked
        {
            if (length > data.Length - cursor)
            {
                throw new QpdfParseException($"Stream declares {length} bytes but only {data.Length - cursor} remain.", offset, null);
            }
        }

        _governor.ReserveMemory(length);
        byte[] bytes = new byte[(int)length];
        data.Slice((int)cursor, (int)length).CopyTo(bytes);
        tokenizer.SetPosition(cursor + length);
        tokenizer.SkipWhitespaceAndComments();
        if (!tokenizer.TryReadToken(out PdfToken end) || end.Kind != PdfTokenKind.EndStream)
        {
            throw new QpdfParseException("Expected 'endstream' after stream data.", offset, null);
        }

        return new PdfStream(dictionary, bytes);
    }

    private long ResolveStreamLength(PdfDictionary dictionary, long offset)
    {
        PdfName lengthKey = new([(byte)'L', (byte)'e', (byte)'n', (byte)'g', (byte)'t', (byte)'h']);
        if (!dictionary.TryGet(lengthKey, out QpdfObject raw))
        {
            throw new QpdfParseException("Stream dictionary has no /Length.", offset, null);
        }

        if (raw is PdfInteger direct)
        {
            if (direct.Value < 0L)
            {
                throw new QpdfParseException($"Stream length {direct.Value} is negative.", offset, null);
            }

            return direct.Value;
        }

        if (raw is PdfReference reference)
        {
            if (_resolver is null)
            {
                throw new QpdfParseException("Indirect stream length needs an object resolver.", offset, null);
            }

            if (!_resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is not PdfInteger indirect)
            {
                throw new QpdfParseException("Unresolvable indirect stream length.", offset, reference.ObjectNumber);
            }

            if (indirect.Value < 0L)
            {
                throw new QpdfParseException($"Stream length {indirect.Value} is negative.", offset, reference.ObjectNumber);
            }

            return indirect.Value;
        }

        throw new QpdfParseException("Stream /Length must be an integer or an indirect reference.", offset, null);
    }

    private static byte[] UnescapeName(ReadOnlyMemory<byte> raw, long offset)
    {
        ReadOnlySpan<byte> span = raw.Span;
        byte[] result = new byte[span.Length];
        int outCount = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'#')
            {
                if (i + 2 >= span.Length)
                {
                    throw new QpdfParseException("Truncated '#' escape in name.", offset, null);
                }

                int hi = HexValue(span[i + 1]);
                int lo = HexValue(span[i + 2]);
                if (hi < 0 || lo < 0)
                {
                    throw new QpdfParseException("Invalid '#' escape in name.", offset, null);
                }

                result[outCount] = (byte)((hi << 4) | lo);
                outCount += 1;
                i += 2;
                continue;
            }

            result[outCount] = span[i];
            outCount += 1;
        }

        if (outCount == result.Length)
        {
            return result;
        }

        byte[] trimmed = new byte[outCount];
        Buffer.BlockCopy(result, 0, trimmed, 0, outCount);
        return trimmed;
    }

    private static byte[] UnescapeLiteral(ReadOnlyMemory<byte> raw, long offset)
    {
        ReadOnlySpan<byte> span = raw.Span;
        byte[] result = new byte[span.Length];
        int outCount = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != (byte)'\\')
            {
                result[outCount] = span[i];
                outCount += 1;
                continue;
            }

            i += 1;
            if (i >= span.Length)
            {
                throw new QpdfParseException("Trailing backslash in literal string.", offset, null);
            }

            byte b = span[i];
            if (b == (byte)'n')
            {
                result[outCount] = (byte)'\n';
                outCount += 1;
            }
            else if (b == (byte)'r')
            {
                result[outCount] = (byte)'\r';
                outCount += 1;
            }
            else if (b == (byte)'t')
            {
                result[outCount] = (byte)'\t';
                outCount += 1;
            }
            else if (b == (byte)'b')
            {
                result[outCount] = (byte)'\b';
                outCount += 1;
            }
            else if (b == (byte)'f')
            {
                result[outCount] = (byte)'\f';
                outCount += 1;
            }
            else if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\')
            {
                result[outCount] = b;
                outCount += 1;
            }
            else if (b == (byte)'\r')
            {
                if (i + 1 < span.Length && span[i + 1] == (byte)'\n')
                {
                    i += 1;
                }
            }
            else if (b == (byte)'\n')
            {
            }
            else if (b >= (byte)'0' && b <= (byte)'7')
            {
                int value = b - (byte)'0';
                for (int k = 0; k < 2; k++)
                {
                    if (i + 1 < span.Length && span[i + 1] >= (byte)'0' && span[i + 1] <= (byte)'7')
                    {
                        i += 1;
                        value = (value * 8) + (span[i] - (byte)'0');
                    }
                    else
                    {
                        break;
                    }
                }

                result[outCount] = (byte)(value & 0xFF);
                outCount += 1;
            }
            else
            {
                result[outCount] = b;
                outCount += 1;
            }
        }

        if (outCount == result.Length)
        {
            return result;
        }

        byte[] trimmed = new byte[outCount];
        Buffer.BlockCopy(result, 0, trimmed, 0, outCount);
        return trimmed;
    }

    private static byte[] DecodeHex(ReadOnlyMemory<byte> raw, long offset)
    {
        ReadOnlySpan<byte> span = raw.Span;
        byte[] digits = new byte[span.Length];
        int count = 0;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\f' || b == 0x00)
            {
                continue;
            }

            int value = HexValue(b);
            if (value < 0)
            {
                throw new QpdfParseException($"Invalid hexadecimal digit 0x{b:X2}.", offset, null);
            }

            digits[count] = (byte)value;
            count += 1;
        }

        int outCount = (count + 1) / 2;
        byte[] result = new byte[outCount];
        for (int i = 0; i < count; i += 2)
        {
            int hi = digits[i];
            int lo = i + 1 < count ? digits[i + 1] : 0;
            result[i / 2] = (byte)((hi << 4) | lo);
        }

        return result;
    }

    private static int HexValue(byte b)
    {
        if (b >= (byte)'0' && b <= (byte)'9')
        {
            return b - (byte)'0';
        }

        if (b >= (byte)'A' && b <= (byte)'F')
        {
            return b - (byte)'A' + 10;
        }

        if (b >= (byte)'a' && b <= (byte)'f')
        {
            return b - (byte)'a' + 10;
        }

        return -1;
    }

    private static string ToAscii(ReadOnlySpan<byte> span, long offset)
    {
        if (span.Length > MaxNumberTextLength)
        {
            throw new QpdfParseException($"Numeric token exceeds {MaxNumberTextLength} characters.", offset, null);
        }

        char[] chars = new char[span.Length];
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] >= 0x80)
            {
                throw new QpdfParseException("Non-ASCII byte in numeric token.", offset, null);
            }

            chars[i] = (char)span[i];
        }

        return new string(chars);
    }
}

