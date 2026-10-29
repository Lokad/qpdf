using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

// Observe cancellation intervals without another lifetime control forcing a
// process-wide collection during this class's parsing/traversal phases.
[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class DocumentLifetimeTests
{
    private static byte[] Input(int padding, bool broken, string fault, bool nested)
    {
        string value = "(" + new string('K', 4096) + ")";
        if (nested) { value = "<< /A [ " + value + " ] /Reference 3 2 R >>"; }
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R" + (fault == "version" ? " /Version 42" : "") + " >>", null),
            (2, "<< /Type /Pages /Kids [3 2 R] /Count 1 >>", null),
            (3, fault == "page" ? "<< /Type /Other >>" : "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>", null),
            (4, "<< /Length 0 >>", []),
            (5, value, null),
            (6, "null %" + new string('A', padding) + "\n", null)
        ], 1, new Dictionary<int, int> { [3] = 2 });
        if (fault == "crypto")
        {
            input = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(input)
                .Replace("/Root 1 0 R >>", "/Root 1 0 R /Encrypt << >> >>", StringComparison.Ordinal));
        }
        if (broken)
        {
            string text = Encoding.ASCII.GetString(input);
            int at = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
            int end = text.IndexOf('\n', at);
            input = Encoding.ASCII.GetBytes(text[..at] + "0" + text[end..]);
        }
        return input;
    }

    private static InMemoryQpdfHost Host(byte[] input)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), input, QpdfInputOwnership.Copy);
        return host;
    }

    private static ValueTask<QpdfDocument> Open(InMemoryQpdfHost host, ResourceGovernor governor, QpdfLimits limits, bool broken)
        => QpdfDocument.OpenCore(host, QpdfPath.From("a.pdf"), limits, governor, new DiagnosticBag(governor),
            broken ? QpdfRecoveryMode.BoundedRepair : QpdfRecoveryMode.Strict, CancellationToken.None);

    private static object Field(object owner, string name)
        => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner)
            ?? throw new InvalidOperationException("Reader storage was not available before disposal.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Storage(QpdfDocument document, DocumentEngine engine)
    {
        Assert.True(MemoryMarshal.TryGetArray(engine.Data, out ArraySegment<byte> data));
        Assert.NotNull(data.Array);
        return [new(data.Array), new(engine.XRefEntries), new(Field(document, "_pages")),
            new(Field(document, "_input")), new(Field(engine, "_cache"))];
    }

    private static void Collect()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposalDropsExclusiveStorageWhileNestedAliasesRemainValid(bool broken, bool nested)
    {
        byte[] input = Input(2 * 1024 * 1024, broken, "", nested);
        InMemoryQpdfHost host = Host(input);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfDocument document = await Open(host, governor, QpdfLimits.Default, broken);
        QpdfObject returned = document.GetObject(5);
        PdfByteString leaf;
        IEnumerable<PdfName>? keys = null;
        if (nested)
        {
            PdfDictionary dictionary = Assert.IsType<PdfDictionary>(returned);
            keys = dictionary.Keys;
            Assert.True(dictionary.TryGet(PdfName.FromAscii("A"), out QpdfObject array));
            leaf = Assert.IsType<PdfByteString>(Assert.IsType<PdfArray>(array)[0]);
        }
        else { leaf = Assert.IsType<PdfByteString>(returned); }
        DocumentEngine engine = document.Engine;
        WeakReference[] storage = Storage(document, engine);
        QpdfExecutionCounters beforeSnapshot = document.Counters;
        long before = beforeSnapshot.LiveMemoryBytes;
        await document.DisposeAsync();
        QpdfExecutionCounters afterSnapshot = document.Counters;
        long after = afterSnapshot.LiveMemoryBytes;
        await document.DisposeAsync();
        QpdfExecutionCounters repeatSnapshot = document.Counters;
        Assert.Equal(after + 88L, repeatSnapshot.LiveMemoryBytes);
        Collect();
        Assert.All(storage, item => Assert.False(item.IsAlive));
        Assert.True(before - after >= input.Length);
        Assert.InRange(after, leaf.Value.Length, input.Length - 1L);
        Assert.Equal(Encoding.ASCII.GetBytes(new string('K', 4096)), leaf.Value);
        if (keys is not null) { Assert.Contains(keys, name => name.Equals(PdfName.FromAscii("A"))); }
        Assert.Equal(1, document.PageCount);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, document.Counters.OpenHandles);
        Assert.Throws<ObjectDisposedException>(() => document.GetObject(5));
        GC.KeepAlive(beforeSnapshot); GC.KeepAlive(afterSnapshot); GC.KeepAlive(repeatSnapshot);
        GC.KeepAlive(document); GC.KeepAlive(engine); GC.KeepAlive(leaf); GC.KeepAlive(keys);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("page")]
    [InlineData("crypto")]
    public async Task FailedOpeningAfterEngineConstructionReleasesTheInputAndContainers(string fault)
    {
        byte[] input = Input(1024 * 1024, false, fault, false);
        InMemoryQpdfHost host = Host(input);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        await Assert.ThrowsAnyAsync<QpdfException>(async () => await Open(host, governor, QpdfLimits.Default, false));
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, 1L, input.Length - 1L);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequentialDocumentsShareAFiniteBudgetWithoutAccumulatingInputCopies(bool broken)
    {
        byte[] input = Input(1024 * 1024, broken, "", false);
        InMemoryQpdfHost host = Host(input);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2 * 1024 * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        List<QpdfDocument> disposed = new();
        for (int i = 0; i < 12; i++)
        {
            QpdfDocument document = await Open(host, governor, limits, broken);
            Assert.Equal(1, document.PageCount);
            await document.DisposeAsync();
            disposed.Add(document);
        }
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, input.Length, limits.MaxMemoryBytes);
        Assert.True(governor.Snapshot().LiveMemoryBytes < input.Length);
        Assert.Equal(0L, host.OpenLeaseCount);
        GC.KeepAlive(disposed);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task MultiSourceSelectionReusesReleasedInputBudgetAndKeepsPageEffects(string route)
    {
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>", null),
            (4, "<< /Length 4 >>", "q Q\n"u8.ToArray()),
            (5, "null %" + new string('A', 1024 * 1024) + "\n", null)
        ], 1);
        InMemoryQpdfHost host = Host(input);
        host.AddInput(QpdfPath.From("b.pdf"), input, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("c.pdf"), input, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2 * 1024 * 1024L };
        if (route == "typed")
        {
            PdfPageSource[] sources = [.. new[] { "a.pdf", "b.pdf", "c.pdf" }
                .Select(name => new PdfPageSource(QpdfPath.From(name), PdfPageSelectionOptions.FromPages([1])))];
            await Qpdf.SelectPagesAsync(host, sources, QpdfPath.From("out.pdf"), limits, CancellationToken.None);
        }
        else
        {
            QpdfExecutionOptions options = new(limits);
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["a.pdf", "--pages", "a.pdf", "1", "b.pdf", "1", "c.pdf", "1", "--", "out.pdf"]), options, CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"a.pdf","outputFile":"out.pdf","pages":[{"file":"a.pdf","range":"1"},{"file":"b.pdf","range":"1"},{"file":"c.pdf","range":"1"}]}""", options, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("written.pdf"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument written = await QpdfDocument.OpenAsync(host, QpdfPath.From("written.pdf"), CancellationToken.None);
        Assert.Equal(3, written.PageCount);
        foreach (PageLocation page in written.PageReferences)
        {
            Assert.True(page.Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
            PdfStream stream = Assert.IsType<PdfStream>(written.GetObject(Assert.IsType<PdfReference>(contents).ObjectNumber));
            byte[] payload = stream.Data;
            if (route != "typed")
            {
                Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
                Assert.True(Assert.IsType<PdfName>(filter).Equals(PdfName.FromAscii("FlateDecode")));
                using MemoryStream encoded = new(payload);
                using System.IO.Compression.ZLibStream decoder = new(encoded, System.IO.Compression.CompressionMode.Decompress);
                using MemoryStream decoded = new();
                decoder.CopyTo(decoded); payload = decoded.ToArray();
            }
            Assert.Equal("q Q\n"u8.ToArray(), payload);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedEngineDisposalDoesNotReleaseCallerFundedInputOrTable(bool fundInput)
    {
        byte[] input = Input(65536, false, "", false);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        if (fundInput) { governor.ReserveMemory(input.Length + 32L); }
        XRefTable table = XRefTable.Parse(input, QpdfLimits.Default, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, valuesEscape: true);
        long callerOwned = governor.Snapshot().LiveMemoryBytes;
        DocumentEngine engine = new(input, table, QpdfLimits.Default, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict);
        engine.Dispose();
        Assert.Equal(callerOwned, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(7, table.Entries.Count);
        GC.KeepAlive(table); GC.KeepAlive(input); GC.KeepAlive(engine);
    }

    [Theory]
    [InlineData("xstream-1page.pdf")]
    [InlineData("xstream-lzw.pdf")]
    [InlineData("objstm-1page.pdf")]
    [InlineData("hybrid-xstm.pdf")]
    [InlineData("incremental-1page.pdf")]
    [InlineData("incremental-delete.pdf")]
    public async Task XrefVariantsReleaseContainersAndPreserveTrailerAndStreamAliases(string fixture)
    {
        byte[] input = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        InMemoryQpdfHost host = Host(input);
        QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("a.pdf"), CancellationToken.None);
        DocumentEngine engine = document.Engine;
        WeakReference[] storage = Storage(document, engine);
        PdfDictionary trailer = engine.Trailer;
        IEnumerable<PdfName> keys = trailer.Keys;
        Assert.True(trailer.TryGet(PdfName.FromAscii("Size"), out QpdfObject size));
        string sizeText = Assert.IsType<PdfInteger>(size).Text;
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(4));
        byte[] payload = stream.Data;
        long before = document.Counters.LiveMemoryBytes;
        await document.DisposeAsync();
        Collect();
        Assert.All(storage, owner => Assert.False(owner.IsAlive));
        Assert.True(before - document.Counters.LiveMemoryBytes >= input.Length);
        Assert.Contains(keys, key => key.Equals(PdfName.FromAscii("Root")));
        Assert.Equal(sizeText, Assert.IsType<PdfInteger>(size).Text);
        Assert.Equal(payload, stream.Data);
        Assert.Equal(1, document.PageCount);
        Assert.Equal(0L, host.OpenLeaseCount);
        GC.KeepAlive(document); GC.KeepAlive(engine); GC.KeepAlive(trailer); GC.KeepAlive(keys); GC.KeepAlive(stream);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int Input, int Xref, int Pages) Geometry(QpdfDocument document)
        => (document.Engine.Data.Length,
            Assert.IsType<Dictionary<int, XrefEntry>>(document.Engine.XRefEntries).EnsureCapacity(0),
            Assert.IsType<List<PageLocation>>(Field(document, "_pages")).Capacity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateExclusiveOwners(object[] owners, int input, int xref, int pages)
    {
        owners[0] = new byte[input];
        owners[1] = new Dictionary<int, XrefEntry>(xref);
        owners[2] = new List<PageLocation>(pages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public async Task ReleasedAdmissionCoversIndependentlyAllocatedInputMapAndPageBacking(int objects)
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(Enumerable.Repeat("null", objects).ToArray(), false);
        QpdfDocument document = await QpdfDocument.OpenAsync(Host(input), QpdfPath.From("a.pdf"), CancellationToken.None);
        (int length, int xref, int pages) = Geometry(document);
        object[] owners = new object[3];
        AllocateExclusiveOwners(owners, 0, 0, 0);
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        AllocateExclusiveOwners(owners, length, xref, pages);
        long independentBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        long before = document.Counters.LiveMemoryBytes;
        await document.DisposeAsync();
        Assert.True(before - document.Counters.LiveMemoryBytes >= independentBytes,
            $"Released admission is below independently allocated exclusive storage ({independentBytes} bytes).");
        GC.KeepAlive(owners); GC.KeepAlive(document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortReadsReleaseStorageOnSuccessAndFailedOpening(bool badVersion)
    {
        byte[] input = Input(65536, false, badVersion ? "version" : "", false);
        FaultingHost host = new(Host(input), "short", null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        if (badVersion)
        {
            await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenCore(host, QpdfPath.From("a.pdf"),
                QpdfLimits.Default, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, CancellationToken.None).AsTask());
        }
        else
        {
            QpdfDocument document = await QpdfDocument.OpenCore(host, QpdfPath.From("a.pdf"), QpdfLimits.Default,
                governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, CancellationToken.None);
            await document.DisposeAsync();
            Assert.Equal(1, document.PageCount);
            GC.KeepAlive(document);
        }
        Assert.True(host.Reads > 2);
        Assert.Equal(host.Reads + 1L, governor.Snapshot().HostOperations);
        Assert.True(governor.Snapshot().LiveMemoryBytes < input.Length);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.Equal(0L, host.Source.OpenLeaseCount);
        Assert.Equal(1, host.Disposes);
    }

    [Theory]
    [InlineData("stall")]
    [InlineData("cancel")]
    public async Task InterruptedShortReadReleasesItsUntransferredReceipt(string mode)
    {
        using CancellationTokenSource cancellation = new();
        FaultingHost host = new(Host(Input(65536, false, "", false)), mode, cancellation);
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        Task<QpdfDocument> opening = QpdfDocument.OpenCore(host, QpdfPath.From("a.pdf"), QpdfLimits.Default,
            governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, cancellation.Token).AsTask();
        if (mode == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening); }
        else
        {
            QpdfHostException failure = await Assert.ThrowsAsync<QpdfHostException>(() => opening);
            Assert.Equal(QpdfHostFailureReason.NonProgressingRead, failure.Reason);
        }
        Assert.InRange(host.Reads, 1, 2);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.Equal(0L, host.Source.OpenLeaseCount);
        Assert.Equal(1, host.Disposes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostCloseFailureDoesNotPreventOwnedStorageCleanup(bool failedOpening)
    {
        byte[] input = Input(1024 * 1024, false, failedOpening ? "version" : "", true);
        FaultingHost host = new(Host(input), "dispose", null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        if (failedOpening)
        {
            QpdfHostException failure = await Assert.ThrowsAsync<QpdfHostException>(() => QpdfDocument.OpenCore(host,
                QpdfPath.From("a.pdf"), QpdfLimits.Default, governor, new DiagnosticBag(governor),
                QpdfRecoveryMode.Strict, CancellationToken.None).AsTask());
            Assert.Equal(QpdfHostFailureReason.ReadFailed, failure.Reason);
        }
        else
        {
            QpdfDocument document = await QpdfDocument.OpenCore(host, QpdfPath.From("a.pdf"), QpdfLimits.Default,
                governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, CancellationToken.None);
            DocumentEngine engine = document.Engine;
            WeakReference[] storage = Storage(document, engine);
            PdfDictionary value = Assert.IsType<PdfDictionary>(document.GetObject(5));
            await Assert.ThrowsAsync<QpdfHostException>(() => document.DisposeAsync().AsTask());
            long released = governor.Snapshot().LiveMemoryBytes;
            await document.DisposeAsync();
            Assert.Equal(released, governor.Snapshot().LiveMemoryBytes);
            Collect();
            Assert.All(storage, item => Assert.False(item.IsAlive));
            Assert.True(value.TryGet(PdfName.FromAscii("A"), out QpdfObject array));
            Assert.Equal(4096, Assert.IsType<PdfByteString>(Assert.IsType<PdfArray>(array)[0]).Value.Length);
            GC.KeepAlive(document); GC.KeepAlive(engine); GC.KeepAlive(value);
        }
        Assert.True(governor.Snapshot().LiveMemoryBytes < input.Length);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.Equal(0L, host.Source.OpenLeaseCount);
        Assert.Equal(1, host.Disposes);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("memory")]
    [InlineData("cancel")]
    public async Task EmptyDocumentOpeningUsesTheSameLimitsAndUntransferredReceiptCleanup(string mode)
    {
        using CancellationTokenSource cancellation = new();
        if (mode == "cancel") { cancellation.Cancel(); }
        QpdfLimits limits = mode == "input" ? QpdfLimits.Default with { MaxInputBytes = 1L }
            : mode == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 128L } : QpdfLimits.Default;
        ResourceGovernor governor = new(limits, cancellation.Token);
        Task<QpdfDocument> opening = QpdfDocument.OpenEmptyDocument(QpdfPath.From("empty"), limits, governor,
            new DiagnosticBag(governor), QpdfRecoveryMode.Strict, cancellation.Token, true).AsTask();
        if (mode == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening); }
        else
        {
            QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => opening);
            Assert.Equal(mode == "input" ? nameof(QpdfLimits.MaxInputBytes) : nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
    }

    [Theory]
    [InlineData(1000L)]
    [InlineData(2000L)]
    [InlineData(3000L)]
    public async Task FiniteOpeningFailuresReleasePendingInputAndTableContainers(long additionalMemory)
    {
        byte[] input = Input(1024 * 1024, false, "", false);
        InMemoryQpdfHost host = Host(input);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = input.Length + additionalMemory };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => Open(host, governor, limits, false).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.True(governor.Snapshot().LiveMemoryBytes < input.Length);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationAfterObservedPageVisitsReleasesExclusiveStorageAndAccountsAcceptedValues()
    {
        const int count = 200000;
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 200000 /Kids [ " + string.Concat(Enumerable.Repeat("3 0 R ", count)) + "] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", null),
            (4, "null", null)
        ], 1);
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        // Establish parsing capacity independently. Observe subsequent page-list
        // growth rather than using an execution-fuel counter.
        ResourceGovernor expected = new(limits, CancellationToken.None);
        expected.ReserveMemory(input.Length + 32L);
        XRefTable table = XRefTable.Parse(input, limits, expected, new DiagnosticBag(expected), QpdfRecoveryMode.Strict, valuesEscape: true);
        DocumentEngine engine = new(input, table, limits, expected, new DiagnosticBag(expected), QpdfRecoveryMode.Strict);
        engine.Load(1); engine.Load(2); engine.Load(3);
        long pageThreshold = expected.Snapshot().PeakMemoryBytes + 32768L;
        engine.Dispose();
        if ((object)table is IDisposable owned) { owned.Dispose(); }
        expected.ReleaseMemory(input.Length + 32L);
        long accepted = expected.Snapshot().LiveMemoryBytes;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            InMemoryQpdfHost host = Host(input);
            using CancellationTokenSource cancellation = new();
            ResourceGovernor governor = new(limits, cancellation.Token);
            Task<QpdfDocument> opening = Task.Factory.StartNew(() => QpdfDocument.OpenCore(host, QpdfPath.From("a.pdf"),
                limits, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, cancellation.Token).AsTask(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (governor.Snapshot().PeakMemoryBytes < pageThreshold && !opening.IsCompleted
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                Thread.SpinWait(16);
            }
            if (opening.IsCompleted) { await (await opening).DisposeAsync(); continue; }
            cancellation.Cancel();
            try { await (await opening.WaitAsync(TimeSpan.FromSeconds(10))).DisposeAsync(); }
            catch (OperationCanceledException)
            {
                // No value escapes this canceled opening. Collected carriers
                // may retire while parsing/traversal runs, so the independently
                // established accepted graph is an upper bound, not an exact
                // total. Exclusive input/containers must add no residual charge.
                Assert.InRange(governor.Snapshot().LiveMemoryBytes, 0L, accepted);
                Assert.Equal(0L, governor.Snapshot().OpenHandles);
                Assert.Equal(0L, host.OpenLeaseCount);
                return;
            }
        }
        Assert.Fail("Page traversal completed before cancellation could be observed in eight attempts.");
    }

    private sealed class FaultingHost(InMemoryQpdfHost source, string mode, CancellationTokenSource? cancellation) : IQpdfHost
    {
        internal InMemoryQpdfHost Source => source;
        private string Mode => mode;
        private CancellationTokenSource? Cancellation => cancellation;
        internal int Reads { get; private set; }
        internal int Disposes { get; private set; }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
            => new FaultingInput(await source.OpenInputAsync(path, cancellationToken), this);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
            => source.CreateOutputAsync(path, cancellationToken);

        private sealed class FaultingInput(IQpdfInput inner, FaultingHost host) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
            {
                host.Reads++;
                if (host.Mode == "stall" && host.Reads == 2) { return ReadOnlyMemory<byte>.Empty; }
                ReadOnlyMemory<byte> read = await inner.ReadAsync(offset, host.Mode == "dispose" ? count : Math.Min(count, 97), cancellationToken);
                if (host.Mode == "cancel") { host.Cancellation?.Cancel(); }
                return read;
            }
            public async ValueTask DisposeAsync()
            {
                host.Disposes++;
                await inner.DisposeAsync();
                if (host.Mode == "dispose") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
            }
        }
    }
}
