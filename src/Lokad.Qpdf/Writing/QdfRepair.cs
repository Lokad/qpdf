using System.Text;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// Line-oriented repair of edited QDF files. Mirrors upstream fix-qdf:
/// recomputes object-stream tables, stream lengths, cross-reference tables
/// or streams, and startxref offsets while passing all other bytes through.
/// </summary>
internal sealed class QdfRepair
{
    private enum RepairState
    {
        Top,
        InObject,
        InStream,
        AfterStream,
        InObjectStreamDictionary,
        InObjectStreamOffsets,
        InObjectStreamOuter,
        InObjectStreamObject,
        InXrefStreamDictionary,
        InLength,
        AtXref,
        BeforeTrailer,
        InTrailer,
        Done,
    }

    private readonly struct XrefEntry
    {
        internal XrefEntry(int type, long field1, long field2)
        {
            Type = type;
            Field1 = field1;
            Field2 = field2;
        }

        internal int Type { get; }

        internal long Field1 { get; }

        internal long Field2 { get; }
    }

    private const byte LineFeed = 10;
    private const byte CarriageReturn = 13;
    private const int ByteCheckInterval = 4096;
    private const int RecordCheckInterval = 1024;

    private static readonly byte[] ObjToken = Encoding.ASCII.GetBytes(" 0 obj\n");
    private static readonly byte[] StreamToken = Encoding.ASCII.GetBytes("stream\n");
    private static readonly byte[] EndstreamToken = Encoding.ASCII.GetBytes("endstream\n");
    private static readonly byte[] EndobjToken = Encoding.ASCII.GetBytes("endobj\n");
    private static readonly byte[] XrefToken = Encoding.ASCII.GetBytes("xref\n");
    private static readonly byte[] TrailerToken = Encoding.ASCII.GetBytes("trailer <<\n");
    private static readonly byte[] TrailerEndToken = Encoding.ASCII.GetBytes(">>\n");
    private static readonly byte[] IgnoreNewlineToken = Encoding.ASCII.GetBytes("%QDF: ignore_newline\n");
    private static readonly byte[] MemberPrefix = Encoding.ASCII.GetBytes("%% Object stream: object ");
    private static readonly byte[] ObjStmMarker = Encoding.ASCII.GetBytes("/Type /ObjStm");
    private static readonly byte[] XrefMarker = Encoding.ASCII.GetBytes("/Type /XRef");
    private static readonly byte[] LengthMarker = Encoding.ASCII.GetBytes("/Length");
    private static readonly byte[] WidthSlashMarker = Encoding.ASCII.GetBytes("/W");
    private static readonly byte[] SizeMarker = Encoding.ASCII.GetBytes("/Size");
    private static readonly byte[] SizePrefix = Encoding.ASCII.GetBytes("  /Size ");
    private static readonly byte[] ExtendsMarker = Encoding.ASCII.GetBytes("/Extends ");

    private readonly ResourceGovernor _governor;
    private readonly IByteSink _sink;

    private byte[] _input = [];
    private int _cursor;
    private long _outputLength;
    private RepairState _state;
    private int _lastObject;
    private readonly List<XrefEntry> _entries = new();
    private long _streamStart;
    private long _streamLength;
    private long _xrefOffset;
    private int _xrefField1;
    private int _xrefField2;
    private int _containerId;
    private int _memberIndex;
    private readonly List<(int Start, int Length)> _memberLines = new();
    private readonly List<int> _commentAt = new();
    private byte[]? _extendsReference;

    private readonly ParserMemoryScope _memory;

    internal static QdfRepair Create(ResourceGovernor governor, IByteSink sink, ParserMemoryScope memory)
    {
        memory.CheckOwner(governor);
        memory.Reserve(256L);
        return new QdfRepair(governor, sink, memory);
    }

    private QdfRepair(ResourceGovernor governor, IByteSink sink, ParserMemoryScope memory)
    {
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _memory = memory;
    }

