using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class DocumentCacheTests
{
    private static byte[] Input(int count, string body)
        => TolerantObjectLoadingTests.Pdf(Enumerable.Repeat(body, count).ToArray(), false);

    // The source/xref fixture is funded separately to isolate engine ownership.
    private static DocumentEngine Engine(byte[] input, QpdfLimits limits, ResourceGovernor governor)
    {
        ResourceGovernor fixture = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable table = XRefTable.Parse(input, QpdfLimits.Default, fixture, new DiagnosticBag(fixture), QpdfRecoveryMode.Strict);
        return new DocumentEngine(input, table, limits, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict);
    }

    private static object Field(DocumentEngine engine, string name)
        => typeof(DocumentEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(engine)
            ?? throw new InvalidOperationException("Collection field is unavailable.");

    private static int Capacity(object collection)
        => (int)(collection.GetType().GetMethod("EnsureCapacity")?.Invoke(collection, [0])
            ?? throw new InvalidOperationException("Capacity query is unavailable."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object NewDictionary<T>(int capacity) => new Dictionary<int, T>(capacity);

    private static long DictionaryAllocation(object cache)
    {
        Type valueType = cache.GetType().GetGenericArguments()[1];
        Func<int, object> allocate = (typeof(DocumentCacheTests).GetMethod(nameof(NewDictionary), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Allocator is unavailable.")).MakeGenericMethod(valueType).CreateDelegate<Func<int, object>>();
        allocate(0);
        int capacity = Capacity(cache);
        long before = GC.GetAllocatedBytesForCurrentThread();
        object dictionary = allocate(capacity);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(dictionary);
        return allocated;
    }

    private static void DisposeEngine(DocumentEngine engine) => Assert.IsAssignableFrom<IDisposable>((object)engine).Dispose();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(2000)]
    public void CacheAdmissionCoversIndependentBackingAllocation(int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DocumentEngine engine = Engine(Input(count, "null"), QpdfLimits.Default, governor);
        for (int number = 5; number < count + 5; number++) { Assert.Same(PdfNull.Instance, engine.Load(number)); }
        long storage = DictionaryAllocation(Field(engine, "_cache"));
        Assert.True(governor.Snapshot().LiveMemoryBytes >= storage,
            $"Admission {governor.Snapshot().LiveMemoryBytes} is below independently allocated cache storage {storage}.");
        DisposeEngine(engine);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ConstructorAdmissionFailsBeforeCollectionAllocation()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 300L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), Assert.Throws<QpdfLimitException>(() => Engine(Input(0, "null"), limits, governor)).LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CacheGrowthFailurePreservesAcceptedAliasesAndSupportsRetry()
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(["(kept)", .. Enumerable.Repeat("null", 2000)], false);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DocumentEngine engine = Engine(input, limits, governor);
        PdfByteString kept = Assert.IsType<PdfByteString>(engine.Load(5));
        int failed = 0;
        for (int number = 6; number < 2006; number++)
        {
            try { engine.Load(number); }
            catch (QpdfLimitException) { failed = number; break; }
        }
        Assert.True(failed > 6);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Assert.Throws<QpdfLimitException>(() => engine.Load(failed));
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
            Assert.Same(kept, engine.Load(5));
        }
        Assert.Equal("kept"u8.ToArray(), kept.Value);
        DisposeEngine(engine);
        Assert.True(governor.Snapshot().LiveMemoryBytes < baseline);
        Assert.Equal("kept"u8.ToArray(), kept.Value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CollectionReferences(DocumentEngine engine)
        => [new(Field(engine, "_cache")), new(Field(engine, "_loading")), new(Field(engine, "_decodingStreams"))];

    [Fact]
    public void EngineDisposalDropsActualStorageAndReleasesAdmissionOnce()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DocumentEngine engine = Engine(Input(2000, "null"), QpdfLimits.Default, governor);
        for (int number = 5; number < 2005; number++) { engine.Load(number); }
        WeakReference[] references = CollectionReferences(engine);
        DisposeEngine(engine);
        DisposeEngine(engine);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        Assert.Throws<ObjectDisposedException>(() => engine.Load(5));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(engine);
    }

    [Fact]
    public async Task DocumentDisposalDropsMetadataButPreservesReturnedNodes()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), Input(2000, "(kept)"), QpdfInputOwnership.Copy);
        QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        PdfByteString kept = Assert.IsType<PdfByteString>(document.GetObject(5));
        for (int number = 6; number < 2005; number++) { document.GetObject(number); }
        WeakReference[] references = CollectionReferences(document.Engine);
        QpdfExecutionCounters beforeSnapshot = document.Counters;
        long before = beforeSnapshot.LiveMemoryBytes;
        await document.DisposeAsync();
        QpdfExecutionCounters afterSnapshot = document.Counters;
        long after = afterSnapshot.LiveMemoryBytes;
        await document.DisposeAsync();
        Assert.True(after < before);
        QpdfExecutionCounters repeatSnapshot = document.Counters;
        Assert.Equal(after + 88L, repeatSnapshot.LiveMemoryBytes);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        Assert.Equal("kept"u8.ToArray(), kept.Value);
        Assert.Equal(0L, host.OpenLeaseCount);
        GC.KeepAlive(beforeSnapshot); GC.KeepAlive(afterSnapshot); GC.KeepAlive(repeatSnapshot);
        GC.KeepAlive(document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateCompressedMembersReleaseOnlyUnborrowedSubtrees(bool flate)
    {
        string first = "(" + new string('A', 4096) + ")";
        string controlBody = "null %" + new string('A', first.Length - 7) + "\n";
        string header = "6 0 6 " + (first.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ";
        byte[] input = ReaderAcceptanceTests.ObjectStreamPdf(first, "(last)", flate, true, header);
        byte[] control = ReaderAcceptanceTests.ObjectStreamPdf(controlBody, "(last)", flate, true, header);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor expected = new(QpdfLimits.Default, CancellationToken.None);
        DocumentEngine engine = Engine(input, QpdfLimits.Default, governor);
        DocumentEngine controlEngine = Engine(control, QpdfLimits.Default, expected);
        engine.Load(5); controlEngine.Load(5);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        long controlBase = expected.Snapshot().LiveMemoryBytes;
        PdfByteString last = Assert.IsType<PdfByteString>(engine.Load(6));
        controlEngine.Load(6);
        Assert.Equal("last"u8.ToArray(), last.Value);
        Assert.Same(last, engine.Load(6));
        Assert.Equal(expected.Snapshot().LiveMemoryBytes - controlBase, governor.Snapshot().LiveMemoryBytes - baseline);
        DisposeEngine(engine); DisposeEngine(controlEngine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposalReleasesUnreadMembersAndPreservesBorrowedMembers(bool flate)
    {
        string unread = "(" + new string('A', 4096) + ")";
        string controlBody = "null %" + new string('A', unread.Length - 7) + "\n";
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor controlGovernor = new(QpdfLimits.Default, CancellationToken.None);
        DocumentEngine engine = Engine(ReaderAcceptanceTests.ObjectStreamPdf("(read)", unread, flate, true, null), QpdfLimits.Default, governor);
        DocumentEngine control = Engine(ReaderAcceptanceTests.ObjectStreamPdf("(read)", controlBody, flate, true, null), QpdfLimits.Default, controlGovernor);
        engine.Load(5); control.Load(5);
        long before = governor.Snapshot().LiveMemoryBytes;
        long controlBefore = controlGovernor.Snapshot().LiveMemoryBytes;
        PdfByteString read = Assert.IsType<PdfByteString>(engine.Load(6));
        control.Load(6);
        Assert.True(governor.Snapshot().LiveMemoryBytes - before > controlGovernor.Snapshot().LiveMemoryBytes - controlBefore + 4096L);
        DisposeEngine(engine); DisposeEngine(control);
        Assert.Equal(controlGovernor.Snapshot().LiveMemoryBytes - controlBefore, governor.Snapshot().LiveMemoryBytes - before);
        Assert.Equal("read"u8.ToArray(), read.Value);
    }

    [Fact]
    public void RemovedLoadingGuardKeysKeepTheirBackingCapacityAdmitted()
    {
        string[] bodies = Enumerable.Range(0, 127).Select(i => "<< /Length " + (i + 6) + " 0 R >>\nstream\n\nendstream").Append("0").ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DocumentEngine engine = Engine(TolerantObjectLoadingTests.Pdf(bodies, false), QpdfLimits.Default, governor);
        Assert.Throws<QpdfParseException>(() => engine.Load(5));
        HashSet<int> loading = Assert.IsType<HashSet<int>>(Field(engine, "_loading"));
        int capacity = loading.EnsureCapacity(0);
        Assert.Empty(loading);
        Assert.True(capacity >= 128);
        long before = GC.GetAllocatedBytesForCurrentThread();
        HashSet<int> comparison = new(capacity);
        long storage = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(governor.Snapshot().LiveMemoryBytes >= storage);
        GC.KeepAlive(comparison);
        DisposeEngine(engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicTraversalEnforcesCacheGrowthAdmission(bool job)
    {
        byte[] input = Input(2000, "(" + new string('A', 32) + ")");
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfPath output = QpdfPath.From("outputs/report.json");
        byte[] previous = "prior output"u8.ToArray();
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(output, CancellationToken.None))
        {
            await prior.WriteAsync(previous, CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = input.Length + 450000L });
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","outputFile":"outputs/report.json","json":"2","jsonKey":["qpdf"]}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--json=2", "--json-key=qpdf", "--", "outputs/report.json"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(nameof(QpdfLimits.MaxMemoryBytes), StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(output, out byte[] unchanged));
        Assert.Equal(previous, unchanged);

        QpdfExecutionOptions generous = new(QpdfLimits.Default with { MaxMemoryBytes = 16 * 1024 * 1024L });
        QpdfExecutionResult completed = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","outputFile":"outputs/report.json","json":"2","jsonKey":["qpdf"]}""", generous, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--json=2", "--json-key=qpdf", "--", "outputs/report.json"]), generous, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, completed.Status);
        Assert.True(host.TryGetOutput(output, out byte[] report));
        using JsonDocument json = JsonDocument.Parse(report);
        Assert.EndsWith(new string('A', 32), json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:2004 0 R").GetProperty("value").GetString());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task TypedCacheGrowthFailsAfterSuccessfulOpeningAndKeepsPriorAlias()
    {
        byte[] input = Input(2000, "(" + new string('A', 32) + ")");
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = input.Length + 450000L };
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None);
        PdfByteString kept = Assert.IsType<PdfByteString>(document.GetObject(5));
        Assert.Throws<QpdfLimitException>(() => { for (int number = 6; number < 2005; number++) { document.GetObject(number); } });
        Assert.Same(kept, document.GetObject(5));
        Assert.Equal(Encoding.ASCII.GetBytes(new string('A', 32)), kept.Value);
    }

    [Fact]
    public async Task CancellationKeepsAcceptedCacheUntilOwnerDisposal()
    {
        byte[] input = Input(20000, "null");
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        ResourceGovernor governor = new(limits, cancellation.Token);
        DocumentEngine engine = Engine(input, limits, governor);
        engine.Load(5);
        Task loading = Task.Factory.StartNew(() => { for (int number = 6; number < 20005; number++) { engine.Load(number); } },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (governor.Snapshot().LiveMemoryBytes < 50000L && !loading.IsCompleted
            && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10)) { Thread.SpinWait(16); }
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 50000L);
        Assert.False(loading.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loading.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(PdfNull.Instance, engine.Load(5));
        Assert.True(governor.Snapshot().LiveMemoryBytes > 0L);
        DisposeEngine(engine);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
