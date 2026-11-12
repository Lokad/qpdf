using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PdfStringCryptTests
{
    [Theory]
    [InlineData("/Type /Sig", true)]
    [InlineData("/Type /DocTimeStamp", true)]
    [InlineData("/Filter /Adobe.PPKLite /ByteRange [0 1 2 3]", true)]
    [InlineData("/Type /Page", false)]
    [InlineData("", false)]
    public void DirectStringTraversalSharesSignatureExceptionsAndLeavesReferencesUntouched(string attributes, bool signature)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfDictionary original = Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(
            "<< " + attributes + " /Contents <0001> /Reason (reason) /Mixed [2 0 R (nested) << /Text <6869> >>] >>")), 0, source));
        using OwnedAllocationBatch encryptedStorage = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        byte[] key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        PdfDictionary encrypted = Assert.IsType<PdfDictionary>(PdfStringCrypt.Value(original, encryptedStorage, 0, key, true));
        PdfByteString contents = Assert.IsType<PdfByteString>(Get(encrypted, "Contents"));
        Assert.Equal(signature, ReferenceEquals(Get(original, "Contents"), contents));
        PdfByteString reason = Assert.IsType<PdfByteString>(Get(encrypted, "Reason")); Assert.True(reason.Hex);
        using Aes aes = Aes.Create(); aes.SetKey(key);
        Assert.Equal("reason"u8.ToArray(), aes.DecryptCbc(reason.ValueSpan[16..], reason.ValueSpan[..16], PaddingMode.PKCS7));
        PdfArray mixed = Assert.IsType<PdfArray>(Get(encrypted, "Mixed"));
        Assert.Same(Assert.IsType<PdfArray>(Get(original, "Mixed"))[0], mixed[0]);
        using OwnedAllocationBatch plainStorage = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        PdfDictionary plain = Assert.IsType<PdfDictionary>(PdfStringCrypt.Value(encrypted, plainStorage, 0, key, false));
        Assert.Equal(new byte[] { 0, 1 }, Assert.IsType<PdfByteString>(Get(plain, "Contents")).Value);
        PdfArray restored = Assert.IsType<PdfArray>(Get(plain, "Mixed"));
        Assert.Equal("nested"u8.ToArray(), Assert.IsType<PdfByteString>(restored[1]).Value);
        Assert.Equal("hi"u8.ToArray(), Assert.IsType<PdfByteString>(Get(Assert.IsType<PdfDictionary>(restored[2]), "Text")).Value);
        Assert.Equal("reason"u8.ToArray(), Assert.IsType<PdfByteString>(Get(original, "Reason")).Value);
    }

    [Fact]
    public void EncryptionTraversalHonorsNestingAndCancellation()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxNestingDepth = 2 }, cancellation.Token);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellation.Token))
        {
            QpdfObject value = new PdfArray([new PdfArray([new PdfArray([new PdfByteString("text"u8.ToArray(), false)])])]);
            Assert.Throws<QpdfLimitException>(() => PdfStringCrypt.Value(value, graph, 0, new byte[32], true));
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => PdfStringCrypt.Value(value, graph, 0, new byte[32], true));
        }
        Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
    }

    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
}
