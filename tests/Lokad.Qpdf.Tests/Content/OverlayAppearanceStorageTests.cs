using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayAppearanceStorageTests
{
    private static PdfNameTable<PdfName> Map(ParserMemoryScope memory, ResourceGovernor governor)
    {
        PdfNameTable<PdfName> map = PdfNameTable<PdfName>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        map.Assign(memory, governor, PdfName.FromAscii("F1"), PdfName.FromAscii("F1_1"), CancellationToken.None);
        return map;
    }

    [Theory]
    [InlineData("/Keep 12 Tf", null)]
    [InlineData("/F1 12 Tf (unfinished", null)]
    [InlineData("/F1 12 Tf 14 Tf /Keep 9 Tf", "/F1_1 12 Tf 14 Tf /Keep 9 Tf")]
    public void StreamingRewriteKeepsOriginalBytesAndMalformedFallback(string text, string? expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<PdfName> map = Map(memory, governor);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        PdfByteString? result = AcroFormCopy.RewriteOverlayDa(new(Encoding.ASCII.GetBytes(text), false), map,
            QpdfLimits.Default, governor, CancellationToken.None);
        if (expected is null) { Assert.Null(result); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); }
        else
        {
            Assert.NotNull(result); Assert.Equal(expected, Encoding.ASCII.GetString(result.ValueSpan));
            Assert.True(governor.Snapshot().LiveMemoryBytes >= baseline + result.ValueSpan.Length);
            governor.CompletePrivateStorage(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Fact]
    public void LargeUnchangedInputRequiresNoTokenSizedStorage()
    {
        PdfByteString input = new(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("/Keep 12 Tf ", 20000))), false);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<PdfName> map = Map(memory, governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Null(AcroFormCopy.RewriteOverlayDa(input, map, limits, governor, CancellationToken.None));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusalOrCancellationPreservesEarlierAdmission(bool cancel)
    {
        PdfByteString input = new(Encoding.ASCII.GetBytes("/F1 12 Tf " + new string(' ', 32768)), false);
        using CancellationTokenSource source = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2048L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<PdfName> map = Map(memory, governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        if (cancel)
        {
            source.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.RewriteOverlayDa(input, map, limits, governor, source.Token));
        }
        else { Assert.Throws<QpdfLimitException>(() => AcroFormCopy.RewriteOverlayDa(input, map, limits, governor, CancellationToken.None)); }
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }
}
