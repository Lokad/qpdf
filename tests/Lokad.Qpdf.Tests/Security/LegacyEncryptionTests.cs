using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class LegacyEncryptionTests
{
    public static IEnumerable<object[]> Vectors()
    {
        foreach (string file in new[] { "security-r2-vectors.json", "security-r3-vectors.json", "security-r3-key-vectors.json" })
        {
            using JsonDocument vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
            foreach (JsonElement vector in vectors.RootElement.EnumerateArray())
            {
                string Text(string name) => vector.GetProperty(name).GetString() ?? throw new InvalidDataException(name);
                yield return [Text("file"), Text("dictionary"), Text("userHex"), Text("ownerHex"), Text("fileKeyHex"), Text("idHex")];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void PublicFixturePasswordsRecoverRecordedKeysWithoutRetainingScratch(string file, string dictionary, string userHex, string ownerHex, string keyHex, string idHex)
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope storage = ParserMemoryScope.Create(governor);
        PdfDictionary encryption = Parse(dictionary, governor, storage); byte[] expectedKey = Convert.FromHexString(keyHex), identifier = Convert.FromHexString(idHex);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        foreach ((string hex, PasswordAuthentication expected) in new[]
        { (userHex, PasswordAuthentication.User), (ownerHex, PasswordAuthentication.Owner), ("77726f6e67", PasswordAuthentication.None) })
        {
            byte[] password = Convert.FromHexString(hex), original = (byte[])password.Clone(), key = new byte[expectedKey.Length]; key.AsSpan().Fill(0xA5);
            Assert.Equal(expected, LegacyEncryption.Authenticate(encryption, identifier, password, key, governor));
            Assert.Equal(expected == PasswordAuthentication.None ? new byte[key.Length] : expectedKey, key);
            Assert.Equal(original, password); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        identifier[0] ^= 1; byte[] invalidKey = new byte[expectedKey.Length]; invalidKey.AsSpan().Fill(0xA5);
        Assert.Equal(PasswordAuthentication.None, LegacyEncryption.Authenticate(encryption, identifier, Convert.FromHexString(userHex), invalidKey, governor));
        Assert.Equal(new byte[invalidKey.Length], invalidKey);
    }

    [Fact]
    public void ObjectKeysUseAllNumberAndGenerationBytesAndClampToSixteenBytes()
    {
        using JsonDocument vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r3-key-vectors.json")));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        foreach (JsonElement vector in vectors.RootElement.EnumerateArray())
        {
            byte[] key = Convert.FromHexString(vector.GetProperty("fileKeyHex").GetString() ?? throw new InvalidDataException());
            byte[] expected = Convert.FromHexString(vector.GetProperty("objectKeyHex").GetString() ?? throw new InvalidDataException());
            byte[] derived = new byte[16]; int length = LegacyEncryption.DeriveObjectKey(key, 66051, 513, derived, false, governor);
            Assert.Equal(Math.Min(key.Length + 5, 16), length); Assert.Equal(expected, derived[..length]);
            Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Theory]
    [InlineData("security-r2-ascii.pdf", false)]
    [InlineData("security-r3-key96-full-digest.pdf", true)]
    [InlineData("security-r3-key96-short-digest.pdf", true)]
    public void EveryAdmissionRefusalAndCancellationClearsTheDestination(string file, bool owner)
    {
        object[] vector = Vectors().Single(value => (string)value[0] == file);
        ResourceGovernor parser = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope storage = ParserMemoryScope.Create(parser);
        PdfDictionary encryption = Parse((string)vector[1], parser, storage); byte[] id = Convert.FromHexString((string)vector[5]);
        byte[] password = Convert.FromHexString((string)vector[owner ? 3 : 2]); int length = ((string)vector[4]).Length / 2;
        RecordingMemoryBudget baseline = new(1048576); Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { Run(new(1048576) { RefuseOnGrowth = growth }, true); }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel(); byte[] canceledKey = new byte[length]; canceledKey.AsSpan().Fill(0xA5);
        Assert.Throws<OperationCanceledException>(() => LegacyEncryption.Authenticate(encryption, id, password, canceledKey, new(QpdfLimits.Default, cancellation.Token)));
        Assert.Equal(new byte[length], canceledKey);
        void Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            byte[] key = new byte[length]; key.AsSpan().Fill(0xA5);
            if (refuses) { Assert.Throws<QpdfMemoryAdmissionException>(() => LegacyEncryption.Authenticate(encryption, id, password, key, governor)); Assert.Equal(new byte[length], key); }
            else { Assert.Equal(owner ? PasswordAuthentication.Owner : PasswordAuthentication.User, LegacyEncryption.Authenticate(encryption, id, password, key, governor)); }
            Assert.Equal(0, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("/O", 2)]
    [InlineData("/U", 2)]
    [InlineData("/O", 3)]
    [InlineData("/U", 3)]
    public void MalformedPasswordEntriesClearPreviouslyPopulatedDestinations(string field, int revision)
    {
        object[] vector = Vectors().First(value => (string)value[0] == $"security-r{revision}-ascii.pdf");
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope storage = ParserMemoryScope.Create(governor);
        string dictionary = (string)vector[1]; int start = dictionary.IndexOf(field + " <", StringComparison.Ordinal) + field.Length + 2;
        int end = dictionary.IndexOf('>', start); dictionary = dictionary[..start] + "00" + dictionary[end..];
        PdfDictionary encryption = Parse(dictionary, governor, storage); byte[] key = new byte[((string)vector[4]).Length / 2]; key.AsSpan().Fill(0xA5);
        Assert.Throws<QpdfParseException>(() => LegacyEncryption.Authenticate(encryption, Convert.FromHexString((string)vector[5]), "reader"u8, key, governor));
        Assert.Equal(new byte[key.Length], key);
    }

    private static PdfDictionary Parse(string text, ResourceGovernor governor, ParserMemoryScope storage)
        => Assert.IsType<PdfDictionary>(new PdfParser(QpdfLimits.Default, governor, new(governor), null)
            .ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, storage));
}
