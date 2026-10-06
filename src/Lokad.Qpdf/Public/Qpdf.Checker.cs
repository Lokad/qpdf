using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    private static async ValueTask<QpdfExecutionResult> RunCheckPlanAsync(
        IQpdfHost host, CheckPlan plan, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            using GovernedBufferStream output = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes));
            bool hasPayload = false;
            // Checking and all following inspections use this same document.
            // Native reports declared counts separately from quiet traversal.
            diagnostics.ReportPageCountMismatch = false;
            await using (QpdfDocument document = await OpenPlanInputAsync(host, plan.Input, plan.EmptyDocument,
                limits, governor, diagnostics, plan.Recovery, cancellationToken).ConfigureAwait(false))
            {
                List<QpdfCheckOutcome> checks = plan.RunCheck
                    ? DocumentCheck.Run(document, new QpdfCheckOptions(), limits, governor, diagnostics, true, cancellationToken)
                    : [];
                if (!checks.Exists(static outcome => outcome.Status == QpdfCheckStatus.Failed))
                {
                    // This order is independent of argv position and JSON key order.
                    if (plan.ShowNpages) { AddPageCountDiagnostic(document, plan.Input, diagnostics); }
                    if (plan.CheckLinearization) { InspectLinearization(document, plan.Input, limits, governor, diagnostics, cancellationToken); }
                    if (plan.ShowLinearization) { ShowLinearization(document, plan.Input, limits, governor, diagnostics, cancellationToken); }
                    if (plan.ShowXref) { ShowXref(document, plan, limits, governor, diagnostics, cancellationToken); }
                    if (plan.ObjectSelector is not null)
                    {
                        bool priorInfo = plan.RunCheck || plan.ShowNpages || plan.CheckLinearization || plan.ShowLinearization || plan.ShowXref;
                        byte[]? payload = await ShowObjectAsync(host, document, new ShowObjectPlan(plan.Input, plan.Recovery, plan.ObjectSelector)
                        {
                            RawStreamData = plan.RawStreamData,
                            FilteredStreamData = plan.FilteredStreamData,
                            NormalizeContent = plan.NormalizeContent,
                            RejectStreamAfterInfo = priorInfo && plan.AttachmentKey is null,
                        },
                            limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                        if (payload is not null)
                        {
                            AppendInspectionBytes(output, payload, governor);
                            hasPayload = true;
                        }
                    }
                    if (plan.ShowPages) { ShowPages(document, plan, limits, governor, diagnostics, cancellationToken); }
                    if (plan.ListAttachments) { _ = ListAttachments(document, plan.Input, limits, governor, diagnostics, cancellationToken); }
                    if (plan.AttachmentKey is not null)
                    {
                        byte[] payload = ReadAttachmentData(document, plan.AttachmentKey, limits, governor, diagnostics, cancellationToken);
                        AppendInspectionBytes(output, payload, governor);
                        hasPayload = true;
                    }
                }
            }
            QpdfExecutionResult result = CompleteWithPresentation(diagnostics.Entries, governor.Snapshot(), plan);
            ReadOnlyMemory<byte>? bytes = null;
            if (result.Status == QpdfExecutionStatus.Completed && hasPayload)
            {
                bytes = new ReadOnlyMemory<byte>(output.ToArray());
            }
            output.Dispose();
            return result.WithCounters(governor.Snapshot()).WithOutputBytes(bytes);
        }
        catch (QpdfException ex)
        {
            QpdfExecutionResult failure = ToFailedResult(ex);
            return new QpdfExecutionResult(failure.Status, failure.ExitCode,
                [.. diagnostics.Entries, .. failure.Diagnostics], governor.Snapshot());
        }
        catch (OperationCanceledException)
        {
            return QpdfExecutionResult.Canceled([], governor.Snapshot());
        }
    }
}
