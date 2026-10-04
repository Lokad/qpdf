using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;
using SixLabors.ImageSharp.Memory;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class JpegMemoryAllocatorTests
{
    [Fact]
    public void OwnersKeepTheirActualTypedCapacityChargedUntilDisposal()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        JpegMemoryAllocator allocator = new(governor);
        using var first = allocator.Allocate<long>(128, AllocationOptions.Clean);
        using var second = allocator.Allocate<byte>(1024, AllocationOptions.None);
        Assert.Equal(128, first.Memory.Length);
        Assert.Equal(1024, second.Memory.Length);
        Assert.Equal(2048L + 2L * JpegMemoryAllocator.OwnerOverheadBytes, governor.Snapshot().LiveMemoryBytes);
        first.Dispose();
        Assert.Equal(1024L + JpegMemoryAllocator.OwnerOverheadBytes, governor.Snapshot().LiveMemoryBytes);
        first.Dispose();
        second.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => first.Memory);
    }

    [Fact]
    public void TightBudgetRejectsBeforeCreatingTheRequestedArray()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        JpegMemoryAllocator allocator = new(governor);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => allocator.Allocate<byte>(1024 * 1024, AllocationOptions.None));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 65536L, $"Budget rejection allocated {allocated} bytes.");
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CanceledAllocationRetainsNothing()
    {
        using CancellationTokenSource source = new();
        ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        JpegMemoryAllocator allocator = new(governor);
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => allocator.Allocate<byte>(1024, AllocationOptions.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
