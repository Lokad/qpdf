using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Security;

public sealed class DocumentSecurityTests
{
    [Theory]
    [InlineData("/Type /Sig /ByteRange [0 1 2 3]")]
    [InlineData("/Type /DocTimeStamp /ByteRange [0 1 2 3]")]
    [InlineData("/Filter /Adobe.PPKLite /ByteRange [0 1 2 3]")]
    public void SignatureContentsRemainRawWhileOtherStringsDecrypt(string signatureEntries)
    {
        WithContext(0, static text => text, (security, key, governor, memory) =>
        {
            byte[] encoded = Encrypt("ordinary field"u8, key);
            string ciphertext = Convert.ToHexString(encoded);
            PdfDictionary source = Parse($"<< {signatureEntries} /Contents <01020304> /Reason <{ciphertext}> /Other [<{ciphertext}> << /Contents <{ciphertext}> >>] >>", governor, memory);
            PdfDictionary result = Assert.IsType<PdfDictionary>(security.Decrypt(source, false, new NoResolver(), memory));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, Assert.IsType<PdfByteString>(Get(result, "Contents")).Value);
            Assert.Equal("ordinary field", Text(Get(result, "Reason")));
            PdfArray other = Assert.IsType<PdfArray>(Get(result, "Other"));
            Assert.Equal("ordinary field", Text(other[0]));
            Assert.Equal("ordinary field", Text(Get(Assert.IsType<PdfDictionary>(other[1]), "Contents")));
            Assert.Equal(encoded, Assert.IsType<PdfByteString>(Get(source, "Reason")).Value);
        });
    }

    [Theory]
    [InlineData("/Filter /Crypt", false)]
    [InlineData("/Filter /Crypt /DecodeParms << /Name /Identity >>", false)]
    [InlineData("/Filter /Crypt /DecodeParms << /Name /StdCF >>", true)]
    [InlineData("/Filter [/Crypt /FlateDecode] /DecodeParms [<< /Name /StdCF >> null]", true)]
    public void ExplicitCryptFiltersOverrideDefaultsAndAreConsumed(string entries, bool encrypted)
    {
        WithContext(0, static text => text, (security, key, governor, memory) =>
        {
            byte[] plain = "encoded stream bytes"u8.ToArray();
            PdfStream stream = new(Parse($"<< {entries} >>", governor, memory), encrypted ? Encrypt(plain, key) : plain);
            PdfStream result = Assert.IsType<PdfStream>(security.Decrypt(stream, false, new NoResolver(), memory));
            Assert.Equal(plain, result.Data);
            if (entries.Contains("FlateDecode", StringComparison.Ordinal))
            {
                PdfArray filters = Assert.IsType<PdfArray>(Get(result.Dictionary, "Filter"));
                Assert.Single(filters.Items); Assert.Equal(PdfName.FromAscii("FlateDecode"), filters[0]);
                PdfArray parms = Assert.IsType<PdfArray>(Get(result.Dictionary, "DecodeParms"));
                Assert.Single(parms.Items); Assert.IsType<PdfNull>(parms[0]);
            }
            else { Assert.False(result.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _)); }
        });
    }

    [Theory]
    [InlineData("/Filter [/FlateDecode /Crypt]")]
    [InlineData("/Filter [/Crypt /Crypt]")]
    [InlineData("/Filter /Crypt /DecodeParms << /Name (invalid) >>")]
    public void MalformedCryptFiltersNeverPassThrough(string entries)
    {
        WithContext(0, static text => text.Replace("/StrF /StdCF", "/StrF /Identity", StringComparison.Ordinal), (security, key, governor, memory) =>
        {
            PdfStream stream = new(Parse($"<< {entries} >>", governor, memory), Encrypt("data"u8, key));
            long before = memory.ReservedBytes;
            Assert.Throws<QpdfParseException>(() => security.Decrypt(stream, false, new NoResolver(), memory));
            Assert.Equal(before, memory.ReservedBytes);
        });
    }

    [Fact]
    public void UnknownCryptFilterRejectsInsteadOfTreatingCiphertextAsPlain()
    {
        WithContext(0, static text => text, (security, key, governor, memory) =>
        {
            PdfStream stream = new(Parse("<< /Filter /Crypt /DecodeParms << /Name /Unknown >> >>", governor, memory), Encrypt("data"u8, key));
            Assert.Throws<QpdfCryptoException>(() => security.Decrypt(stream, false, new NoResolver(), memory));
        });
    }

    [Fact]
    public void OnlyRootMetadataIsExemptAndExplicitFiltersStillApply()
    {
        WithContext(1, static text => text, (security, key, governor, memory) =>
        {
            Assert.False(security.EncryptMetadata);
            byte[] plain = "<metadata/>"u8.ToArray();
            PdfDictionary header = Parse("<< /Type /Metadata >>", governor, memory);
            PdfStream root = new(header, plain), component = new(header, Encrypt(plain, key));
            Assert.Equal(plain, Assert.IsType<PdfStream>(security.Decrypt(root, true, new NoResolver(), memory)).Data);
            Assert.Equal(plain, Assert.IsType<PdfStream>(security.Decrypt(component, false, new NoResolver(), memory)).Data);
            PdfStream explicitStream = new(Parse("<< /Type /Metadata /Filter /Crypt /DecodeParms << /Name /StdCF >> >>", governor, memory), Encrypt(plain, key));
            Assert.Equal(plain, Assert.IsType<PdfStream>(security.Decrypt(explicitStream, true, new NoResolver(), memory)).Data);
        });
    }

    [Fact]
    public void EmbeddedFilesUseEffWhileStringsUseStrFAndXrefsStayUntouched()
    {
        WithContext(0, static text => text.Replace("/StmF /StdCF", "/StmF /Identity /EFF /StdCF", StringComparison.Ordinal), (security, key, governor, memory) =>
        {
            byte[] plain = "attachment data"u8.ToArray();
            string encryptedName = Convert.ToHexString(Encrypt("filename.txt"u8, key));
            PdfStream embedded = new(Parse($"<< /Type /EmbeddedFile /Private <{encryptedName}> >>", governor, memory), Encrypt(plain, key));
            PdfStream result = Assert.IsType<PdfStream>(security.Decrypt(embedded, false, new NoResolver(), memory));
            Assert.Equal(plain, result.Data); Assert.Equal("filename.txt", Text(Get(result.Dictionary, "Private")));
            PdfStream ordinary = new(Parse("<< >>", governor, memory), plain);
            Assert.Equal(plain, Assert.IsType<PdfStream>(security.Decrypt(ordinary, false, new NoResolver(), memory)).Data);
            PdfStream xref = new(Parse("<< /Type /XRef /ID [<01020304> <05060708>] >>", governor, memory), plain);
            Assert.Same(xref, security.Decrypt(xref, false, new NoResolver(), memory));
        });
    }

    [Fact]
    public void ExternalPayloadsStayOpaqueWhileTheirDictionaryStringsDecrypt()
    {
        WithContext(0, static text => text, (security, key, governor, memory) =>
        {
            string encryptedPath = Convert.ToHexString(Encrypt("unopened.bin"u8, key));
            PdfStream stream = new(Parse($"<< /F <{encryptedPath}> >>", governor, memory), [1, 2, 3]);
            PdfStream result = Assert.IsType<PdfStream>(security.Decrypt(stream, false, new NoResolver(), memory));
            Assert.Equal(new byte[] { 1, 2, 3 }, result.Data);
            Assert.Equal("unopened.bin", Text(Get(result.Dictionary, "F")));
        });
    }

    [Theory]
    [InlineData("/Filter /Standard", "/Filter /Unknown")]
    [InlineData("/CFM /AESV3", "/CFM /Unknown")]
    [InlineData("/StrF /StdCF", "/StrF /Unknown")]
    [InlineData("/StmF /StdCF", "/StmF /Unknown")]
    [InlineData("/AuthEvent /DocOpen", "/AuthEvent /Unknown")]
    public void UnsupportedHandlersAndSelectedFiltersNeverProduceAContext(string original, string replacement)
    {
        object[] vector = Revision6EncryptionTests.Vectors().First();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfDictionary dictionary = Parse(((string)vector[1]).Replace(original, replacement, StringComparison.Ordinal), governor, memory);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfCryptoException>(() => DocumentSecurity.TryCreate(dictionary, "reader"u8, governor, out _));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("/Length 256", "/Length 128")]
    [InlineData("/Length 32", "/Length 16")]
    [InlineData("/AuthEvent /DocOpen", "/AuthEvent /EFOpen")]
    [InlineData("/StrF /StdCF", "/StrF (StdCF)")]
    public void MalformedParametersStayDistinctFromUnsupportedHandlers(string original, string replacement)
    {
        object[] vector = Revision6EncryptionTests.Vectors().First();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfDictionary dictionary = Parse(((string)vector[1]).Replace(original, replacement, StringComparison.Ordinal), governor, memory);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfParseException>(() => DocumentSecurity.TryCreate(dictionary, "reader"u8, governor, out _));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void WrongPasswordReturnsNoContextAndDisposedContextsCannotDecrypt()
    {
        object[] vector = Revision6EncryptionTests.Vectors().First();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            PdfDictionary dictionary = Parse((string)vector[1], governor, memory);
            long baseline = governor.Snapshot().LiveMemoryBytes;
            Assert.Equal(PasswordAuthentication.None, DocumentSecurity.TryCreate(dictionary, "wrong"u8, governor, out DocumentSecurity? wrong));
            Assert.Null(wrong); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(PasswordAuthentication.User, DocumentSecurity.TryCreate(dictionary, Convert.FromHexString((string)vector[2]), governor, out DocumentSecurity? security));
            Assert.NotNull(security); security.Dispose(); security.Dispose();
            Assert.Throws<ObjectDisposedException>(() => security.Decrypt(PdfNull.Instance, false, new NoResolver(), memory));
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static void WithContext(int vectorIndex, Func<string, string> edit, Action<DocumentSecurity, byte[], ResourceGovernor, ParserMemoryScope> check)
    {
        object[] vector = Revision6EncryptionTests.Vectors().ElementAt(vectorIndex);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            PdfDictionary dictionary = Parse(edit((string)vector[1]), governor, memory);
            Assert.Equal(PasswordAuthentication.User, DocumentSecurity.TryCreate(dictionary, Convert.FromHexString((string)vector[2]), governor, out DocumentSecurity? security));
            Assert.NotNull(security);
            using (security) { check(security, Convert.FromHexString((string)vector[4]), governor, memory); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static byte[] Encrypt(ReadOnlySpan<byte> plain, byte[] key)
    { using Aes aes = Aes.Create(); aes.SetKey(key); byte[] iv = new byte[16]; return [.. iv, .. aes.EncryptCbc(plain, iv, PaddingMode.PKCS7)]; }

    private static PdfDictionary Parse(string text, ResourceGovernor governor, ParserMemoryScope memory)
        => Assert.IsType<PdfDictionary>(new PdfParser(QpdfLimits.Default, governor, new DiagnosticBag(governor), null)
            .ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, memory));

    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }

    private static string Text(QpdfObject value) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(value).ValueSpan);

    private sealed class NoResolver : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { value = null; return false; }
    }
}
