using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonSelectorAdmissionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");
    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Opens;
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Opens++; return storage.OpenInputAsync(path, token); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
    private sealed class CancelOnGrowth(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            bool accepted = inner.TryReserve(bytes, out IMemoryReservation? storage);
            reservation = storage is null ? null : new Reservation(storage, cancellation);
            return accepted;
        }
        private sealed class Reservation(IMemoryReservation inner, CancellationTokenSource cancellation) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            { bool accepted = inner.TryResize(bytes); if (accepted && bytes >= 32768) { cancellation.Cancel(); } return accepted; }
            public void Dispose() => inner.Dispose();
        }
    }

    private static InMemoryQpdfHost Storage()
    {
        InMemoryQpdfHost storage = new();
        storage.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        return storage;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargePreownedUniqueSelectionRefusesBeforeInput(bool shared)
    {
        QpdfJsonOptions options = new(2, ["qpdf"], "none", null,
            Enumerable.Range(1, 262144).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), true);
        using InMemoryQpdfHost storage = Storage();
        RecordingMemoryBudget root = new(shared ? 131072 : 10485760);
        using (IMemoryScope caller = root.CreateScope())
        {
            Host host = new(storage, caller);
            QpdfLimits limits = shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 131072 };
            long before = GC.GetAllocatedBytesForCurrentThread();
            Task Attempt() => Qpdf.GetDocumentJsonAsync(host, Input, Output, options, limits, CancellationToken.None).AsTask();
            if (shared) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Attempt); }
            else { await Assert.ThrowsAsync<QpdfLimitException>(Attempt); }
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 524288);
            Assert.Equal(0, host.Opens); Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.False(storage.TryGetOutput(Output, out _));
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("trailer")]
    [InlineData("1,0000000000")]
    public async Task RepeatedPreownedSelectorsUseOneMembershipEntry(string selector)
    {
        QpdfJsonOptions options = new(2, ["qpdf"], "none", null, Enumerable.Repeat(selector, 262144).ToArray(), true);
        using InMemoryQpdfHost storage = Storage();
        RecordingMemoryBudget root = new(131072);
        using (IMemoryScope caller = root.CreateScope())
        {
            Host host = new(storage, caller); long before = GC.GetAllocatedBytesForCurrentThread();
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, Input, Output, options,
                QpdfLimits.Default with { MaxMemoryBytes = 131072 }, CancellationToken.None);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 131072);
            using JsonDocument json = JsonDocument.Parse(result.Json);
            Assert.Single(json.RootElement.GetProperty("qpdf")[1].EnumerateObject());
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task CancellationDuringPreparationStopsBeforeInputAndRefunds()
    {
        QpdfJsonOptions options = new(2, ["qpdf"], "none", null,
            Enumerable.Range(1, 65536).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), true);
        using InMemoryQpdfHost storage = Storage();
        RecordingMemoryBudget root = new(16777216); using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            Host host = new(storage, new CancelOnGrowth(caller, cancellation));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Qpdf.GetDocumentJsonAsync(host, Input, Output, options, cancellation.Token).AsTask());
            Assert.True(cancellation.IsCancellationRequested); Assert.Equal(0, host.Opens);
            Assert.False(storage.TryGetOutput(Output, out _));
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidBorrowedSelectorHasAnAdmittedBoundedExcerpt(bool shared)
    {
        string selector = new string('x', 127) + "\ud83d\ude00" + new string('x', 1048576);
        QpdfJsonOptions options = new(2, ["qpdf"], "none", null, [selector], true);
        using InMemoryQpdfHost storage = Storage(); RecordingMemoryBudget root = new(shared ? 131072 : 10485760);
        using (IMemoryScope caller = root.CreateScope())
        {
            Host host = new(storage, caller); long before = GC.GetAllocatedBytesForCurrentThread();
            QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host, Input, Output,
                options, shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 131072 }, CancellationToken.None).AsTask());
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536);
            Assert.InRange(failure.Message.Length, 100, 256);
            Assert.Contains(new string('x', 127) + "...", failure.Message, StringComparison.Ordinal);
            Assert.Contains("must be trailer or obj[,gen]", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0L, failure.ByteOffset); Assert.Equal(0, host.Opens);
            Assert.False(storage.TryGetOutput(Output, out _));
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
