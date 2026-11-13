using Lokad.Qpdf.Filters;

namespace Lokad.Qpdf.Runtime;

internal static class ExecutionFailures
{
    internal static ExecutionOutcome CompleteWithPresentation(ResourceGovernor governor)
    {
        PresentedDiagnostics result = governor.CompleteExecutionPresentation();
        return new ExecutionOutcome(result.ExitCode == QpdfExitCode.Error ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed,
            result.ExitCode, result.Diagnostics);
    }

    internal static ExecutionOutcome CryptoFailed(string option)
    {
        QpdfDiagnostic diagnostic = new(
            QpdfDiagnosticCode.UnsupportedCryptoOperation,
            QpdfDiagnosticSeverity.Error,
            $"Cryptographic option '{option}' is not supported.");
        return ExecutionOutcome.Failed([diagnostic]);
    }

    internal static ExecutionOutcome ToFailedResult(QpdfException ex)
    {
        if (ex is QpdfCryptoException crypto)
        {
            return CryptoFailed(crypto.Option);
        }

        if (ex is QpdfEncryptedInputException)
        {
            return Failed(QpdfDiagnosticCode.UnsupportedCryptoOperation, ex.Message);
        }

        if (ex is QpdfSecurityException security)
        {
            return Failed(security.Reason switch
            {
                QpdfSecurityFailure.PasswordRequired => QpdfDiagnosticCode.PasswordRequired,
                QpdfSecurityFailure.InvalidPassword => QpdfDiagnosticCode.InvalidPassword,
                _ => QpdfDiagnosticCode.UnsupportedCryptoOperation,
            }, security.Message);
        }

        if (ex is QpdfLimitException or QpdfMemoryAdmissionException)
        {
            return Failed(QpdfDiagnosticCode.BudgetExceeded, ex.Message);
        }

        if (ex is QpdfFilterException or QpdfUnsupportedFeatureException)
        {
            return Failed(QpdfDiagnosticCode.UnsupportedFeature, ex.Message);
        }

        if (ex is QpdfParseException)
        {
            return Failed(QpdfDiagnosticCode.InvalidArguments, ex.Message);
        }

        return Failed(QpdfDiagnosticCode.HostOperationFailed, ex.Message);
    }

    internal static ExecutionOutcome Failed(QpdfDiagnosticCode code, string message)
    {
        QpdfDiagnostic diagnostic = new(code, QpdfDiagnosticSeverity.Error, message);
        return ExecutionOutcome.Failed([diagnostic]);
    }

}
