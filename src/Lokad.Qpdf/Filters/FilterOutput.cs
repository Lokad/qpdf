using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>Owns codec output scratch; its exact returned array has a separate receipt.</summary>
internal sealed class FilterOutput : IDisposable
{
    private const long WrapperBytes = 160L;
    private readonly long _maximumRawBytes;
    private readonly long _selectorStride;
    private readonly long _stageLimit;
    private ResourceGovernor? _governor;
    private GovernedBufferStream? _buffer;

    private FilterOutput(ResourceGovernor governor, QpdfLimits limits, CancellationToken token, long selectorStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(selectorStride);
        ArgumentOutOfRangeException.ThrowIfEqual(selectorStride, 1L);
        _governor = governor; _selectorStride = selectorStride;
        _stageLimit = Math.Min(limits.MaxDecodedBytes, Array.MaxLength);
        long samples = Math.Min(_stageLimit, governor.RemainingDecodedBytes);
        // No decoded-byte charges change during this synchronous codec call.
        // Cache the raw ceiling so writes need neither a governor call nor a
        // predictor division per byte. One trailing selector carries no sample.
        _maximumRawBytes = selectorStride == 0L ? samples : samples + samples / (selectorStride - 1L) + 1L;
        _buffer = new(governor, Math.Max(1L, _maximumRawBytes), nameof(QpdfLimits.MaxDecodedBytes), token, 128);
    }

    internal static FilterOutput Create(ResourceGovernor governor, QpdfLimits limits, CancellationToken token)
        => Create(governor, limits, token, 0L);

    internal static FilterOutput Create(ResourceGovernor governor, QpdfLimits limits, CancellationToken token, long selectorStride)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        governor.ReserveMemory(WrapperBytes);
        try { return new(governor, limits, token, selectorStride); }
        catch { governor.ReleaseMemory(WrapperBytes); throw; }
    }
    private GovernedBufferStream Buffer => _buffer ?? throw new ObjectDisposedException(nameof(FilterOutput));
    internal void WriteByte(byte value)
    {
        GovernedBufferStream buffer = Buffer;
        CheckLength(buffer.Length + 1L); buffer.WriteByte(value);
    }
    internal void Write(ReadOnlySpan<byte> bytes)
    {
        GovernedBufferStream buffer = Buffer;
        CheckLength(buffer.Length + bytes.Length); buffer.Write(bytes);
    }
    internal byte[] ToArray() => Buffer.ToArray();

    private void CheckLength(long rawBytes)
    {
        if (rawBytes <= _maximumRawBytes) { return; }
        long samples = _selectorStride == 0L ? rawBytes : rawBytes - (rawBytes + _selectorStride - 1L) / _selectorStride;
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(FilterOutput));
        governor.CheckDecodedBytes(samples);
        throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), samples, _stageLimit);
    }

    public void Dispose()
    {
        ResourceGovernor? governor = _governor; GovernedBufferStream? buffer = _buffer;
        _governor = null; _buffer = null;
        try { buffer?.Dispose(); }
        finally { governor?.ReleaseMemory(WrapperBytes); }
    }
}
