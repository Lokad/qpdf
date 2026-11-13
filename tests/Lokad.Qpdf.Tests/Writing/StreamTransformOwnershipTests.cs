using System.Runtime.CompilerServices;
using System.IO.Compression;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(NameTableCollection))]
public sealed class StreamTransformOwnershipTests
{
    [Fact]
    public void UnfilteredSliceCompressesWithinWorkingStorageWithoutCopyingThePayload()
    {
        byte[] backing = new byte[1048576 + 2]; backing.AsSpan().Fill(7);
        PdfStream source = PdfStream.FromImmutableSlice(new(new Dictionary<PdfName, QpdfObject>()), backing.AsMemory(1, 1048576));
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 600000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfStream compressed;
        using (DocumentEngine engine = Engine())
        {
            compressed = Assert.IsType<PdfStream>(StreamTransformer.Transform(source, 4, engine, true,
                "generalized", false, null, limits, governor, new(governor), CancellationToken.None));
            Assert.True(governor.Snapshot().PeakMemoryBytes < limits.MaxMemoryBytes);
        }
        Array.Fill(backing, (byte)0);
        using MemoryStream encoded = new(compressed.Data); using ZLibStream inflater = new(encoded, CompressionMode.Decompress);
        using MemoryStream decoded = new(); inflater.CopyTo(decoded);
        Assert.Equal(1048576L, decoded.Length); Assert.Equal(-1, decoded.GetBuffer().AsSpan(0, (int)decoded.Length).IndexOfAnyExcept((byte)7));
    }

    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
        return TestDocumentEngine.Create(trailer, governor);
    }
    private static PdfStream Source() => new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("ASCIIHexDecode"), [PdfName.FromAscii("Keep")] = new PdfInteger("1", 1L) }), "61626364>"u8.ToArray());
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PdfStream Run(ResourceGovernor governor, QpdfLimits limits, bool compress)
    {
        using DocumentEngine engine = Engine();
        return Assert.IsType<PdfStream>(StreamTransformer.Transform(Source(), 4, engine, compress,
            "generalized", false, null, limits, governor, new DiagnosticBag(governor), governor.CancellationToken));
    }
    private sealed class Holder(object value, WeakReference governor)
    { internal object? Value = value; internal readonly WeakReference Governor = governor; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Holder Capture(ResourceGovernor governor, bool compress, string alias)
    {
        PdfStream stream = Run(governor, QpdfLimits.Default, compress);
        object value = alias switch
        {
            "bytes" => stream.AllocationCarrier,
            "keys" => stream.Dictionary.Keys,
            "header" => stream.Dictionary,
            "filter" => Get(stream.Dictionary, "Filter"),
            _ => stream
        };
        return new(value, new(governor));
    }
    private static QpdfObject Get(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : throw new InvalidOperationException();
    [Theory]
    [InlineData(false, "bytes")]
    [InlineData(false, "keys")]
    [InlineData(false, "header")]
    [InlineData(false, "stream")]
    [InlineData(true, "bytes")]
    [InlineData(true, "keys")]
    [InlineData(true, "header")]
    [InlineData(true, "stream")]
    [InlineData(true, "filter")]
    public void EscapingStorageRemainsFundedUntilItsLastAliasDies(bool compress, string alias)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Holder holder = Capture(governor, compress, alias); Collect();
        Assert.True(governor.Snapshot().LiveMemoryBytes > 0);
        holder.Value = null; Collect(); governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(holder);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Holder Completed(bool compress) => Capture(new(QpdfLimits.Default, CancellationToken.None), compress, "stream");
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputDoesNotAnchorCompletedExecutionState(bool compress)
    { Holder holder = Completed(compress); Collect(); Assert.False(holder.Governor.IsAlive); GC.KeepAlive(holder.Value); }
    [Theory]
    [InlineData("memory")]
    [InlineData("output")]
    [InlineData("cancel")]
    public void EncodingFailurePreservesPriorAdmission(string kind)
    {
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = kind == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 4096L }
            : kind == "output" ? QpdfLimits.Default with { MaxOutputBytes = 1L } : QpdfLimits.Default;
        ResourceGovernor governor = new(limits, cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        if (kind == "cancel") { cancellation.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => Run(governor, limits, true)); }
        else { Assert.Throws<QpdfLimitException>(() => Run(governor, limits, true)); }
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes);
    }
    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
