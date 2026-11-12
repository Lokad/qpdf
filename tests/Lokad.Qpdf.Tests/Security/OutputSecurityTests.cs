using System.Security.Cryptography;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class OutputSecurityTests
{
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
            Assert.Equal(PasswordAuthentication.Owner, Revision6Encryption.Authenticate(security.Dictionary, "owner"u8, key, governor));
            encrypted = security.Encrypt("stored content"u8, payload, governor);
            security.Dispose();
            Assert.Throws<ObjectDisposedException>(() => security.Dictionary);
            Assert.Throws<ObjectDisposedException>(() => security.Encrypt([], payload, governor));
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
