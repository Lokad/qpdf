using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    private static async ValueTask<ExecutionOutcome> RunSecurityPlanAsync(IQpdfHost host, SecurityPlan plan,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        EncryptionInspection inspection = plan.EmptyDocument ? default
            : await QpdfDocument.InspectSecurityAsync(host, plan.Input, plan.Password, limits, governor,
                diagnostics, plan.Recovery, cancellationToken).ConfigureAwait(false);
        ExecutionOutcome result = CompleteWithPresentation(governor);
        if (result.Status != QpdfExecutionStatus.Completed) { return result; }
        // These are native predicate results, not warning/error severities.
        // In particular --warning-exit-0 cannot change a matching password's 3.
        QpdfExitCode code = inspection.Parameters is null ? QpdfExitCode.Error
            : plan.RequiresPassword && inspection.Authentication != PasswordAuthentication.None ? QpdfExitCode.Warning
            : QpdfExitCode.Success;
        return result with { ExitCode = code };
    }
}
