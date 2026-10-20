using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    private static async ValueTask<ExecutionOutcome> RunCheckPlanAsync(
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
                limits, governor, diagnostics, plan.Recovery, cancellationToken, false).ConfigureAwait(false))
            {
                using InspectionGraph? inspection = await PrepareInspectionGraphAsync(host, document, plan,
                    limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
                        using ParserMemoryScope objectMemory = ParserMemoryScope.Create(governor);
                        ReadOnlyMemory<byte>? payload = await ShowObjectAsync(host, document, new ShowObjectPlan(plan.Input, plan.Recovery, plan.ObjectSelector)
                        {
                            RawStreamData = plan.RawStreamData,
                            FilteredStreamData = plan.FilteredStreamData,
                            NormalizeContent = plan.NormalizeContent,
                            RejectStreamAfterInfo = priorInfo && plan.AttachmentKey is null,
                        },
                            limits, governor, diagnostics, objectMemory, cancellationToken).ConfigureAwait(false);
                        if (payload is not null)
                        {
                            AppendInspectionBytes(output, payload.Value, governor);
                            hasPayload = true;
                        }
                    }
                    if (plan.ShowPages) { ShowPages(document, plan, limits, governor, diagnostics, cancellationToken); }
                    if (plan.ListAttachments) { _ = ListAttachments(document, plan.Input, limits, governor, diagnostics, cancellationToken); }
                    if (plan.AttachmentKey is not null)
                    {
                        using ParserMemoryScope attachmentMemory = ParserMemoryScope.Create(governor);
                        ReadOnlyMemory<byte> payload = ReadAttachmentData(document, plan.AttachmentKey, limits, governor, diagnostics, attachmentMemory, cancellationToken);
                        AppendInspectionBytes(output, payload, governor);
                        hasPayload = true;
                    }
                }
            }
            ReadOnlyMemory<byte>? bytes = null;
            if (hasPayload)
            {
                bytes = new ReadOnlyMemory<byte>(output.ToArray());
            }
            output.Dispose();
            ExecutionOutcome result = CompleteWithPresentation(diagnostics.Entries, plan, governor);
            if (result.Status != QpdfExecutionStatus.Completed) { bytes = null; }
            return result.WithOutputBytes(bytes);
        }
        catch (QpdfException ex)
        {
            ExecutionOutcome failure = ToFailedResult(ex);
            if (ex is QpdfLimitException limit && (limit.LimitName == nameof(QpdfLimits.MaxDiagnostics)
                || limit.LimitName == nameof(QpdfLimits.MaxDiagnosticBytes)))
            {
                return failure;
            }
            return new ExecutionOutcome(failure.Status, failure.ExitCode,
                [.. diagnostics.Entries, .. failure.Diagnostics]);
        }
        catch (OperationCanceledException)
        {
            return ExecutionOutcome.Canceled([]);
        }
    }
}
