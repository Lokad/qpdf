using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class PublicCopyMemoryTests
{
    private sealed class CancelingBudget(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            bool admitted = inner.TryReserve(bytes, out reservation);
            cancellation.Cancel();
            return admitted;
        }
    }

    private static (Func<IMemoryBudget, CancellationToken, Array> Copy, Action<Array> Verify, long Bytes) Example(string kind)
    {
        const int length = 1048576;
        if (kind == "items")
        {
            PdfName referent = PdfName.FromAscii("retained");
            PdfArray value = new(Enumerable.Repeat<QpdfObject>(referent, length / IntPtr.Size).ToArray());
            return ((budget, token) => value.CopyItems(budget, token), copy =>
            {
                QpdfObject[] items = Assert.IsType<QpdfObject[]>(copy);
                Assert.Same(referent, items[^1]);
                items[0] = PdfNull.Instance;
                Assert.Same(referent, value[0]);
            }, length + 32L);
        }
        byte[] bytes = Enumerable.Repeat((byte)'A', length).ToArray();
        Func<IMemoryBudget, CancellationToken, Array> copy;
        Func<byte[]> original;
        if (kind == "name")
        {
            PdfName value = new(bytes);
            copy = (budget, token) => value.CopyRawBytes(budget, token);
            original = () => value.RawBytes;
        }
        else if (kind == "string")
        {
            PdfByteString value = new(bytes, false);
            copy = (budget, token) => value.CopyValue(budget, token);
            original = () => value.Value;
        }
        else
        {
            PdfStream value = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), bytes);
            copy = (budget, token) => value.CopyData(budget, token);
            original = () => value.Data;
        }
        return (copy, result =>
        {
            byte[] data = Assert.IsType<byte[]>(result);
            Assert.Equal(bytes, data);
            data[0] = (byte)'B';
            Assert.Equal((byte)'A', original()[0]);
        }, length + 32L);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("string")]
    [InlineData("items")]
    [InlineData("stream")]
    public void LargeCopiesHaveOneIndependentCallerAdmission(string kind)
    {
        var example = Example(kind);
        RecordingMemoryBudget root = new(example.Bytes);
        using IMemoryScope owner = root.CreateScope();
        Array copied = example.Copy(owner, CancellationToken.None);
        Assert.Equal(example.Bytes, root.ReservedBytes);
        Assert.Equal(1, root.ReserveCalls);
        Assert.Equal(0, root.ResizeCalls);
        example.Verify(copied);
        copied = Array.Empty<byte>();
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("string")]
    [InlineData("items")]
    [InlineData("stream")]
    public void RefusalAndCancellationRefundWithoutMakingTheLargeCopy(string kind)
    {
        var example = Example(kind);
        RecordingMemoryBudget refused = new(example.Bytes - 1L);
        using IMemoryScope refusedOwner = refused.CreateScope();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfMemoryAdmissionException>(() => example.Copy(refusedOwner, CancellationToken.None));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 65536L);
        Assert.Equal(0L, refused.ReservedBytes);
        Assert.Equal(1, refused.ReserveCalls);

        RecordingMemoryBudget canceled = new(example.Bytes);
        using IMemoryScope canceledOwner = canceled.CreateScope();
        using CancellationTokenSource cancellation = new();
        CancelingBudget budget = new(canceledOwner, cancellation);
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<OperationCanceledException>(() => example.Copy(budget, cancellation.Token));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 65536L);
        Assert.Equal(example.Bytes, canceled.PeakBytes);
        Assert.Equal(0L, canceled.ReservedBytes);
        Assert.Equal(1, canceled.ReserveCalls);
    }

    [Fact]
    public void StreamCopiesAdmitOnlyTheirIndependentSlice()
    {
        byte[] backing = new byte[1048576];
        backing.AsSpan(1024, 100).Fill((byte)'A');
        PdfStream stream = PdfStream.FromImmutableSlice(
            new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), backing.AsMemory(1024, 100));
        RecordingMemoryBudget root = new(132L);
        using IMemoryScope owner = root.CreateScope();
        byte[] copy = stream.CopyData(owner, CancellationToken.None);
        Assert.Equal(132L, root.ReservedBytes);
        Assert.Equal(100, copy.Length);
        Assert.All(copy, value => Assert.Equal((byte)'A', value));
        backing.AsSpan().Clear();
        Assert.Equal((byte)'A', copy[0]);
        copy = [];
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void SharedEmptyArraysNeedNoReservation()
    {
        RecordingMemoryBudget root = new(0L);
        Assert.Empty(new PdfName([]).CopyRawBytes(root, CancellationToken.None));
        Assert.Empty(new PdfByteString([], false).CopyValue(root, CancellationToken.None));
        Assert.Empty(new PdfArray([]).CopyItems(root, CancellationToken.None));
        Assert.Empty(new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), [])
            .CopyData(root, CancellationToken.None));
        Assert.Equal(0, root.ReserveCalls);
        Assert.Equal(0L, root.ReservedBytes);
    }
}
