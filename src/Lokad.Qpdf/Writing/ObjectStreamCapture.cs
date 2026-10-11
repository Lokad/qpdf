using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns one bounded object's stream-member table and captured copies.</summary>
internal sealed class ObjectStreamCapture : IDisposable
{
    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private ParserMemoryScope? _memory;
    private byte[]?[]? _members;
    private BufferedSink? _scratch;
    private int _count;

    private ObjectStreamCapture(QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, byte[]?[] members)
    {
        _limits = limits;
        _governor = governor;
        _memory = memory;
        _members = members;
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
            memory.Reserve(checked(64L + 32L + count * (long)IntPtr.Size));
            governor.ChargeWork(count);
            return new ObjectStreamCapture(limits, governor, memory, new byte[]?[count]);
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(ObjectStreamCapture));

    internal int Count { get { _ = Memory; return _count; } }

    internal void CheckOwner(ResourceGovernor governor) => Memory.CheckOwner(governor);

    internal byte[] this[int index]
    {
        get
        {
            byte[]?[] members = _members ?? throw new ObjectDisposedException(nameof(ObjectStreamCapture));
            return members[index] ?? throw new InvalidOperationException("Object-stream member has not been captured.");
        }
    }

    internal async ValueTask AppendAsync(PdfSerializer serializer, QpdfObject value, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(value);
        _ = Memory;
        if (_count >= (_members?.Length ?? 0)) { throw new InvalidOperationException("Object-stream capture exceeds its member count."); }
        cancellationToken.ThrowIfCancellationRequested();
        BufferedSink capture = _scratch ??= BufferedSink.Create(_limits, _governor, cancellationToken);
        try
        {
            await serializer.AppendValueAsync(value, capture, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            Append(capture, cancellationToken);
            // Keep only the normal small backing across this bounded group.
            // Member copies remain independent when scratch is reset or reused.
            if (capture.Length <= 8192L) { capture.ResetWrittenBytes(); }
            else { ReleaseScratch(); }
        }
        catch { ReleaseScratch(); throw; }
    }

    internal void Append(BufferedSink capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ParserMemoryScope memory = Memory;
        byte[]?[] members = _members ?? throw new ObjectDisposedException(nameof(ObjectStreamCapture));
        if (_count >= members.Length) { throw new InvalidOperationException("Object-stream capture exceeds its member count."); }
        cancellationToken.ThrowIfCancellationRequested();
        _governor.ChargeWork(1L);
        members[_count] = capture.ToArray(memory, cancellationToken);
        _count++;
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        ReleaseScratch();
        _members = null;
        _memory = null;
        _count = 0;
        memory?.Dispose();
    }

    private void ReleaseScratch()
    {
        BufferedSink? scratch = _scratch;
        _scratch = null;
        scratch?.Dispose();
    }
}
