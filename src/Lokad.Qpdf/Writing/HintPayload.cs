using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns a hint table or complete hint object through its final consumer.</summary>
internal sealed class HintPayload : IDisposable
{
    private ParserMemoryScope? _memory;
    private byte[]? _bytes;

    private HintPayload(ParserMemoryScope memory, byte[] bytes)
    {
        _memory = memory;
        _bytes = bytes;
    }

    internal ReadOnlyMemory<byte> Data => Bytes;
    internal byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(HintPayload));

    internal static HintPayload Create(int length, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        long maximum = Math.Min(limits.MaxOutputBytes, Array.MaxLength);
        if (length > maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, maximum); }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + (length == 0 ? 0L : 32L + length));
            governor.ChargeWork(length);
            cancellationToken.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            return new HintPayload(memory, length == 0 ? Array.Empty<byte>() : new byte[length]);
        }
        catch { memory.Dispose(); throw; }
    }

    // Independently escaping helper results keep only their own allocation receipt.
    internal byte[] Escape()
    {
        byte[] bytes = Bytes;
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(HintPayload));
        if (bytes.Length > 0) { memory.RetainAllocation(bytes, 32L + bytes.Length); }
        _bytes = null;
        return bytes;
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _bytes = null;
        _memory = null;
        memory?.Dispose();
    }
}
