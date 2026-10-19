using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns one directly formatted header, ID or indentation through its sink await.</summary>
internal sealed class WriterFormatting : IDisposable
{
    private const int VisitBytes = 8192;
    internal const int ShortTokenBytes = 20;
    private ParserMemoryScope? _memory;
    private byte[]? _bytes;

    private WriterFormatting(ParserMemoryScope memory, byte[] bytes) { _memory = memory; _bytes = bytes; }

    internal ReadOnlyMemory<byte> Data => _bytes ?? throw new ObjectDisposedException(nameof(WriterFormatting));

    internal static WriterFormatting Create(ReadOnlySpan<WriterPart> parts, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken cancellationToken)
        => CreateCore(parts, null, null, limits, governor, cancellationToken, null);

    // Internal block observation lets tests cancel after actual formatting work
    // without racing the scheduler. Ordinary callers retain no callback/state.
    internal static WriterFormatting Create(ReadOnlySpan<WriterPart> parts, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken cancellationToken, Action observeFill)
    {
        ArgumentNullException.ThrowIfNull(observeFill);
        return CreateCore(parts, null, null, limits, governor, cancellationToken, observeFill);
    }

    internal static WriterFormatting CreatePadded(ReadOnlySpan<WriterPart> parts, int width, string overflowMessage,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentNullException.ThrowIfNull(overflowMessage);
        return CreateCore(parts, width, overflowMessage, limits, governor, cancellationToken, null);
    }

    internal static long MeasureParts(ReadOnlySpan<WriterPart> parts, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken cancellationToken)
        => MeasureParts(parts, null, null, limits, governor, cancellationToken);

