using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    private static void ShowEncryption(EncryptionInspection inspection, DiagnosticBag diagnostics)
    {
        if (inspection.Parameters is not EncryptionParameters parameters)
        {
            diagnostics.Add(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info, "File is not encrypted");
            return;
        }
        if (inspection.Authentication == PasswordAuthentication.None)
        { diagnostics.Add(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info, "Incorrect password supplied"); }
        diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info, ["R = ", parameters.Revision]);
        diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info, ["P = ", parameters.Permissions]);
        if (inspection.Authentication != PasswordAuthentication.None)
        {
            diagnostics.Add(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info,
                inspection.Authentication == PasswordAuthentication.Owner
                    ? "Supplied password is owner password" : "Supplied password is user password");
        }
        EncryptionPermissions permissions = new(parameters.Revision, parameters.Permissions);
        Permission("extract for accessibility: ", permissions.Accessibility);
        Permission("extract for any purpose: ", permissions.Extract);
        Permission("print low resolution: ", permissions.PrintLow);
        Permission("print high resolution: ", permissions.PrintHigh);
        Permission("modify document assembly: ", permissions.Assemble);
        Permission("modify forms: ", permissions.Forms);
        Permission("modify annotations: ", permissions.Annotations);
        Permission("modify other: ", permissions.ModifyOther);
        Permission("modify anything: ", permissions.ModifyAll);
        diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info,
            ["stream encryption method: ", EncryptionParameters.CipherName(parameters.Streams)]);
        diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info,
            ["string encryption method: ", EncryptionParameters.CipherName(parameters.Strings)]);
        diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info,
            ["file encryption method: ", EncryptionParameters.CipherName(parameters.Files)]);

        void Permission(string label, bool allowed) => diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport,
            QpdfDiagnosticSeverity.Info, [label, allowed ? "allowed" : "not allowed"]);
    }

    private static async ValueTask<ExecutionOutcome> RunSecurityPlanAsync(IQpdfHost host, SecurityPlan plan,
        QpdfPassword? password, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        EncryptionInspection inspection = plan.EmptyDocument ? default
            : await QpdfDocument.InspectSecurityAsync(host, plan.Input, password, limits, governor,
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
