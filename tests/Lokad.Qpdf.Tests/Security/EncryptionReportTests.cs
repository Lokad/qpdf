using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Security;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptionReportTests
{
    // qpdf 12.4.2: full printing/extraction with each --modify mode.
    [Theory]
    [InlineData(-4, true, true, true, true, true)]
    [InlineData(-1324, false, false, false, false, false)]
    [InlineData(-44, true, true, false, false, false)]
    [InlineData(-12, true, true, true, false, false)]
    [InlineData(-300, true, false, false, false, false)]
    public void PermissionCombinationsMatchNative(int flags, bool assembly, bool forms, bool annotations, bool other, bool all)
    {
        foreach (int revision in new[] { 4, 6 })
        {
            EncryptionPermissions permissions = new(revision, flags);
            Assert.True(permissions.Accessibility); Assert.True(permissions.Extract);
            Assert.True(permissions.PrintLow); Assert.True(permissions.PrintHigh);
            Assert.Equal(assembly, permissions.Assemble); Assert.Equal(forms, permissions.Forms);
            Assert.Equal(annotations, permissions.Annotations); Assert.Equal(other, permissions.ModifyOther);
            Assert.Equal(all, permissions.ModifyAll);
        }
    }

    [Theory]
    [InlineData("security-r2-ascii.pdf", null, 2, null)]
    [InlineData("security-r2-ascii.pdf", "reader", 2, "user")]
    [InlineData("security-r2-ascii.pdf", "fixture-owner", 2, "owner")]
    [InlineData("security-r2-empty.pdf", null, 2, "user")]
    [InlineData("security-r3-ascii.pdf", null, 3, null)]
    [InlineData("security-r3-ascii.pdf", "reader", 3, "user")]
    [InlineData("security-r3-ascii.pdf", "fixture-owner", 3, "owner")]
    [InlineData("security-r3-empty.pdf", null, 3, "user")]
    [InlineData("security-r4-ascii.pdf", null, 4, null)]
    [InlineData("security-r4-ascii.pdf", "reader", 4, "user")]
    [InlineData("security-r4-ascii.pdf", "fixture-owner", 4, "owner")]
    [InlineData("security-r6-ascii.pdf", "private-wrong-password", 6, null)]
    [InlineData("security-r6-ascii.pdf", "reader", 6, "user")]
    [InlineData("security-r6-ascii.pdf", "fixture-owner", 6, "owner")]
    [InlineData("security-r4-empty.pdf", null, 4, "user")]
    [InlineData("security-r6-empty.pdf", null, 6, "user")]
    public async Task ArgvAndJobReportsMatchNonSecretNativeFacts(string file, string? password, int revision, string? role)
    {
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); Add(host, file);
            QpdfExecutionResult result = await Execute(host, file, password, job, false);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            string[] lines = result.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray();
            string cipher = revision == 4 ? "AESv2" : "AESv3";
            List<string> expected = [];
            if (role is null) { expected.Add("Incorrect password supplied"); }
            expected.AddRange(["R = " + revision, "P = " + (revision == 2 ? -64 : -3388)]);
            if (role is not null) { expected.Add("Supplied password is " + role + " password"); }
            // Captured from qpdf 12.4.2 for the fixture's low-print-only restrictions.
            expected.AddRange([
                "extract for accessibility: " + (revision == 2 ? "not allowed" : "allowed"), "extract for any purpose: not allowed",
                "print low resolution: " + (revision == 2 ? "not allowed" : "allowed"), "print high resolution: not allowed",
                "modify document assembly: not allowed", "modify forms: not allowed",
                "modify annotations: not allowed", "modify other: not allowed", "modify anything: not allowed"]);
            if (revision >= 4) { expected.AddRange(["stream encryption method: " + cipher, "string encryption method: " + cipher, "file encryption method: " + cipher]); }
            Assert.Equal(expected, lines);
            Assert.All(result.Diagnostics, diagnostic => Assert.Equal(QpdfDiagnosticCode.EncryptionReport, diagnostic.Code));
            Assert.DoesNotContain(lines, line => line.Contains("User password =", StringComparison.Ordinal)
                || line.Contains("Encryption key", StringComparison.Ordinal) || line.Contains("private-wrong-password", StringComparison.Ordinal)
                || line.Contains("reader", StringComparison.Ordinal) || line.Contains("fixture-owner", StringComparison.Ordinal));
            Assert.Null(result.OutputBytes); Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
        }
    }

    [Theory]
    [InlineData("minimal-1page.pdf")]
    [InlineData("cyclic-pages.pdf")]
    public async Task PlainReportsDoNotTraversePages(string file)
    {
        using InMemoryQpdfHost host = new(); Add(host, file);
        QpdfExecutionResult result = await Execute(host, file, null, false, false);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal("File is not encrypted", Assert.Single(result.Diagnostics).Message);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CombinedInspectionsRequireSuccessfulAuthentication(bool job, bool valid)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        QpdfExecutionResult result = await Execute(host, "security-r6-ascii.pdf", valid ? "reader" : "wrong", job, true);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(valid, result.Diagnostics.Any(diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCount));
        Assert.Equal(!valid, result.Diagnostics.Any(diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckSkipped
            && diagnostic.Message == "Other inspections were skipped because the password did not authenticate."));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.EncryptionReport);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("--show-encryption=yes")]
    [InlineData("--show-encryption", "out.pdf")]
    [InlineData("--show-encryption", "--show-encryption-key")]
    public async Task InvalidOrExcludedCombinationsFailBeforeInputAccess(params string[] options)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["missing.pdf", .. options]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(0, result.Counters.HostOperations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReportDiagnosticLimitsRemainBinding(bool job, bool byteLimit)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        QpdfExecutionOptions options = new(byteLimit ? QpdfLimits.Default with { MaxDiagnosticBytes = 40L }
            : QpdfLimits.Default with { MaxDiagnostics = 2L });
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"security-r6-ascii.pdf","showEncryption":""}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["security-r6-ascii.pdf", "--show-encryption"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles); Assert.Null(result.OutputBytes);
    }

    [Fact]
    public async Task EmptyInputReportNeedsNoHostInput()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["--empty", "--show-encryption"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal("File is not encrypted", Assert.Single(result.Diagnostics).Message);
        Assert.Equal(0, result.Counters.HostOperations);
    }

    [Fact]
    public async Task StreamBytesCannotFollowAnEncryptionReport()
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
            ["security-r6-ascii.pdf", "--password=reader", "--show-encryption", "--show-object=7", "--filtered-stream-data"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Null(result.OutputBytes);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.EncryptionReport);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string file, string? password, bool job, bool check)
    {
        if (job)
        {
            Dictionary<string, string> fields = new() { ["inputFile"] = file, ["showEncryption"] = "" };
            if (password is not null) { fields["password"] = password; }
            if (check) { fields["check"] = ""; fields["showNpages"] = ""; }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
        }
        List<string> argv = [file, "--show-encryption"];
        if (password is not null) { argv.Add("--password=" + password); }
        if (check) { argv.AddRange(["--check", "--show-npages"]); }
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
    }

    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(QpdfPath.From(file), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
}
