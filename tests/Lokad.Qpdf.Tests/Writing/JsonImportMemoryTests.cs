using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonImportMemoryTests
{
    private sealed class MemoryHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal int OutputCalls;
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { OutputCalls++; return storage.CreateOutputAsync(path, token); }
    }

    private sealed class DeferredHost(InMemoryQpdfHost storage, IMemoryBudget budget, bool closeFault) : IQpdfHost, IQpdfMemoryHost
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IMemoryBudget MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(this, await storage.OpenInputAsync(path, token), closeFault);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => storage.CreateOutputAsync(path, token);

        private sealed class Input(DeferredHost owner, IQpdfInput inner, bool closeFault) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            {
                ReadOnlyMemory<byte> bytes = await inner.ReadAsync(offset, count, token);
                if (!closeFault)
                { owner.Entered.TrySetResult(); await owner.Resume.Task; }
                return bytes;
            }
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (closeFault) { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
            }
        }
    }

    private static byte[] Fixture(string objectBody) => Encoding.UTF8.GetBytes($$$"""
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Audit":"4 0 R"}},
          "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},
          "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,1,1]}},
          "obj:4 0 R":{{{objectBody}}},"trailer":{"value":{"/Root":"1 0 R"}}
        }]}
        """);

    [Fact]
    public void DocumentAdmissionDistinguishesTokenDensityFromOneLargeString()
    {
        byte[] dense = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("0", 100000)) + "]");
        byte[] scalar = JsonSerializer.SerializeToUtf8Bytes(new string('A', dense.Length - 2));
        Assert.Equal(dense.Length, scalar.Length);
        RecordingMemoryBudget root = new(16777216L);
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost storage = new();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(storage, owner));
        long scalarCharge;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            JsonImportMemory.AdmitDocument(memory, scalar, governor, CancellationToken.None);
            scalarCharge = root.ReservedBytes;
            Assert.InRange(scalarCharge, 1L, 16384L);
        }
        Assert.Equal(0L, root.ReservedBytes);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            JsonImportMemory.AdmitDocument(memory, dense, governor, CancellationToken.None);
            Assert.True(root.ReservedBytes > dense.Length * 8L);
            Assert.True(root.ReservedBytes > scalarCharge * 100L);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task ModelRefusalRefundsBeforeTheNextImportOnTheSameBorrowedBudget()
    {
        byte[] dense = Fixture("{\"value\":[" + string.Join(',', Enumerable.Repeat("0", 20000)) + "]}");
        byte[] small = Fixture("{\"value\":7}");
        using InMemoryQpdfHost storage = new();
        QpdfPath input = QpdfPath.From("dense"), next = QpdfPath.From("small"), output = QpdfPath.From("output");
        storage.AddInput(input, dense, QpdfInputOwnership.Transfer);
        storage.AddInput(next, small, QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(1500000L);
        using IMemoryScope owner = root.CreateScope();
        MemoryHost host = new(storage, owner);
        await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() =>
            Qpdf.CreatePdfFromJsonAsync(host, input, output, CancellationToken.None).AsTask());
        Assert.Equal(0, host.OutputCalls);
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.Equal(0L, root.ReservedBytes);
        Assert.True(root.PeakBytes > dense.Length * 4L);
        Assert.True(storage.TryGetOutput(output, out byte[] unchanged));
        Assert.Equal("prior"u8.ToArray(), unchanged);
        QpdfJsonImportResult result = await Qpdf.CreatePdfFromJsonAsync(host, next, output, CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.InRange(root.ReservedBytes, 1L, 4096L);
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenumberingBorrowsAdmittedStreamPayloads(bool danglingReference)
    {
        byte[] payload = Enumerable.Range(0, 1048576).Select(index => (byte)index).ToArray();
        string dict = danglingReference ? "{\"/Extra\":\"999 0 R\"}" : "{}";
        byte[] source = Fixture("{\"stream\":{\"dict\":" + dict + ",\"data\":\"" + Convert.ToBase64String(payload) + "\"}}");
        using JsonDocument json = JsonDocument.Parse(source);
        using InMemoryQpdfHost storage = new();
        RecordingMemoryBudget root = new(16777216L);
        using IMemoryScope owner = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(storage, owner));
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            ImportedModel model = await JsonImport.BuildCreateModelAsync(storage, json, QpdfLimits.Default, governor, memory, CancellationToken.None);
            PdfStream original = Assert.IsType<PdfStream>(model.Objects[4]);
            JsonImport.RenumberedModel remapped = JsonImport.Renumber(model, QpdfLimits.Default, governor, memory, CancellationToken.None);
            PdfStream actual = Assert.IsType<PdfStream>(Assert.Single(remapped.Model, item => item.Number == 4).Value);
            Assert.Same(original.AllocationCarrier, actual.AllocationCarrier);
            Assert.True(actual.RawMemory.Span.SequenceEqual(payload));
            if (danglingReference)
            { Assert.True(actual.Dictionary.TryGet(PdfName.FromAscii("Extra"), out QpdfObject value)); Assert.IsType<PdfNull>(value); }
        }
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, 1L, 128L);
        Assert.InRange(root.ReservedBytes, 1L, 1024L);
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"qpdf\":[]}")]
    public async Task FailedInputDoesNotRetainItsReadOrDomOwner(string source)
    {
        using InMemoryQpdfHost storage = new();
        QpdfPath input = QpdfPath.From("input");
        storage.AddInput(input, Encoding.UTF8.GetBytes(source), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope owner = root.CreateScope();
        await Assert.ThrowsAsync<QpdfParseException>(() =>
            Qpdf.CreatePdfFromJsonAsync(new MemoryHost(storage, owner), input, QpdfPath.From("output"), CancellationToken.None).AsTask());
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task NestedScalarConversionObservesTheRemainingWorkBudget()
    {
        byte[] source = Fixture("{\"value\":[" + string.Join(',', Enumerable.Repeat("0", 20000)) + "]}");
        using JsonDocument json = JsonDocument.Parse(source);
        using InMemoryQpdfHost storage = new();
        RecordingMemoryBudget root = new(4194304L);
        using IMemoryScope owner = root.CreateScope();
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 80000L, CancellationCheckInterval = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None, new MemoryHost(storage, owner));
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
                JsonImport.BuildCreateModelAsync(storage, json, limits, governor, memory, CancellationToken.None).AsTask());
            Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
            Assert.True(memory.ReservedBytes > 1000000L);
            Assert.True(governor.Snapshot().TotalWork > 70000L);
        }
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, 0L, 128L);
        Assert.InRange(root.ReservedBytes, 0L, 1024L);
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionObservesEitherCancellationToken(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        using JsonDocument json = JsonDocument.Parse(Fixture("{\"value\":7}"));
        using InMemoryQpdfHost storage = new();
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope owner = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None,
            new MemoryHost(storage, owner));
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                JsonImport.BuildCreateModelAsync(storage, json, QpdfLimits.Default, governor, memory,
                    governorToken ? CancellationToken.None : cancellation.Token).AsTask());
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task LargeScalarHasABackingEstimateAndBoundedAdmissionCalls()
    {
        const int length = 1048576;
        byte[] source = Fixture("{\"value\":\"u:" + new string('A', length) + "\"}");
        using JsonDocument json = JsonDocument.Parse(source);
        using InMemoryQpdfHost storage = new();
        RecordingMemoryBudget root = new(16777216L);
        using IMemoryScope owner = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(storage, owner));
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            ImportedModel model = await JsonImport.BuildCreateModelAsync(storage, json, QpdfLimits.Default, governor, memory, CancellationToken.None);
            PdfByteString value = Assert.IsType<PdfByteString>(model.Objects[4]);
            Assert.Equal(length, value.ValueSpan.Length);
            Assert.Equal((byte)'A', value.ValueSpan[0]);
            Assert.Equal((byte)'A', value.ValueSpan[^1]);
            Assert.True(root.ReservedBytes >= length);
            Assert.True(root.PeakBytes < source.Length * 6L);
            Assert.InRange(root.ReserveCalls + root.ResizeCalls, 1, 8);
        }
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputOwnersAwaitPendingReadsAndRefundCloseFailures(bool closeFault)
    {
        byte[] source = Fixture("{\"value\":\"u:" + new string('A', 262144) + "\"}");
        using InMemoryQpdfHost storage = new();
        QpdfPath input = QpdfPath.From("input");
        storage.AddInput(input, source, QpdfInputOwnership.Transfer);
        RecordingMemoryBudget root = new(4194304L);
        using IMemoryScope owner = root.CreateScope();
        using CancellationTokenSource cancellation = new();
        DeferredHost host = new(storage, owner, closeFault);
        Task<QpdfJsonImportResult> running = Qpdf.CreatePdfFromJsonAsync(host, input, QpdfPath.From("output"), cancellation.Token).AsTask();
        if (closeFault) { await Assert.ThrowsAsync<QpdfHostException>(() => running); }
        else
        {
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(root.ReservedBytes >= source.Length);
            host.Resume.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task PageCountingDoesNotMaterializeLargeBorrowedTypeNames()
    {
        using InMemoryQpdfHost storage = new();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using JsonDocument warmJson = JsonDocument.Parse(Fixture("{\"value\":7}"));
        ImportedModel warm = await JsonImport.BuildCreateModelAsync(storage, warmJson, QpdfLimits.Default, governor, memory, CancellationToken.None);
        JsonImport.Renumber(warm, QpdfLimits.Default, governor, memory, CancellationToken.None);
        string source = Encoding.UTF8.GetString(Fixture("{\"value\":7}"))
            .Replace("\"/Type\":\"/Page\"", "\"/Type\":\"/" + new string('A', 1048576) + "\"", StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(source);
        ImportedModel model = await JsonImport.BuildCreateModelAsync(storage, json, QpdfLimits.Default, governor, memory, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        JsonImport.RenumberedModel remapped = JsonImport.Renumber(model, QpdfLimits.Default, governor, memory, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, remapped.PageCount);
        Assert.InRange(allocated, 0L, 262144L);
    }
}
