using System.Collections;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonGenerationOwnershipTests
{
    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget caller, string fault, CancellationTokenSource cancellation)
        : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => caller;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        private void Fail(string phase)
        {
            if (fault == "cancel" && phase == "write") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            if (fault == phase) { throw new QpdfHostException(QpdfPath.From("output"), QpdfHostFailureReason.WriteFailed); }
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Fail("create"); return new Transaction(this, await storage.CreateOutputAsync(path, token)); }
        private sealed class Transaction(Host host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            { host.Fail("write"); return inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token)
            { host.Fail("commit"); return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (string fault in new[] { "create", "write", "commit", "cancel", "memory" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [fault, route]; }
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task NormalizedUpdatesPreserveTargetsAndRefundPrivateStorage(string fault, string route)
    {
        RecordingMemoryBudget root = new(16777216L);
        using (IMemoryScope durable = root.CreateScope())
        using (IMemoryScope caller = root.CreateScope())
        using (InMemoryQpdfHost storage = new(QpdfLimits.Default, null, durable))
        {
            storage.AddInput(QpdfPath.From("input"), JsonGenerationTests.Source("cycle", 7, true), QpdfInputOwnership.Transfer);
            storage.AddInput(QpdfPath.From("json"), "{\"qpdf\":[{\"jsonversion\":2},{}]}"u8.ToArray(), QpdfInputOwnership.Transfer);
            await JsonPageTreeTests.Seed(storage); long before = root.ReservedBytes;
            using CancellationTokenSource cancellation = new(); Host host = new(storage, caller, fault, cancellation);
            QpdfLimits limits = fault == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 4096L } : QpdfLimits.Default;
            if (route == "typed")
            {
                Task<QpdfJsonImportResult> running = JsonPageTreeTests.Typed(host, true, limits, cancellation.Token);
                if (fault == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
                else if (fault == "memory") { await Assert.ThrowsAsync<QpdfLimitException>(() => running); }
                else { await Assert.ThrowsAsync<QpdfHostException>(() => running); }
                Assert.Equal(before, root.ReservedBytes);
            }
            else
            {
                QpdfExecutionResult result = await JsonPageTreeTests.Execute(host, true, route, limits, cancellation.Token);
                Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
                Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
                Assert.InRange(root.ReservedWithin(caller), 1L, 4096L);
            }
            Assert.Equal(0L, storage.OpenLeaseCount);
            using (IMemoryScope export = root.CreateScope())
            { Assert.True(storage.TryGetOutput(QpdfPath.From("output"), export, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output); }
            caller.Dispose(); Assert.Equal(before, root.ReservedBytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task SuccessfulUpdateResultsSurviveTheirHostUnderTheCallerOwner()
    {
        RecordingMemoryBudget root = new(16777216L);
        using (IMemoryScope caller = root.CreateScope())
        {
            QpdfJsonImportResult result;
            using (IMemoryScope durable = root.CreateScope())
            using (InMemoryQpdfHost storage = new(QpdfLimits.Default, null, durable))
            using (CancellationTokenSource cancellation = new())
            {
                storage.AddInput(QpdfPath.From("input"), JsonGenerationTests.Source("contents", 7, false), QpdfInputOwnership.Transfer);
                storage.AddInput(QpdfPath.From("json"), "{\"qpdf\":[{\"jsonversion\":2},{}]}"u8.ToArray(), QpdfInputOwnership.Transfer);
                result = await JsonPageTreeTests.Typed(new Host(storage, caller, "none", cancellation), true, QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(1, result.PageCount); Assert.Empty(result.Warnings); Assert.True(result.Counters.TotalOutputBytes > 0L);
            Assert.True(root.ReservedBytes >= QpdfJsonImportResult.StorageBytes); Assert.Equal(result.PageCount, (result with { }).PageCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(8192L)]
    [InlineData(128L)]
    public void SourceNormalizationBorrowsPayloadsAndLeavesOriginalHeadersIntact(long maximum)
    {
        byte[] backing = new byte[8388610]; backing[1] = 17; backing[^2] = 41;
        PdfReference reference = new(5, 7);
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Audit")] = reference });
        PdfStream source = PdfStream.FromImmutableSlice(dictionary, backing.AsMemory(1, 8388608));
        Dictionary<int, XrefEntry> entries = new() { [5] = XrefEntry.Uncompressed(0L, 7) };
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            if (maximum == 128L)
            { Assert.Throws<QpdfLimitException>(() => ObjectRemap.NormalizeSource(source, entries, memory, governor, CancellationToken.None)); }
            else
            {
                PdfStream normalized = Assert.IsType<PdfStream>(ObjectRemap.NormalizeSource(source, entries, memory, governor, CancellationToken.None));
                Assert.Same(source.AllocationCarrier, normalized.AllocationCarrier); Assert.Equal(source.RawMemory, normalized.RawMemory);
                Assert.True(normalized.Dictionary.TryGet(PdfName.FromAscii("Audit"), out QpdfObject raw));
                Assert.Equal(0, Assert.IsType<PdfReference>(raw).Generation); Assert.True(governor.CounterValues.PeakMemoryBytes <= 8192L);
            }
            Assert.True(source.Dictionary.TryGet(PdfName.FromAscii("Audit"), out QpdfObject original)); Assert.Same(reference, original);
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes); Assert.Equal((byte)17, backing[1]); Assert.Equal((byte)41, backing[^2]);
    }

    private sealed class CancelingIndex(CancellationTokenSource cancellation) : IReadOnlyDictionary<int, XrefEntry>
    {
        private readonly Dictionary<int, XrefEntry> _entries = new() { [5] = XrefEntry.Uncompressed(0L, 7) };
        internal int Visits { get; private set; }
        public bool TryGetValue(int key, out XrefEntry value)
        { if (++Visits == 64) { cancellation.Cancel(); } return _entries.TryGetValue(key, out value); }
        public XrefEntry this[int key] => _entries[key];
        public IEnumerable<int> Keys => _entries.Keys;
        public IEnumerable<XrefEntry> Values => _entries.Values;
        public int Count => _entries.Count;
        public bool ContainsKey(int key) => _entries.ContainsKey(key);
        public IEnumerator<KeyValuePair<int, XrefEntry>> GetEnumerator() => _entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void SharedAdmissionRefusesTheArrayCloneBeforeAllocationAndRefundsEarlierStorage()
    {
        RecordingMemoryBudget root = new(1048576L) { RefuseOnGrowth = 2 };
        using InMemoryQpdfHost storage = new(); using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new Host(storage, root, "none", cancellation));
        PdfReference reference = new(5, 7); PdfArray source = new(Enumerable.Repeat<QpdfObject>(reference, 2048).ToArray());
        Dictionary<int, XrefEntry> entries = new() { [5] = XrefEntry.Uncompressed(0L, 7) };
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Assert.Throws<QpdfMemoryAdmissionException>(() => ObjectRemap.NormalizeSource(source, entries, memory, governor, CancellationToken.None));
            Assert.Same(reference, source[0]); Assert.Same(reference, source[2047]); Assert.Equal(2, root.GrowthCalls);
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceReferenceWalkObservesBothCancellationTokensAndRefundsItsClone(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); CancelingIndex entries = new(cancellation);
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        PdfReference original = new(5, 7); PdfArray source = new(Enumerable.Repeat<QpdfObject>(original, 1024).ToArray());
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => ObjectRemap.NormalizeSource(source, entries, memory, governor,
                governorToken ? CancellationToken.None : cancellation.Token));
            Assert.Equal(64, entries.Visits); Assert.Same(original, source[0]); Assert.Same(original, source[1023]);
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }
}
