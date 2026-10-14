using System.Buffers;

namespace Lokad.Qpdf.Tests.Reading;

// Observe real parser reads on the executing thread, without scheduling a
// competing cancellation task. Immutable stream slices keep this observation.
internal sealed class ObservedInput(byte[] input, Action observe) : MemoryManager<byte>
{
    public override Span<byte> GetSpan() { observe(); return input; }
    protected override bool TryGetArray(out ArraySegment<byte> segment)
    { segment = new(input); return true; }
    public override MemoryHandle Pin(int elementIndex) => throw new NotSupportedException();
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { }
}
