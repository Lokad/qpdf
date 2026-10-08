using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class InlineResourceGovernanceTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] values)
        => new(values.ToDictionary(item => PdfName.FromAscii(item.Key), item => item.Value));

    [Theory]
    [InlineData(512L)]
    [InlineData(2048L)]
    public void NameCollisionWorkFailurePreservesPriorReceipt(long maximum)
    {
        PdfDictionary fonts = new(Enumerable.Range(1, 128).ToDictionary(index => PdfName.FromAscii("IIm" + index), _ => (QpdfObject)PdfName.FromAscii("Font")));
        PdfDictionary resources = Dictionary(("Font", fonts));
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = maximum };
        for (int attempt = 0; attempt < 8; attempt++)
        {
            ResourceGovernor governor = new(limits, CancellationToken.None);
            using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
            long baseline = governor.Snapshot().LiveMemoryBytes;
            int suffix = 1; PoolResolver resolver = new(new Dictionary<int, QpdfObject>());
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => InlineImageStorage.Create(
                "/W 1 /H 1"u8.ToArray(), "a"u8.ToArray(), 0L, 0L, resources, ref suffix,
                limits, governor, new(governor), resolver, CancellationToken.None));
            Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes);
            Assert.True(governor.Snapshot().TotalWork >= maximum);
        }
        GC.KeepAlive(resources);
    }

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
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); governor.ReserveMemory(0L); }
        Assert.False(output.IsAlive); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(61L, prior.ReservedBytes);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 62L, 32768L);
        GC.KeepAlive(resources); GC.KeepAlive(colors); GC.KeepAlive(borrowed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Capture(ResourceGovernor governor, PdfDictionary resources, PdfDictionary colors, PdfByteString borrowed)
    {
        int suffix = 1; PoolResolver resolver = new(new Dictionary<int, QpdfObject> { [17] = colors });
        InlineImageEntry image = InlineImageStorage.Create("/W 1 /H 1 /CS /Local"u8.ToArray(), "a"u8.ToArray(),
            0L, 0L, resources, ref suffix, QpdfLimits.Default, governor, new(governor), resolver, CancellationToken.None) ?? throw new InvalidOperationException();
        Assert.Equal(PdfName.FromAscii("IIm2"), image.Name);
        Assert.True(image.Stream.Dictionary.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject? color)); Assert.Same(borrowed, color);
        return new(image.Stream);
    }
}
