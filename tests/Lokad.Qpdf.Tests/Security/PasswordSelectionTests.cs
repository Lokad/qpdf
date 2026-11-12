using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Reading;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordSelectionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    [Fact]
    public async Task Revision6WithoutAnIdentifierGetsAnOwnedIdentifierAndRetainsProtection()
    {
        string original = Encoding.Latin1.GetString(Fixture("security-r6-ascii.pdf"));
        string changed = System.Text.RegularExpressions.Regex.Replace(original, @"/ID\s*\[[^\]]*\]", "");
        Assert.NotEqual(original, changed);
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Encoding.Latin1.GetBytes(changed), QpdfInputOwnership.Copy);
        _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"), CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        QpdfPath copy = QpdfPath.From("copy"); host.AddInput(copy, output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText("fixture-owner"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, document.PageCount); Assert.Equal(6, document.Encryption?.Revision);
        PdfArray identifiers = Assert.IsType<PdfArray>(Get(document.Engine.Trailer, "ID"));
        Assert.Equal(16, Assert.IsType<PdfByteString>(identifiers[0]).Value.Length);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (object[] vector in Revision4EncryptionTests.Vectors().Concat(Revision6EncryptionTests.Vectors()))
        foreach (bool owner in new[] { false, true })
        { yield return [vector[0], vector[2], vector[3], owner]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TypedSelectionPreservesProtectionAndDuplicatePages(string file, string userHex, string ownerHex, bool owner)
    {
        byte[] input = Fixture(file), original = (byte[])input.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, input, QpdfInputOwnership.Copy);
        QpdfPassword password = QpdfPassword.FromBytes(Convert.FromHexString(owner ? ownerHex : userHex));
        byte[] content; QpdfEncryptionInfo originalSecurity;
        await using (QpdfDocument source = await QpdfDocument.OpenAsync(host, Input, password, QpdfLimits.Default, CancellationToken.None))
        { content = Content(source, 0); originalSecurity = Assert.IsType<QpdfEncryptionInfo>(source.Encryption); }
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FromPages([1, 1, 1]),
            [new PdfPageRotation(PdfRotationMode.Set, 90, [2])], password, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(3, result.PageCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        Assert.DoesNotContain("TitleText", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(original, input);
        QpdfPath copied = QpdfPath.From("copied.pdf"); host.AddInput(copied, bytes, QpdfInputOwnership.Copy);
        foreach (string hex in new[] { userHex, ownerHex })
        {
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copied,
                QpdfPassword.FromBytes(Convert.FromHexString(hex)), QpdfLimits.Default, CancellationToken.None))
            {
                Assert.Equal(3, document.PageCount);
                QpdfEncryptionInfo security = Assert.IsType<QpdfEncryptionInfo>(document.Encryption);
                Assert.Equal(originalSecurity.Revision, security.Revision); Assert.Equal(originalSecurity.Permissions, security.Permissions);
                Assert.Equal(originalSecurity.EncryptMetadata, security.EncryptMetadata);
                for (int page = 0; page < 3; page++) { Assert.Equal(content, Content(document, page)); }
                Assert.True(document.PageReferences[1].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject angle));
                Assert.Equal(90, Assert.IsType<PdfInteger>(angle).Value);
                PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Info")));
                Assert.Equal("TitleText"u8.ToArray(), Assert.IsType<PdfByteString>(Get(info, "Title")).Value);
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copied, new QpdfCheckOptions { Password = QpdfPassword.FromBytes(Convert.FromHexString(hex)) }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        }
        await Assert.ThrowsAsync<QpdfSecurityException>(() => QpdfDocument.OpenAsync(host, copied,
            QpdfPassword.FromText("wrong password"), QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("security-r4-empty.pdf")]
    [InlineData("security-r6-empty.pdf")]
    public async Task ExplicitAbsentCredentialPreservesEmptyPasswordProtection(string file)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture(file), QpdfInputOwnership.Copy);
        _ = await Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FirstAndLast(1),
            (QpdfPassword?)null, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None);
        Assert.Equal(1, document.PageCount); Assert.False(document.Encryption?.EncryptMetadata);
    }

    [Theory]
    [InlineData("security-r4-ascii.pdf")]
    [InlineData("security-r6-ascii.pdf")]
    public async Task EverySelectionAdmissionFailureReleasesSecurityAndStaging(string file)
    {
        (int before, int growths) = await Run(new(4194304), false);
        Assert.True(growths > 0);
        for (int growth = 1; growth <= growths; growth++)
        { _ = await Run(new(4194304) { RefuseOnGrowth = before + growth }, true); }

        async Task<(int Before, int Growths)> Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            host.AddInput(Input, Fixture(file), QpdfInputOwnership.Copy);
            int before = budget.GrowthCalls; long reserved = budget.ReservedBytes;
            Task<PdfPageSelectionResult> Select() => Qpdf.SelectPageAsync(host, Input, Output,
                PdfPageSelectionOptions.FromPages([1, 1]), QpdfPassword.FromText("reader"), CancellationToken.None).AsTask();
            if (refuses)
            {
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Select);
                Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(reserved, budget.ReservedBytes);
            }
            else { _ = await Select(); }
            Assert.Equal(0, host.OpenLeaseCount);
            return (before, budget.GrowthCalls - before);
        }
    }

    [Theory]
    [InlineData("security-r4-ascii.pdf", false)]
    [InlineData("security-r4-ascii.pdf", true)]
    [InlineData("security-r6-ascii.pdf", false)]
    [InlineData("security-r6-ascii.pdf", true)]
    public async Task AuthenticationFailuresAndCancellationLeavePriorOutputUntouched(string file, bool wrong)
    {
        RecordingMemoryBudget budget = new(1048576);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget); host.AddInput(Input, Fixture(file), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior output"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        long baseline = budget.ReservedBytes;
        QpdfPassword? password = wrong ? QpdfPassword.FromText("secret-wrong") : null;
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.SelectPageAsync(host, Input, Output,
            new PdfPageSelectionOptions(1), password, CancellationToken.None).AsTask());
        Assert.Equal(wrong ? QpdfSecurityFailure.InvalidPassword : QpdfSecurityFailure.PasswordRequired, error.Reason);
        Assert.DoesNotContain("secret-wrong", error.ToString(), StringComparison.Ordinal);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Qpdf.SelectPageAsync(host, Input, Output,
            new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"), cancellation.Token).AsTask());
        Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(baseline, budget.ReservedBytes);
        Assert.True(host.TryGetOutput(Output, out byte[] previous)); Assert.Equal("prior output"u8.ToArray(), previous);
    }

    [Theory]
    [InlineData("security-r4-ascii.pdf")]
    [InlineData("security-r6-ascii.pdf")]
    public async Task ExternalHostFallbackKeepsOneStableLease(string file)
    {
        byte[] bytes = Fixture(file);
        // Repeat the unchanged footer after unused whitespace, retaining all
        // object offsets while exceeding the selective reader's first window.
        int footer = bytes.AsSpan().LastIndexOf("startxref"u8);
        byte[] padded = [.. bytes, .. new byte[9 * 1048576], .. bytes.AsSpan(footer).ToArray()];
        Array.Fill(padded, (byte)' ', bytes.Length, 9 * 1048576);
        SelectiveInputTests.Host host = new(padded, 65536) { ReplaceFutureInput = true };
        using (host.Outputs)
        {
            _ = await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"), CancellationToken.None);
            Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases); Assert.Equal(1, host.OutputAttempts);
            Assert.True(host.Outputs.TryGetOutput(Output, out byte[] output));
            host.Outputs.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host.Outputs, QpdfPath.From("copy"),
                QpdfPassword.FromText("fixture-owner"), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount); Assert.NotNull(document.Encryption);
        }
    }

    private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static byte[] Content(QpdfDocument document, int page)
        => Assert.IsType<PdfStream>(Resolve(document, Get(document.PageReferences[page].Page, "Contents"))).Data;
}
