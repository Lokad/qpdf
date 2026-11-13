using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using static Lokad.Qpdf.Operations.AttachmentExecution;
using static Lokad.Qpdf.Operations.InspectionExecution;
using static Lokad.Qpdf.Operations.JsonExecution;
using static Lokad.Qpdf.Operations.MultiPageSelection;
using static Lokad.Qpdf.Operations.SinglePageSelection;
using static Lokad.Qpdf.Runtime.ExecutionFailures;

namespace Lokad.Qpdf.Operations;

internal static class JobExecution
{
    internal static async ValueTask<ExecutionOutcome> RunExpandedInvocationAsync(
        IQpdfHost host, IReadOnlyList<string> expanded, QpdfExecutionOptions options,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {

        if (HelpText.TryMatchCrypto(expanded, out string crypto))
        {
            diagnostics.Add(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, crypto);
            return ExecutionOutcome.Completed(diagnostics.Entries);
        }

        if (HelpText.TryMatchHelp(expanded, out string help))
        {
            return ExecutionOutcome.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, help)]);
        }

        if (HelpText.TryMatchVersion(expanded, out string version))
        {
            return ExecutionOutcome.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, version)]);
        }

        if (HelpText.TryMatchCopyright(expanded, out string copyright))
        {
            return ExecutionOutcome.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, copyright)]);
        }

        if (HelpText.TryMatchJobJsonHelp(expanded, out string jobJsonHelp))
        {
            return ExecutionOutcome.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, jobJsonHelp)]);
        }

        if (HelpText.TryMatchJsonHelp(expanded, out string jsonHelp))
        {
            return ExecutionOutcome.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, jsonHelp)]);
        }

        if (HelpText.TryMatchCompletionBash(expanded, out string completionBash))
        {
            return ExecutionOutcome.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, completionBash)]);
        }

        if (HelpText.TryMatchCompletionZsh(expanded, out string completionZsh))
        {
            return ExecutionOutcome.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, completionZsh)]);
        }

        if (HelpText.TryMatchZopfli(expanded, out string zopfli))
        {
            return ExecutionOutcome.Failed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Error, zopfli)]);
        }

        JobJsonLoader.LoadedJob loaded;
        try
        {
            loaded = await JobJsonLoader.ParseArgvAsync(host, expanded, options.Limits, governor, cancellationToken).ConfigureAwait(false);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        catch (OperationCanceledException)
        {
            return ExecutionOutcome.Canceled([]);
        }

        return await RunLoadedJobAsync(host, loaded, options, governor, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<ExecutionOutcome> RunLoadedJobAsync(
        IQpdfHost host, JobJsonLoader.LoadedJob loaded, QpdfExecutionOptions options,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        ExecutionOutcome result;
        using (loaded)
        {
            result = await RunPlanAsync(host, loaded.Plan, options.Limits, options.ExplicitLimits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    internal static QpdfLimits ResolveParserLimits(JobPlan plan, QpdfLimits host, bool hostExplicit)
    {
        QpdfLimits limits = host;
        if (plan.MaxStreamFilters is long maxStreamFilters)
        {
            RequireParserCeiling(nameof(QpdfLimits.MaxStreamFilters), maxStreamFilters, host.MaxStreamFilters, hostExplicit);
            limits = limits with { MaxStreamFilters = maxStreamFilters };
        }

        if (plan.ParserMaxNesting is long parserMaxNesting)
        {
            RequireParserCeiling(nameof(QpdfLimits.MaxNestingDepth), parserMaxNesting, host.MaxNestingDepth, hostExplicit);
            limits = limits with { MaxNestingDepth = parserMaxNesting };
        }

        if (plan.MaxContainerSize is long maxContainerSize)
        {
            RequireParserCeiling(nameof(QpdfLimits.MaxContainerSize), maxContainerSize, host.MaxContainerSize, hostExplicit);
            limits = limits with { MaxContainerSize = maxContainerSize };
        }

        if (plan.MaxContainerSizeDamaged is long maxContainerSizeDamaged)
        {
            RequireParserCeiling(nameof(QpdfLimits.MaxContainerSizeDamaged), maxContainerSizeDamaged, host.MaxContainerSizeDamaged, hostExplicit);
            limits = limits with { MaxContainerSizeDamaged = maxContainerSizeDamaged };
        }

        if (plan.ParserMaxErrors is long parserMaxErrors)
        {
            // Zero means unlimited semantic errors, not a tighter numeric cap.
            if (hostExplicit && host.MaxParserErrors != 0L
                && (parserMaxErrors == 0L || parserMaxErrors > host.MaxParserErrors))
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxParserErrors),
                    parserMaxErrors == 0L ? long.MaxValue : parserMaxErrors, host.MaxParserErrors);
            }
            limits = limits with { MaxParserErrors = parserMaxErrors };
        }

        if (plan.NoDefaultLimits && !hostExplicit)
        {
            // Mirror upstream disable_defaults: only optional parser defaults lift,
            // and only when neither the invocation nor the host set them explicitly.
            // Parser nesting and the undamaged container size are never lifted.
            if (plan.ParserMaxErrors is null)
            {
                limits = limits with { MaxParserErrors = 0L };
            }

            if (plan.MaxContainerSizeDamaged is null)
            {
                limits = limits with { MaxContainerSizeDamaged = 4294967295L };
            }

            if (plan.MaxStreamFilters is null)
            {
                limits = limits with { MaxStreamFilters = 4294967295L };
            }
        }

        return limits;
    }

    internal static void RequireParserCeiling(string name, long requested, long hostCeiling, bool hostExplicit)
    {
        // Invocation parser knobs only tighten within the embedding host hard
        // ceilings. Value equality with a library default does not establish that
        // the host left the ceiling untouched: only default-constructed options
        // leave invocation values (within their own absolute bounds) in charge.
        if (hostExplicit && requested > hostCeiling)
        {
            throw new QpdfLimitException(name, requested, hostCeiling);
        }
    }

    internal static async ValueTask<ExecutionOutcome> RunPlanAsync(
        IQpdfHost host,
        JobPlan plan,
        QpdfLimits limits,
        bool hostExplicit,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        ExecutionPresentation? presentation = null;
        try
        {
            limits = ResolveParserLimits(plan, limits, hostExplicit);
            governor.MaxNestingDepth = limits.MaxNestingDepth;
            presentation = ExecutionPresentation.Create(governor, diagnostics, plan.Verbose,
                plan.ReportMemoryUsage, plan.Progress, plan.NoWarn, plan.WarningExitZero, cancellationToken);
            ExecutionOutcome result = await RunPlanCoreAsync(host, plan, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            ExecutionPresentation? active = presentation ?? governor.ExecutionPresentation;
            return result.Status == QpdfExecutionStatus.Completed || active is null ? result : active.Failure(result);
        }
        catch (QpdfException ex)
        {
            ExecutionOutcome result = ToFailedResult(ex);
            return (presentation ?? governor.ExecutionPresentation)?.Failure(result) ?? result;
        }
        catch (OperationCanceledException)
        {
            ExecutionOutcome result = ExecutionOutcome.Canceled([]);
            return (presentation ?? governor.ExecutionPresentation)?.Failure(result) ?? result;
        }
        finally { (presentation ?? governor.ExecutionPresentation)?.Dispose(); }
    }

    internal static async ValueTask<ExecutionOutcome> RunPlanCoreAsync(IQpdfHost host, JobPlan plan,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        using Security.CommandPasswordLease? suppliedPassword = plan.PasswordFiles is null ? null
            : await Security.CommandPasswordLease.ResolveAsync(host, plan.PasswordFiles, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        if (plan.IgnoreXrefStreams)
        {
            limits = limits with { IgnoreXrefStreams = true };
        }

        if (plan is SecurityPlan security)
        {
            return await RunSecurityPlanAsync(host, security, suppliedPassword is null ? security.Password : suppliedPassword.Password,
                limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        if (plan is CheckPlan check)
        {
            return await RunCheckPlanAsync(host, check, suppliedPassword is null ? check.Password : suppliedPassword.Password,
                limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        if (plan is ShowObjectPlan showObject)
        {
            await ShowObjectCoreAsync(host, showObject, suppliedPassword is null ? showObject.TransformationPassword : suppliedPassword.Password,
                limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            return CompleteWithPresentation(governor);
        }

        if (plan is AttachmentPlan attachments)
        {
            QpdfPassword? password = suppliedPassword is null ? attachments.TransformationPassword : suppliedPassword.Password;
            if (attachments.List)
            {
                await ListAttachmentsCoreAsync(host, attachments.Input, password, limits, attachments.Recovery, attachments.EmptyDocument, governor, diagnostics, false, cancellationToken).ConfigureAwait(false);
            }

            if (attachments.Key is not null)
            {
                QpdfPath output = attachments.Output ?? throw new QpdfParseException("An output file is required after --.", 0L, null);
                await ExtractAttachmentCoreAsync(host, attachments.Input, attachments.Key, output, password, limits, attachments.Recovery, attachments.EmptyDocument, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            }

            return CompleteWithPresentation(governor);
        }

        if (plan is JsonPlan json)
        {
            Commands.JsonInspection inspection = new(json.Version, json.Keys, json.DecodeLevel, json.StreamDataMode, json.StreamPrefix, json.Objects, json.MinimalOutput)
            { Password = suppliedPassword is null ? json.Password : suppliedPassword.Password };
            JsonInspectionOutcome inspected = await JsonCoreAsync(
                host,
                json.Input,
                json.Output,
                inspection,
                limits,
                json.Recovery,
                json.EmptyDocument,
                governor,
                diagnostics,
                json.TestJsonSchema,
                !json.Output.HasValue,
                cancellationToken).ConfigureAwait(false);
            return CompleteWithPresentation(governor).WithOutputText(json.Output.HasValue ? null : inspected.Json);
        }

        if (plan is JsonInputPlan jsonInput)
        {
            await ImportCreateCoreAsync(
                host,
                jsonInput.Input,
                jsonInput.Output,
                jsonInput.MinVersion,
                jsonInput.ForceVersion,
                jsonInput.StaticId,
                jsonInput.NewlineBeforeEndstream,
                jsonInput.OutputEncryption ?? QpdfOutputEncryption.Preserve,
                jsonInput.Recovery,
                limits,
                governor,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            return CompleteWithPresentation(governor);
        }

        if (plan is JsonUpdatePlan jsonUpdate)
        {
            await ImportUpdateCoreAsync(
                host,
                jsonUpdate.Input,
                jsonUpdate.JsonPath,
                jsonUpdate.Output,
                jsonUpdate.MinVersion,
                jsonUpdate.ForceVersion,
                jsonUpdate.StaticId,
                jsonUpdate.NewlineBeforeEndstream,
                suppliedPassword is null ? jsonUpdate.TransformationPassword : suppliedPassword.Password,
                jsonUpdate.OutputEncryption ?? QpdfOutputEncryption.Preserve,
                limits,
                jsonUpdate.Recovery,
                governor,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            return CompleteWithPresentation(governor);
        }

        ValidateFirstPassInputs(plan, governor, cancellationToken);
        QpdfPassword? transformationPassword = suppliedPassword is null ? plan.TransformationPassword : suppliedPassword.Password;
        if (plan is WholeFilePlan whole)
        {
            try
            {
                await WholeFileCoreAsync(host, whole, transformationPassword, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                return CompleteWithPresentation(governor);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return ExecutionOutcome.Failed(
                    [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)]);
            }
        }

        try
        {
            if (plan is MergePlan merge)
            {
                try
                {
                    List<PdfPageSource> resolved = new(merge.Sources.Count);
                    foreach (PdfPageSource source in merge.Sources)
                    {
                        QpdfPath path = source.Input.Value == "." ? merge.Input : source.Input;
                        resolved.Add(new PdfPageSource(path, source.Selection,
                            !merge.EmptyInput && path == merge.Input ? transformationPassword : source.Password));
                    }

                    await SelectPagesCoreAsync(
                        host: host,
                        sources: resolved,
                        outputPath: merge.Output,
                        primaryInput: merge.Input,
                        pruneUnreachableFields: true,
                        limits: limits,
                        recovery: merge.Recovery,
                        settings: SelectionSettings.FromPlan(merge, pruneEnabled: true, password: transformationPassword),
                        collation: merge.Collation,
                        verbatimLabels: null,
                        splitPages: merge.SplitPages,
                        replaceInput: merge.ReplaceInput,
                        governor: governor,
                        diagnostics: diagnostics,
                        emptyPrimary: merge.EmptyInput,
                        preparedSource: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    return CompleteWithPresentation(governor);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return ExecutionOutcome.Failed(
                        [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)]);
                }
                catch (ArgumentException ex) when (ex.ParamName == "sources")
                {
                    return ExecutionOutcome.Failed([new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments,
                        QpdfDiagnosticSeverity.Error, "Repeated page sources must use the same credential value and representation.")]);
                }
            }

            SelectionPlan selection = (SelectionPlan)plan;
            if (selection.SplitPages.HasValue || selection.ReplaceInput)
            {
                try
                {
                    List<PdfPageSource> solo = new(1)
                    {
                        new PdfPageSource(selection.Input, new PdfPageSelectionOptions(selection.Page), transformationPassword),
                    };
                    QpdfPath target = selection.ReplaceInput ? selection.Input : selection.Output;
                    await SelectPagesCoreAsync(
                        host: host,
                        sources: solo,
                        outputPath: target,
                        primaryInput: selection.Input,
                        pruneUnreachableFields: true,
                        limits: limits,
                        recovery: selection.Recovery,
                        settings: SelectionSettings.FromPlan(selection, pruneEnabled: true, password: transformationPassword),
                        collation: null,
                        verbatimLabels: null,
                        splitPages: selection.SplitPages,
                        replaceInput: selection.ReplaceInput,
                        governor: governor,
                        diagnostics: diagnostics,
                        emptyPrimary: false,
                        preparedSource: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    return CompleteWithPresentation(governor);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return ExecutionOutcome.Failed(
                        [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)]);
                }
            }

            await SelectPageCoreAsync(
                host: host,
                inputPath: selection.Input,
                outputPath: selection.Output,
                options: new PdfPageSelectionOptions(selection.Page),
                limits: limits,
                recovery: selection.Recovery,
                settings: SelectionSettings.FromPlan(selection, pruneEnabled: true, password: transformationPassword),
                governor: governor,
                diagnostics: diagnostics,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return CompleteWithPresentation(governor);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ExecutionOutcome.Failed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)]);
        }
    }

    internal static void ValidateFirstPassInputs(JobPlan plan, ResourceGovernor governor, CancellationToken token)
    {
        if (!plan.Linearize || plan.LinearizePass1 is not QpdfPath pass1) { return; }
        bool empty = plan.EmptyDocument || plan is MergePlan { EmptyInput: true };
        if (!empty) { Check(plan.Input.Value); }
        if (plan is MergePlan merge)
        {
            foreach (PdfPageSource source in merge.Sources)
            {
                if (source.Input.Value == ".") { if (!empty) { Check(plan.Input.Value); } }
                else { Check(source.Input.Value); }
            }
        }
        foreach (OverlaySpec overlay in plan.Overlays) { Check(overlay.File); }
        foreach (OverlaySpec underlay in plan.Underlays) { Check(underlay.File); }
        foreach (AttachmentCopy copy in plan.AttachmentsToCopy) { Check(copy.File); }
        foreach (AttachmentAdd add in plan.AttachmentsToAdd) { Check(add.File); }

        void Check(string input)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (string.Equals(pass1.Value, input, StringComparison.Ordinal))
            { throw new QpdfParseException("The first-pass output must differ from every input file.", null, null); }
        }
    }

}
