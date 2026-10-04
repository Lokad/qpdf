using System.Buffers;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Runtime;
using SixLabors.ImageSharp.Memory;

namespace Lokad.Qpdf.Filters;

/// <summary>Admits ImageSharp buffer owners against the job before allocating, without a shared pool.</summary>
internal sealed class JpegMemoryAllocator : MemoryAllocator
{
    // Conservative array alignment/header and owner-object allowance on the supported CLR.
    internal const long OwnerOverheadBytes = 96L;

    private readonly ResourceGovernor _governor;

    internal JpegMemoryAllocator(ResourceGovernor governor)
    {
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
    }

    protected override int GetBufferCapacityInBytes() => 4 * 1024 * 1024;

    public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _governor.CheckCancellation();
        T sample = default;
        int elementBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref sample, 1)).Length;
        long reserved = checked((long)length * elementBytes + OwnerOverheadBytes);
        _governor.ReserveMemory(reserved);
        try
        {
            // Zero-initialization also satisfies AllocationOptions.Clean. A new owner has
            // exactly the requested capacity, with no uncharged cross-job pool retention.
            return new BufferOwner<T>(new T[length], _governor, reserved);
        }
        catch
        {
            _governor.ReleaseMemory(reserved);
            throw;
        }
    }

    private sealed class BufferOwner<T> : IMemoryOwner<T>
        where T : struct
    {
        private T[]? _buffer;
        private readonly ResourceGovernor _governor;
        private readonly long _reserved;

        internal BufferOwner(T[] buffer, ResourceGovernor governor, long reserved)
        {
            _buffer = buffer;
            _governor = governor;
            _reserved = reserved;
        }

        public Memory<T> Memory => _buffer is T[] buffer
            ? buffer.AsMemory()
            : throw new ObjectDisposedException(nameof(BufferOwner<T>));

        public void Dispose()
        {
            if (_buffer is not null)
            {
                _buffer = null;
                _governor.ReleaseMemory(_reserved);
            }
        }
    }
}
