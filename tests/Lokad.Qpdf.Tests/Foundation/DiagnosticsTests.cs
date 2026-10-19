using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class DiagnosticsTests
{
    private static QpdfExecutionCounters EmptyCounters()
    {
        return new QpdfExecutionCounters(0L, 0L, 0L, 0L, 0L, 0L, 0L);
    }

    [Fact]
    public void CompletedMapsDiagnosticsToExitCodes()
    {
        QpdfExecutionResult clean = QpdfExecutionResult.Completed([], EmptyCounters());
        Assert.Equal(QpdfExitCode.Success, clean.ExitCode);
        Assert.Equal(QpdfExecutionStatus.Completed, clean.Status);

        QpdfDiagnostic warning = new(QpdfDiagnosticCode.HostOperationFailed, QpdfDiagnosticSeverity.Warning, "slow host");
        QpdfExecutionResult warned = QpdfExecutionResult.Completed([warning], EmptyCounters());
        Assert.Equal(QpdfExitCode.Warning, warned.ExitCode);

        QpdfDiagnostic error = new(QpdfDiagnosticCode.BudgetExceeded, QpdfDiagnosticSeverity.Error, "over budget");
        QpdfExecutionResult errored = QpdfExecutionResult.Completed([error, warning], EmptyCounters());
        Assert.Equal(QpdfExitCode.Error, errored.ExitCode);
    }

    [Fact]
    public void FailedAndCanceledMapToError()
    {
        Assert.Equal(QpdfExitCode.Error, QpdfExecutionResult.Failed([], EmptyCounters()).ExitCode);
        Assert.Equal(QpdfExitCode.Error, QpdfExecutionResult.Canceled([], EmptyCounters()).ExitCode);
        Assert.Equal(QpdfExecutionStatus.Failed, QpdfExecutionResult.Failed([], EmptyCounters()).Status);
        Assert.Equal(QpdfExecutionStatus.Canceled, QpdfExecutionResult.Canceled([], EmptyCounters()).Status);
    }

    [Fact]
    public void ResultsDefensivelyCopyDiagnostics()
    {
        List<QpdfDiagnostic> source =
        [
            new QpdfDiagnostic(QpdfDiagnosticCode.BudgetExceeded, QpdfDiagnosticSeverity.Error, "full"),
        ];
        QpdfExecutionResult result = QpdfExecutionResult.Failed(source, EmptyCounters());
        source.Clear();
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void HostFailuresNeverLeakTransportDetail()
    {
        const string Canary = "canary-secret-transport-detail";
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        QpdfHostException failure;
        try
        {
            throw new InvalidOperationException(Canary);
        }
        catch (InvalidOperationException ex)
        {
            failure = new QpdfHostException(path, QpdfHostFailureReason.ReadFailed, ex);
        }

        Assert.Equal(QpdfHostFailureReason.ReadFailed, failure.Reason);
        Assert.Equal(path, failure.Path);
        Assert.DoesNotContain(Canary, failure.Message);
        Assert.NotNull(failure.InnerException);
    }

    [Fact]
    public void LimitFailuresCarryBudgetsWithoutContent()
    {
        QpdfLimitException ex = new(nameof(QpdfLimits.MaxOutputBytes), 100L, 10L);
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), ex.LimitName);
        Assert.Equal(100L, ex.Requested);
        Assert.Equal(10L, ex.Allowed);
    }

    [Fact]
    public void DiagnosticBagRejectsNullMessages()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        Assert.Throws<ArgumentNullException>(() => bag.Add(QpdfDiagnosticCode.BudgetExceeded, QpdfDiagnosticSeverity.Error, null!));
    }
}

