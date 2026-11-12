using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class Revision6EncryptionTests
{
    [Theory]
    [MemberData(nameof(Vectors))]
    public void CreationMatchesAllNativeEntriesWithRecordedRandomness(string file, string dictionary,
        string userHex, string ownerHex, string keyHex)
    {
        Assert.NotEmpty(file);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        PdfDictionary native = Parse(dictionary, governor, source);
        byte[] expectedKey = Convert.FromHexString(keyHex);
        byte[] user = Bytes(native, "U"), owner = Bytes(native, "O"), perms = Bytes(native, "Perms");
        using Aes aes = Aes.Create(); aes.SetKey(expectedKey);
        byte[] clearPerms = aes.DecryptEcb(perms, PaddingMode.None);
        byte[] randomness = [.. expectedKey, .. user.AsSpan(32).ToArray(), .. owner.AsSpan(32).ToArray(), .. clearPerms.AsSpan(12).ToArray()];
        byte[] originalRandomness = (byte[])randomness.Clone();
        byte[] key = new byte[32], entries = new byte[176];
        int permissions = EncryptionDictionary.ReadInteger(native, "P"u8, governor);
        bool metadata = EncryptionDictionary.ReadMetadata(native, governor);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Revision6Encryption.CreateEntries(Convert.FromHexString(userHex), Convert.FromHexString(ownerHex), permissions,
            metadata, randomness, key, entries, governor);
        Assert.Equal(expectedKey, key);
        Assert.Equal([.. user, .. owner, .. Bytes(native, "UE"), .. Bytes(native, "OE"), .. perms], entries);
        Assert.Equal(originalRandomness, randomness); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void GeneratedKeysAndEntriesAuthenticateBothPasswords(string file, string dictionary,
        string userHex, string ownerHex, string keyHex)
    {
        Assert.NotEmpty(file); Assert.NotEmpty(keyHex);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        PdfDictionary template = Parse(dictionary, governor, source);
        byte[] user = Convert.FromHexString(userHex), owner = Convert.FromHexString(ownerHex);
        byte[] userOriginal = (byte[])user.Clone(), ownerOriginal = (byte[])owner.Clone();
        byte[] key = new byte[32], entries = new byte[176], secondKey = new byte[32], secondEntries = new byte[176];
        int permissions = EncryptionDictionary.ReadInteger(template, "P"u8, governor);
        bool metadata = EncryptionDictionary.ReadMetadata(template, governor);
        Revision6Encryption.Generate(user, owner, permissions, metadata, key, entries, governor);
        Revision6Encryption.Generate(user, owner, permissions, metadata, secondKey, secondEntries, governor);
        Assert.False(key.AsSpan().SequenceEqual(secondKey)); Assert.False(entries.AsSpan().SequenceEqual(secondEntries));
        Dictionary<PdfName, QpdfObject> values = template.Keys.ToDictionary(name => name, name =>
        { Assert.True(template.TryGet(name, out QpdfObject value)); return value; });
        foreach ((string name, int offset, int count) in new[] { ("U", 0, 48), ("O", 48, 48), ("UE", 96, 32), ("OE", 128, 32), ("Perms", 160, 16) })
        { values[PdfName.FromAscii(name)] = new PdfByteString(entries.AsSpan(offset, count).ToArray(), true); }
        PdfDictionary created = new(values);
        byte[] recovered = new byte[32];
        Assert.Equal(PasswordAuthentication.User, Revision6Encryption.Authenticate(created, user, recovered, governor)); Assert.Equal(key, recovered);
        Assert.Equal(PasswordAuthentication.Owner, Revision6Encryption.Authenticate(created, owner, recovered, governor)); Assert.Equal(key, recovered);
        Assert.Equal(PasswordAuthentication.None, Revision6Encryption.Authenticate(created, "wrong"u8, recovered, governor));
        Assert.All(recovered, value => Assert.Equal(0, value)); Assert.Equal(userOriginal, user); Assert.Equal(ownerOriginal, owner);
    }

    [Fact]
    public void EveryCreationRefusalAndCancellationClearsOutputsAndRefundsScratch()
    {
        RecordingMemoryBudget baseline = new(1048576); Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        { Run(new(1048576) { RefuseOnGrowth = growth }, true); }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor canceled = new(QpdfLimits.Default, cancellation.Token);
        byte[] key = new byte[32], entries = new byte[176]; Array.Fill(key, (byte)99); Array.Fill(entries, (byte)99);
        Assert.Throws<OperationCanceledException>(() => Revision6Encryption.Generate("reader"u8, "owner"u8, -4, true, key, entries, canceled));
        Assert.All(key, value => Assert.Equal(0, value)); Assert.All(entries, value => Assert.Equal(0, value));

        static void Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            byte[] key = new byte[32], entries = new byte[176]; Array.Fill(key, (byte)99); Array.Fill(entries, (byte)99);
            if (refuses)
            {
                Assert.Throws<QpdfMemoryAdmissionException>(() => Revision6Encryption.Generate("reader"u8, "owner"u8, -4, false, key, entries, governor));
                Assert.All(key, value => Assert.Equal(0, value)); Assert.All(entries, value => Assert.Equal(0, value));
            }
            else { Revision6Encryption.Generate("reader"u8, "owner"u8, -4, false, key, entries, governor); }
            Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static byte[] Bytes(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return Assert.IsType<PdfByteString>(value).Value; }

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