    private static long MeasureParts(ReadOnlySpan<WriterPart> parts, int? width, string? overflowMessage,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor);
        CheckCancellation(governor, cancellationToken);
        long maximum = Math.Min(limits.MaxOutputBytes, Array.MaxLength);
        long length = 0L;
        foreach (ref readonly WriterPart part in parts)
        {
            CheckCancellation(governor, cancellationToken);
            governor.NoteOperation();
            length = checked(length + Measure(in part, governor));
            if (width.HasValue && length > width.Value)
            {
                throw new QpdfParseException(overflowMessage ?? "Writer formatting exceeds its reserved space.", null, null);
            }
            CheckLength(length, maximum);
        }
        if (width.HasValue) { length = width.Value; CheckLength(length, maximum); }
        return length;
    }

    private static WriterFormatting CreateCore(ReadOnlySpan<WriterPart> parts, int? width, string? overflowMessage,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken, Action? observeFill)
    {
        long length = MeasureParts(parts, width, overflowMessage, limits, governor, cancellationToken);
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + (length == 0L ? 0L : 32L + length));
            governor.NoteOperation();
            CheckCancellation(governor, cancellationToken);
            // Fields and final padding fill the entire result before publication.
            byte[] bytes = length == 0L ? Array.Empty<byte>() : GC.AllocateUninitializedArray<byte>((int)length);
            FillParts(parts, bytes, governor, cancellationToken, observeFill);
            return new WriterFormatting(memory, bytes);
        }
        catch { memory.Dispose(); throw; }
    }

    private static long Measure(in WriterPart part, ResourceGovernor governor)
    {
        switch (part.Type)
        {
            case WriterPart.Kind.Empty: return 0L;
            case WriterPart.Kind.Text: return (part.Value ?? throw new InvalidOperationException("Missing text field.")).Length;
            case WriterPart.Kind.Spaces: return part.Width;
            case WriterPart.Kind.Hex: return 2L * part.Bytes.Length;
            default:
                Span<byte> number = stackalloc byte[20];
                int count = WriterNumber.Format(part.Integer, number, governor);
                if (part.Type == WriterPart.Kind.RightPaddedNumber)
                {
                    if (count > part.Width) { throw new QpdfException("Linearized cross-reference exceeds its reserved space."); }
                    return part.Width;
                }
                return part.Type == WriterPart.Kind.ZeroPaddedNumber
                    ? Math.Max(count, (long)part.Width + (part.Integer < 0L ? 1L : 0L)) : count;
        }
    }

    internal static void FillParts(ReadOnlySpan<WriterPart> parts, Span<byte> target,
        ResourceGovernor governor, CancellationToken token, Action? observeFill)
    {
        int used = 0;
        foreach (ref readonly WriterPart part in parts)
        {
            CheckCancellation(governor, token); governor.NoteOperation();
            Fill(in part, target, ref used, governor, token, observeFill);
        }
        FillRepeated(target.Slice(used), (byte)' ', governor, token, observeFill);
    }

    // Common object headers, references and classic xref rows fit in twenty
    // bytes. Format once; the caller copies only the written prefix into scratch.
    internal static bool TryFillShort(ReadOnlySpan<WriterPart> parts, Span<byte> target,
        ResourceGovernor governor, CancellationToken token, out int written)
    {
        written = 0;
        if (parts.Length != 2 || target.Length < ShortTokenBytes) { return false; }
        ref readonly WriterPart number = ref parts[0];
        ref readonly WriterPart suffix = ref parts[1];
        if (suffix.Type != WriterPart.Kind.Text || suffix.Value is not string text || text.Length > 10
            || number.Integer < 0L) { return false; }
        if (number.Type == WriterPart.Kind.Number)
        {
            if (number.Integer > int.MaxValue) { return false; }
        }
        else if (number.Type != WriterPart.Kind.ZeroPaddedNumber || number.Width != 10
            || number.Integer > 9_999_999_999L) { return false; }

        CheckCancellation(governor, token);
        governor.NoteOperation(); // Both field visits and the bounded shape checks.
        Fill(in number, target, ref written, governor, token, null);
        CheckCancellation(governor, token);
        Fill(in suffix, target, ref written, governor, token, null);
        return true;
    }

    private static void Fill(in WriterPart part, Span<byte> target, ref int used, ResourceGovernor governor, CancellationToken token,
        Action? observeFill)
    {
        if (part.Type == WriterPart.Kind.Empty) { return; }
        if (part.Type == WriterPart.Kind.Text)
        {
            string text = part.Value ?? throw new InvalidOperationException("Missing text field.");
            ReadOnlySpan<byte> literal = text switch
            {
                " 0 R" => " 0 R"u8,
                " 0 obj\n" => " 0 obj\n"u8,
                " 00000 n \n" => " 00000 n \n"u8,
                _ => default,
            };
            if (!literal.IsEmpty)
            {
                CheckCancellation(governor, token);
                governor.NoteOperation();
                literal.CopyTo(target.Slice(used));
                used += literal.Length;
                observeFill?.Invoke();
                return;
            }
            for (int offset = 0; offset < text.Length;)
            {
                CheckCancellation(governor, token);
                int count = Math.Min(VisitBytes, text.Length - offset);
                governor.NoteOperation(); // One source visit and one output byte.
                for (int index = 0; index < count; index++)
                {
                    char value = text[offset + index];
                    target[used + index] = value <= 127 ? (byte)value : (byte)'?';
                }
                used += count; offset += count;
                observeFill?.Invoke();
            }
            return;
        }
        if (part.Type == WriterPart.Kind.Hex)
        {
            const string digits = "0123456789ABCDEF";
            ReadOnlySpan<byte> bytes = part.Bytes.Span;
            for (int offset = 0; offset < bytes.Length;)
            {
                CheckCancellation(governor, token);
                int count = Math.Min(VisitBytes, bytes.Length - offset);
                governor.NoteOperation();
                for (int index = 0; index < count; index++)
                {
                    byte value = bytes[offset + index];
                    target[used++] = (byte)digits[value >> 4];
                    target[used++] = (byte)digits[value & 15];
                }
                offset += count;
                observeFill?.Invoke();
            }
            return;
        }
        if (part.Type == WriterPart.Kind.Spaces)
        {
            FillRepeated(target.Slice(used, part.Width), (byte)' ', governor, token, observeFill);
            used += part.Width;
            return;
        }
        Span<byte> number = stackalloc byte[20];
        int countNumber = WriterNumber.Format(part.Integer, number, governor);
        int padding = part.Type == WriterPart.Kind.ZeroPaddedNumber
            ? Math.Max(0, part.Width - countNumber + (part.Integer < 0L ? 1 : 0)) : 0;
        int start = 0;
        if (padding != 0 && part.Integer < 0L)
        {
            governor.NoteOperation();
            target[used++] = (byte)'-'; start = 1;
        }
        FillRepeated(target.Slice(used, padding), (byte)'0', governor, token, observeFill);
        used += padding;
        GovernedBufferStream.CopyBytes(number.Slice(start, countNumber - start), target.Slice(used), governor, token, CancellationToken.None);
        used += countNumber - start;
        if (part.Type == WriterPart.Kind.RightPaddedNumber)
        {
            FillRepeated(target.Slice(used, part.Width - countNumber), (byte)' ', governor, token, observeFill);
            used += part.Width - countNumber;
        }
    }

    private static void FillRepeated(Span<byte> target, byte value, ResourceGovernor governor, CancellationToken token,
        Action? observeFill)
    {
        for (int offset = 0; offset < target.Length;)
        {
            CheckCancellation(governor, token);
            int count = Math.Min(VisitBytes, target.Length - offset);
            governor.NoteOperation();
            target.Slice(offset, count).Fill(value);
            offset += count;
            observeFill?.Invoke();
        }
    }

    private static void CheckCancellation(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
    }

    private static void CheckLength(long length, long maximum)
    {
        if (length > maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, maximum); }
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _bytes = null; _memory = null;
        memory?.Dispose();
    }
}
