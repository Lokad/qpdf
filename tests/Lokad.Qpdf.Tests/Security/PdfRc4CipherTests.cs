using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PdfRc4CipherTests
{
    // Published independent vectors: https://www.rfc-editor.org/rfc/rfc6229.txt
    [Theory]
    [InlineData("0102030405", 0, "b2396305f03dc027ccc3524a0a1118a8")]
    [InlineData("0102030405", 16, "6982944f18fc82d589c403a47a0d0919")]
    [InlineData("0102030405", 4096, "ff25b58995996707e51fbdf08b34d875")]
    [InlineData("0102030405060708090a0b0c0d0e0f10", 0, "9ac7cc9a609d1ef7b2932899cde41b97")]
    [InlineData("0102030405060708090a0b0c0d0e0f10", 256, "d39d566bc6bce3010768151549f3873f")]
    [InlineData("ebb46227c6cc8b37641910833222772a", 2048, "5865fdbb5b48064104e830b380f2aede")]
    public void IndependentVectorsMatchAcrossCancellationCheckBoundaries(string keyHex, int offset, string expectedHex)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] key = Convert.FromHexString(keyHex), original = (byte[])Convert.FromHexString(keyHex).Clone();
        byte[] bytes = new byte[offset + 16];
        PdfRc4Cipher.Transform(key, bytes, governor);
        Assert.Equal(Convert.FromHexString(expectedHex), bytes[offset..]);
        PdfRc4Cipher.Transform(key, bytes, governor);
        Assert.Equal(new byte[bytes.Length], bytes); Assert.Equal(original, key);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RefusalAndInitialCancellationDoNotTouchBorrowedData()
    {
        byte[] data = [1, 2, 3]; byte[] key = new byte[16];
        RecordingMemoryBudget budget = new(0);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
        ResourceGovernor refused = new(QpdfLimits.Default, CancellationToken.None, host);
        Assert.Throws<QpdfMemoryAdmissionException>(() => PdfRc4Cipher.Transform(key, data, refused));
        Assert.Equal(new byte[] { 1, 2, 3 }, data); Assert.Equal(0L, budget.ReservedBytes);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor canceled = new(QpdfLimits.Default, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => PdfRc4Cipher.Transform(key, data, canceled));
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
    }
}
