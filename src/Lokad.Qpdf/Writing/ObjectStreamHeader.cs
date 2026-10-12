using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns the directly formatted object-stream header through its sink await.</summary>
internal sealed class ObjectStreamHeader : IDisposable
{
    private ParserMemoryScope? _memory;
    private byte[]? _bytes;

    private ObjectStreamHeader(ParserMemoryScope memory, byte[] bytes)
    {
        _memory = memory;
        _bytes = bytes;
    }

    internal ReadOnlyMemory<byte> Data => _bytes ?? throw new ObjectDisposedException(nameof(ObjectStreamHeader));

    internal static ObjectStreamHeader Create(int number, int count, long length, long first, bool compressed, bool qdf,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        Span<byte> target = stackalloc byte[256];
        int used = Format(number, count, length, first, compressed, qdf, target, limits, governor, cancellationToken);
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + 32L + used);
            governor.ChargeWork(used);
            cancellationToken.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            byte[] bytes = GC.AllocateUninitializedArray<byte>(used);
            GovernedBufferStream.CopyBytes(target.Slice(0, used), bytes, governor, cancellationToken, CancellationToken.None);
            return new ObjectStreamHeader(memory, bytes);
        }
        catch { memory.Dispose(); throw; }
    }

    internal static int Measure(int number, int count, long length, long first, bool compressed, bool qdf,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        Span<byte> target = stackalloc byte[256];
        return Format(number, count, length, first, compressed, qdf, target, limits, governor, cancellationToken);
    }

    private static int Format(int number, int count, long length, long first, bool compressed, bool qdf,
        Span<byte> target, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor);
        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        if (number < 1 || count < 1 || count > 100 || length < 0L || first < 0L || (qdf && compressed))
        {
            throw new QpdfException("Invalid object-stream header fields.");
        }
        // Four decimal fields and the longest fixed syntax fit in 256 bytes.
        int used = 0;
        AppendNumber(number, target, ref used, governor);
        AppendLiteral(qdf ? " 0 obj\n<<\n  /Type /ObjStm\n  /Length "u8 : " 0 obj\n<< /Type /ObjStm /Length "u8, target, ref used, governor, cancellationToken);
        AppendNumber(length, target, ref used, governor);
        if (compressed) { AppendLiteral(" /Filter /FlateDecode"u8, target, ref used, governor, cancellationToken); }
        AppendLiteral(qdf ? "\n  /N "u8 : " /N "u8, target, ref used, governor, cancellationToken);
        AppendNumber(count, target, ref used, governor);
        AppendLiteral(qdf ? "\n  /First "u8 : " /First "u8, target, ref used, governor, cancellationToken);
        AppendNumber(first, target, ref used, governor);
        AppendLiteral(qdf ? "\n>>\nstream\n"u8 : " >>\nstream\n"u8, target, ref used, governor, cancellationToken);
        if (used > limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), used, limits.MaxOutputBytes);
        }
        return used;
    }

    private static void AppendNumber(long value, Span<byte> target, ref int used, ResourceGovernor governor)
        => used += WriterNumber.Format(value, target.Slice(used), governor);

    private static void AppendLiteral(ReadOnlySpan<byte> value, Span<byte> target, ref int used,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.ChargeWork(value.Length);
        value.CopyTo(target.Slice(used));
        used += value.Length;
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _bytes = null;
        _memory = null;
        memory?.Dispose();
    }
}
