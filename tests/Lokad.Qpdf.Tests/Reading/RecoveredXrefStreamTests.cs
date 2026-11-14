using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class RecoveredXrefStreamTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Theory]
    [InlineData("objstm-1page.pdf", null)]
    [InlineData("security-r6-ascii.pdf", "reader")]
    public async Task AnIntactTerminalXrefStreamRecoversCompressedPagesAndSecurity(string fixture, string? password)
    {
        QpdfPath input = QpdfPath.From("damaged.pdf"), output = QpdfPath.From("repaired.pdf");
        using InMemoryQpdfHost host = new();
        host.AddInput(input, TypedSelectionRecoveryTests.Damage(Fixture(fixture), "startxref"), QpdfInputOwnership.Transfer);
        QpdfPassword? credential = password is null ? null : QpdfPassword.FromText(password);
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPageAsync(host, input, output, new(1), credential, CancellationToken.None).AsTask());
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, input, output, new(1), [], credential,
            QpdfOutputEncryption.Remove, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.Contains(result.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged
            && warning.Message.Contains("terminal cross-reference stream", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(1, independent.NumberOfPages); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ANewerClassicTrailerPreventsResurrectingAnOlderStream()
    {
        byte[] source = Fixture("objstm-1page.pdf");
        byte[] appended = [.. source, .. "\nxref\n0 0\ntrailer\n<< /Size 100 /Root << /Type /Catalog /Pages << /Type /Pages /Count 0 /Kids [] >> >> >>\nstartxref\n999999999\n%%EOF\n"u8];
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("updated.pdf"), appended, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["updated.pdf", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.EndsWith(": 0", Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.RecoveryEngaged
            && diagnostic.Message.Contains("active trailer", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReanchoredStreamDecodeExhaustionDoesNotRetryAWeakerRepair()
    {
        using InMemoryQpdfHost host = new(); QpdfPath input = QpdfPath.From("limited.pdf");
        host.AddInput(input, TypedSelectionRecoveryTests.Damage(Fixture("objstm-1page.pdf"), "startxref"), QpdfInputOwnership.Transfer);
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.CountPagesAsync(host, input,
            null, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default with { MaxDecodedBytes = 1 }, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), failure.LimitName); Assert.Equal(0L, host.OpenLeaseCount);
    }
}
