using System.Buffers;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Memory;

namespace Lokad.Qpdf.ImageSharp;

/// <summary>Admits ImageSharp buffer owners against the job before allocating, without a shared pool.</summary>
internal sealed class JpegMemoryAllocator : MemoryAllocator
{
    // Conservative array alignment/header and owner-object allowance on the supported CLR.
    internal const long OwnerOverheadBytes = 96L;

    private readonly QpdfJpegCodecContext _context;

    internal JpegMemoryAllocator(QpdfJpegCodecContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override int GetBufferCapacityInBytes() => 4 * 1024 * 1024;

    public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _context.CheckCancellation();
        T sample = default;
        int elementBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref sample, 1)).Length;
        long reserved = checked((long)length * elementBytes + OwnerOverheadBytes);
        IDisposable reservation = _context.ReserveMemory(reserved);
        try
        {
            // Zero-initialization also satisfies AllocationOptions.Clean. A new owner has
            // exactly the requested capacity, with no uncharged cross-job pool retention.
            return new BufferOwner<T>(new T[length], reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private sealed class BufferOwner<T> : IMemoryOwner<T>
        where T : struct
    {
        private T[]? _buffer;
        private readonly IDisposable _reservation;

        internal BufferOwner(T[] buffer, IDisposable reservation)
        {
            _buffer = buffer;
            _reservation = reservation;
        }

        public Memory<T> Memory => _buffer is T[] buffer
            ? buffer.AsMemory()
            : throw new ObjectDisposedException(nameof(BufferOwner<T>));

        public void Dispose()
        {
            if (_buffer is not null)
            {
                _buffer = null;
                _reservation.Dispose();
            }
        }
    }
}
