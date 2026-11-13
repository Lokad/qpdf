using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PrimaryAttachmentPasswordTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Source = QpdfPath.From("source.pdf"), Template = QpdfPath.From("template.pdf"), Output = QpdfPath.From("output.pdf"), PayloadOutput = QpdfPath.From("payload.bin");
    private static readonly byte[] Payload = "Private attachment payload.\n"u8.ToArray();
    private static readonly QpdfAttachmentSpec Addition = new("extra.bin", "extra.bin", "extra.bin", "Added file", "application/octet-stream", null, null, false);

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public async Task TypedListingAndExtractionAuthenticateEitherPassword(int revision, bool owner)
    {
        using InMemoryQpdfHost host = new(); Prepare(host, revision);
        QpdfPassword? password = Password(revision, owner);
        IReadOnlyList<QpdfAttachmentInfo> attachments = await Qpdf.ListAttachmentsAsync(host, Input, password, QpdfLimits.Default, CancellationToken.None);
        QpdfAttachmentInfo attachment = Assert.Single(attachments); Assert.Equal("private.bin", attachment.Key); Assert.Equal(0, host.OpenLeaseCount);
        QpdfAttachmentExtraction result = await Qpdf.ExtractAttachmentAsync(host, Input, "private.bin", PayloadOutput, password, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(Payload.Length, result.ByteCount); Assert.True(host.TryGetOutput(PayloadOutput, out byte[] bytes)); Assert.Equal(Payload, bytes);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ExistingInspectionOverloadsTryTheEmptyPassword()
    {
        using InMemoryQpdfHost host = new(); Prepare(host, 6);
        Assert.Equal("private.bin", Assert.Single(await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None)).Key);
        _ = await Qpdf.ExtractAttachmentAsync(host, Input, "private.bin", PayloadOutput, CancellationToken.None);
        Assert.True(host.TryGetOutput(PayloadOutput, out byte[] bytes)); Assert.Equal(Payload, bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (int revision in new[] { 4, 6 })
        foreach (string operation in new[] { "add", "remove", "copy" })
        foreach (string policy in new[] { "preserve", "remove", "new", "template" }) { yield return [revision, operation, policy]; }
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TypedEditsUsePrimaryCredentialsAndExplicitOutputPolicies(int revision, string operation, string policy)
    {
        using InMemoryQpdfHost host = new(); Prepare(host, revision);
        QpdfAttachmentEditResult result = await Edit(host, operation, Password(revision, true), Protection(policy));
        Assert.Equal(operation == "remove" ? 0 : 2, result.AttachmentCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        QpdfPassword?[] passwords = policy switch
        {
            "remove" => [null],
            "new" => [QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner")],
            "template" => [QpdfPassword.FromText("reader"), QpdfPassword.FromText("fixture-owner")],
            _ => [Password(revision, false), Password(revision, true)],
        };
        foreach (QpdfPassword? password in passwords)
        {
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, password, QpdfLimits.Default, CancellationToken.None))
            {
                Assert.Equal(1, document.PageCount);
                Assert.Equal(policy == "remove" ? null : policy == "new" ? 6 : policy == "template" ? 4 : (int?)revision, document.Encryption?.Revision);
                if (policy != "remove") { Assert.Equal(policy == "template" || policy == "preserve" && revision == 4, document.Encryption?.EncryptMetadata); }
            }
            IReadOnlyList<QpdfAttachmentInfo> attachments = await Qpdf.ListAttachmentsAsync(host, copy, password, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(result.AttachmentCount, attachments.Count);
            if (operation != "remove")
            {
                _ = await Qpdf.ExtractAttachmentAsync(host, copy, "private.bin", PayloadOutput, password, QpdfLimits.Default, CancellationToken.None);
                Assert.True(host.TryGetOutput(PayloadOutput, out byte[] retained)); Assert.Equal(Payload, retained);
                string addedKey = operation == "add" ? "extra.bin" : "copied-private.bin";
                _ = await Qpdf.ExtractAttachmentAsync(host, copy, addedKey, PayloadOutput, password, QpdfLimits.Default, CancellationToken.None);
                Assert.True(host.TryGetOutput(PayloadOutput, out byte[] added)); Assert.Equal(operation == "add" ? "Added bytes"u8.ToArray() : Payload, added);
            }
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("extract")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("copy")]
    public async Task InvalidPrimaryCredentialsPreservePriorOutput(string operation)
    {
        using InMemoryQpdfHost host = new(); Prepare(host, 4);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfPassword password = QpdfPassword.FromText("private-wrong");
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(async () =>
        {
            if (operation == "list") { _ = await Qpdf.ListAttachmentsAsync(host, Input, password, QpdfLimits.Default, CancellationToken.None); }
            else if (operation == "extract") { _ = await Qpdf.ExtractAttachmentAsync(host, Input, "private.bin", Output, password, QpdfLimits.Default, CancellationToken.None); }
            else { _ = await Edit(host, operation, password, QpdfOutputEncryption.Remove); }
        });
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, error.Reason); Assert.DoesNotContain("private-wrong", error.Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("extract")]
    [InlineData("copy")]
    public async Task EveryPrimaryAttachmentAdmissionRefusalRefundsAndClosesInputs(string operation)
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost storage = new(); Prepare(storage, 4); using IMemoryScope caller = budget.CreateScope(); MemoryBudgetHost host = new(storage, caller);
            async Task Operate()
            {
                if (operation == "list") { _ = await Qpdf.ListAttachmentsAsync(host, Input, Password(4, true), QpdfLimits.Default, CancellationToken.None); }
                else if (operation == "extract") { _ = await Qpdf.ExtractAttachmentAsync(host, Input, "private.bin", Output, Password(4, true), QpdfLimits.Default, CancellationToken.None); }
                else { _ = await Edit(host, "copy", Password(4, true), Protection("new")); }
            }
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Operate); Assert.False(storage.TryGetOutput(Output, out _)); } else { await Operate(); }
            Assert.Equal(0, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }
    private static ValueTask<QpdfAttachmentEditResult> Edit(IQpdfHost host, string operation, QpdfPassword? password, QpdfOutputEncryption protection)
        => operation switch
        {
            "add" => Qpdf.AddAttachmentsAsync(host, Input, Output, [Addition], password, protection, QpdfLimits.Default, CancellationToken.None),
            "remove" => Qpdf.RemoveAttachmentsAsync(host, Input, Output, ["private.bin"], password, protection, QpdfLimits.Default, CancellationToken.None),
            _ => Qpdf.CopyAttachmentsAsync(host, Input, Output, [new(Source, "copied-", null)], password, protection, QpdfLimits.Default, CancellationToken.None),
        };
    private static QpdfOutputEncryption Protection(string policy) => policy switch
    {
        "remove" => QpdfOutputEncryption.Remove,
        "new" => QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner"), -4, false),
        "template" => QpdfOutputEncryption.CopyFrom(Template, QpdfPassword.FromText("reader")),
        _ => QpdfOutputEncryption.Preserve,
    };
    private static QpdfPassword? Password(int revision, bool owner) => revision == 4
        ? owner ? QpdfPassword.FromBytes(Convert.FromHexString("6f776e6572e991")) : QpdfPassword.FromText("café•")
        : owner ? QpdfPassword.FromText("fixture-owner") : null;
    private static void Prepare(InMemoryQpdfHost host, int revision)
    {
        Add(Input, $"security-r{revision}-attachment.pdf"); Add(Source, "security-r6-attachment.pdf"); Add(Template, "security-r4-ascii.pdf");
        host.AddInput(QpdfPath.From("extra.bin"), "Added bytes"u8.ToArray(), QpdfInputOwnership.Copy);
        void Add(QpdfPath path, string file) => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
    }

}
