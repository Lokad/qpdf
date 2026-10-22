using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentCopyAdmissionTests
{
    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    private sealed class CancelOnKeyAdmission(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            bool accepted = inner.TryReserve(bytes, out IMemoryReservation? storage);
            reservation = storage is null ? null : new Reservation(storage, cancellation);
            if (accepted && bytes >= 1048576) { cancellation.Cancel(); }
            return accepted;
        }
        private sealed class Reservation(IMemoryReservation inner, CancellationTokenSource cancellation) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                bool accepted = inner.TryResize(bytes);
                if (accepted && bytes >= 1048576) { cancellation.Cancel(); }
                return accepted;
            }
            public void Dispose() => inner.Dispose();
        }
    }

    private static byte[] Primary() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf"));

    private static async Task<byte[]> Source(int count)
    {
        using InMemoryQpdfHost seed = new();
        seed.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        seed.AddInput(QpdfPath.From("payload"), [17], QpdfInputOwnership.Transfer);
        await Qpdf.AddAttachmentsAsync(seed, QpdfPath.From("input"), QpdfPath.From("source"),
            Enumerable.Range(0, count).Select(index => new QpdfAttachmentSpec("payload", "key" + index, "name", null, null, null, null, false)).ToArray(), CancellationToken.None);
        Assert.True(seed.TryGetOutput(QpdfPath.From("source"), out byte[] output));
        return output;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task BorrowedLargePrefixRefusesBeforeDerivedStrings(int count, bool shared)
    {
        byte[] source = await Source(count);
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("source"), source, QpdfInputOwnership.Transfer);
        QpdfAttachmentCopy[] copies = [new(QpdfPath.From("source"), new string('p', 1048576))];
        RecordingMemoryBudget root = new(shared ? 131072 : 10485760);
        using (IMemoryScope caller = root.CreateScope())
        {
            QpdfLimits limits = shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 131072 };
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Task Attempt() => Qpdf.CopyAttachmentsAsync(new Host(storage, caller), QpdfPath.From("input"), QpdfPath.From("output"), copies, limits, CancellationToken.None).AsTask();
            if (shared) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Attempt); }
            else { await Assert.ThrowsAsync<QpdfLimitException>(Attempt); }
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 262144);
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.False(storage.TryGetOutput(QpdfPath.From("output"), out _));
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task CancellationAfterKeyAdmissionPrecedesStringConstruction()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("source"), await Source(4), QpdfInputOwnership.Transfer);
        QpdfAttachmentCopy[] copies = [new(QpdfPath.From("source"), new string('p', 1048576))];
        RecordingMemoryBudget root = new(16777216);
        using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Qpdf.CopyAttachmentsAsync(
                new Host(storage, new CancelOnKeyAdmission(caller, cancellation)), QpdfPath.From("input"), QpdfPath.From("output"), copies, cancellation.Token).AsTask());
            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 262144);
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.False(storage.TryGetOutput(QpdfPath.From("output"), out _));
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task ManyLongConflictingPathsProduceABoundedUsefulError()
    {
        byte[] source = await Source(1);
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        QpdfAttachmentCopy[] copies = new QpdfAttachmentCopy[12];
        for (int index = 0; index < copies.Length; index++)
        {
            QpdfPath path = QpdfPath.From("first-source-" + new string('f', 4077) + index.ToString("D6"));
            storage.AddInput(path, source, QpdfInputOwnership.Copy); copies[index] = new(path, null);
        }
        RecordingMemoryBudget root = new(131072);
        using (IMemoryScope caller = root.CreateScope())
        {
            QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.CopyAttachmentsAsync(
                new Host(storage, caller), QpdfPath.From("input"), QpdfPath.From("output"), copies,
                QpdfLimits.Default with { MaxMemoryBytes = 131072 }, CancellationToken.None).AsTask());
            Assert.InRange(failure.Message.Length, 100, 512);
            Assert.Contains("first-source-", failure.Message, StringComparison.Ordinal);
            Assert.Contains("...", failure.Message, StringComparison.Ordinal);
            Assert.Contains("additional source paths omitted", failure.Message, StringComparison.Ordinal);
            Assert.Contains("use --prefix", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0L, failure.ByteOffset);
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.False(storage.TryGetOutput(QpdfPath.From("output"), out _));
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
