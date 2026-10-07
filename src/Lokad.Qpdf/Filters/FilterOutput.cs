using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>Owns codec output scratch; its exact returned array has a separate receipt.</summary>
internal sealed class FilterOutput : IDisposable
{
    private const long WrapperBytes = 160L;
    private ResourceGovernor? _governor;
    private GovernedBufferStream? _buffer;

    private FilterOutput(ResourceGovernor governor, QpdfLimits limits, CancellationToken token)
    { _governor = governor; _buffer = new(governor, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), token, 128); }

    internal static FilterOutput Create(ResourceGovernor governor, QpdfLimits limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        governor.ReserveMemory(WrapperBytes);
        try { return new(governor, limits, token); }
        catch { governor.ReleaseMemory(WrapperBytes); throw; }
    }
    private GovernedBufferStream Buffer => _buffer ?? throw new ObjectDisposedException(nameof(FilterOutput));
    internal void WriteByte(byte value) => Buffer.WriteByte(value);
    internal void Write(ReadOnlySpan<byte> bytes) => Buffer.Write(bytes);
    internal byte[] ToArray() => Buffer.ToArray();

    public void Dispose()
    {
        ResourceGovernor? governor = _governor; GovernedBufferStream? buffer = _buffer;
        _governor = null; _buffer = null;
        try { buffer?.Dispose(); }
        finally { governor?.ReleaseMemory(WrapperBytes); }
    }
}
