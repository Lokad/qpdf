namespace Lokad.Qpdf.Image;

// The codec API borrows a stream; Image's packed sample API borrows a span.
// A visible MemoryStream can be borrowed. Other streams need one admitted copy.
internal sealed class JpegInput(ReadOnlyMemory<byte> memory, IDisposable? reservation) : IDisposable
{
    internal ReadOnlyMemory<byte> Memory { get; private set; } = memory;
    private IDisposable? _reservation = reservation;

    internal static JpegInput Read(Stream input, QpdfJpegCodecContext context)
    {
        context.CheckCancellation();
        if (input is MemoryStream visible && visible.TryGetBuffer(out ArraySegment<byte> segment))
        {
            long remaining = Math.Max(0L, visible.Length - visible.Position);
            ReadOnlyMemory<byte> borrowed = remaining == 0L ? ReadOnlyMemory<byte>.Empty
                : segment.AsMemory(checked((int)visible.Position), checked((int)remaining));
            visible.Position = visible.Length;
            context.CheckCancellation();
            return new(borrowed, null);
        }
        long hint = input.CanSeek ? Math.Max(0L, input.Length - input.Position) : 0L;
        if (hint > Array.MaxLength) { throw new InvalidDataException("Encoded JPEG exceeds the supported buffer size."); }
        IDisposable reservation = context.ReserveMemory(hint == 0L ? 0L : hint + 32L);
        byte[] buffer = [];
        try
        {
            if (hint > 0L) { buffer = new byte[(int)hint]; }
            Span<byte> scratch = stackalloc byte[8192];
            int length = 0;
            while (true)
            {
                context.CheckCancellation();
                int read = input.Read(scratch);
                context.CheckCancellation();
                if (read == 0) { break; }
                long needed = (long)length + read;
                if (needed > buffer.Length)
                {
                    long capacity = Math.Min(Array.MaxLength, Math.Max(needed, Math.Max(256L, buffer.LongLength * 2L)));
                    if (capacity < needed) { throw new InvalidDataException("Encoded JPEG exceeds the supported buffer size."); }
                    context.ResizeMemory(reservation, buffer.LongLength + capacity + 64L);
                    byte[] next = new byte[(int)capacity];
                    buffer.AsSpan(0, length).CopyTo(next); buffer = next;
                    context.ResizeMemory(reservation, capacity + 32L);
                    context.CheckCancellation();
                }
                scratch[..read].CopyTo(buffer.AsSpan(length)); length += read;
            }
            return new(buffer.AsMemory(0, length), reservation);
        }
        catch { buffer = []; reservation.Dispose(); throw; }
    }

    public void Dispose()
    {
        Memory = default;
        IDisposable? reservation = _reservation; _reservation = null; reservation?.Dispose();
    }
}