    internal async ValueTask RepairAsync(byte[] input, CancellationToken cancellationToken)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        CheckCancellation(cancellationToken);
        _input = input;
        _cursor = 0;
        _outputLength = 0L;
        _state = RepairState.Top;
        _lastObject = 0;
        _entries.Clear();
        _memberLines.Clear();
        _commentAt.Clear();
        _extendsReference = null;

        while (_cursor < _input.Length && _state != RepairState.Done)
        {
            CheckCancellation(cancellationToken);
            _governor.NoteOperation();
            int start = _cursor;
            int end = start;
            bool streamData = _state is RepairState.InStream or RepairState.InObjectStreamOffsets
                or RepairState.InObjectStreamOuter or RepairState.InObjectStreamObject;
            while (end < _input.Length && _input[end] != LineFeed)
            {
                if ((end & (ByteCheckInterval - 1)) == 0) { CheckCancellation(cancellationToken); }
                if (!streamData && _input[end] == CarriageReturn)
                {
                    throw new QpdfParseException("QDF syntax requires line-feed endings outside stream data.", end, null);
                }
                end = checked(end + 1);
            }

            bool terminated = end < _input.Length;
            int length = terminated ? checked(end - start + 1) : end - start;
            _cursor = terminated ? checked(end + 1) : end;
            await ProcessLineAsync(start, length, terminated, cancellationToken).ConfigureAwait(false);
        }

