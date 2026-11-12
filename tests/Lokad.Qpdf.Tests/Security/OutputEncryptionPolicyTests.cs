using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class OutputEncryptionPolicyTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Cases()
    {
        foreach (string file in new[] { "meta-1page.pdf", "security-r4-ascii.pdf", "security-r6-ascii.pdf", "security-r6-empty.pdf" })
        foreach (string policy in new[] { "preserve", "remove", "new-clear", "new-encrypted" })
        { yield return [file, policy]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExplicitPoliciesPreserveRemoveOrReplaceProtection(string file, string policy)
    {
        using InMemoryQpdfHost host = new(); Add(host, file);
        bool encryptedInput = file.StartsWith("security", StringComparison.Ordinal);
        QpdfPassword? inputPassword = encryptedInput && !file.Contains("empty", StringComparison.Ordinal) ? QpdfPassword.FromText("reader") : null;
        QpdfOutputEncryption outputEncryption = policy switch
        {
            "preserve" => QpdfOutputEncryption.Preserve,
            "remove" => QpdfOutputEncryption.Remove,
            _ => QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new-café-π"),
                QpdfPassword.FromBytes("new owner"u8.ToArray()), -3388, policy == "new-encrypted"),
        };
        PdfPageSelectionResult selected = await Qpdf.SelectPageAsync(host, Input, Output,
            PdfPageSelectionOptions.FromPages([1, 1]), inputPassword, outputEncryption, CancellationToken.None);
        Assert.Equal(2, selected.PageCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        QpdfPath copy = QpdfPath.From("copy"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        bool newEncryption = policy.StartsWith("new", StringComparison.Ordinal);
        bool protectedOutput = newEncryption || encryptedInput && policy == "preserve";
        QpdfPassword? user = newEncryption ? QpdfPassword.FromText("new-café-π") : inputPassword;
        QpdfPassword? owner = newEncryption ? QpdfPassword.FromText("new owner") : encryptedInput ? QpdfPassword.FromText("fixture-owner") : null;
        foreach (QpdfPassword? credential in new[] { user, owner })
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy,
                credential, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(2, document.PageCount);
            if (protectedOutput)
            {
                QpdfEncryptionInfo info = Assert.IsType<QpdfEncryptionInfo>(document.Encryption);
                Assert.Equal(newEncryption ? 6 : file.Contains("r4", StringComparison.Ordinal) ? 4 : 6, info.Revision);
                Assert.Equal(-3388, info.Permissions);
                Assert.Equal(newEncryption ? policy == "new-encrypted" : !file.Contains("empty", StringComparison.Ordinal), info.EncryptMetadata);
                Assert.DoesNotContain("TitleText", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
            }
            else { Assert.Null(document.Encryption); }
            PdfDictionary infoDictionary = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Info")));
            Assert.Equal("TitleText"u8.ToArray(), Assert.IsType<PdfByteString>(Get(infoDictionary, "Title")).Value);
        }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = user }, QpdfLimits.Default, CancellationToken.None);
        Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        if (newEncryption)
        {
            await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, copy,
                QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None).AsTask());
            Assert.True(Encoding.ASCII.GetString(bytes.AsSpan(0, 8)) is "%PDF-1.7" or "%PDF-2.0");
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReusingARequestCreatesFreshProtectionAndPreservesBorrowedCredentialBytes()
    {
        using InMemoryQpdfHost host = new(); Add(host, "meta-1page.pdf");
        string userText = new('é', 80), ownerText = new('O', 150);
        byte[] userBytes = Encoding.UTF8.GetBytes(userText), expectedUser = (byte[])userBytes.Clone();
        QpdfOutputEncryption policy = QpdfOutputEncryption.CreateAes256(QpdfPassword.FromBytes(userBytes), QpdfPassword.FromText(ownerText), -4, true);
        Assert.DoesNotContain(userText, policy.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ownerText, System.Text.Json.JsonSerializer.Serialize(policy), StringComparison.Ordinal);
        byte[]? previousEntry = null;
        for (int index = 0; index < 2; index++)
        {
            _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1),
                [new PdfPageRotation(PdfRotationMode.Set, 90, null)], null, policy, QpdfLimits.Default, CancellationToken.None);
            Assert.True(host.TryGetOutput(Output, out byte[] bytes));
            QpdfPath copy = QpdfPath.From("copy" + index); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
            foreach (QpdfPassword password in new[] { QpdfPassword.FromText(userText), QpdfPassword.FromText(ownerText) })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, password, QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(1, document.PageCount); Assert.Equal(-4, document.Encryption?.Permissions);
                Assert.Equal(90, Assert.IsType<PdfInteger>(Get(document.PageReferences[0].Page, "Rotate")).Value);
            }
            await using QpdfDocument inspected = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromBytes(userBytes), QpdfLimits.Default, CancellationToken.None);
            PdfDictionary encryption = Assert.IsType<PdfDictionary>(Resolve(inspected, Get(inspected.Engine.Trailer, "Encrypt")));
            byte[] entry = Assert.IsType<PdfByteString>(Get(encryption, "U")).Value;
            if (previousEntry is not null) { Assert.False(previousEntry.AsSpan().SequenceEqual(entry)); }
            previousEntry = entry;
        }
        Assert.Equal(expectedUser, userBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputPoliciesStillRequireValidInputAuthentication(bool remove)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        QpdfOutputEncryption policy = remove ? QpdfOutputEncryption.Remove
            : QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new user"), QpdfPassword.FromText("new owner"), -4, true);
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.SelectPageAsync(host, Input, Output,
            new PdfPageSelectionOptions(1), QpdfPassword.FromText("secret-wrong"), policy, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, error.Reason);
        Assert.DoesNotContain("secret-wrong", error.ToString(), StringComparison.Ordinal);
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-516)]
    [InlineData(int.MinValue)]
    public void InvalidPermissionWordsAreRejected(int permissions)
        => Assert.Throws<ArgumentOutOfRangeException>(() => QpdfOutputEncryption.CreateAes256(
            QpdfPassword.FromText(""), QpdfPassword.FromText("owner"), permissions, true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOwnerWithNonemptyUserIsRefusedAndBothEmptyRemainValid(bool emptyUser)
    {
        using InMemoryQpdfHost host = new(); Add(host, "meta-1page.pdf");
        QpdfOutputEncryption policy = QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText(emptyUser ? "" : "user"), QpdfPassword.FromText(""), -3392, false);
        Task<PdfPageSelectionResult> Write() => Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), null, policy, CancellationToken.None).AsTask();
        if (!emptyUser)
        {
            await Assert.ThrowsAsync<QpdfUnsupportedFeatureException>(Write);
            Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount); return;
        }
        _ = await Write(); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        QpdfPath copy = QpdfPath.From("copy"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, CancellationToken.None);
        Assert.Equal(-3392, document.Encryption?.Permissions); Assert.Equal(6, document.Encryption?.Revision);
    }

    [Fact]
    public async Task EveryNewProtectionAdmissionFailureRefundsBeforePublication()
    {
        (int before, int growths) = await Run(new(4194304), false);
        Assert.True(growths > 0);
        for (int growth = 1; growth <= growths; growth++)
        { _ = await Run(new(4194304) { RefuseOnGrowth = before + growth }, true); }

        async Task<(int Before, int Growths)> Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget); Add(host, "meta-1page.pdf");
            int before = budget.GrowthCalls; long baseline = budget.ReservedBytes;
            QpdfOutputEncryption policy = QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("reader"), QpdfPassword.FromText("owner"), -3388, false);
            Task<PdfPageSelectionResult> Write() => Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), null, policy, CancellationToken.None).AsTask();
            if (refuses)
            {
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Write);
                Assert.Equal(baseline, budget.ReservedBytes); Assert.False(host.TryGetOutput(Output, out _));
            }
            else { _ = await Write(); }
            Assert.Equal(0, host.OpenLeaseCount); return (before, budget.GrowthCalls - before);
        }
    }

    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
}
