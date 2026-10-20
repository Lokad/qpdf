using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class LinearizationDataOwnershipTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static byte[] Input(string id)
    {
        if (id == "compressed")
        { return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linearized-2page.pdf")); }
        using Stream stream = typeof(LinearizationDataOwnershipTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Inspection.LinearizationCheckCases.json") ?? throw new InvalidOperationException();
        JsonObject corpus = JsonNode.Parse(stream) as JsonObject ?? throw new InvalidOperationException();
        JsonObject row = (corpus["Cases"] as JsonArray ?? throw new InvalidOperationException()).OfType<JsonObject>()
            .Single(row => row["Id"]?.GetValue<string>() == id);
        string digest = row["SHA256"]?.GetValue<string>() ?? throw new InvalidOperationException();
        return Convert.FromBase64String(corpus["Files"]?[digest]?.GetValue<string>() ?? throw new InvalidOperationException());
    }

    private static InMemoryQpdfHost Host(string id)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), Input(id), QpdfInputOwnership.Copy);
        return host;
    }

    private static object ReadOwned(QpdfDocument document, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        MethodInfo read = typeof(LinearizationCheck).GetMethod("TryReadOwnedData", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The hint reader has no complete scoped owner.");
        try { return read.Invoke(null, new object[] { document.Engine, document.PageCount, limits, governor, token }) ?? throw new InvalidOperationException(); }
        catch (TargetInvocationException failure) when (failure.InnerException is QpdfException error) { throw error; }
        catch (TargetInvocationException failure) when (failure.InnerException is OperationCanceledException error) { throw error; }
    }

    private static LinearizationData Data(object owner)
        => owner.GetType().GetProperty("Data", Fields)?.GetValue(owner) as LinearizationData ?? throw new InvalidOperationException();

    private static object ReadObserved(QpdfDocument document, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken token, Action<ReadOnlyMemory<byte>> observe)
    {
        MethodInfo read = typeof(LinearizationCheck).GetMethod("TryReadOwnedDataObserved", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected a bounded hint-backing observation.");
        try { return read.Invoke(null, new object[] { document.Engine, document.PageCount, limits, governor, token, observe }) ?? throw new InvalidOperationException(); }
        catch (TargetInvocationException failure) when (failure.InnerException is QpdfException error) { throw error; }
        catch (TargetInvocationException failure) when (failure.InnerException is OperationCanceledException error) { throw error; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Owner, (WeakReference Backing, bool Borrowed)[] Buffers) CaptureBuffers(
        QpdfDocument document, ResourceGovernor governor)
    {
        HashSet<object> borrowed = new(ReferenceEqualityComparer.Instance);
        foreach (int number in document.Engine.XRefEntries.Keys.Where(number => number > 0))
        {
            if (document.GetObject(number) is PdfStream stream) { borrowed.Add(stream.AllocationCarrier); }
        }
        List<(WeakReference, bool)> buffers = new();
        object owner = ReadObserved(document, QpdfLimits.Default, governor, CancellationToken.None, memory =>
        {
            Assert.True(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment));
            byte[] bytes = segment.Array ?? throw new InvalidOperationException();
            buffers.Add((new WeakReference(bytes), borrowed.Contains(bytes)));
        });
        return (owner, buffers.ToArray());
    }

    [Theory]
    [InlineData("compressed")]
    [InlineData("overflow-36-valid")]
    [InlineData("overflow-61-valid")]
    public async Task DecodedAndCombinedBackingDiesWhileParsedOwnerAndSourceSurvive(string id)
    {
        InMemoryQpdfHost host = Host(id);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        (object owner, (WeakReference Backing, bool Borrowed)[] buffers) = CaptureBuffers(document, governor);
        try
        {
            Assert.Contains(buffers, value => !value.Borrowed);
            Collect();
            Assert.All(buffers, value => Assert.Equal(value.Borrowed, value.Backing.IsAlive));
            Assert.True(Data(owner).PageCount > 0);
            Assert.True(governor.Snapshot().LiveMemoryBytes >= 128L);
            GC.KeepAlive(document); GC.KeepAlive(owner);
        }
        finally { ((IDisposable)owner).Dispose(); }
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("compressed", false)]
    [InlineData("compressed", true)]
    [InlineData("overflow-36-valid", false)]
    [InlineData("overflow-36-valid", true)]
    public async Task EitherTokenAfterObservedDecodeRetiresScratch(string id, bool governorToken)
    {
        InMemoryQpdfHost host = Host(id);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        bool observed = false;
        Assert.ThrowsAny<OperationCanceledException>(() => ReadObserved(document, QpdfLimits.Default, governor,
            governorToken ? CancellationToken.None : cancellation.Token, memory =>
            {
                Assert.True(memory.Length > 0);
                observed = true;
                cancellation.Cancel();
            }));
        Assert.True(observed);
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.True(document.PageCount > 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Owner, ParserMemoryScope Scope, WeakReference[] Arrays) Capture(QpdfDocument document,
        QpdfLimits limits, ResourceGovernor governor)
    {
        object owner = ReadOwned(document, limits, governor, CancellationToken.None);
        OwnedAllocationBatch allocations = owner.GetType().GetField("_allocations", Fields)?.GetValue(owner) as OwnedAllocationBatch
            ?? throw new InvalidOperationException();
        LinearizationData data = Data(owner);
        List<WeakReference> arrays = new() { new(data.PageTable.Entries), new(data.SharedTable.Entries) };
        foreach (PageOffsetEntry entry in data.PageTable.Entries)
        {
            if (entry.SharedIdentifiers.Count > 0) { arrays.Add(new WeakReference(entry.SharedIdentifiers)); }
            if (entry.SharedNumerators.Count > 0) { arrays.Add(new WeakReference(entry.SharedNumerators)); }
        }
        Assert.True(allocations.Memory.ReservedBytes >= 128L + 144L + 80L + 64L);
        return (owner, allocations.Memory, arrays.ToArray());
    }

    private static void Collect()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [Theory]
    [InlineData("classic-valid-two-member-group")]
    [InlineData("part8-streamed-control")]
    [InlineData("overflow-36-valid")]
    [InlineData("overflow-61-valid")]
    [InlineData("outlines-control")]
    [InlineData("compressed")]
    public async Task OwnedTablesDetachWithRootedOwnersAndReceipts(string id)
    {
        InMemoryQpdfHost host = Host(id);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), CancellationToken.None);
        QpdfLimits limits = id == "compressed" ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 32L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        (object owner, ParserMemoryScope scope, WeakReference[] arrays) = Capture(document, limits, governor);
        Collect();
        Assert.All(arrays, value => Assert.True(value.IsAlive));
        ((IDisposable)owner).Dispose();
        Assert.Equal(0L, scope.ReservedBytes);
        Assert.Null(owner.GetType().GetField("_data", Fields)?.GetValue(owner));
        Assert.Null(owner.GetType().GetField("_allocations", Fields)?.GetValue(owner));
        Collect();
        Assert.All(arrays, value => Assert.False(value.IsAlive));
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.True(document.PageCount > 0);
        GC.KeepAlive(owner); GC.KeepAlive(scope); GC.KeepAlive(document);
    }

    [Theory]
    [InlineData("overflow-36-valid", true)]
    [InlineData("overflow-61-valid", true)]
    [InlineData("overflow-36-short", false)]
    [InlineData("classic-wrong-page-shared-id", false)]
    [InlineData("compressed", true)]
    public async Task RepeatedInspectionRetiresDecodeAndTableStorage(string id, bool valid)
    {
        InMemoryQpdfHost host = Host(id);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), CancellationToken.None);
        QpdfLimits limits = id == "compressed" ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 32L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        for (int cycle = 0; cycle < 32; cycle++)
        {
            LinearizationInspection result = LinearizationCheck.Inspect(document.Engine, document.PageCount, limits, governor, CancellationToken.None);
            Assert.Equal(valid ? LinearizationStatus.Valid : LinearizationStatus.Invalid, result.Status);
            governor.CompletePrivateStorage();
            Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        if (id == "compressed") { Assert.True(governor.Snapshot().TotalDecodedBytes > 0L); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherTokenRejectsWithoutLeavingHintStorage(bool governorToken)
    {
        InMemoryQpdfHost host = Host("overflow-36-valid");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ReadOwned(document, QpdfLimits.Default, governor,
            governorToken ? CancellationToken.None : cancellation.Token));
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
    }
}
