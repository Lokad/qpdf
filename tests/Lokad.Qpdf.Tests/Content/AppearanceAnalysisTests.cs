using System.Buffers;
using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceAnalysisTests
{
    [Fact]
    public void LargeUnchangedAppearanceBorrowsBytesWithoutTokenSizedStorage()
    {
        byte[] input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("/F#31 12 Tf ", 20000)));
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GenerateAppearances.TfResult result = GenerateAppearances.AnalyzeDa(input, governor, memory, CancellationToken.None);
        Assert.Equal(input.AsMemory(), result.Appearance);
        Assert.Equal("F1"u8.ToArray(), result.FontName);
        Assert.Equal(12.0, result.Size);
        Assert.Equal(34L, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("/F1 12 Tf %keep\n/F#32 0 Tf  0 g", "/F1 12 Tf %keep\n/F#32 12 Tf  0 g", "F2", 12.0)]
    [InlineData("/F1 1001 Tf", "/F1 11 Tf", "F1", 11.0)]
    [InlineData("/F1 0.5 Tf /F2 10 Tf", "/F1 0.5 Tf /F2 10 Tf", "F2", 10.0)]
    [InlineData("/F#2f1 2 Tf", "/F#2f1 2 Tf", "F/1", 2.0)]
    [InlineData("/F1 12 Tf (unterminated", "/F1 12 Tf (unterminated", "", 11.0)]
    public void StreamingAnalysisPreservesSpellingAndLastFontSemantics(string text, string expected, string font, double size)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GenerateAppearances.TfResult result = GenerateAppearances.AnalyzeDa(Encoding.ASCII.GetBytes(text),
            governor, memory, CancellationToken.None);
        Assert.Equal(expected, Encoding.ASCII.GetString(result.Appearance.Span));
        Assert.Equal(font, Encoding.ASCII.GetString(result.FontName));
        Assert.Equal(size, result.Size);
    }

    [Fact]
    public void CorrectedLargeAppearanceRefusesBeforeCopyingAndPreservesPriorAdmission()
    {
        byte[] input = Encoding.ASCII.GetBytes(new string(' ', 200000) + "/F1 0 Tf");
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Assert.Throws<QpdfLimitException>(() => GenerateAppearances.AnalyzeDa(input, governor, memory, CancellationToken.None));
        }
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringAnalysisPreservesPriorAdmission(bool governorToken)
    {
        using CancellationTokenSource source = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        using MemoryManager<byte> input = new CancelingMemory(Encoding.ASCII.GetBytes(new string(' ', 20000) + "/F1 12 Tf"), source);
        Assert.ThrowsAny<OperationCanceledException>(() => GenerateAppearances.AnalyzeDa(input.Memory,
            governor, memory, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingMemory(byte[] bytes, CancellationTokenSource source) : MemoryManager<byte>
    {
        private int _visits;
        public override Span<byte> GetSpan()
        {
            if (++_visits == 8) { source.Cancel(); }
            return bytes;
        }
        public override MemoryHandle Pin(int elementIndex) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
