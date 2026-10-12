using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Writing;

/// <summary>Combines small writes into bounded immutable packets for publication.</summary>
internal sealed class BatchingSink : IByteSink, IDisposable
{
    private const int Capacity = 256;
    private readonly IByteSink _destination;
    private readonly ResourceGovernor _governor;
    private readonly long _maximum;
    private ParserMemoryScope? _memory;
    private GovernedBufferStream? _scratch;
    private long _length;

    private BatchingSink(IByteSink destination, QpdfLimits limits, ResourceGovernor governor,
        ParserMemoryScope memory, GovernedBufferStream scratch)
    {
        _destination = destination; _governor = governor; _maximum = limits.MaxOutputBytes;
        _memory = memory; _scratch = scratch;
    }

    internal static BatchingSink Create(IByteSink destination, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + 128L);
            GovernedBufferStream scratch = new(governor, Math.Min(Capacity, limits.MaxOutputBytes),
                nameof(QpdfLimits.MaxOutputBytes), token, Capacity);
            return new BatchingSink(destination, limits, governor, memory, scratch);
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(BatchingSink));
    private GovernedBufferStream Scratch => _scratch ?? throw new ObjectDisposedException(nameof(BatchingSink));
    public long Length { get { _ = Memory; return _length; } }

    private bool Fits(long length, CancellationToken token)
    {
        _ = Memory; CheckCancellation(token);
        long next = checked(_length + length);
        if (next > _maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), next, _maximum); }
        return length <= Capacity - Scratch.Length;
    }

    internal bool TryAppendFormatting(ReadOnlySpan<WriterPart> parts, QpdfLimits limits, CancellationToken token)
    {
        long length = WriterFormatting.MeasureParts(parts, limits, _governor, token);
        if (!Fits(length, token)) { return false; }
        WriterFormatting.FillParts(parts, Scratch.AppendRegion((int)length, token), _governor, token, null);
        _length += length; return true;
    }

    internal bool TryAppendAscii(string text, QpdfLimits limits, CancellationToken token)
    {
        long length = WriterEncoding.MeasureAscii(text, limits, _governor, token);
        if (!Fits(length, token)) { return false; }
        WriterEncoding.FillAscii(text, Scratch.AppendRegion((int)length, token), _governor, token);
        _length += length; return true;
    }

    internal bool TryAppendName(PdfName name, QpdfLimits limits, CancellationToken token)
    {
        // Keep failed in-buffer probes bounded; large tokens use the existing
        // encoder without another full measurement scan.
        if (1L + name.RawSpan.Length > Capacity - Scratch.Length) { return false; }
        long length = WriterEncoding.MeasureName(name, limits, _governor, token);
        if (!Fits(length, token)) { return false; }
        WriterEncoding.FillName(name, Scratch.AppendRegion((int)length, token), _governor, token);
        _length += length; return true;
    }

    internal bool TryAppendString(PdfByteString text, QpdfLimits limits, CancellationToken token)
    {
        long minimum = text.Hex ? 2L + 2L * text.ValueSpan.Length : 2L + text.ValueSpan.Length;
        if (minimum > Capacity - Scratch.Length) { return false; }
        long length = WriterEncoding.MeasureString(text, limits, _governor, token);
        if (!Fits(length, token)) { return false; }
        WriterEncoding.FillString(text, Scratch.AppendRegion((int)length, token), _governor, token);
        _length += length; return true;
    }

    public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        _ = Memory; CheckCancellation(cancellationToken);
        long next = checked(_length + chunk.Length);
        if (next > _maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), next, _maximum); }
        if (chunk.Length >= Capacity)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            await _destination.AppendAsync(chunk, cancellationToken).ConfigureAwait(false);
            CheckCancellation(cancellationToken);
            _length = next;
            return;
        }
        int offset = 0;
        while (offset < chunk.Length)
        {
            CheckCancellation(cancellationToken);
            int count = Math.Min(chunk.Length - offset, Capacity - (int)Scratch.Length);
            await Scratch.WriteAsync(chunk.Slice(offset, count), cancellationToken).ConfigureAwait(false);
            _length += count; offset += count;
            if (Scratch.Length == Capacity) { await FlushAsync(cancellationToken).ConfigureAwait(false); }
        }
    }

    internal async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        ParserMemoryScope memory = Memory;
        CheckCancellation(cancellationToken);
        if (Scratch.Length == 0) { return; }
        byte[]? packet = Scratch.ToArray(memory, cancellationToken);
        long admission = packet.Length + GovernedBufferStream.ArrayOverheadBytes;
        Scratch.ResetWrittenBytes();
        try
        {
            // A completed await does not authorize reusing a host-retained
            // backing. The exact written copy is immutable for its lifetime.
            await _destination.AppendAsync(packet, cancellationToken).ConfigureAwait(false);
            CheckCancellation(cancellationToken);
        }
        finally { packet = null; memory.Release(admission); }
    }

    private void CheckCancellation(CancellationToken token)
    { token.ThrowIfCancellationRequested(); _governor.CheckCancellation(); }

    public void Dispose()
    {
        GovernedBufferStream? scratch = _scratch;
        ParserMemoryScope? memory = _memory;
        _scratch = null; _memory = null;
        scratch?.Dispose(); memory?.Dispose();
    }
}
