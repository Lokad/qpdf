using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordCheckTests
{
    [Theory]
    [InlineData("security-r4-linearized.pdf", "reader")]
    [InlineData("security-r4-linearized.pdf", "fixture-owner")]
    [InlineData("security-r6-linearized.pdf", "reader")]
    [InlineData("security-r6-linearized.pdf", "fixture-owner")]
    public async Task EncryptedLinearizedFilesPassTypedAndCommandChecks(string file, string password)
    {
        using InMemoryQpdfHost host = new(); QpdfPath path = Add(host, file);
        QpdfCheckResult typed = await Qpdf.CheckAsync(host, path,
            new QpdfCheckOptions { Password = QpdfPassword.FromText(password) }, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(2, typed.PageCount);
        Assert.DoesNotContain(typed.Checks, check => check.Status == QpdfCheckStatus.Failed);
        QpdfCheckOutcome linearization = Assert.Single(typed.Checks, check => check.Name == "linearization");
        Assert.Equal(QpdfCheckStatus.Passed, linearization.Status); Assert.Contains("hint tables valid", linearization.Detail, StringComparison.Ordinal);
        QpdfExecutionResult command = await Qpdf.ExecuteAsync(host,
            new([file, "--password=" + password, "--check-linearization", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, command.Status); Assert.Equal(QpdfExitCode.Success, command.ExitCode);
        Assert.Contains(command.Diagnostics, item => item.Code == QpdfDiagnosticCode.LinearizationReport && item.Message.EndsWith("no linearization errors", StringComparison.Ordinal));
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("security-r4-ascii.pdf", "reader")]
    [InlineData("security-r6-ascii.pdf", "fixture-owner")]
    [InlineData("security-r4-empty.pdf", null)]
    [InlineData("security-r6-empty.pdf", null)]
    [InlineData("security-r4-incremental.pdf", "fixture-owner")]
    [InlineData("security-r6-hybrid-incremental.pdf", "reader")]
    public async Task StructuralChecksUseAuthenticatedObjectsAndReportThatFact(string file, string? password)
    {
        using InMemoryQpdfHost host = new(); QpdfPath path = Add(host, file);
        QpdfCheckOptions options = new() { Password = password is null ? null : QpdfPassword.FromText(password) };
        QpdfCheckResult result = await Qpdf.CheckAsync(host, path, options, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.DoesNotContain(result.Checks, check => check.Status == QpdfCheckStatus.Failed);
        Assert.Contains(result.Checks, check => check.Name == "object-graph" && check.Status == QpdfCheckStatus.Passed);
        Assert.Contains(result.Checks, check => check.Name == "streams" && check.Status == QpdfCheckStatus.Passed);
        Assert.Equal("Catalog root is reachable after password authentication.", Assert.Single(result.Checks, check => check.Name == "trailer").Detail);
        Assert.Equal(0, host.OpenLeaseCount);
        if (password is not null) { Assert.DoesNotContain(password, options.ToString(), StringComparison.Ordinal); }
    }

    [Theory]
    [InlineData("security-r4-ascii.pdf", null, QpdfSecurityFailure.PasswordRequired)]
    [InlineData("security-r6-ascii.pdf", null, QpdfSecurityFailure.PasswordRequired)]
    [InlineData("security-r4-ascii.pdf", "private-bad-password", QpdfSecurityFailure.InvalidPassword)]
    [InlineData("security-r6-ascii.pdf", "private-bad-password", QpdfSecurityFailure.InvalidPassword)]
    public async Task AuthenticationFailuresAndCancellationCloseInputsAndRefundMemory(string file, string? password, QpdfSecurityFailure reason)
    {
        RecordingMemoryBudget budget = new(1048576);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget); QpdfPath path = Add(host, file);
        long baseline = budget.ReservedBytes;
        QpdfCheckOptions options = new() { Password = password is null ? null : QpdfPassword.FromText(password) };
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.CheckAsync(host, path, options, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(reason, error.Reason); Assert.DoesNotContain("private-bad-password", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(baseline, budget.ReservedBytes);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Qpdf.CheckAsync(host, path, options, QpdfLimits.Default, cancellation.Token).AsTask());
        Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(baseline, budget.ReservedBytes);
    }

    private static QpdfPath Add(InMemoryQpdfHost host, string file)
    {
        QpdfPath path = QpdfPath.From(file);
        host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
        return path;
    }
}