        if (_state != RepairState.Done)
        {
            if (_lastObject == 0)
            {
                throw new QpdfParseException("QDF repair found no numbered objects.", _input.Length, null);
            }

            throw new QpdfParseException("QDF repair reached the end of input before the cross-reference.", _input.Length, _lastObject);
        }
    }

    private async ValueTask ProcessLineAsync(int start, int length, bool terminated, CancellationToken cancellationToken)
    {
        switch (_state)
        {
            case RepairState.Top:
                await ProcessTopAsync(start, length, terminated, cancellationToken).ConfigureAwait(false);
                break;
            case RepairState.InObject:
                await ProcessObjectAsync(start, length, terminated, cancellationToken).ConfigureAwait(false);
                break;
            case RepairState.InStream:
                long contentEnd = _outputLength;
                await AppendRawAsync(start, length, cancellationToken).ConfigureAwait(false);
                if (terminated && MatchesLiteral(start, length, EndstreamToken))
                {
                    _streamLength = checked(contentEnd - _streamStart);
                    _state = RepairState.AfterStream;
                }

                break;
            case RepairState.AfterStream:
                long headerStart = _outputLength;
                await AppendRawAsync(start, length, cancellationToken).ConfigureAwait(false);
                if (terminated && MatchesLiteral(start, length, IgnoreNewlineToken))
                {
                    if (_streamLength > 0L)
                    {
                        _streamLength = checked(_streamLength - 1L);
                    }
                }
                else if (terminated && TryParseObjectHeader(start, length, cancellationToken, out int number))
                {
                    CheckObjectId(number, start, headerStart);
                    _state = RepairState.InLength;
                }

                break;
            case RepairState.InLength:
                if (!terminated || !IsBareInteger(start, length))
                {
                    throw new QpdfParseException("QDF repair expected a bare integer stream length.", start, _lastObject);
                }

                await AppendTextAsync(FormatLong(_streamLength) + "\n", cancellationToken).ConfigureAwait(false);
                _state = RepairState.Top;
                break;
            case RepairState.InObjectStreamDictionary:
                if (terminated && MatchesLiteral(start, length, StreamToken))
                {
                    _state = RepairState.InObjectStreamOffsets;
                }
                else
                {
                    CaptureExtends(start, length, cancellationToken);
                }

                break;
            case RepairState.InObjectStreamOffsets:
                if (terminated && MatchesMember(start, length, cancellationToken, out int member))
                {
                    CheckObjectId(member, start, _outputLength);
                    GovernedCollections.AddItem(_memory, _governor, _commentAt, _memberLines.Count, sizeof(int));
                    BufferMemberLine(start, length);
                    _state = RepairState.InObjectStreamOuter;
                }

                break;
            case RepairState.InObjectStreamOuter:
                FlipLastEntry();
                BufferMemberLine(start, length);
                _state = RepairState.InObjectStreamObject;
                break;
            case RepairState.InObjectStreamObject:
                if (terminated && MatchesLiteral(start, length, EndstreamToken))
                {
                    await FlushContainerAsync(cancellationToken).ConfigureAwait(false);
                    await AppendRawAsync(start, length, cancellationToken).ConfigureAwait(false);
                    _state = RepairState.InObject;
                }
                else
                {
                    BufferMemberLine(start, length);
                    if (terminated && MatchesMember(start, length, cancellationToken, out int next))
                    {
                        CheckObjectId(next, start, _outputLength);
                        GovernedCollections.AddItem(_memory, _governor, _commentAt, _memberLines.Count - 1, sizeof(int));
                        _state = RepairState.InObjectStreamOuter;
                    }
                }

                break;
            case RepairState.InXrefStreamDictionary:
                await ProcessXrefDictionaryAsync(start, length, terminated, cancellationToken).ConfigureAwait(false);
                break;
            case RepairState.AtXref:
                await EmitClassicTableAsync(cancellationToken).ConfigureAwait(false);
                _state = RepairState.BeforeTrailer;
                break;
            case RepairState.BeforeTrailer:
                if (terminated && MatchesLiteral(start, length, TrailerToken))
                {
                    await AppendRawAsync(start, length, cancellationToken).ConfigureAwait(false);
                    _state = RepairState.InTrailer;
                }

                break;
            case RepairState.InTrailer:
                await ProcessTrailerAsync(start, length, terminated, cancellationToken).ConfigureAwait(false);
                break;
            case RepairState.Done:
                break;
            default:
                throw new QpdfParseException("QDF repair reached an unknown state.", start, null);
        }
    }

    private async ValueTask ProcessTopAsync(int start, int length, bool terminated, CancellationToken cancellationToken)
    {
        if (terminated && TryParseObjectHeader(start, length, cancellationToken, out int number))
        {
            CheckObjectId(number, start, _outputLength);
            _state = RepairState.InObject;
        }
        else if (terminated && MatchesLiteral(start, length, XrefToken))
        {
            _xrefOffset = _outputLength;
            _state = RepairState.AtXref;
        }

        await AppendRawAsync(start, length, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ProcessObjectAsync(int start, int length, bool terminated, CancellationToken cancellationToken)
    {
        await AppendRawAsync(start, length, cancellationToken).ConfigureAwait(false);
        if (terminated && MatchesLiteral(start, length, StreamToken))
        {
            _state = RepairState.InStream;
            _streamStart = _outputLength;
        }
        else if (terminated && MatchesLiteral(start, length, EndobjToken))
        {
            _state = RepairState.Top;
        }
        else if (ContainsToken(start, length, ObjStmMarker, cancellationToken))
        {
            _state = RepairState.InObjectStreamDictionary;
            _containerId = _lastObject;
            _memberIndex = 0;
            _memberLines.Clear();
            _commentAt.Clear();
            _extendsReference = null;
        }
        else if (ContainsToken(start, length, XrefMarker, cancellationToken))
        {
            await BeginXrefStreamAsync(start, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask FlushContainerAsync(CancellationToken cancellationToken)
    {
        CheckCancellation(cancellationToken);
        if (_commentAt.Count == 0)
        {
            throw new QpdfParseException("QDF repair found an object stream without members.", _outputLength, _lastObject);
        }

        using ParserMemoryScope temporary = ParserMemoryScope.Create(_governor);
        // Numeric table formatting keeps offsets, builder capacity, a final
        // string and encoded bytes live together. Admit that overlap once.
        temporary.Reserve(checked(256L + 192L * _commentAt.Count));
        CheckCancellation(cancellationToken);
        long headerFirst = _memberLines[_commentAt[0]].Length;
        List<long> offsets = new(_commentAt.Count);
        long prefix = 0L;
        for (int m = 0; m < _commentAt.Count; m++)
        {
            if ((m & (RecordCheckInterval - 1)) == 0) { CheckCancellation(cancellationToken); }
            int headerLength = _memberLines[_commentAt[m]].Length;
            offsets.Add(checked(prefix + headerLength - headerFirst));
            for (int k = _commentAt[m]; k < BlockEnd(m); k++)
            {
                if ((k & (RecordCheckInterval - 1)) == 0) { CheckCancellation(cancellationToken); }
                prefix = checked(prefix + _memberLines[k].Length);
            }
        }

        System.Text.StringBuilder table = new(checked(32 * _commentAt.Count));
        for (int m = 0; m < _commentAt.Count; m++)
        {
            if ((m & (RecordCheckInterval - 1)) == 0) { CheckCancellation(cancellationToken); }
            table.Append(FormatLong(checked((long)_containerId + 1L + m)));
            table.Append(' ');
            table.Append(FormatLong(offsets[m]));
            table.Append('\n');
        }

        CheckCancellation(cancellationToken);
        byte[] tableBytes = Encoding.ASCII.GetBytes(table.ToString());
        CheckCancellation(cancellationToken);
        long first = checked((long)tableBytes.Length + headerFirst);
        long payloadLength = checked((long)tableBytes.Length + prefix);
        await AppendTextAsync("  /Length " + FormatLong(payloadLength) + "\n", cancellationToken).ConfigureAwait(false);
        await AppendTextAsync("  /N " + FormatLong(_commentAt.Count) + "\n", cancellationToken).ConfigureAwait(false);
        await AppendTextAsync("  /First " + FormatLong(first) + "\n", cancellationToken).ConfigureAwait(false);
        if (_extendsReference is not null)
        {
            await AppendTextAsync("  /Extends " + Encoding.ASCII.GetString(_extendsReference) + "\n", cancellationToken).ConfigureAwait(false);
        }

        await AppendTextAsync(">>\n", cancellationToken).ConfigureAwait(false);
        await AppendTextAsync("stream\n", cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(tableBytes, cancellationToken).ConfigureAwait(false);
        foreach ((int Start, int Length) line in _memberLines)
        {
            await AppendRawAsync(line.Start, line.Length, cancellationToken).ConfigureAwait(false);
        }
        if (_extendsReference is not null)
        {
            _memory.Release(checked(32L + _extendsReference.Length));
            _extendsReference = null;
        }

        _memberLines.Clear();
        _commentAt.Clear();
    }

    private int BlockEnd(int member)
    {
        if (member + 1 < _commentAt.Count)
        {
            return _commentAt[member + 1];
        }

        return _memberLines.Count;
    }

    private async ValueTask BeginXrefStreamAsync(int start, CancellationToken cancellationToken)
    {
        if (_entries.Count == 0)
        {
            throw new QpdfParseException("QDF repair found a cross-reference stream without objects.", start, null);
        }

        _xrefOffset = _entries[_entries.Count - 1].Field1;
        _xrefField1 = 0;
        long width = _xrefOffset;
        while (width != 0L)
        {
            width >>= 8;
            _xrefField1 = checked(_xrefField1 + 1);
        }

        if (_xrefField1 > 8)
        {
            throw new QpdfParseException("QDF repair cannot address cross-reference offsets.", start, _lastObject);
        }

        long widest = 1L;
        for (int i = 0; i < _entries.Count; i++)
        {
            if ((i & (RecordCheckInterval - 1)) == 0) { CheckCancellation(cancellationToken); }
            XrefEntry entry = _entries[i];
            if (entry.Type == 2 && entry.Field2 > widest)
            {
                widest = entry.Field2;
            }
        }

        _xrefField2 = 0;
        while (widest != 0L)
        {
            widest >>= 8;
            _xrefField2 = checked(_xrefField2 + 1);
        }

        if (_xrefField2 > 8)
        {
            throw new QpdfParseException("QDF repair cannot address object-stream members.", start, _lastObject);
        }

        long size = checked((long)_entries.Count + 1L);
        long entrySize = checked(1L + _xrefField1 + _xrefField2);
        long length = checked(size * entrySize);
        await AppendTextAsync("  /Length " + FormatLong(length) + "\n", cancellationToken).ConfigureAwait(false);
        await AppendTextAsync("  /W [ 1 " + FormatLong(_xrefField1) + " " + FormatLong(_xrefField2) + " ]\n", cancellationToken).ConfigureAwait(false);
        _state = RepairState.InXrefStreamDictionary;
    }

    private async ValueTask ProcessXrefDictionaryAsync(int start, int length, bool terminated, CancellationToken cancellationToken)
    {
        if (ContainsToken(start, length, LengthMarker, cancellationToken) || ContainsToken(start, length, WidthSlashMarker, cancellationToken))
        {
            return;
        }

        if (ContainsToken(start, length, SizeMarker, cancellationToken))
        {
            await AppendTextAsync("  /Size " + FormatLong(checked((long)_entries.Count + 1L)) + "\n", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AppendRawAsync(start, length, cancellationToken).ConfigureAwait(false);
        }

        if (terminated && MatchesLiteral(start, length, StreamToken))
        {
            await EmitXrefBinaryAsync(cancellationToken).ConfigureAwait(false);
            await AppendTextAsync("\nendstream\nendobj\n\n", cancellationToken).ConfigureAwait(false);
            await AppendTextAsync("startxref\n" + FormatLong(_xrefOffset) + "\n%%EOF\n", cancellationToken).ConfigureAwait(false);
            _state = RepairState.Done;
        }
    }

    private async ValueTask EmitXrefBinaryAsync(CancellationToken cancellationToken)
    {
        await AppendBinaryAsync(0L, 1, cancellationToken).ConfigureAwait(false);
        await AppendBinaryAsync(0L, _xrefField1, cancellationToken).ConfigureAwait(false);
        await AppendBinaryAsync(0L, _xrefField2, cancellationToken).ConfigureAwait(false);
        foreach (XrefEntry entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Type == 1)
            {
                await AppendBinaryAsync(1L, 1, cancellationToken).ConfigureAwait(false);
                await AppendBinaryAsync(entry.Field1, _xrefField1, cancellationToken).ConfigureAwait(false);
                await AppendBinaryAsync(0L, _xrefField2, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AppendBinaryAsync(2L, 1, cancellationToken).ConfigureAwait(false);
                await AppendBinaryAsync(entry.Field1, _xrefField1, cancellationToken).ConfigureAwait(false);
                await AppendBinaryAsync(entry.Field2, _xrefField2, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask EmitClassicTableAsync(CancellationToken cancellationToken)
    {
        await AppendTextAsync("0 " + FormatLong(checked((long)_entries.Count + 1L)) + "\n", cancellationToken).ConfigureAwait(false);
        await AppendTextAsync("0000000000 65535 f \n", cancellationToken).ConfigureAwait(false);
        foreach (XrefEntry entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Field1 > 9999999999L)
            {
                throw new QpdfParseException("QDF repair cannot address classic cross-reference offsets.", _outputLength, null);
            }

            await AppendTextAsync(FormatLong(entry.Field1).PadLeft(10, '0') + " 00000 n \n", cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ProcessTrailerAsync(int start, int length, bool terminated, CancellationToken cancellationToken)
    {
        if (terminated && MatchesSizeLine(start, length, cancellationToken))
        {
            await AppendTextAsync("  /Size " + FormatLong(checked((long)_entries.Count + 1L)) + "\n", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AppendRawAsync(start, length, cancellationToken).ConfigureAwait(false);
        }

        if (terminated && MatchesLiteral(start, length, TrailerEndToken))
        {
            await AppendTextAsync("startxref\n" + FormatLong(_xrefOffset) + "\n%%EOF\n", cancellationToken).ConfigureAwait(false);
            _state = RepairState.Done;
        }
    }

    private void CheckObjectId(int number, int start, long outputStart)
    {
        if (number != checked(_lastObject + 1))
        {
            throw new QpdfParseException("QDF repair expected the next sequential object.", start, _lastObject);
        }

        _lastObject = number;
        GovernedCollections.AddItem(_memory, _governor, _entries, new XrefEntry(1, outputStart, 0L), 24L);
    }


    private void FlipLastEntry()
    {
        if (_entries.Count == 0)
        {
            throw new QpdfParseException("QDF repair found an object-stream member without an object.", _outputLength, null);
        }

        _entries[_entries.Count - 1] = new XrefEntry(2, _containerId, _memberIndex);
        _memberIndex = checked(_memberIndex + 1);
    }

    private void CaptureExtends(int start, int length, CancellationToken cancellationToken)
    {
        int at = IndexOfToken(start, length, ExtendsMarker, cancellationToken);
        if (at < 0)
        {
            return;
        }

        int digits = at + ExtendsMarker.Length;
        int count = 0;
        while (digits + count < start + length && IsDigit(_input[digits + count]))
        {
            if (count == 10) { return; }
            count = checked(count + 1);
        }

        if (count == 0 || count > 10)
        {
            return;
        }

        byte[] tail = Encoding.ASCII.GetBytes(" 0 R");
        if (!MatchesAt(digits + count, tail))
        {
            return;
        }

        _memory.Reserve(checked(32L + count + 4L));
        byte[] reference = new byte[checked(count + 4)];
        Buffer.BlockCopy(_input, digits, reference, 0, checked(count + 4));
        if (_extendsReference is not null)
        {
            _memory.Release(checked(32L + _extendsReference.Length));
        }

        _extendsReference = reference;
    }

    private void BufferMemberLine(int start, int length)
    {
        // The complete immutable input remains owned through awaited output.
        // Only the offset/length index needs separate capacity admission.
        GovernedCollections.AddItem(_memory, _governor, _memberLines, (start, length), 2L * sizeof(int));
    }

    private async ValueTask AppendRawAsync(int start, int length, CancellationToken cancellationToken)
    {
        await AppendBytesAsync(new ReadOnlyMemory<byte>(_input, start, length), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendTextAsync(string text, CancellationToken cancellationToken)
    {
        await AppendBytesAsync(Encoding.ASCII.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await AppendBytesAsync(new ReadOnlyMemory<byte>(bytes), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendBytesAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        CheckCancellation(cancellationToken);
        await _sink.AppendAsync(chunk, cancellationToken).ConfigureAwait(false);
        checked
        {
            _outputLength += chunk.Length;
        }
        CheckCancellation(cancellationToken);
    }

    private async ValueTask AppendBinaryAsync(long value, int bytes, CancellationToken cancellationToken)
    {
        if (bytes < 0 || bytes > 8)
        {
            throw new QpdfParseException("QDF repair cannot address cross-reference widths.", _outputLength, null);
        }

        byte[] encoded = new byte[bytes];
        long rest = value;
        for (int i = bytes - 1; i >= 0; i--)
        {
            encoded[i] = (byte)(rest & 255L);
            rest >>= 8;
        }

        await AppendBytesAsync(encoded, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatLong(long value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private bool MatchesLiteral(int start, int length, byte[] literal)
    {
        if (length != literal.Length)
        {
            return false;
        }

        for (int i = 0; i < literal.Length; i++)
        {
            if (_input[start + i] != literal[i])
            {
                return false;
            }
        }

        return true;
    }

    private bool MatchesAt(int start, byte[] literal)
    {
        if (start < 0 || checked(start + literal.Length) > _input.Length)
        {
            return false;
        }

        for (int i = 0; i < literal.Length; i++)
        {
            if (_input[start + i] != literal[i])
            {
                return false;
            }
        }

        return true;
    }

    private bool ContainsToken(int start, int length, byte[] token, CancellationToken cancellationToken)
        => IndexOfToken(start, length, token, cancellationToken) >= 0;

    private int IndexOfToken(int start, int length, byte[] token, CancellationToken cancellationToken)
    {
        CheckCancellation(cancellationToken);
        if (token.Length == 0 || token.Length > length)
        {
            return -1;
        }

        for (int i = start; i + token.Length <= start + length; i++)
        {
            if ((i & (ByteCheckInterval - 1)) == 0) { CheckCancellation(cancellationToken); }
            bool match = true;
            for (int k = 0; k < token.Length; k++)
            {
                if (_input[i + k] != token[k])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsDigit(byte value)
    {
        return value >= 48 && value <= 57;
    }

    private bool TryParseObjectHeader(int start, int length, CancellationToken cancellationToken, out int number)
    {
        CheckCancellation(cancellationToken);
        number = 0;
        int digits = 0;
        while (digits < length && IsDigit(_input[start + digits]))
        {
            if (digits == 10) { return false; }
            digits = checked(digits + 1);
        }

        if (digits == 0 || digits > 10)
        {
            return false;
        }

        if (!MatchesAt(start + digits, ObjToken))
        {
            return false;
        }

        if (digits + ObjToken.Length != length)
        {
            return false;
        }

        long parsed = 0L;
        for (int i = 0; i < digits; i++)
        {
            parsed = checked(parsed * 10L + (_input[start + i] - 48));
        }

        if (parsed < 1L || parsed > int.MaxValue)
        {
            return false;
        }

        number = (int)parsed;
        return true;
    }

    private bool MatchesMember(int start, int length, CancellationToken cancellationToken, out int number)
    {
        CheckCancellation(cancellationToken);
        number = 0;
        if (length <= MemberPrefix.Length + 1)
        {
            return false;
        }

        for (int i = 0; i < MemberPrefix.Length; i++)
        {
            if (_input[start + i] != MemberPrefix[i])
            {
                return false;
            }
        }

        int digits = 0;
        while (MemberPrefix.Length + digits < length && IsDigit(_input[start + MemberPrefix.Length + digits]))
        {
            if (digits == 10) { return false; }
            digits = checked(digits + 1);
        }

        if (digits == 0 || digits > 10)
        {
            return false;
        }

        long parsed = 0L;
        for (int i = 0; i < digits; i++)
        {
            parsed = checked(parsed * 10L + (_input[start + MemberPrefix.Length + i] - 48));
        }

        if (parsed < 1L || parsed > int.MaxValue)
        {
            return false;
        }

        number = (int)parsed;
        return true;
    }

    private bool IsBareInteger(int start, int length)
    {
        if (length < 2 || _input[start + length - 1] != LineFeed)
        {
            return false;
        }

        int digits = length - 1;
        if (digits == 0 || digits > 10)
        {
            return false;
        }

        long parsed = 0L;
        for (int i = 0; i < digits; i++)
        {
            if (!IsDigit(_input[start + i]))
            {
                return false;
            }

            parsed = checked(parsed * 10L + (_input[start + i] - 48));
        }

        return parsed <= int.MaxValue;
    }

    private bool MatchesSizeLine(int start, int length, CancellationToken cancellationToken)
    {
        CheckCancellation(cancellationToken);
        if (length <= SizePrefix.Length + 1)
        {
            return false;
        }

        for (int i = 0; i < SizePrefix.Length; i++)
        {
            if (_input[start + i] != SizePrefix[i])
            {
                return false;
            }
        }

        int digits = 0;
        while (SizePrefix.Length + digits < length - 1 && IsDigit(_input[start + SizePrefix.Length + digits]))
        {
            if (digits == 10) { return false; }
            digits = checked(digits + 1);
        }

        if (digits == 0 || digits > 10)
        {
            return false;
        }

        return start + SizePrefix.Length + digits + 1 == start + length && _input[start + length - 1] == LineFeed;
    }

    private void CheckCancellation(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _governor.CheckCancellation();
    }
}
