using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class Revision4EncryptionTests
{
    public static IEnumerable<object[]> Vectors()
        => ReadVectors("security-r4-vectors.json");

    public static IEnumerable<object[]> Rc4Vectors() => ReadVectors("security-r4-rc4-vectors.json");

    private static IEnumerable<object[]> ReadVectors(string file)
    {
        using JsonDocument vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        foreach (JsonElement vector in vectors.RootElement.EnumerateArray())
        {
            string Text(string key) => vector.GetProperty(key).GetString() ?? throw new InvalidDataException(key);
            yield return [Text("file"), Text("dictionary"), Text("userHex"), Text("ownerHex"), Text("fileKeyHex"), Text("idHex")];
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    [MemberData(nameof(Rc4Vectors))]
    public void NativeUserAndOwnerPasswordsRecoverTheFileKey(string file, string dictionary,
        string userHex, string ownerHex, string keyHex, string idHex)
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        PdfDictionary encryption = Parse(dictionary, governor, source);
        byte[] key = new byte[16], id = Convert.FromHexString(idHex);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        foreach ((string passwordHex, PasswordAuthentication match) in new[]
        { (userHex, PasswordAuthentication.User), (ownerHex, PasswordAuthentication.Owner), ("77726F6E67", PasswordAuthentication.None) })
        {
            byte[] password = Convert.FromHexString(passwordHex), original = (byte[])Convert.FromHexString(passwordHex).Clone();
            key.AsSpan().Fill(0xA5);
            Assert.Equal(match, LegacyEncryption.Authenticate(encryption, id, password, key, governor));
            Assert.Equal(match == PasswordAuthentication.None ? new byte[16] : Convert.FromHexString(keyHex), key);
            Assert.Equal(original, password); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        id[0] ^= 1;
        Assert.Equal(PasswordAuthentication.None, LegacyEncryption.Authenticate(encryption, id, Convert.FromHexString(userHex), key, governor));
        Assert.Equal(new byte[16], key);
    }

    [Fact]
    public void EveryAdmissionFailureAndCancellationClearTheKeyAndRefundScratch()
    {
        object[] vector = Vectors().First();
        ResourceGovernor parserGovernor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(parserGovernor);
        PdfDictionary dictionary = Parse((string)vector[1], parserGovernor, source);
        byte[] id = Convert.FromHexString((string)vector[5]), password = Convert.FromHexString((string)vector[2]);
        RecordingMemoryBudget baseline = new(1048576); Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        {
            RecordingMemoryBudget refused = new(1048576) { RefuseOnGrowth = growth };
            Run(refused, true); Assert.Equal(growth, refused.GrowthCalls);
        }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        byte[] canceledKey = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        Assert.Throws<OperationCanceledException>(() => LegacyEncryption.Authenticate(dictionary, id, password, canceledKey, new(QpdfLimits.Default, cancellation.Token)));
        Assert.Equal(new byte[16], canceledKey);

        void Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            byte[] key = Enumerable.Repeat((byte)0xA5, 16).ToArray();
            if (refuses)
            {
                Assert.Throws<QpdfMemoryAdmissionException>(() => LegacyEncryption.Authenticate(dictionary, id, password, key, governor));
                Assert.Equal(new byte[16], key);
            }
            else { Assert.Equal(PasswordAuthentication.User, LegacyEncryption.Authenticate(dictionary, id, password, key, governor)); }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, budget.ReservedBytes);
        }
    }

    private static PdfDictionary Parse(string text, ResourceGovernor governor, ParserMemoryScope source)
        => Assert.IsType<PdfDictionary>(new PdfParser(QpdfLimits.Default, governor, new DiagnosticBag(governor), null)
            .ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, source));
}
