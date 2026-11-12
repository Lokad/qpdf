using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class OutputSecurityTests
{
    [Theory]
    [InlineData("", false, true)]
    [InlineData("/Type /EmbeddedFile", false, true)]
    [InlineData("/Type /Metadata", true, false)]
    [InlineData("/Type /Metadata", false, true)]
    [InlineData("/F (external)", false, false)]
    [InlineData("/F 10 0 R", false, true)]
    [InlineData("/Type /XRef", false, false)]
    public void PhysicalStreamEncryptionPreservesRequiredExceptionsAndUpdatesLengths(string fields, bool rootMetadata, bool encrypted)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using ParserMemoryScope destination = ParserMemoryScope.Create(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfDictionary header = Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(
            "<< " + fields + " /Length 13 /Note (header text) >>")), 0, source));
        PdfStream stream = new(header, "payload bytes"u8.ToArray());
        using OutputSecurity security = OutputSecurity.CreateRevision6("reader"u8, "owner"u8, -4, false, governor);
        PdfStream written = Assert.IsType<PdfStream>(security.EncryptObject(stream, 4, 0, rootMetadata, new NullResolver(), destination, governor));
        bool xref = fields.Contains("XRef", StringComparison.Ordinal);
        Assert.Equal(xref, ReferenceEquals(stream, written));
        byte[] key = new byte[32];
        Assert.Equal(PasswordAuthentication.User, Aes256Encryption.Authenticate(security.Dictionary, "reader"u8, key, governor));
        using Aes aes = Aes.Create(); aes.SetKey(key);
        if (encrypted)
        {
            Assert.Equal(PdfAesCipher.EncryptedLength(13), written.Data.Length);
            Assert.Equal("payload bytes"u8.ToArray(), aes.DecryptCbc(written.RawMemory.Span[16..], written.RawMemory.Span[..16], PaddingMode.PKCS7));
            Assert.True(written.Dictionary.TryGet(PdfName.FromAscii("Length"), out QpdfObject length));
            Assert.Equal(written.Data.Length, Assert.IsType<PdfInteger>(length).Value);
        }
        else { Assert.Equal("payload bytes"u8.ToArray(), written.Data); }
        Assert.True(written.Dictionary.TryGet(PdfName.FromAscii("Note"), out QpdfObject rawNote));
        byte[] note = Assert.IsType<PdfByteString>(rawNote).Value;
        Assert.Equal("header text"u8.ToArray(), xref ? note : aes.DecryptCbc(note.AsSpan(16), note.AsSpan(0, 16), PaddingMode.PKCS7));
        Assert.Equal("payload bytes"u8.ToArray(), stream.Data);
        Assert.Same(security.Dictionary, security.EncryptObject(security.Dictionary, 7, 0, false, new NullResolver(), destination, governor));
    }

    [Fact]
    public void UnconsumedCryptStagesFailWithoutTransferringPartialOutput()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope destination = ParserMemoryScope.Create(governor);
        PdfDictionary header = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("Crypt") });
        PdfStream stream = new(header, "opaque"u8.ToArray());
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfUnsupportedFeatureException>(() => security.EncryptObject(stream, 4, 0, false, new NullResolver(), destination, governor));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0, destination.ReservedBytes);
    }

    private sealed class NullResolver : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value) { value = PdfNull.Instance; return true; }
    }

    [Fact]
    public void EveryObjectEncryptionRefusalLeavesItsDestinationUnchanged()
    {
        (int before, int growths) = Run(new(1048576), false);
        for (int growth = 1; growth <= growths; growth++)
        { _ = Run(new(1048576) { RefuseOnGrowth = before + growth }, true); }

        static (int Before, int Growths) Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
            using ParserMemoryScope destination = ParserMemoryScope.Create(governor);
            PdfDictionary header = new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Note")] = new PdfByteString("header"u8.ToArray(), false) });
            PdfStream stream = new(header, new byte[1024]);
            int before = budget.GrowthCalls; long reserved = budget.ReservedBytes;
            if (refuses)
            {
                Assert.Throws<QpdfMemoryAdmissionException>(() => security.EncryptObject(stream, 4, 0, false, new NullResolver(), destination, governor));
                Assert.Equal(0, destination.ReservedBytes); Assert.Equal(reserved, budget.ReservedBytes);
            }
            else { _ = security.EncryptObject(stream, 4, 0, false, new NullResolver(), destination, governor); }
            int growths = budget.GrowthCalls - before;
            destination.Dispose(); security.Dispose(); Assert.Equal(0, budget.ReservedBytes);
            return (before, growths);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GeneratedDictionaryAuthenticatesAndCiphertextOutlivesTheKeyOwner(bool metadata)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope payload = ParserMemoryScope.Create(governor);
        long before = governor.Snapshot().LiveMemoryBytes;
        byte[] encrypted;
        byte[] key = new byte[32];
        using (OutputSecurity security = OutputSecurity.CreateRevision6("reader"u8, "owner"u8, -3388, metadata, governor))
        {
            Assert.Equal(PasswordAuthentication.User, DocumentSecurity.TryCreate(security.Dictionary, [], "reader"u8, governor, out DocumentSecurity? reader));
            using (reader)
            {
                Assert.NotNull(reader); Assert.Equal(6, reader.Revision); Assert.Equal(metadata, reader.EncryptMetadata);
                Assert.Equal(-3388, reader.Permissions);
            }
            Assert.Equal(PasswordAuthentication.Owner, Aes256Encryption.Authenticate(security.Dictionary, "owner"u8, key, governor));
            encrypted = security.Encrypt("stored content"u8, 4, 0, payload, governor);
            security.Dispose();
            Assert.Throws<ObjectDisposedException>(() => security.Dictionary);
            Assert.Throws<ObjectDisposedException>(() => security.Encrypt([], 4, 0, payload, governor));
        }
        Assert.Equal(before + payload.ReservedBytes, governor.Snapshot().LiveMemoryBytes);
        using Aes aes = Aes.Create(); aes.SetKey(key);
        Assert.Equal("stored content"u8.ToArray(), aes.DecryptCbc(encrypted.AsSpan(16), encrypted.AsSpan(0, 16), PaddingMode.PKCS7));
        CryptographicOperations.ZeroMemory(key);
        payload.Dispose(); Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EveryOutputContextAdmissionFailureRefundsKeyAndDictionaryStorage()
    {
        RecordingMemoryBudget baseline = new(1048576); Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        { Run(new(1048576) { RefuseOnGrowth = growth }, true); }

        static void Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            if (refuses)
            { Assert.Throws<QpdfMemoryAdmissionException>(() => OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor)); }
            else { using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor); }
            Assert.Equal(0, budget.ReservedBytes); Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
        }
    }
}
