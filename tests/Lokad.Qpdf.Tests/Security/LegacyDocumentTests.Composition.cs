using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Security;

public sealed partial class LegacyDocumentTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FullJsonUpdatesRetainLegacyProtectionAndPayloads(int revision)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, $"security-r{revision}-ascii.pdf");
        QpdfPath patch = QpdfPath.From("patch.json"); QpdfPassword password = QpdfPassword.FromText("reader");
        QpdfJsonResult exported = await Qpdf.GetDocumentJsonAsync(host, Input, patch,
            new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], true) { Password = password }, CancellationToken.None);
        host.AddInput(patch, Encoding.UTF8.GetBytes(exported.Json), QpdfInputOwnership.Copy);
        _ = await Qpdf.UpdatePdfFromJsonAsync(host, Input, patch, Output, password, QpdfOutputEncryption.Preserve, CancellationToken.None);
        await VerifyOutput(host, revision, ["reader", "fixture-owner"]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task HostFileHexPasswordsAndAttachmentExtractionShareTheLegacyReader(int revision)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, $"security-r{revision}-attachment.pdf");
        host.AddInput(QpdfPath.From("password"), "636166e980\r\n"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
        { inputFile = Input.Value, outputFile = Output.Value, passwordFile = "password", passwordMode = "hex-bytes", showAttachment = "private.bin" }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        Assert.Equal("Private attachment payload.\n"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PagePlacementAndAttachmentSourcesKeepIndependentLegacyCredentials()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r2-ascii.pdf"); Add(host, Template, "security-r3-ascii.pdf");
        Add(host, QpdfPath.From("attachments.pdf"), "security-r3-attachment.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password=reader",
            "--pages", ".", "1", Template.Value, "--password=fixture-owner", "1", "--", "--overlay", Template.Value, "--password=reader", "--",
            "--copy-attachments-from", "attachments.pdf", "--password=café•", "--prefix=copy-", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy); QpdfPassword owner = QpdfPassword.FromText("fixture-owner");
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, owner, QpdfLimits.Default, CancellationToken.None))
        { Assert.Equal(2, document.PageCount); Assert.Equal(2, document.Encryption?.Revision); AssertTitle(document, "TitleText"); }
        QpdfPath payload = QpdfPath.From("payload.bin");
        _ = await Qpdf.ExtractAttachmentAsync(host, copy, "copy-private.bin", payload, owner, QpdfLimits.Default, CancellationToken.None);
        Assert.True(host.TryGetOutput(payload, out byte[] attachment)); Assert.Equal("Private attachment payload.\n"u8.ToArray(), attachment);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SplittingPreservesThePrimaryLegacyPolicyAfterClosingSources()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r2-ascii.pdf"); Add(host, Template, "security-r3-empty.pdf");
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
            [new(Input, new(1), QpdfPassword.FromText("reader")), new(Template, new(1))], Output,
            new PdfPageMergeOptions { SplitPages = 1 }, CancellationToken.None);
        Assert.Equal(2, result.OutputPaths.Count); Assert.Equal(0, host.OpenLeaseCount);
        foreach (QpdfPath output in result.OutputPaths)
        {
            Assert.True(host.TryGetOutput(output, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy-" + output.Value); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
            foreach (string password in new[] { "reader", "fixture-owner" })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(1, document.PageCount); Assert.Equal(2, document.Encryption?.Revision); Assert.Equal(-64, document.Encryption?.Permissions);
            }
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
}
