using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class DictionaryRebuildTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FreshEmptyBinaryAndLongNamesOwnExactlyOneCopiedBuffer(int shape)
    {
        byte[] raw = shape switch { 0 => [], 1 => [0, 255, 35, 128], _ => new byte[16385] };
        if (raw.Length > 8192) { raw[8192] = 99; raw[^1] = 91; }
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>());
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 65536L }, CancellationToken.None);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, source, raw, borrowed);
        PdfName key = Assert.Single(result.Keys);
        Assert.Equal(raw, key.RawSpan.ToArray()); Assert.NotSame(raw, key.AllocationCarrier);
        if (shape == 0) { Assert.NotSame(Array.Empty<byte>(), key.AllocationCarrier); }
        Assert.True(result.TryGet(key, out QpdfObject value)); Assert.Same(borrowed, value);
        long overhead = graph.Memory.ReservedBytes - graph.Bytes;
        Assert.InRange(overhead, 1L, 4096L);
        graph.Retain(); Assert.Equal(overhead, graph.Memory.ReservedBytes);
        Assert.Equal(0, source.Count); GC.KeepAlive(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerCancellationStopsBeforeConstruction(bool strip)
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<OperationCanceledException>(() => BuildCanceled(governor, cancellation.Token, strip));
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);     }

    private static void BuildCanceled(ResourceGovernor governor, CancellationToken token, bool strip)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>());
        _ = strip ? PdfDictionaryRebuild.StripStreamEncoding(graph, source) : PdfDictionaryRebuild.ReplaceEntry(graph, source, "Contents"u8, PdfNull.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterConstructionKeepsScopeFundingWithoutActivatingReceipts(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, governorToken ? CancellationToken.None : cancellation.Token);
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>());
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, source, "Contents"u8, PdfNull.Instance);
        long live = governor.Snapshot().LiveMemoryBytes, reserved = graph.Memory.ReservedBytes;
        cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => graph.Retain());
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(reserved, graph.Memory.ReservedBytes);         graph.Dispose(); governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1, result.Count); GC.KeepAlive(result);
    }

    [Fact]
    public void LongNameAdmissionFailurePreservesOnlyPriorStorage()
    {
        byte[] raw = new byte[32769]; PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>());
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long live = governor.Snapshot().LiveMemoryBytes;
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PdfDictionaryRebuild.ReplaceEntry(graph, source, raw, PdfNull.Instance));
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        }
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes); Assert.Equal(0, source.Count);
    }

    private sealed class Escaped(object value, WeakReference governor)
    { internal object? Value = value; internal readonly WeakReference Governor = governor; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Escaped Capture(ResourceGovernor governor, bool bytesOnly)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>());
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, source, ReadOnlySpan<byte>.Empty, PdfNull.Instance);
        graph.Retain(); PdfName key = Assert.Single(result.Keys);
        return new(bytesOnly ? key.AllocationCarrier : key, new(governor));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Verify(Escaped escaped, bool bytesOnly)
    {
        object value = escaped.Value ?? throw new InvalidOperationException();
        if (bytesOnly) { Assert.Empty(Assert.IsType<byte[]>(value)); }
        else { Assert.Empty(Assert.IsType<PdfName>(value).RawSpan.ToArray()); }
    }

    private static void Collect(ResourceGovernor? governor)
    { for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); governor?.ReserveMemory(0L); } }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyNameAndItsBytesRetireIndependentlyFromStaticEmptyArrays(bool bytesOnly)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); Escaped escaped = Capture(governor, bytesOnly);
        Collect(governor); Assert.True(governor.Snapshot().LiveMemoryBytes >= (bytesOnly ? 32L : 56L)); Verify(escaped, bytesOnly);
        escaped.Value = null; Collect(governor); governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(governor); GC.KeepAlive(escaped);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Escaped Complete(bool bytesOnly) => Capture(new(QpdfLimits.Default, CancellationToken.None), bytesOnly);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyNameAliasesDoNotAnchorCompletedGovernors(bool bytesOnly)
    {
        Escaped escaped = Complete(bytesOnly); Collect(null); Assert.False(escaped.Governor.IsAlive); Verify(escaped, bytesOnly); GC.KeepAlive(escaped);
    }
}
