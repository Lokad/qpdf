using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class InlineResourceGovernanceTests
{
    [Fact]
    public void UnresolvedColorspaceDoesNotMaterializeTheBorrowedPayload()
    {
        byte[] payload = new byte[1048576]; int suffix = 1;
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768 }, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor); PoolResolver resolver = new(new Dictionary<int, QpdfObject>());
        long before = GC.GetAllocatedBytesForCurrentThread();
        InlineImageEntry? result = InlineImageStorage.CreateWithResources("/W 1 /H 1 /BPC 8 /CS /Missing"u8.ToArray(), payload,
            0, 0, Dictionary(), ref suffix, QpdfLimits.Default, governor, diagnostics, resolver, CancellationToken.None);
        Assert.Null(result); Assert.Equal(1, suffix);
        // Counting must not export the diagnostic list to a surviving caller owner.
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
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
        int suffix = 1; PoolResolver resolver = new(new Dictionary<int, QpdfObject> { [17] = colors });
        InlineImageEntry image = InlineImageStorage.CreateWithResources("/W 1 /H 1 /CS /Local"u8.ToArray(), "a"u8.ToArray(),
            0L, 0L, resources, ref suffix, QpdfLimits.Default, governor, new(governor), resolver, CancellationToken.None) ?? throw new InvalidOperationException();
        Assert.Equal(PdfName.FromAscii("IIm2"), image.Name);
        Assert.True(image.Stream.Dictionary.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject? color)); Assert.Same(borrowed, color);
        return new(image.Stream);
    }
}
