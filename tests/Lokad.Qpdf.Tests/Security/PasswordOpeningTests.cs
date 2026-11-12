using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordOpeningTests
{
    [Theory]
    [MemberData(nameof(Revision4EncryptionTests.Vectors), MemberType = typeof(Revision4EncryptionTests))]
    [MemberData(nameof(Revision4EncryptionTests.Rc4Vectors), MemberType = typeof(Revision4EncryptionTests))]
    public async Task R4OpeningSupportsExactBytesAndBoundedTextEncodingRecovery(string file, string dictionary,
        string userHex, string ownerHex, string keyHex, string idHex)
    {
        Assert.NotEmpty(dictionary); Assert.NotEmpty(keyHex); Assert.NotEmpty(idHex);
        using InMemoryQpdfHost host = new(); QpdfPath path = Add(host, file);
        foreach ((string hex, bool owner) in new[] { (userHex, false), (ownerHex, true) })
        {
            byte[] bytes = Convert.FromHexString(hex);
            string text = file.EndsWith("-pdfdoc.pdf", StringComparison.Ordinal) ? owner ? "owneré‚" : "café•" : Encoding.UTF8.GetString(bytes);
            foreach (QpdfPassword password in new[] { QpdfPassword.FromBytes(bytes), QpdfPassword.FromText(text) })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, password, QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(1, document.PageCount);
                QpdfEncryptionInfo info = Assert.IsType<QpdfEncryptionInfo>(document.Encryption);
                Assert.Equal(4, info.Revision); Assert.Equal(128, info.KeyLengthBits);
                Assert.Equal(-3388, info.Permissions); Assert.Equal(owner, info.OwnerPasswordMatched);
            }
            Assert.Equal(Convert.FromHexString(hex), bytes);
        }
    }

    [Fact]
    public async Task LegacyRawBytesNeverParticipateInTextEncodingRecovery()
    {
        using InMemoryQpdfHost host = new(); QpdfPath path = Add(host, "security-r4-pdfdoc.pdf");
        QpdfPassword raw = QpdfPassword.FromBytes(Encoding.UTF8.GetBytes("café•"));
        QpdfSecurityException failure = await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, path, raw, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, failure.Reason);
    }

    [Theory]
    [MemberData(nameof(Revision6EncryptionTests.Vectors), MemberType = typeof(Revision6EncryptionTests))]
    public async Task PublicOpeningAcceptsTextAndRawUserAndOwnerPasswords(string file, string dictionary,
        string userHex, string ownerHex, string keyHex)
    {
        Assert.NotEmpty(dictionary);
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        QpdfPath path = QpdfPath.From("input.pdf");
        using InMemoryQpdfHost host = new(); host.AddInput(path, input, QpdfInputOwnership.Copy);
        foreach ((string hex, bool owner) in new[] { (userHex, false), (ownerHex, true) })
        {
            byte[] bytes = Convert.FromHexString(hex), original = (byte[])Convert.FromHexString(hex).Clone();
            foreach (QpdfPassword password in new[] { QpdfPassword.FromText(Encoding.UTF8.GetString(bytes)), QpdfPassword.FromBytes(bytes) })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, password, QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(1, document.PageCount);
                QpdfEncryptionInfo info = Assert.IsType<QpdfEncryptionInfo>(document.Encryption);
                Assert.Equal(6, info.Revision); Assert.Equal(256, info.KeyLengthBits);
                Assert.Equal(-3388, info.Permissions); Assert.Equal(owner, info.OwnerPasswordMatched);
                Assert.DoesNotContain(keyHex, info.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.Equal("QpdfPassword [redacted]", password.ToString());
            }
            Assert.Equal(original, bytes);
        }
        Assert.Equal(input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
    }

    [Fact]
    public async Task AbsentPasswordTriesEmptyAndDiffersFromAnInvalidExplicitPassword()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath empty = Add(host, "security-r6-empty.pdf"), protectedPath = Add(host, "security-r6-ascii.pdf");
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, empty, CancellationToken.None))
        { Assert.Equal(1, document.PageCount); Assert.False(document.Encryption?.EncryptMetadata); }
        await using (QpdfDocument legacy = await QpdfDocument.OpenAsync(host, Add(host, "security-r4-empty.pdf"), CancellationToken.None))
        { Assert.Equal(1, legacy.PageCount); Assert.False(legacy.Encryption?.EncryptMetadata); }
        QpdfSecurityException required = await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, protectedPath, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.PasswordRequired, required.Reason);
        foreach (QpdfPassword password in new[] { QpdfPassword.FromText(""), QpdfPassword.FromBytes("wrong-secret"u8.ToArray()) })
        {
            QpdfSecurityException invalid = await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, protectedPath, password, QpdfLimits.Default, CancellationToken.None).AsTask());
            Assert.Equal(QpdfSecurityFailure.InvalidPassword, invalid.Reason);
            Assert.DoesNotContain("wrong-secret", invalid.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TextIsNotUnicodeNormalizedAndCredentialsDoNotChangeUnencryptedInputs()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath encrypted = Add(host, "security-r6-utf8.pdf"), plain = Add(host, "meta-1page.pdf");
        QpdfPassword decomposed = QpdfPassword.FromText("café-π-密碼".Normalize(NormalizationForm.FormD));
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, encrypted, decomposed, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, error.Reason);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, plain, decomposed, QpdfLimits.Default, CancellationToken.None);
        Assert.Null(document.Encryption); Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public async Task DirectEncryptionDictionariesInXrefStreamsAuthenticate()
    {
        object[] vector = Revision6EncryptionTests.Vectors().First();
        string source = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", (string)vector[0])));
        // Only the final xref stream header grows; all object offsets and its
        // startxref remain unchanged. The original encryption object is unused.
        const string reference = "/Encrypt 8 0 R";
        Assert.Contains(reference, source, StringComparison.Ordinal);
        byte[] direct = Encoding.Latin1.GetBytes(source.Replace(reference, "/Encrypt " + (string)vector[1], StringComparison.Ordinal));
        using InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("direct.pdf");
        host.AddInput(path, direct, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, document.PageCount); Assert.Equal(6, document.Encryption?.Revision);
    }

    [Fact]
    public async Task RawPasswordsPreserveIncompleteUtf8SequencesAtTheByteLimit()
    {
        object[] vector = Revision6EncryptionTests.Vectors().Last();
        byte[] raw = Convert.FromHexString((string)vector[2])[..127];
        Assert.NotEqual(raw, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(raw)));
        using InMemoryQpdfHost host = new(); QpdfPath path = Add(host, "security-r6-truncated.pdf");
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, QpdfPassword.FromBytes(raw), QpdfLimits.Default, CancellationToken.None))
        { Assert.Equal(1, document.PageCount); }
        QpdfPassword replaced = QpdfPassword.FromText(Encoding.UTF8.GetString(raw));
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, path, replaced, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, error.Reason);
    }

    [Theory]
    [InlineData("typed", "security-r6-empty.pdf")]
    [InlineData("argv", "security-r6-empty.pdf")]
    [InlineData("job", "security-r6-empty.pdf")]
    [InlineData("typed", "security-r4-empty.pdf")]
    [InlineData("argv", "security-r4-empty.pdf")]
    [InlineData("job", "security-r4-empty.pdf")]
    public async Task TransformationRoutesNeverPublishPlaintextByAccident(string route, string file)
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = Add(host, file);
        QpdfPath output = QpdfPath.From("output.pdf");
        if (route == "typed")
        {
            _ = await Qpdf.SelectPageAsync(host, input, output, new PdfPageSelectionOptions(1), CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([input.Value, output.Value]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"" + file + "\",\"outputFile\":\"output.pdf\"}", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        }
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        QpdfPath copied = QpdfPath.From("copied.pdf"); host.AddInput(copied, bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copied, CancellationToken.None);
        Assert.Equal(1, document.PageCount); Assert.NotNull(document.Encryption);
    }

    [Theory]
    [InlineData("security-r6-ascii.pdf")]
    [InlineData("security-r4-ascii.pdf")]
    public async Task FailedAuthenticationRefundsBorrowedHostMemoryAndCancellationIsTerminal(string file)
    {
        RecordingMemoryBudget budget = new(1048576);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
        QpdfPath path = Add(host, file);
        long baseline = budget.ReservedBytes;
        QpdfPassword password = QpdfPassword.FromText("wrong-secret");
        await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, path, password, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(baseline, budget.ReservedBytes);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QpdfDocument.OpenAsync(host, path, password, QpdfLimits.Default, cancellation.Token).AsTask());
        Assert.Equal(baseline, budget.ReservedBytes);
    }

    [Fact]
    public async Task DecryptedValuesSurviveDocumentDisposal()
    {
        using InMemoryQpdfHost host = new(); QpdfPath path = Add(host, "security-r6-ascii.pdf");
        PdfByteString? title = null;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None))
        {
            for (int number = 1; number < 8; number++)
            {
                if (document.GetObject(number) is PdfDictionary dictionary && dictionary.TryGet(PdfName.FromAscii("Title"), out QpdfObject value))
                { title = Assert.IsType<PdfByteString>(value); }
            }
        }
        Assert.NotNull(title); Assert.Equal("TitleText", Encoding.ASCII.GetString(title.Value));
    }

    private static QpdfPath Add(InMemoryQpdfHost host, string file)
    {
        QpdfPath path = QpdfPath.From(file);
        host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
        return path;
    }
}
