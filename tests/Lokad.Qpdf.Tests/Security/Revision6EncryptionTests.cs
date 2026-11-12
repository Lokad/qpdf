using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class Revision6EncryptionTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using JsonDocument vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-vectors.json")));
        foreach (JsonElement vector in vectors.RootElement.EnumerateArray())
        {
            string Text(string key) => vector.GetProperty(key).GetString() ?? throw new InvalidDataException(key);
            yield return [Text("file"), Text("dictionary"), Text("userHex"), Text("ownerHex"), Text("fileKeyHex")];
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void BothPasswordsRecoverTheNativeKeyAndWrongPasswordsFail(string file, string dictionary,
        string userHex, string ownerHex, string keyHex)
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        PdfDictionary encryption = Parse(dictionary, governor, source);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        byte[] key = new byte[32];
        foreach ((string hex, PasswordAuthentication expected) in new[]
        {
            (userHex, PasswordAuthentication.User), (ownerHex, PasswordAuthentication.Owner),
            (Convert.ToHexString("wrong-password"u8), PasswordAuthentication.None)
        })
        {
            byte[] password = Convert.FromHexString(hex);
            byte[] original = (byte[])password.Clone();
            key.AsSpan().Fill(0xA5);
            Assert.Equal(expected, Revision6Encryption.Authenticate(encryption, password, key, governor));
            Assert.Equal(expected == PasswordAuthentication.None ? new byte[32] : Convert.FromHexString(keyHex), key);
            Assert.Equal(original, password);
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Theory]
    [InlineData("P")]
    [InlineData("EncryptMetadata")]
    [InlineData("Perms")]
    [InlineData("UE")]
    [InlineData("OE")]
    [InlineData("U")]
    [InlineData("O")]
    public void DamagedParametersNeverReturnAKey(string field)
    {
        object[] vector = Vectors().First();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        PdfDictionary original = Parse((string)vector[1], governor, source);
        Dictionary<PdfName, QpdfObject> entries = original.Keys.ToDictionary(key => key, key =>
        { Assert.True(original.TryGet(key, out QpdfObject value)); return value; });
        PdfName name = PdfName.FromAscii(field);
        entries[name] = field switch
        {
            "P" => new PdfInteger("-4", -4),
            "EncryptMetadata" => PdfBoolean.False,
            "Perms" => new PdfByteString(new byte[16], true),
            _ => new PdfByteString(new byte[1], true)
        };
        PdfDictionary damaged = new(entries);
        byte[] key = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfParseException>(() => Revision6Encryption.Authenticate(damaged, Convert.FromHexString((string)vector[2]), key, governor));
        Assert.Equal(new byte[32], key);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RefusalAndCancellationClearDestinationAndRefundScratch()
    {
        object[] vector = Vectors().First();
        ResourceGovernor parserGovernor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(parserGovernor);
        PdfDictionary encryption = Parse((string)vector[1], parserGovernor, source);
        byte[] password = Convert.FromHexString((string)vector[2]);
        byte[] key = new byte[32];
        ResourceGovernor limited = new(QpdfLimits.Default with { MaxMemoryBytes = 8192 }, CancellationToken.None);
        key.AsSpan().Fill(0xA5);
        Assert.Throws<QpdfLimitException>(() => Revision6Encryption.Authenticate(encryption, password, key, limited));
        Assert.Equal(new byte[32], key);
        Assert.Equal(0L, limited.Snapshot().LiveMemoryBytes);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor canceled = new(QpdfLimits.Default, cancellation.Token);
        key.AsSpan().Fill(0xA5);
        Assert.Throws<OperationCanceledException>(() => Revision6Encryption.Authenticate(encryption, password, key, canceled));
        Assert.Equal(new byte[32], key);
    }

    private static PdfDictionary Parse(string text, ResourceGovernor governor, ParserMemoryScope source)
    {
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        return Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, source));
    }

    [Fact]
    public void EveryHostAdmissionFailureIsTerminalAndRefundsKeyDerivation()
    {
        object[] vector = Vectors().First();
        ResourceGovernor parserGovernor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(parserGovernor);
        PdfDictionary encryption = Parse((string)vector[1], parserGovernor, source);
        byte[] password = Convert.FromHexString((string)vector[2]);
        RecordingMemoryBudget baseline = new(1048576);
        Run(baseline, false);
        Assert.True(baseline.GrowthCalls > 1);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        {
            RecordingMemoryBudget refusing = new(1048576) { RefuseOnGrowth = growth };
            Run(refusing, true);
            Assert.Equal(growth, refusing.GrowthCalls);
        }

        void Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            byte[] key = new byte[32]; key.AsSpan().Fill(0xA5);
            if (refuses)
            {
                Assert.Throws<QpdfMemoryAdmissionException>(() => Revision6Encryption.Authenticate(encryption, password, key, governor));
                Assert.Equal(new byte[32], key);
            }
            else { Assert.Equal(PasswordAuthentication.User, Revision6Encryption.Authenticate(encryption, password, key, governor)); }
            Assert.Equal(0L, budget.ReservedBytes);
        }
    }
}
