using System.Buffers;
using System.Text;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceValueStorageTests
{
    [Theory]
    [InlineData("636166E9", "café")]
    [InlineData("FEFF00630061006600E9", "café")]
    [InlineData("FFFE630061006600E900", "café")]
    [InlineData("FEFFD83DDE00", "😀")]
    [InlineData("FEFFD8000041", "A")]
    [InlineData("FEFFDC110041FF", "\u0011A")]
    [InlineData("FEFFD800", "")]
    [InlineData("AD", "\uFFFD")]
    public void GovernedTextDecodePreservesPdfDocUtf16AndMalformedPolicies(string hex, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            string value = PdfText.Decode(Convert.FromHexString(hex), memory, governor, CancellationToken.None);
            Assert.Equal(expected, value);
            Assert.Equal(GovernedText.StringBytes(value.Length), memory.ReservedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(0, "café😀", "6361663F3F3F")]
    [InlineData(1, "café€😀", "636166E9803F")]
    [InlineData(2, "café😀", "6361668E3F")]
    public void ValueEncodingOwnsOnlyItsFinalArray(int encoding, string text, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            byte[] bytes = GenerateAppearances.EncodeValue(text, (GenerateAppearances.AppearanceEncoding)encoding, memory, governor, CancellationToken.None);
            Assert.Equal(expected, Convert.ToHexString(bytes));
            Assert.Equal(32L + bytes.Length, memory.ReservedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeValueRefusesBeforeMaterializingTextOrEncodedBytes(bool encode)
    {
        byte[] input = Encoding.ASCII.GetBytes(new string('A', 1048576));
        string text = new('A', input.Length);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() =>
        {
            if (encode) { GenerateAppearances.EncodeValue(text, GenerateAppearances.AppearanceEncoding.Ascii, memory, governor, CancellationToken.None); }
            else { PdfText.Decode(input, memory, governor, CancellationToken.None); }
        });
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < input.Length);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBetweenSizingAndTextFillReturnsTheNewReservation(bool governorToken)
    {
        using CancellationTokenSource source = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        using MemoryManager<byte> input = new CancelingMemory(new byte[32768], source);
        Assert.ThrowsAny<OperationCanceledException>(() => PdfText.Decode(input.Memory, memory, governor,
            governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingMemory(byte[] bytes, CancellationTokenSource source) : MemoryManager<byte>
    {
        private int _reads;
        public override Span<byte> GetSpan() { if (++_reads == 3) { source.Cancel(); } return bytes; }
        public override MemoryHandle Pin(int elementIndex) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
