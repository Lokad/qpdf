using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns one directly formatted header, ID or indentation through its sink await.</summary>
internal sealed class WriterFormatting : IDisposable
{
    private const int VisitBytes = 8192;
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

    private static WriterFormatting CreateCore(ReadOnlySpan<WriterPart> parts, int? width, string? overflowMessage,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken, Action? observeFill)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor);
        CheckCancellation(governor, cancellationToken);
        long maximum = Math.Min(limits.MaxOutputBytes, Array.MaxLength);
        long length = 0L;
        foreach (WriterPart part in parts)
        {
            CheckCancellation(governor, cancellationToken);
            governor.ChargeWork(1L);
            length = checked(length + Measure(part, governor));
            if (width.HasValue && length > width.Value)
            {
                throw new QpdfParseException(overflowMessage ?? "Writer formatting exceeds its reserved space.", null, null);
            }
            CheckLength(length, maximum);
        }
        if (width.HasValue) { length = width.Value; CheckLength(length, maximum); }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + (length == 0L ? 0L : 32L + length));
            governor.ChargeWork(length); // Array zeroing is admitted work before allocation.
            CheckCancellation(governor, cancellationToken);
            byte[] bytes = length == 0L ? Array.Empty<byte>() : new byte[(int)length];
            int used = 0;
            foreach (WriterPart part in parts)
            {
                CheckCancellation(governor, cancellationToken);
                governor.ChargeWork(1L);
                Fill(part, bytes, ref used, governor, cancellationToken, observeFill);
            }
            FillRepeated(bytes.AsSpan(used), (byte)' ', governor, cancellationToken, observeFill);
            return new WriterFormatting(memory, bytes);
        }
        catch { memory.Dispose(); throw; }
    }

    private static long Measure(WriterPart part, ResourceGovernor governor)
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

    private static void Fill(WriterPart part, byte[] target, ref int used, ResourceGovernor governor, CancellationToken token,
        Action? observeFill)
    {
        if (part.Type == WriterPart.Kind.Empty) { return; }
        if (part.Type == WriterPart.Kind.Text)
        {
            string text = part.Value ?? throw new InvalidOperationException("Missing text field.");
            for (int offset = 0; offset < text.Length;)
            {
                CheckCancellation(governor, token);
                int count = Math.Min(VisitBytes, text.Length - offset);
                governor.ChargeWork(2L * count); // One source visit and one output byte.
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
                governor.ChargeWork(3L * count);
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
            FillRepeated(target.AsSpan(used, part.Width), (byte)' ', governor, token, observeFill);
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
            governor.ChargeWork(1L);
            target[used++] = (byte)'-'; start = 1;
        }
        FillRepeated(target.AsSpan(used, padding), (byte)'0', governor, token, observeFill);
        used += padding;
        GovernedBufferStream.CopyBytes(number.Slice(start, countNumber - start), target.AsSpan(used), governor, token, CancellationToken.None);
        used += countNumber - start;
        if (part.Type == WriterPart.Kind.RightPaddedNumber)
        {
            FillRepeated(target.AsSpan(used, part.Width - countNumber), (byte)' ', governor, token, observeFill);
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
            governor.ChargeWork(count);
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
