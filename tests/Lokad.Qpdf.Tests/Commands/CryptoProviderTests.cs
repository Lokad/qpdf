using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class CryptoProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneQueryReportsManagedImplementation(bool responseFile)
    {
        using InMemoryQpdfHost host = new();
        if (responseFile) { host.AddInput(QpdfPath.From("args"), "--show-crypto\n"u8.ToArray(), QpdfInputOwnership.Copy); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([responseFile ? "@args" : "--show-crypto"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.HelpText, diagnostic.Code);
        Assert.Equal(QpdfDiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal("managed", diagnostic.Message);
        Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
        if (!responseFile) { Assert.Equal(0L, result.Counters.HostOperations); }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("--show-crypto=private")]
    [InlineData("--show-crypto", "missing.pdf", "out.pdf")]
    [InlineData("missing.pdf", "--show-crypto", "out.pdf")]
    [InlineData("--version", "--show-crypto")]
    [InlineData("--show-crypto", "--help")]
    [InlineData("--show-crypto", "--show-crypto")]
    public async Task InvalidFormsFailBeforeHostAccess(params string[] argv)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.DoesNotContain("private", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.False(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDiagnosticBudgetRemainBinding(bool canceled)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["--show-crypto"]),
            new(QpdfLimits.Default with { MaxDiagnosticBytes = 1 }), new CancellationToken(canceled));
        Assert.Equal(canceled ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        if (!canceled) { Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code); }
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PasswordLiteralDoesNotBecomeAnIdentityCommand()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["--empty", "out.pdf", "--encrypt", "reader", "--show-crypto", "256", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.HelpText);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] bytes));
        host.AddInput(QpdfPath.From("copy.pdf"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy.pdf"),
            QpdfPassword.FromText("--show-crypto"), QpdfLimits.Default, CancellationToken.None);
        Assert.True(document.Encryption?.OwnerPasswordMatched);
        Assert.Equal(6, document.Encryption?.Revision);
    }
}
