using System.Text;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class RecoveryHeaderTests
{
    private static readonly QpdfPath Input = QpdfPath.From("prefixed.pdf");

    private static byte[] Classic(int streamLength)
        => PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>", null),
            (4, $"<< /Length {streamLength} >>", Enumerable.Repeat((byte)' ', streamLength).ToArray())
        ], 1, null, "1.7", "");

    [Theory]
    [InlineData("bom", false)]
    [InlineData("comment", false)]
    [InlineData("boundary", false)]
    [InlineData("decoy", false)]
    [InlineData("comment", true)]
    public async Task BoundedHeaderSearchKeepsPhysicalCrossReferenceOffsets(string shape, bool absolute)
    {
        byte[] prefix = shape switch
        {
            "bom" => [0xef, 0xbb, 0xbf],
            "boundary" => Enumerable.Repeat((byte)'x', 1023).ToArray(),
            "decoy" => "leading %PDF-1.4 is prose\n"u8.ToArray(),
            _ => "% lead\n"u8.ToArray()
        };
        byte[] source = Classic(16);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        long sourceXref = XRefTable.ReadTailOffset(source, governor);
        if (absolute)
        {
            string text = Regex.Replace(Encoding.ASCII.GetString(source), @"(?m)^\d{10}(?= 00000 n )",
                match => (long.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture) + prefix.Length)
                    .ToString("D10", System.Globalization.CultureInfo.InvariantCulture));
            text = text.Replace($"startxref\n{sourceXref}\n", $"startxref\n{sourceXref + prefix.Length}\n", StringComparison.Ordinal);
            source = Encoding.ASCII.GetBytes(text);
        }

        using InMemoryQpdfHost host = new();
        host.AddInput(Input, [.. prefix, .. source], QpdfInputOwnership.Transfer);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(host, Input, CancellationToken.None).AsTask());
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, null, QpdfRecoveryMode.BoundedRepair,
            QpdfLimits.Default, CancellationToken.None))
        {
            Assert.Equal(new PdfVersion(1, 7), document.HeaderVersion);
            Assert.Equal(1, document.PageCount);
            Assert.Equal(source.AsSpan().IndexOf("4 0 obj"u8) + prefix.Length, document.Engine.XRefEntries[4].First);
            Assert.Equal(16, Assert.IsType<PdfStream>(document.GetObject(4)).Data.Length);
            Assert.Equal(absolute, document.Warnings.Any(warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundedSearchStopsBeforeOffset1024()
    {
        byte[] input = [.. Enumerable.Repeat((byte)'x', 1024), .. Classic(16)];
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, input, QpdfInputOwnership.Transfer);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(host, Input, null,
            QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReconstructedObjectsAndTrailersStartAtTheAcceptedHeader()
    {
        const string prefix = "8 0 obj\n<< /Type /Catalog /Pages 99 0 R >>\nendobj\ntrailer\n<< /Root 8 0 R /Size 9 >>\n";
        byte[] source = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Classic(16))
            .Replace("startxref\n", "bad-footer\n", StringComparison.Ordinal));
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, [.. Encoding.ASCII.GetBytes(prefix), .. source], QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, null, QpdfRecoveryMode.BoundedRepair,
            QpdfLimits.Default, CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            Assert.DoesNotContain(8, document.Engine.XRefEntries);
            Assert.Contains(document.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PrefixedPageCountFallsBackThroughTheSameAcquisition()
    {
        byte[] input = [.. "% leading comment\n"u8, .. Classic(256 * 1024)];
        SelectiveInputTests.Host host = new(input, 113, Input) { ReplaceFutureInput = true };
        QpdfPageCountResult result = await Qpdf.CountPagesAsync(host, Input, null, QpdfRecoveryMode.BoundedRepair,
            QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.Empty(result.Warnings);
        Assert.True(host.ReadBytes >= input.Length);
        Assert.Equal(1, host.Opened);
        Assert.Equal(0, host.Leases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefixedCountRefusalClosesItsOnlyLease(bool cancel)
    {
        byte[] input = [.. "% leading comment\n"u8, .. Classic(256 * 1024)];
        using CancellationTokenSource cancellation = new();
        SelectiveInputTests.Host host = new(input, 37, Input) { Cancel = cancel ? cancellation : null };
        QpdfLimits limits = cancel ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 64 * 1024 };
        Func<Task> count = () => Qpdf.CountPagesAsync(host, Input, null, QpdfRecoveryMode.BoundedRepair,
            limits, cancellation.Token).AsTask();
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(count); }
        else
        {
            QpdfLimitException limit = await Assert.ThrowsAsync<QpdfLimitException>(count);
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), limit.LimitName);
        }
        Assert.Equal(1, host.Opened);
        Assert.Equal(1, host.Closed);
        Assert.Equal(0, host.Leases);
        Assert.Equal(0, host.OutputAttempts);
    }

    [Theory]
    [InlineData("incremental-1page.pdf", "Second")]
    [InlineData("hybrid-xstm.pdf", "Third")]
    [InlineData("prev-stream.pdf", "Third")]
    public async Task RelativeCoordinatesCoverEverySectionInARevisionChain(string fixture, string marker)
    {
        byte[] source = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, [.. "% leading comment\n"u8, .. source], QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, null, QpdfRecoveryMode.BoundedRepair,
            QpdfLimits.Default, CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            Assert.Empty(document.Warnings);
            PdfStream contents = Assert.IsType<PdfStream>(document.GetObject(4));
            string program = Encoding.ASCII.GetString(contents.Data);
            Assert.Contains($"({marker}) Tj", program, StringComparison.Ordinal);
            Assert.IsType<PdfDictionary>(document.GetObject(5));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RelativeOffsetsKeepPasswordAuthenticationAndObjectIdentity()
    {
        byte[] source = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r4-rc4-ascii.pdf"));
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, [0xef, 0xbb, 0xbf, .. source], QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromText("reader"),
            QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None))
        {
            Assert.Equal(4, document.Encryption?.Revision);
            Assert.Equal(1, document.PageCount);
            Assert.Empty(document.Warnings);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CommandsCountAndSelectPrefixedInputUsingTheSharedReader()
    {
        QpdfPath output = QpdfPath.From("selected.pdf");
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, [.. "% leading comment\n"u8, .. Classic(16)], QpdfInputOwnership.Transfer);
        QpdfExecutionResult count = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"prefixed.pdf","showNpages":""}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, count.ExitCode);
        Assert.Contains(count.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageCount && item.Message == $"{Input.Value}: 1");
        QpdfExecutionResult selection = await Qpdf.ExecuteAsync(host,
            new([Input.Value, "--pages", ".", "1", "--", output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, selection.ExitCode);
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        Assert.True(bytes.AsSpan().StartsWith("%PDF-1.7"u8));
        host.AddInput(output, bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument selected = await QpdfDocument.OpenAsync(host, output, CancellationToken.None))
        { Assert.Equal(1, selected.PageCount); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultidigitHeadersKeepCatalogIdentityOnPublicRecovery(bool job)
    {
        byte[] pdf = TolerantObjectLoadingTests.Pdf(Enumerable.Repeat("null", 50).ToArray(), true);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","showObject":"1"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=1"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message == "<< /Pages 2 0 R /Type /Catalog >>");
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void RecoveryEntriesUseCompleteHeadersRatherThanDigitSuffixes()
    {
        byte[] pdf = TolerantObjectLoadingTests.Pdf(Enumerable.Repeat("null", 50).ToArray(), true);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable table = XRefTable.Parse(pdf, QpdfLimits.Default, governor, new DiagnosticBag(governor), QpdfRecoveryMode.BoundedRepair, valuesEscape: true);
        for (int number = 1; number < 55; number++)
        {
            byte[] header = Encoding.ASCII.GetBytes($"\n{number} 0 obj\n");
            int expectedOffset = pdf.AsSpan().IndexOf(header) + 1;
            Assert.True(expectedOffset > 0);
            Assert.Equal(XrefEntryKind.Uncompressed, table.Entries[number].Kind);
            Assert.Equal(expectedOffset, table.Entries[number].First);
            Assert.Equal(0, table.Entries[number].Second);
        }
    }
}
