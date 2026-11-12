using System.Security.Cryptography;
using System.Text.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PdfAesCipherTests
{
    [Fact]
    public void NativeEncryptedStreamsMatchNativePlaintextBeforeFilterDecoding()
    {
        using JsonDocument vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-vectors.json")));
        foreach (JsonElement vector in vectors.RootElement.EnumerateArray())
        {
            string file = vector.GetProperty("file").GetString() ?? throw new InvalidDataException();
            byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
            byte[] original = (byte[])data.Clone();
            byte[] key = Convert.FromHexString(vector.GetProperty("fileKeyHex").GetString() ?? throw new InvalidDataException());
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            DiagnosticBag diagnostics = new(governor);
            long inputBytes = data.Length + GovernedBufferStream.ArrayOverheadBytes;
            governor.ReserveMemory(inputBytes);
            XRefTable table = XRefTable.Parse(data, QpdfLimits.Default, governor, diagnostics, QpdfRecoveryMode.Strict, false);
            using (DocumentEngine engine = DocumentEngine.CreateOwned(data, table, QpdfLimits.Default, governor, diagnostics,
                QpdfRecoveryMode.Strict, inputBytes, false, false, null))
            {
                Assert.NotEmpty(vector.GetProperty("streams").EnumerateArray());
                foreach (JsonElement stream in vector.GetProperty("streams").EnumerateArray())
                {
                    PdfStream encrypted = Assert.IsType<PdfStream>(engine.Load(stream.GetProperty("number").GetInt32()));
                    using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
                    byte[] plain = PdfAesCipher.Decrypt(encrypted.RawMemory.Span, key, owner, governor);
                    Assert.Equal(Convert.FromBase64String(stream.GetProperty("plainBase64").GetString() ?? throw new InvalidDataException()), plain);
                }
            }
            Assert.Equal(original, data);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 32)]
    [InlineData(15, 16)]
    [InlineData(16, 32)]
    [InlineData(65535, 32)]
    [InlineData(65536, 16)]
    [InlineData(65537, 32)]
    [InlineData(131072, 32)]
    public void ChunkBoundariesKeepCbcStateAndAdmitOnlyTheExactPlaintext(int length, int keyLength)
    {
        byte[] key = Enumerable.Range(1, keyLength).Select(value => (byte)value).ToArray();
        byte[] plain = Enumerable.Range(0, length).Select(value => (byte)(value % 251)).ToArray();
        byte[] encoded = Encrypt(plain, key, PaddingMode.PKCS7);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            Assert.Equal(plain, PdfAesCipher.Decrypt(encoded, key, owner, governor));
            Assert.Equal(length == 0 ? 0L : length + 32L, owner.ReservedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void BadLengthPaddingAndAdmissionNeverRetainPartialPlaintext()
    {
        byte[] key = new byte[32];
        byte[] invalidPadding = Encrypt(new byte[16], key, PaddingMode.None);
        byte[] valid = Encrypt(new byte[131072], key, PaddingMode.PKCS7);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            Assert.Throws<QpdfParseException>(() => PdfAesCipher.Decrypt(new byte[31], key, owner, governor));
            Assert.Throws<QpdfParseException>(() => PdfAesCipher.Decrypt(invalidPadding, key, owner, governor));
            Assert.Equal(0L, owner.ReservedBytes);
        }
        // Growth 1 belongs to the caller's owner; later growths fund the cipher
        // scratch and result. Its original owner survives either refusal.
        for (int refusal = 2; refusal <= 3; refusal++)
        {
            RecordingMemoryBudget budget = new(1048576) { RefuseOnGrowth = refusal };
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor refused = new(QpdfLimits.Default, CancellationToken.None, host);
            using (ParserMemoryScope owner = ParserMemoryScope.Create(refused))
            {
                Assert.Throws<QpdfMemoryAdmissionException>(() => PdfAesCipher.Decrypt(valid, key, owner, refused));
                Assert.Equal(0L, owner.ReservedBytes);
            }
            Assert.Equal(0L, budget.ReservedBytes);
            Assert.Equal(refusal, budget.GrowthCalls);
        }
    }

    private static byte[] Encrypt(byte[] plain, byte[] key, PaddingMode padding)
    {
        using Aes aes = Aes.Create(); aes.SetKey(key);
        byte[] iv = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        return [.. iv, .. aes.EncryptCbc(plain, iv, padding)];
    }
}
