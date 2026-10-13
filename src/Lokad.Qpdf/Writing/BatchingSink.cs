using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Writing;

/// <summary>Combines small writes in bounded scratch; retaining sinks receive immutable packets.</summary>
internal sealed class BatchingSink : IByteSink, IOutputCapacityHint, IDisposable
{
    private const int InitialCapacity = 256;
    private const int PreferredCapacity = 4096;
    private readonly IByteSink _destination;
    private readonly ResourceGovernor _governor;
    private readonly long _maximum;
    private ParserMemoryScope? _memory;
    private GovernedBufferStream? _scratch;
    private long _length;
    private int _capacity = InitialCapacity;

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
            GovernedBufferStream scratch = new(governor, Math.Min(PreferredCapacity, limits.MaxOutputBytes),
                nameof(QpdfLimits.MaxOutputBytes), token, InitialCapacity);
            return new BatchingSink(destination, limits, governor, memory, scratch);
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(BatchingSink));
    private GovernedBufferStream Scratch => _scratch ?? throw new ObjectDisposedException(nameof(BatchingSink));
    public long Length { get { _ = Memory; return _length; } }

    void IOutputCapacityHint.EnsureCapacity(long length, CancellationToken token)
    {
        _ = Memory; CheckCancellation(token);
        if (length > _maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, _maximum); }
        if (length >= 16384L && Scratch.Length == 0L)
        {
            // Leave room for growth overlap, immutable async packets and other
            // writer storage. Small outputs and tight jobs keep the old batch.
            long headroom = Math.Min(_governor.RemainingMemoryBytes, _governor.RemainingWorkUnits) / 16L;
            int capacity = InitialCapacity;
            int preferred = length < 131072L ? PreferredCapacity / 2 : PreferredCapacity;
            while (capacity < preferred && capacity * 2L <= headroom) { capacity *= 2; }
            if (capacity > _capacity)
            {
                // The counted output justifies one admitted scratch allocation
                // rather than repeatedly growing it while formatting tokens.
                Scratch.PrepareCapacity(capacity, token);
                _capacity = capacity;
            }
        }
        if (_destination is IOutputCapacityHint destination) { destination.EnsureCapacity(length, token); }
    }

    private bool Fits(long length, CancellationToken token)
    {
        _ = Memory; CheckCancellation(token);
        long next = checked(_length + length);
        if (next > _maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), next, _maximum); }
        return length <= _capacity - Scratch.Length;
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
        if (1L + name.RawSpan.Length > _capacity - Scratch.Length) { return false; }
        long length = WriterEncoding.MeasureName(name, limits, _governor, token);
        if (!Fits(length, token)) { return false; }
        WriterEncoding.FillName(name, Scratch.AppendRegion((int)length, token), _governor, token);
        _length += length; return true;
    }

    internal bool TryAppendString(PdfByteString text, QpdfLimits limits, CancellationToken token)
    {
        long minimum = text.Hex ? 2L + 2L * text.ValueSpan.Length : 2L + text.ValueSpan.Length;
        if (minimum > _capacity - Scratch.Length) { return false; }
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
        if (chunk.Length >= _capacity)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_destination is ISynchronousByteSink synchronous && synchronous.CanAppendSynchronously)
            { synchronous.Append(chunk.Span, cancellationToken); }
            else { await _destination.AppendAsync(chunk, cancellationToken).ConfigureAwait(false); }
            CheckCancellation(cancellationToken);
            _length = next;
            return;
        }
        int offset = 0;
        while (offset < chunk.Length)
        {
            CheckCancellation(cancellationToken);
            int count = Math.Min(chunk.Length - offset, _capacity - (int)Scratch.Length);
            await Scratch.WriteAsync(chunk.Slice(offset, count), cancellationToken).ConfigureAwait(false);
            _length += count; offset += count;
            if (Scratch.Length == _capacity) { await FlushAsync(cancellationToken).ConfigureAwait(false); }
        }
    }

    internal async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        ParserMemoryScope memory = Memory;
        CheckCancellation(cancellationToken);
        if (Scratch.Length == 0) { return; }
        if (_destination is ISynchronousByteSink synchronous && synchronous.CanAppendSynchronously)
        {
            // The span cannot survive this synchronous call. Retaining/async
            // destinations below still receive independent immutable packets.
            try { synchronous.Append(Scratch.WrittenSpan, cancellationToken); }
            finally { Scratch.ResetWrittenBytes(); }
            CheckCancellation(cancellationToken);
            return;
        }
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
