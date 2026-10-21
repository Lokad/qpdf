using System.Collections;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentRequestAdmissionTests
{
    private sealed class Host(IMemoryBudget budget, IQpdfHost? storage) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Opens;
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Opens++; return storage is null ? throw new InvalidOperationException("Projection must precede input.") : storage.OpenInputAsync(path, token); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => storage is null ? throw new InvalidOperationException("Unexpected publication.") : storage.CreateOutputAsync(path, token);
    }

    private sealed class ObservedList<T>(int count, T item, CancellationTokenSource? cancellation) : IReadOnlyList<T>
    {
        internal int Reads;
        public int Count => count;
        public T this[int index]
        {
            get { Reads++; if (Reads == 32) { cancellation?.Cancel(); } return item; }
        }
        public IEnumerator<T> GetEnumerator() { for (int index = 0; index < count; index++) { yield return this[index]; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static Task Run(string route, IQpdfHost host, IReadOnlyList<QpdfAttachmentSpec> adds,
        IReadOnlyList<string> removes, IReadOnlyList<QpdfAttachmentCopy> copies, QpdfLimits limits, CancellationToken token)
        => route switch
        {
            "add" => Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), adds, limits, token).AsTask(),
            "remove" => Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), removes, limits, token).AsTask(),
            _ => Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), copies, limits, token).AsTask(),
        };

    [Theory]
    [InlineData("add", "local")]
    [InlineData("add", "shared")]
    [InlineData("add", "cancel")]
    [InlineData("remove", "local")]
    [InlineData("remove", "shared")]
    [InlineData("remove", "cancel")]
    [InlineData("copy", "local")]
    [InlineData("copy", "shared")]
    [InlineData("copy", "cancel")]
    public async Task LargeProjectionRefusesOrCancelsBeforeEnumerationAndInput(string route, string outcome)
    {
        using CancellationTokenSource cancellation = new(); if (outcome == "cancel") { cancellation.Cancel(); }
        ObservedList<QpdfAttachmentSpec> adds = new(262144, new("file", "key", "name", null, null, null, null, false), null);
        ObservedList<string> removes = new(262144, "key", null);
        ObservedList<QpdfAttachmentCopy> copies = new(262144, new(QpdfPath.From("file"), null), null);
        RecordingMemoryBudget root = new(outcome == "shared" ? 0 : 64 * 1024 * 1024);
        using IMemoryScope caller = root.CreateScope(); Host host = new(caller, null);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        Task Attempt() => Run(route, host, adds, removes, copies, limits, cancellation.Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(Attempt); }
        else if (outcome == "shared") { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Attempt); }
        else { await Assert.ThrowsAsync<QpdfLimitException>(Attempt); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
        Assert.Equal(0, host.Opens); Assert.Equal(0, adds.Reads + removes.Reads + copies.Reads);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("copy")]
    public async Task CancellationDuringProjectionStopsEnumerationAndRefunds(string route)
    {
        using CancellationTokenSource cancellation = new();
        ObservedList<QpdfAttachmentSpec> adds = new(1024, new("file", "key", "name", null, null, null, null, false), cancellation);
        ObservedList<string> removes = new(1024, "key", cancellation);
        ObservedList<QpdfAttachmentCopy> copies = new(1024, new(QpdfPath.From("file"), null), cancellation);
        RecordingMemoryBudget root = new(1048576); using IMemoryScope caller = root.CreateScope(); Host host = new(caller, null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(route, host, adds, removes, copies, QpdfLimits.Default, cancellation.Token));
        Assert.Equal(32, adds.Reads + removes.Reads + copies.Reads); Assert.Equal(0, host.Opens);
        Assert.InRange(root.ReserveCalls + root.ResizeCalls, 1, 4); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("copy")]
    public async Task InvalidEntriesFailBeforeInputAndRefundProjectedCapacity(string route)
    {
        RecordingMemoryBudget root = new(1048576); using IMemoryScope caller = root.CreateScope(); Host host = new(caller, null);
        await Assert.ThrowsAsync<ArgumentException>(() => Run(route, host, new QpdfAttachmentSpec[1], new string[1],
            new QpdfAttachmentCopy[1], QpdfLimits.Default, CancellationToken.None));
        Assert.Equal(0, host.Opens); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionRefundsBeforeResultCountersAndOnMalformedPdf(bool malformed)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), malformed ? "bad pdf"u8.ToArray()
            : File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        storage.AddInput(QpdfPath.From("file"), "payload"u8.ToArray(), QpdfInputOwnership.Copy);
        RecordingMemoryBudget root = new(8 * 1024 * 1024);
        using (IMemoryScope caller = root.CreateScope())
        {
            Host host = new(caller, storage);
            QpdfAttachmentSpec[] specs = Enumerable.Range(0, 64).Select(index =>
                new QpdfAttachmentSpec("file", index.ToString(), "name", null, null, null, null, false)).ToArray();
            Task<QpdfAttachmentEditResult> Attempt() => Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), specs, CancellationToken.None).AsTask();
            if (malformed)
            {
                await Assert.ThrowsAsync<QpdfParseException>(Attempt);
                Assert.Equal(0L, root.ReservedBytes); Assert.False(storage.TryGetOutput(QpdfPath.From("output"), out byte[] _));
            }
            else
            {
                QpdfAttachmentEditResult result = await Attempt();
                Assert.InRange(root.ReservedBytes, 1L, 4096L);
                Assert.Equal(root.ReservedBytes, result.Counters.LiveMemoryBytes);
                Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] pdf)); Assert.NotEmpty(pdf);
            }
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
