using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class InlineResourceGovernanceTests
{
    [Fact]
    public void UnresolvedColorspaceRefusesFramingBeforePayloadAdmission()
    {
        byte[] payload = new byte[1048576]; int suffix = 1;
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768 }, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor); PoolResolver resolver = new(new Dictionary<int, QpdfObject>());
        ContentTokenizer tokenizer = InlineImageStorageTests.DataTokenizer(payload, governor, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfFilterException exception = Assert.Throws<QpdfFilterException>(() => InlineImageStorage.ReadWithResources("/W 1 /H 1 /BPC 8 /CS /Missing"u8.ToArray(), tokenizer,
            0, 0, Dictionary(), true, ref suffix, QpdfLimits.Default, governor, diagnostics, resolver, CancellationToken.None));
        Assert.Equal(QpdfFilterFailureReason.UnsupportedFilter, exception.Reason); Assert.Equal(1, suffix);
        // Counting must not export the diagnostic list to a surviving caller owner.
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 131072L);
        governor.CompleteStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] values)
        => new(values.ToDictionary(item => PdfName.FromAscii(item.Key), item => item.Value));

    [Fact]
    public void ResolvedColorspaceIsBorrowedAndDoesNotFundOrRemapItsBacking()
    {
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        PdfDictionary colors = Dictionary(("Local", borrowed));
        PdfDictionary resources = Dictionary(("ColorSpace", new PdfReference(17, 0)), ("Font", Dictionary(("IIm1", PdfName.FromAscii("Font")))));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        WeakReference output = Capture(governor, resources, colors, borrowed);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(output.IsAlive); governor.CompletePrivateStorage();
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(61L, prior.ReservedBytes);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 62L, 32768L);
        GC.KeepAlive(resources); GC.KeepAlive(colors); GC.KeepAlive(borrowed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Capture(ResourceGovernor governor, PdfDictionary resources, PdfDictionary colors, PdfByteString borrowed)
    {
        PoolResolver resolver = new(new Dictionary<int, QpdfObject> { [17] = colors });
        using ParserMemoryScope parsed = ParserMemoryScope.Create(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), resolver);
        PdfDictionary raw = Assert.IsType<PdfDictionary>(parser.ParseValue(new("<< /W 1 /CS /Local >>"u8.ToArray()), 0, parsed));
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        PdfDictionary image = InlineImageDictionary.BuildWithResources(graph, raw, resources, new(governor), resolver) ?? throw new InvalidOperationException();
        graph.RetainParsed(parsed);
        Assert.True(image.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject? color)); Assert.Same(borrowed, color);
        return new(image);
    }
}
