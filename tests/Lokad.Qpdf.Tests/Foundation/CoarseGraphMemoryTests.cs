using System.Text;
using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class CoarseGraphMemoryTests
{
    private sealed class MemoryHost(IQpdfHost inner, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => inner.CreateOutputAsync(path, token);
    }

    private sealed class DecliningShrinkBudget(IMemoryBudget inner) : IMemoryBudget
    {
        private int _rejected;
        internal int RejectedShrinks => Volatile.Read(ref _rejected);
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out IMemoryReservation? admitted)) { reservation = null; return false; }
            reservation = new Reservation(this, admitted);
            return true;
        }
        private sealed class Reservation(DecliningShrinkBudget owner, IMemoryReservation inner) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(bytes);
                if (bytes < inner.Bytes) { Interlocked.Increment(ref owner._rejected); return false; }
                return inner.TryResize(bytes);
            }
            public void Dispose() => inner.Dispose();
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1048576)]
    public void DeclinedCleanupTrimKeepsAdmissionWithoutFailingTheResult(int length)
    {
        RecordingMemoryBudget root = new(4194304L);
        using IMemoryScope owner = root.CreateScope();
        DecliningShrinkBudget budget = new(owner);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(new InMemoryQpdfHost(), budget));
        governor.PrepareTypedResult(80L);
        using (ParserMemoryScope construction = ParserMemoryScope.Create(governor))
        {
            construction.Reserve(length + 64L);
            construction.RetainGraph();
        }
        QpdfJsonResult result = new JsonInspectionOutcome("{}", Array.Empty<QpdfDiagnostic>()).Present(governor);
        governor.FinishTypedCounters();
        Assert.Equal("{}", result.Json);
        Assert.Equal(168L, result.Counters.LiveMemoryBytes);
        Assert.True(root.ReservedBytes > 168L);
        Assert.Equal(1, budget.RejectedShrinks);
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1048576)]
    public void PrivateGraphsRefundAtExecutionCleanupWithoutCollection(int length)
    {
        RecordingMemoryBudget root = new(4194304L);
        using IMemoryScope owner = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(new InMemoryQpdfHost(), owner));
        governor.PrepareTypedResult(80L);
        using (ParserMemoryScope construction = ParserMemoryScope.Create(governor))
        {
            construction.Reserve(length + 64L);
            PdfByteString value = PdfByteString.FromOwnedBytes(new byte[length], false);
            construction.RetainGraph();
        }
        Assert.Equal(length + 64L, governor.PrivateRetainedBytes);
        Assert.True(root.ReservedBytes >= length + 64L);
        QpdfJsonResult result = new JsonInspectionOutcome("{}", Array.Empty<QpdfDiagnostic>()).Present(governor);
        governor.FinishTypedCounters();
        Assert.Equal(0L, governor.PrivateRetainedBytes);
        Assert.Equal(168L, result.Counters.LiveMemoryBytes);
        Assert.Equal(168L, root.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedResultArraysSurviveOrRefundWithTheirActualOutcome(bool accept)
    {
        RecordingMemoryBudget root = new(4194304L);
        using IMemoryScope owner = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(new InMemoryQpdfHost(), owner));
        governor.PrepareTypedResult(PdfPageSelectionResult.StorageBytes);
        int[] source = Enumerable.Range(1, 100000).ToArray();
        IReadOnlyList<int> pages;
        using (ParserMemoryScope construction = ParserMemoryScope.Create(governor))
        { pages = ResultCollectionStorage.Copy(construction, source, sizeof(int), governor, CancellationToken.None); }
        Assert.True(governor.PrivateRetainedBytes >= 400000L);
        Assert.Equal(0L, governor.CallerStorageBytes);
        if (accept)
        {
            PdfPageSelectionResult result = new PageSelectionOutcome(100000, pages, 100000, Array.Empty<QpdfDiagnostic>()).Present(governor);
            governor.FinishTypedCounters();
            Assert.Equal(100000, result.SourcePages.Count);
            Assert.Equal(100000, result.SourcePages[^1]);
            Assert.True(governor.CallerStorageBytes >= 400000L);
            Assert.True(root.ReservedBytes >= 400000L);
        }
        else
        {
            governor.AbandonTypedCounters();
            Assert.Equal(0L, governor.CallerStorageBytes);
            Assert.Equal(0L, root.ReservedBytes);
        }
        Assert.Equal(0L, governor.PrivateRetainedBytes);
    }

    [Fact]
    public async Task PublicDocumentKeepsAdmissionForCounterReadsAfterDisposal()
    {
        RecordingMemoryBudget root = new(4194304L);
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost storage = new();
        QpdfPath input = QpdfPath.From("input");
        storage.AddInput(input, CheckTests.Assemble(CheckTests.SinglePage("<< /Length 0 >>", []), 1), QpdfInputOwnership.Transfer);
        QpdfDocument document = await QpdfDocument.OpenAsync(new MemoryHost(storage, owner), input, CancellationToken.None);
        PdfVersion version = document.HeaderVersion;
        await document.DisposeAsync();
        long before = root.ReservedBytes;
        List<QpdfExecutionCounters> snapshots = [];
        for (int index = 0; index < 1000; index++) { snapshots.Add(document.Counters); }
        Assert.True(root.ReservedBytes >= before + 88000L - SharedMemoryOwner.BlockBytes);
        Assert.Equal("1.4", version.ToString());
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.All(snapshots, snapshot => Assert.Equal(0L, snapshot.OpenHandles));
        Assert.InRange(root.GrowthCalls, 1, 20);
        snapshots.Clear();
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicOpenOnlyRetainsItsGraphWhenADocumentReturns(bool fail)
    {
        const int length = 131072;
        RecordingMemoryBudget root = new(4194304L);
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost storage = new();
        QpdfPath input = QpdfPath.From("input");
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Large (" + new string('A', length) + ") >>", null);
        if (fail) { objects[1] = (2, "<< /Type /Pages /Count 1 /Kids [2 0 R] >>", null); }
        string pdf = Encoding.ASCII.GetString(CheckTests.Assemble(objects, 1));
        pdf = pdf.Replace("trailer\n<<", "trailer\n<< /Extra (" + new string('B', length) + ") ", StringComparison.Ordinal);
        storage.AddInput(input, Encoding.ASCII.GetBytes(pdf), QpdfInputOwnership.Transfer);
        MemoryHost host = new(storage, owner);
        if (fail)
        {
            await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(host, input, CancellationToken.None).AsTask());
            // The header's small metadata estimate can remain with the caller;
            // the large, unreturned graph must be reusable without collection.
            Assert.InRange(root.ReservedBytes, 0L, 128L);
        }
        else
        {
            QpdfDocument document = await QpdfDocument.OpenAsync(host, input, CancellationToken.None);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("Large"), out QpdfObject value));
            PdfByteString large = Assert.IsType<PdfByteString>(value);
            await document.DisposeAsync();
            Assert.Equal(length, large.ValueSpan.Length);
            Assert.Equal((byte)'A', large.ValueSpan[0]);
            Assert.True(root.ReservedBytes >= length);
        }
        Assert.True(root.PeakBytes >= length * 2L);
        Assert.Equal(0L, storage.OpenLeaseCount);
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }
}
