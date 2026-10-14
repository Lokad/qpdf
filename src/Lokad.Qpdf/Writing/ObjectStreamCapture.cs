using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns one bounded object's stream-member offsets and contiguous capture.</summary>
internal sealed class ObjectStreamCapture : IDisposable
{
    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private ParserMemoryScope? _memory;
    private int[]? _ends;
    private BufferedSink? _scratch;
    private BatchingSink? _batch;
    private int _count;

    private ObjectStreamCapture(QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, int[] ends)
    {
        _limits = limits;
        _governor = governor;
        _memory = memory;
        _ends = ends;
    }

    internal static ObjectStreamCapture Create(int count, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor);
        if (count < 1 || count > 100) { throw new QpdfException("Object-stream groups must hold 1..100 members."); }
        cancellationToken.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(checked(64L + 32L + count * (long)sizeof(int)));
            governor.ChargeWork(count);
            return new ObjectStreamCapture(limits, governor, memory, new int[count]);
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(ObjectStreamCapture));

    internal int Count { get { _ = Memory; return _count; } }

    internal void CheckOwner(ResourceGovernor governor) => Memory.CheckOwner(governor);

    // Borrow only until the next append or disposal. Payload assembly consumes
    // these slices synchronously while this owner remains alive.
    internal ReadOnlyMemory<byte> this[int index]
    {
        get
        {
            int[] ends = _ends ?? throw new ObjectDisposedException(nameof(ObjectStreamCapture));
            if ((uint)index >= (uint)ends.Length) { throw new ArgumentOutOfRangeException(nameof(index)); }
            if (index >= _count) { throw new InvalidOperationException("Object-stream member has not been captured."); }
            int start = index == 0 ? 0 : ends[index - 1];
            return (_scratch ?? throw new InvalidOperationException("Object-stream capture has no storage."))
                .WrittenMemory.Slice(start, ends[index] - start);
        }
    }

    internal async ValueTask AppendAsync(PdfSerializer serializer, QpdfObject value, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(value);
        _ = Memory;
        if (_count >= (_ends?.Length ?? 0)) { throw new InvalidOperationException("Object-stream capture exceeds its member count."); }
        cancellationToken.ThrowIfCancellationRequested();
        BufferedSink capture = _scratch ??= BufferedSink.Create(_limits, _governor, cancellationToken, 256);
        int start = checked((int)capture.Length);
        try
        {
            // Reuse the ordinary token formatter for this bounded group. Tight
            // jobs retain direct capture without another scratch admission.
            if (_batch is null && _governor.RemainingMemoryBytes >= 4096L && _governor.RemainingWorkUnits >= 4096L)
            { _batch = BatchingSink.Create(capture, _limits, _governor, cancellationToken); }
            if (_batch is BatchingSink batch)
            {
                batch.ResetWrittenBytes();
                await serializer.AppendValueAsync(value, batch, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
                await batch.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await serializer.AppendValueAsync(value, capture, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            }
            CompleteMember(capture, cancellationToken);
        }
        catch { RollBack(capture, start); throw; }
    }

    internal void Append(BufferedSink capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _ = Memory;
        if (_count >= (_ends?.Length ?? 0)) { throw new InvalidOperationException("Object-stream capture exceeds its member count."); }
        cancellationToken.ThrowIfCancellationRequested();
        capture.CheckOwner(_governor);
        BufferedSink destination = _scratch ??= BufferedSink.Create(_limits, _governor, cancellationToken, 256);
        int start = checked((int)destination.Length);
        try
        {
            destination.Append(capture.WrittenMemory.Span, cancellationToken);
            CompleteMember(destination, cancellationToken);
        }
        catch { RollBack(destination, start); throw; }
    }

    private void CompleteMember(BufferedSink capture, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _governor.CheckCancellation();
        _governor.ChargeWork(1L);
        (_ends ?? throw new ObjectDisposedException(nameof(ObjectStreamCapture)))[_count] = checked((int)capture.Length);
        _count++;
    }

    private void RollBack(BufferedSink capture, int start)
    {
        _batch?.ResetWrittenBytes();
        if (_count == 0) { ReleaseScratch(); }
        else { capture.TruncateWrittenBytes(start); }
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        ReleaseScratch();
        _ends = null;
        _memory = null;
        _count = 0;
        memory?.Dispose();
    }

    private void ReleaseScratch()
    {
        BatchingSink? batch = _batch;
        _batch = null;
        batch?.Dispose();
        BufferedSink? scratch = _scratch;
        _scratch = null;
        scratch?.Dispose();
    }
}
