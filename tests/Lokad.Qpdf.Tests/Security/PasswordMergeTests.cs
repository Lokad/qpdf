using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Reading;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordMergeTests
{
    private static readonly QpdfPath First = QpdfPath.From("first.pdf"), Second = QpdfPath.From("second.pdf"), Output = QpdfPath.From("merged.pdf");

    public static IEnumerable<object[]> Cases()
    {
        foreach (string first in new[] { "security-r4-pdfdoc.pdf", "security-r6-ascii.pdf", "meta-1page.pdf" })
        foreach (string policy in new[] { "preserve", "remove", "new" })
        foreach (bool split in new[] { false, true }) { yield return [first, policy, split]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrimaryProtectionControlsMergedAndSplitOutputs(string first, string policy, bool split)
    {
        using InMemoryQpdfHost host = new(); Add(host, First, first); Add(host, Second, "security-r6-empty.pdf");
        string? firstPassword = first.Contains("r4", StringComparison.Ordinal) ? "café•" : first.Contains("r6", StringComparison.Ordinal) ? "reader" : null;
        PdfPageSource[] sources = [new(First, new(1), Password(firstPassword)), new(Second, new(1)), new(First, new(1), Password(firstPassword))];
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host, sources, Output, new PdfPageMergeOptions
        {
            OutputEncryption = Policy(policy), SplitPages = split ? 2 : null,
            Rotations = [new(PdfRotationMode.Set, 90, [2])],
        }, CancellationToken.None);
        Assert.Equal(3, result.PageCount); Assert.Equal([0, 1, 2], result.Mapping.Select(item => item.SourceIndex));
        Assert.Equal([1, 1, 1], result.SourcePageCounts); Assert.Equal(split ? 2 : 1, result.OutputPaths.Count);
        bool encrypted = policy == "new" || policy == "preserve" && firstPassword is not null;
        QpdfPassword? user = Password(policy == "new" ? "new-reader" : firstPassword);
        QpdfPassword? owner = policy == "new" ? Password("new-owner") : first.Contains("r4", StringComparison.Ordinal)
            ? QpdfPassword.FromBytes(Convert.FromHexString("6F776E6572E991")) : Password("fixture-owner");
        byte[]? previousUserEntry = null;
        for (int output = 0; output < result.OutputPaths.Count; output++)
        {
            Assert.True(host.TryGetOutput(result.OutputPaths[output], out byte[] bytes));
            QpdfPath copied = QpdfPath.From("copied" + output); host.AddInput(copied, bytes, QpdfInputOwnership.Copy);
            foreach (QpdfPassword? credential in new[] { user, owner })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copied, credential, QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(split ? output == 0 ? 2 : 1 : 3, document.PageCount);
                if (encrypted)
                {
                    Assert.Equal(policy == "new" ? 6 : first.Contains("r4", StringComparison.Ordinal) ? 4 : 6, document.Encryption?.Revision);
                    Assert.Equal(policy == "new" ? -4 : -3388, document.Encryption?.Permissions);
                    Assert.Equal(policy != "new", document.Encryption?.EncryptMetadata);
                    Assert.DoesNotContain("TitleText", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
                }
                else { Assert.Null(document.Encryption); }
                PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Info")));
                Assert.Equal("TitleText"u8.ToArray(), Assert.IsType<PdfByteString>(Get(info, "Title")).Value);
                for (int page = 0; page < document.PageCount; page++)
                {
                    PdfStream content = Assert.IsType<PdfStream>(Resolve(document, Get(document.PageReferences[page].Page, "Contents")));
                    string text;
                    if (content.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter))
                    {
                        Assert.Equal(PdfName.FromAscii("FlateDecode"), filter);
                        using MemoryStream encoded = new(content.Data);
                        using System.IO.Compression.ZLibStream decoded = new(encoded, System.IO.Compression.CompressionMode.Decompress);
                        using StreamReader reader = new(decoded, Encoding.ASCII);
                        text = reader.ReadToEnd();
                    }
                    else { text = Encoding.ASCII.GetString(content.Data); }
                    Assert.Contains("Page", text, StringComparison.Ordinal);
                }
                if (output == 0) { Assert.Equal(90, Assert.IsType<PdfInteger>(Get(document.PageReferences[1].Page, "Rotate")).Value); }
                if (!split)
                { Assert.Equal(Get(document.PageReferences[0].Page, "Contents"), Get(document.PageReferences[2].Page, "Contents")); }
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copied, new QpdfCheckOptions { Password = user }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
            if (policy == "new")
            {
                await using QpdfDocument inspected = await QpdfDocument.OpenAsync(host, copied, user, QpdfLimits.Default, CancellationToken.None);
                byte[] entry = Assert.IsType<PdfByteString>(Get(Assert.IsType<PdfDictionary>(Resolve(inspected, Get(inspected.Engine.Trailer, "Encrypt"))), "U")).Value;
                if (previousUserEntry is not null) { Assert.False(previousUserEntry.AsSpan().SequenceEqual(entry)); }
                previousUserEntry = entry;
            }
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CollationCanOmitPrimaryPagesWithoutChangingOutputProtection()
    {
        using InMemoryQpdfHost host = new(); Add(host, First, "security-r4-ascii.pdf"); Add(host, Second, "security-r6-empty.pdf");
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
            [new(First, new(1), Password("reader")), new(Second, new(1))], Output,
            new PdfPageMergeOptions { Collation = new([0, 1]) }, CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.Equal(1, result.Mapping[0].SourceIndex);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), Password("reader"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(4, document.Encryption?.Revision); Assert.True(document.Encryption?.EncryptMetadata);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultOverloadsPreserveEmptyPasswordProtection(bool rotations)
    {
        using InMemoryQpdfHost host = new(); Add(host, First, "security-r6-empty.pdf");
        PdfPageSource[] sources = [new(First, new(1))];
        _ = rotations ? await Qpdf.SelectPagesAsync(host, sources, Output, [new PdfPageRotation(PdfRotationMode.Set, 90, null)], CancellationToken.None)
            : await Qpdf.SelectPagesAsync(host, sources, Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None);
        Assert.Equal(6, document.Encryption?.Revision);
    }

    [Theory]
    [InlineData("different")]
    [InlineData("representation")]
    [InlineData("absent")]
    public async Task RepeatedSourcesRejectConflictingCredentialsRegardlessOfOrder(string conflict)
    {
        using InMemoryQpdfHost host = new(); Add(host, First, "security-r6-ascii.pdf");
        QpdfPassword? other = conflict switch { "different" => Password("private-wrong"), "representation" => QpdfPassword.FromBytes("reader"u8.ToArray()), _ => null };
        PdfPageSource[] sources = [new(First, new(1), Password("reader")), new(First, new(1), other)];
        foreach (bool reverse in new[] { false, true })
        {
            if (reverse) { Array.Reverse(sources); }
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => Qpdf.SelectPagesAsync(host, sources, Output, CancellationToken.None).AsTask());
            Assert.DoesNotContain("private-wrong", error.ToString(), StringComparison.Ordinal);
            Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
        }
        Assert.DoesNotContain("reader", sources[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-wrong", System.Text.Json.JsonSerializer.Serialize(sources), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSecondaryCredentialNeverPublishesEvenForExplicitDecryption(bool split)
    {
        using InMemoryQpdfHost host = new(); Add(host, First, "meta-1page.pdf"); Add(host, Second, "security-r6-ascii.pdf");
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.SelectPagesAsync(host,
            [new(First, new(1)), new(Second, new(1), Password("private-wrong"))], Output,
            new PdfPageMergeOptions { SplitPages = split ? 1 : null, OutputEncryption = QpdfOutputEncryption.Remove }, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, error.Reason);
        Assert.Equal(0, host.OpenLeaseCount); Assert.False(host.TryGetOutput(Output, out _));
        Assert.False(host.TryGetOutput(QpdfPath.From("merged-1.pdf"), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryMergeAdmissionFailureReleasesClosedSourceSecurity(bool fresh)
    {
        (int before, int growths) = await Run(new(4194304), false); Assert.True(growths > 0);
        for (int growth = 1; growth <= growths; growth++)
        { _ = await Run(new(4194304) { RefuseOnGrowth = before + growth }, true); }

        async Task<(int Before, int Growths)> Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            Add(host, First, "security-r4-ascii.pdf"); Add(host, Second, "security-r6-empty.pdf");
            int initial = budget.GrowthCalls; long baseline = budget.ReservedBytes;
            Task<PdfMultiPageSelectionResult> Write() => Qpdf.SelectPagesAsync(host,
                [new(First, new(1), Password("reader")), new(Second, new(1))], Output,
                new PdfPageMergeOptions { OutputEncryption = Policy(fresh ? "new" : "preserve") }, CancellationToken.None).AsTask();
            if (refuses)
            {
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Write);
                Assert.Equal(baseline, budget.ReservedBytes); Assert.False(host.TryGetOutput(Output, out _));
            }
            else { _ = await Write(); }
            Assert.Equal(0, host.OpenLeaseCount); return (initial, budget.GrowthCalls - initial);
        }
    }

    [Fact]
    public async Task SelectiveMergeRetriesEncryptedInputOnItsOriginalLease()
    {
        byte[] bytes = Fixture("security-r4-ascii.pdf"); int footer = bytes.AsSpan().LastIndexOf("startxref"u8);
        byte[] padded = [.. bytes, .. new byte[9 * 1048576], .. bytes.AsSpan(footer).ToArray()];
        Array.Fill(padded, (byte)' ', bytes.Length, 9 * 1048576);
        SelectiveInputTests.Host host = new(padded, 65536) { ReplaceFutureInput = true };
        using (host.Outputs)
        {
            _ = await Qpdf.SelectPagesAsync(host, [new(First, new(1), Password("reader"))], Output, CancellationToken.None);
            Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases); Assert.Equal(1, host.OutputAttempts);
            Assert.True(host.Outputs.TryGetOutput(Output, out byte[] output)); host.Outputs.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host.Outputs, QpdfPath.From("copy"), Password("reader"), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(4, document.Encryption?.Revision);
        }
    }

    private static QpdfOutputEncryption Policy(string policy) => policy switch
    {
        "preserve" => QpdfOutputEncryption.Preserve, "remove" => QpdfOutputEncryption.Remove,
        _ => QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner"), -4, false),
    };
    private static QpdfPassword? Password(string? text) => text is null ? null : QpdfPassword.FromText(text);
    private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
    private static void Add(InMemoryQpdfHost host, QpdfPath path, string file) => host.AddInput(path, Fixture(file), QpdfInputOwnership.Copy);
    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
}
