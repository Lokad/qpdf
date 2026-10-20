using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Filters;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class ContentBufferGraphTests
{
    private delegate PdfStream JoinCall(ContentEntries entries, PoolResolver resolver, ContentTransformRequest request, QpdfLimits limits, ResourceGovernor governor, CancellationToken token);
    private delegate PdfStream? NormalizeCall(PdfStream original, PoolResolver resolver, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token);
    private static readonly JoinCall Join = (typeof(ContentTransforms).GetMethod("BuildCoalescedContent", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException()).CreateDelegate<JoinCall>();
    private static readonly NormalizeCall Normalize = (typeof(ContentTransforms).GetMethod("BuildNormalizedContent", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException()).CreateDelegate<NormalizeCall>();
    private static PoolResolver Resolver() => new(new Dictionary<int, QpdfObject>());
    private static PdfStream Stream(string filter, byte[] data)
        => new(filter == "" ? DecoderOwnershipFixtures.Dictionary() : DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii(filter)), ("Length", new PdfInteger("999", 999L))),
            filter == "" ? data : DecoderOwnershipFixtures.Encode(filter, data));
    private static ContentEntries Entries(ResourceGovernor construction, params PdfStream[] streams)
    {
        ContentEntries entries = ContentEntries.Create(construction, CancellationToken.None);
        for (int index = 0; index < streams.Length; index++) { entries.Add(new(null, index, streams[index])); }
        return entries;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoalescingPreservesEmptyChunksNewlinesAndCompression(bool compress)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ContentEntries entries = Entries(construction, Stream("", []), Stream("ASCIIHexDecode", "q\n"u8.ToArray()), Stream("", []), Stream("FlateDecode", "Q"u8.ToArray()));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfStream output = Join(entries, Resolver(), new(true, false, compress, 9, false, 0L), QpdfLimits.Default, governor, CancellationToken.None);
        byte[] actual = output.Data;
        if (compress)
        {
            using ParserMemoryScope decoded = ParserMemoryScope.Create(construction);
            actual = AnalysisChunks.DecodeForContent(output, Resolver(), QpdfLimits.Default, construction, decoded, CancellationToken.None).Data;
        }
        Assert.Equal("\nq\n\nQ"u8.ToArray(), actual);
        Assert.Equal(compress ? 1 : 0, output.Dictionary.Count); Assert.True(governor.Snapshot().LiveMemoryBytes > output.RawMemory.Length);
        GC.KeepAlive(output);
    }

    [Fact]
    public void CoalescingKeepsFatalDecodePolicyAndReleasesTheValidPrefix()
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        PdfStream damaged = new(DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii("ASCIIHexDecode"))), "q Q"u8.ToArray());
        using ContentEntries entries = Entries(construction, Stream("FlateDecode", "q"u8.ToArray()), damaged);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(23L); long live = governor.Snapshot().LiveMemoryBytes;
        QpdfFilterException failure = Assert.Throws<QpdfFilterException>(() => Join(entries, Resolver(), new(true, false, false, null, false, 0L), QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, failure.Reason); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal("q Q"u8.ToArray(), damaged.Data);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ASCIIHexDecode")]
    [InlineData("invalid-filter-data")]
    public void NormalizingPreservesHeaderAndDecodeFallbackPolicy(string filter)
    {
        PdfStream original = filter == "invalid-filter-data"
            ? new(DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii("ASCIIHexDecode"))), "q\rQ"u8.ToArray())
            : Stream(filter, "q\rQ"u8.ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None), construction = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(construction);
        PdfStream output = Normalize(original, Resolver(), QpdfLimits.Default, governor, diagnostics, CancellationToken.None) ?? throw new InvalidOperationException();
        Assert.Equal("q\nQ"u8.ToArray(), output.Data); Assert.Empty(diagnostics.Entries);
        if (filter == "ASCIIHexDecode") { Assert.Equal(0, output.Dictionary.Count); }
        else { Assert.Same(original.Dictionary, output.Dictionary); }
        byte[] copy = output.Data; copy[0] = (byte)'X'; Assert.Equal((byte)'q', output.RawMemory.Span[0]);
        GC.KeepAlive(output);
    }

    [Fact]
    public void NormalizingDropsScratchBeforeTheOriginalWarning()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None), construction = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(construction); PdfStream original = Stream("ASCIIHexDecode", "(unterminated"u8.ToArray());
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(23L); long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Null(Normalize(original, Resolver(), QpdfLimits.Default, governor, diagnostics, CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        QpdfDiagnostic warning = Assert.Single(diagnostics.Entries);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code); Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("Left a content stream unnormalized after a content error.", warning.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerCancellationWinsBeforeBuilderAdmission(bool normalize)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None); PdfStream original = Stream("", "q"u8.ToArray());
        using ContentEntries entries = Entries(construction, original, original); using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1L }, CancellationToken.None);
        Assert.Throws<OperationCanceledException>(() => normalize ? Normalize(original, Resolver(), QpdfLimits.Default, governor, new(construction), cancellation.Token)
            : Join(entries, Resolver(), new(true, false, false, null, false, 0L), QpdfLimits.Default, governor, cancellation.Token));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);     }

    [Fact]
    public void JoinedBytesTransferWithoutCopyAndRejectAnotherGovernor()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None), other = new(QpdfLimits.Default, CancellationToken.None);
        using JoinedContent joined = JoinedContent.Create(["q"u8.ToArray(), "Q"u8.ToArray()], governor, CancellationToken.None);
        using OwnedAllocationBatch wrong = OwnedAllocationBatch.Create(other, CancellationToken.None);
        long live = governor.Snapshot().LiveMemoryBytes; Assert.Throws<InvalidOperationException>(() => joined.TakeBytes(wrong)); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        byte[] expected = joined.RawBytes;
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        byte[] actual = joined.TakeBytes(graph); Assert.Same(expected, actual); Assert.Equal("q\nQ"u8.ToArray(), actual);
        Assert.Throws<ObjectDisposedException>(() => joined.RawBytes); Assert.Throws<ObjectDisposedException>(() => joined.TakeBytes(graph));
        graph.Add(actual.Length + 32L); graph.Memory.Reserve(32L);
        PdfStream stream = PdfStream.FromOwnedData(DecoderOwnershipFixtures.Dictionary(), actual); graph.Add(32L); graph.Retain();
        Assert.Same(actual, stream.AllocationCarrier); joined.Dispose(); graph.Dispose(); Assert.Equal("q\nQ"u8.ToArray(), stream.Data); GC.KeepAlive(stream);
    }

    [Fact]
    public void OwnedFactoryPreservesPublicCopyingAndDoesNotAnchorCompletedGovernors()
    {
        byte[] raw = "q Q"u8.ToArray(); PdfStream publicStream = new(DecoderOwnershipFixtures.Dictionary(), raw); raw[0] = (byte)'X'; Assert.Equal((byte)'q', publicStream.RawMemory.Span[0]);
        var captured = Completed();
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(captured.Governor.IsAlive); Assert.Equal("q\nQ"u8.ToArray(), captured.Stream.Data); GC.KeepAlive(captured.Stream);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PdfStream Stream, WeakReference Governor) Completed()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None), construction = new(QpdfLimits.Default, CancellationToken.None);
        PdfStream stream = Normalize(Stream("ASCIIHexDecode", "q\rQ"u8.ToArray()), Resolver(), QpdfLimits.Default, governor, new(construction), CancellationToken.None) ?? throw new InvalidOperationException();
        return (stream, new(governor));
    }
}
