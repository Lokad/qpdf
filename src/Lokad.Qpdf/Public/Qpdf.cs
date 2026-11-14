using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Operations;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Operations.AttachmentExecution;
using static Lokad.Qpdf.Operations.InspectionExecution;
using static Lokad.Qpdf.Operations.JobExecution;
using static Lokad.Qpdf.Operations.JsonExecution;
using static Lokad.Qpdf.Operations.MultiPageSelection;
using static Lokad.Qpdf.Runtime.ExecutionFailures;

namespace Lokad.Qpdf;

/// <summary>
/// PDF operations over the shared reader, transformations and writer. Inputs
/// and staged output transactions are supplied by the caller's host.
/// Every call builds a fresh per-job governor from the supplied or default
/// <see cref="QpdfLimits"/>; exhausted budgets fail explicitly.
/// Calls hold no shared mutable state and are safe to run concurrently against
/// distinct hosts. Cancellation is cooperative: operations returning typed domain
/// results throw <see cref="OperationCanceledException"/>. Execution entry points
/// returning <see cref="QpdfExecutionResult"/> report <see cref="QpdfExecutionStatus.Canceled"/>.
/// Failure or cancellation discards uncommitted staging. Outputs commit
/// independently; a later output or post-commit cleanup failure does not undo
/// an earlier publication. The host defines each transaction's guarantees.
/// </summary>
/// <remarks>
/// Derived PDFs retain stored signature appearances but disable active signature
/// field attributes and certification restrictions. They do not preserve source
/// signature validity. Read-only inspection retains the original values; this
/// library does not sign or validate signatures.
/// </remarks>
public static partial class Qpdf
    // Command adapters: argv and job JSON converge on the typed selection plan.
{
    /// <summary>
    /// Copies the selected pages into a standalone PDF with default budgets. Streams
    /// travel opaquely; inherited page attributes are materialized.
    /// The primary document's attachment collection is retained independently of
    /// the selected pages, including first/last preview selections.
    /// </summary>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
        CancellationToken cancellationToken)
    {
        return SelectPageAsync(host, inputPath, outputPath, options, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Copies the selected pages into a standalone PDF with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page exceeds the count.</exception>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
        QpdfLimits limits,
        CancellationToken cancellationToken)
        => SelectPageAsync(host, inputPath, outputPath, options, (QpdfPassword?)null, limits, cancellationToken);

    /// <summary>
    /// Copies the selected pages into a standalone PDF, rotating output
    /// pages, with default budgets.
    /// </summary>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
        IReadOnlyList<PdfPageRotation> rotations,
        CancellationToken cancellationToken)
    {
        return SelectPageAsync(host, inputPath, outputPath, options, rotations, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Copies the selected pages into a standalone PDF, rotating output
    /// pages, with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page exceeds the count.</exception>
    public static ValueTask<PdfPageSelectionResult> SelectPageAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
        IReadOnlyList<PdfPageRotation> rotations,
        QpdfLimits limits,
        CancellationToken cancellationToken)
        => SelectPageAsync(host, inputPath, outputPath, options, rotations, null, limits, cancellationToken);

    /// <summary>
    /// Copies pages from several inputs into one standalone PDF with default
    /// budgets. Each source keeps its own object identity: shared numbers across
    /// documents never collide, and every page keeps its own resources, so
    /// conflicting resource or field names stay inert without content rewriting.
    /// </summary>
    public static ValueTask<PdfMultiPageSelectionResult> SelectPagesAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        CancellationToken cancellationToken)
    {
        return SelectPagesAsync(host, sources, outputPath, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Copies pages from several inputs into one standalone PDF with explicit budgets.
    /// The first source supplies the retained document attachment collection;
    /// other sources contribute files reachable from their selected pages.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No page source was supplied.</exception>
    public static async ValueTask<PdfMultiPageSelectionResult> SelectPagesAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        if (sources.Count == 0) { throw new ArgumentException("At least one page source is required.", nameof(sources)); }

        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(PdfMultiPageSelectionResult.StorageBytes);
        try
        {
            using TypedSelectionPreparation preparation = TypedSelectionPreparation.Create(governor);
            preparation.FreezeSources(sources);
            DiagnosticBag diagnostics = new(governor);
            MultiPageSelectionOutcome result = (await SelectPagesCoreAsync(
                host: host,
                sources: preparation.Sources,
                outputPath: outputPath,
                primaryInput: preparation.Sources[0].Input,
                pruneUnreachableFields: true,
                limits: limits,
                recovery: QpdfRecoveryMode.Strict,
                settings: SelectionSettings.Typed with { AuthenticatePrimary = true },
                collation: null,
                verbatimLabels: null,
                splitPages: null,
                replaceInput: false,
                governor: governor,
                diagnostics: diagnostics,
                emptyPrimary: false,
                preparedSource: null,
                cancellationToken: cancellationToken).ConfigureAwait(false)).Result;
            preparation.Dispose();
            PdfMultiPageSelectionResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>
    /// Copies pages from several inputs into one standalone PDF, rotating output
    /// pages, with default budgets.
    /// </summary>
    public static ValueTask<PdfMultiPageSelectionResult> SelectPagesAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        IReadOnlyList<PdfPageRotation> rotations,
        CancellationToken cancellationToken)
    {
        return SelectPagesAsync(host, sources, outputPath, rotations, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Copies pages from several inputs into one standalone PDF, rotating output
    /// pages, with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No page source was supplied.</exception>
    public static async ValueTask<PdfMultiPageSelectionResult> SelectPagesAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        IReadOnlyList<PdfPageRotation> rotations,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ArgumentNullException.ThrowIfNull(rotations);
        if (sources.Count == 0) { throw new ArgumentException("At least one page source is required.", nameof(sources)); }

        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(PdfMultiPageSelectionResult.StorageBytes);
        try
        {
            using TypedSelectionPreparation preparation = TypedSelectionPreparation.Create(governor);
            preparation.FreezeSources(sources);
            preparation.ConvertRotations(rotations);
            DiagnosticBag diagnostics = new(governor);
            MultiPageSelectionOutcome result = (await SelectPagesCoreAsync(
                host: host,
                sources: preparation.Sources,
                outputPath: outputPath,
                primaryInput: preparation.Sources[0].Input,
                pruneUnreachableFields: true,
                limits: limits,
                recovery: QpdfRecoveryMode.Strict,
                settings: SelectionSettings.Typed with { Rotations = preparation.Rotations, AuthenticatePrimary = true },
                collation: null,
                verbatimLabels: null,
                splitPages: null,
                replaceInput: false,
                governor: governor,
                diagnostics: diagnostics,
                emptyPrimary: false,
                preparedSource: null,
                cancellationToken: cancellationToken).ConfigureAwait(false)).Result;
            preparation.Dispose();
            PdfMultiPageSelectionResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>
    /// Copies pages from several inputs into one standalone PDF with merge options
    /// and default budgets.
    /// </summary>
    public static ValueTask<PdfMultiPageSelectionResult> SelectPagesAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        PdfPageMergeOptions mergeOptions,
        CancellationToken cancellationToken)
    {
        return SelectPagesAsync(host, sources, outputPath, mergeOptions, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Copies pages from several inputs into one standalone PDF with merge options
    /// and explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No page source was supplied.</exception>
    public static async ValueTask<PdfMultiPageSelectionResult> SelectPagesAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        PdfPageMergeOptions mergeOptions,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        if (mergeOptions is null)
        {
            throw new ArgumentNullException(nameof(mergeOptions));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        if (mergeOptions.RecoveryMode is not (QpdfRecoveryMode.Strict or QpdfRecoveryMode.BoundedRepair))
        {
            throw new ArgumentOutOfRangeException(nameof(mergeOptions), mergeOptions.RecoveryMode, "Unknown recovery mode.");
        }
        ArgumentNullException.ThrowIfNull(mergeOptions.Rotations, "rotations");
        ArgumentNullException.ThrowIfNull(mergeOptions.Labels, "labels");
        ArgumentNullException.ThrowIfNull(mergeOptions.OutputEncryption, "outputEncryption");
        if (sources.Count == 0) { throw new ArgumentException("At least one page source is required.", nameof(sources)); }

        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(PdfMultiPageSelectionResult.StorageBytes);
        try
        {
            using TypedSelectionPreparation preparation = TypedSelectionPreparation.Create(governor);
            preparation.FreezeSources(sources);
            if (mergeOptions.Collation is not null
                && mergeOptions.Collation.GroupSizes.Count != 1
                && mergeOptions.Collation.GroupSizes.Count != preparation.Sources.Count)
            {
                throw new ArgumentException("Collation group sizes must hold one value or one per source.", nameof(mergeOptions));
            }

            if (mergeOptions.SplitPages.HasValue && mergeOptions.SplitPages.Value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(mergeOptions), "Split pages must be a positive group size.");
            }

            preparation.ConvertRotations(mergeOptions.Rotations);
            preparation.ConvertLabels(mergeOptions.Labels);
            DiagnosticBag diagnostics = new(governor);
            MultiPageSelectionOutcome result = (await SelectPagesCoreAsync(
                host: host,
                sources: preparation.Sources,
                outputPath: outputPath,
                primaryInput: preparation.Sources[0].Input,
                pruneUnreachableFields: true,
                limits: limits,
                recovery: mergeOptions.RecoveryMode,
                settings: SelectionSettings.Typed with { Rotations = preparation.Rotations, Labels = preparation.Labels,
                    AuthenticatePrimary = true, OutputEncryption = mergeOptions.OutputEncryption },
                collation: mergeOptions.Collation,
                verbatimLabels: null,
                splitPages: mergeOptions.SplitPages,
                replaceInput: false,
                governor: governor,
                diagnostics: diagnostics,
                emptyPrimary: false,
                preparedSource: null,
                cancellationToken: cancellationToken).ConfigureAwait(false)).Result;
            preparation.Dispose();
            PdfMultiPageSelectionResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>
    /// Inspects a document with default budgets. Skipped stages stay visible;
    /// this partial check never claims conformance, safe content, or rendering.
    /// </summary>
    public static ValueTask<QpdfCheckResult> CheckAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        CancellationToken cancellationToken)
    {
        return CheckAsync(host, inputPath, new QpdfCheckOptions(), QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Inspects a document with explicit options and budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A budget or the decode level is outside its supported range.</exception>
    public static async ValueTask<QpdfCheckResult> CheckAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfCheckOptions options,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        if (options.DecodeLevel is not (QpdfStreamDecodeLevel.None or QpdfStreamDecodeLevel.Generalized
            or QpdfStreamDecodeLevel.Specialized or QpdfStreamDecodeLevel.All))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.DecodeLevel, "Unknown stream decode level.");
        }
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfCheckResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            TypedCheckOutcome result = await CheckCoreAsync(host, inputPath, options, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            QpdfCheckResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>
    /// Lists the embedded files of a document with default budgets.
    /// </summary>
    public static ValueTask<IReadOnlyList<QpdfAttachmentInfo>> ListAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        CancellationToken cancellationToken)
    {
        return ListAttachmentsAsync(host, inputPath, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Lists the embedded files of a document with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">Host or limits are null.</exception>
    public static ValueTask<IReadOnlyList<QpdfAttachmentInfo>> ListAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfLimits limits,
        CancellationToken cancellationToken)
        => ListAttachmentsAsync(host, inputPath, null, limits, cancellationToken);

    /// <summary>Lists embedded files after authenticating the input, with explicit budgets.</summary>
    /// <remarks>A null password tries empty. Credentials are borrowed until completion.</remarks>
    public static async ValueTask<IReadOnlyList<QpdfAttachmentInfo>> ListAttachmentsAsync(IQpdfHost host,
        QpdfPath inputPath, QpdfPassword? password, QpdfLimits limits, CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        DiagnosticBag diagnostics = new(governor);
        try { return await ListAttachmentsCoreAsync(host, inputPath, password, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, true, cancellationToken).ConfigureAwait(false); }
        finally { governor.CompleteStorage(); }
    }

    /// <summary>
    /// Extracts one embedded file by key into an output path with default budgets.
    /// </summary>
    public static ValueTask<QpdfAttachmentExtraction> ExtractAttachmentAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        string key,
        QpdfPath outputPath,
        CancellationToken cancellationToken)
    {
        return ExtractAttachmentAsync(host, inputPath, key, outputPath, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Extracts one embedded file by key into an output path with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>An explicitly empty key identifies an existing empty name-tree key.</remarks>
    public static ValueTask<QpdfAttachmentExtraction> ExtractAttachmentAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        string key,
        QpdfPath outputPath,
        QpdfLimits limits,
        CancellationToken cancellationToken)
        => ExtractAttachmentAsync(host, inputPath, key, outputPath, null, limits, cancellationToken);

    /// <summary>Extracts a plaintext embedded file after authenticating the PDF, with explicit budgets.</summary>
    /// <remarks>A null password tries empty. Credentials are borrowed until completion.</remarks>
    public static async ValueTask<QpdfAttachmentExtraction> ExtractAttachmentAsync(IQpdfHost host,
        QpdfPath inputPath, string key, QpdfPath outputPath, QpdfPassword? password, QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfAttachmentExtraction.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            AttachmentExtractionOutcome result = await ExtractAttachmentCoreAsync(host, inputPath, key, outputPath, password, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            QpdfAttachmentExtraction presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>
    /// Inspects a document as JSON with default budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ValueTask<QpdfJsonResult> GetDocumentJsonAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        QpdfJsonOptions options,
        CancellationToken cancellationToken)
    {
        return GetDocumentJsonAsync(host, inputPath, outputPath, options, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Inspects a document as JSON with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async ValueTask<QpdfJsonResult> GetDocumentJsonAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        QpdfJsonOptions options,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfJsonResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            JsonInspectionOutcome result;
            using (ConfigurationText? textStorage = options.Objects.Count == 0 ? null : ConfigurationText.Create(governor))
            {
                HashSet<(bool Trailer, int Number, int Generation)>? wanted = ValidateJsonObjects(options.Objects, 0L, textStorage);
                Commands.JsonInspection inspection = new(options.Version, options.Keys, null, options.StreamData, options.StreamPrefix, wanted, options.MinimalOutput)
                { Password = options.Password };
                result = await JsonCoreAsync(host, inputPath, outputPath, inspection, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, false, true, cancellationToken).ConfigureAwait(false);
            }
            QpdfJsonResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>
    /// Creates a PDF document from qpdf JSON with default budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ValueTask<QpdfJsonImportResult> CreatePdfFromJsonAsync(
        IQpdfHost host,
        QpdfPath jsonPath,
        QpdfPath outputPath,
        CancellationToken cancellationToken)
    {
        return CreatePdfFromJsonAsync(host, jsonPath, outputPath, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Creates a PDF document from qpdf JSON with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ValueTask<QpdfJsonImportResult> CreatePdfFromJsonAsync(
        IQpdfHost host,
        QpdfPath jsonPath,
        QpdfPath outputPath,
        QpdfLimits limits,
        CancellationToken cancellationToken)
        => CreatePdfFromJsonAsync(host, jsonPath, outputPath, QpdfOutputEncryption.Preserve, limits, cancellationToken);

    /// <summary>Creates a PDF from plaintext qpdf JSON with explicit protection and budgets.</summary>
    /// <remarks>Preserve produces plaintext only when JSON has no /Encrypt declaration. An encrypted
    /// JSON export requires an explicit Remove, CreateAes256 or CopyFrom policy. Its values are plaintext;
    /// template protection uses an independently authenticated PDF.
    /// Credentials are borrowed until completion.</remarks>
    public static async ValueTask<QpdfJsonImportResult> CreatePdfFromJsonAsync(IQpdfHost host, QpdfPath jsonPath,
        QpdfPath outputPath, QpdfOutputEncryption outputEncryption, QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputEncryption);
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfJsonImportResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            JsonImportOutcome result = await ImportCreateCoreAsync(host, jsonPath, outputPath, null, null, false, false,
                outputEncryption, QpdfRecoveryMode.Strict, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            QpdfJsonImportResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>
    /// Updates a PDF document from qpdf JSON with default budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ValueTask<QpdfJsonImportResult> UpdatePdfFromJsonAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath jsonPath,
        QpdfPath outputPath,
        CancellationToken cancellationToken)
    {
        return UpdatePdfFromJsonAsync(host, inputPath, jsonPath, outputPath, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Updates a PDF document from qpdf JSON with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ValueTask<QpdfJsonImportResult> UpdatePdfFromJsonAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath jsonPath,
        QpdfPath outputPath,
        QpdfLimits limits,
        CancellationToken cancellationToken)
        => UpdatePdfFromJsonAsync(host, inputPath, jsonPath, outputPath, null, QpdfOutputEncryption.Preserve, limits, cancellationToken);

    /// <summary>Updates an authenticated PDF from qpdf JSON with an explicit output protection policy.</summary>
    /// <remarks>A null password tries empty. JSON object values and stream data are plaintext;
    /// preservation validates any repeated /Encrypt declaration against the authenticated input.
    /// Explicit output policies replace that declaration. Credentials are borrowed until completion.</remarks>
    public static ValueTask<QpdfJsonImportResult> UpdatePdfFromJsonAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath jsonPath, QpdfPath outputPath, QpdfPassword? password, QpdfOutputEncryption outputEncryption,
        CancellationToken cancellationToken)
        => UpdatePdfFromJsonAsync(host, inputPath, jsonPath, outputPath, password, outputEncryption, QpdfLimits.Default, cancellationToken);

    /// <summary>Updates an authenticated PDF from qpdf JSON with explicit budgets and output protection.</summary>
    public static async ValueTask<QpdfJsonImportResult> UpdatePdfFromJsonAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath jsonPath, QpdfPath outputPath, QpdfPassword? password, QpdfOutputEncryption outputEncryption,
        QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputEncryption);
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfJsonImportResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            JsonImportOutcome result = await ImportUpdateCoreAsync(host, inputPath, jsonPath, outputPath, null, null, false, false,
                password, outputEncryption, limits, QpdfRecoveryMode.Strict, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            QpdfJsonImportResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>
    /// Embeds host files into a document with default budgets.
    /// </summary>
    public static ValueTask<QpdfAttachmentEditResult> AddAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        IReadOnlyList<QpdfAttachmentSpec> attachments,
        CancellationToken cancellationToken)
    {
        return AddAttachmentsAsync(host, inputPath, outputPath, attachments, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Embeds host files into a document with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No attachment, an empty file, or an empty key or filename was supplied.</exception>
    public static ValueTask<QpdfAttachmentEditResult> AddAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        IReadOnlyList<QpdfAttachmentSpec> attachments,
        QpdfLimits limits,
        CancellationToken cancellationToken)
        => AddAttachmentsAsync(host, inputPath, outputPath, attachments, null, QpdfOutputEncryption.Preserve, limits, cancellationToken);

    /// <summary>Adds embedded files to an authenticated PDF with explicit output protection and budgets.</summary>
    /// <remarks>Credentials are borrowed until completion. A null input password tries empty.</remarks>
    public static ValueTask<QpdfAttachmentEditResult> AddAttachmentsAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, IReadOnlyList<QpdfAttachmentSpec> attachments, QpdfPassword? password,
        QpdfOutputEncryption outputEncryption, QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0) { throw new ArgumentException("At least one attachment is required.", nameof(attachments)); }
        return EditAttachmentsAsync(host, inputPath, outputPath, attachments, null, null, password, outputEncryption, limits, cancellationToken);
    }

    /// <summary>
    /// Removes embedded files by key with default budgets.
    /// </summary>
    public static ValueTask<QpdfAttachmentEditResult> RemoveAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        return RemoveAttachmentsAsync(host, inputPath, outputPath, keys, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Removes embedded files by key with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No key was supplied or a key entry is null.</exception>
    /// <remarks>Explicitly empty keys identify existing empty name-tree keys.</remarks>
    public static ValueTask<QpdfAttachmentEditResult> RemoveAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        IReadOnlyList<string> keys,
        QpdfLimits limits,
        CancellationToken cancellationToken)
        => RemoveAttachmentsAsync(host, inputPath, outputPath, keys, null, QpdfOutputEncryption.Preserve, limits, cancellationToken);

    /// <summary>Removes embedded files from an authenticated PDF with explicit output protection and budgets.</summary>
    /// <remarks>Credentials are borrowed until completion. A null input password tries empty.</remarks>
    public static ValueTask<QpdfAttachmentEditResult> RemoveAttachmentsAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, IReadOnlyList<string> keys, QpdfPassword? password,
        QpdfOutputEncryption outputEncryption, QpdfLimits limits, CancellationToken cancellationToken)
    {
        if (keys is null)
        {
            throw new ArgumentNullException(nameof(keys));
        }

        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one attachment key is required.", nameof(keys));
        }

        return EditAttachmentsAsync(host, inputPath, outputPath, null, keys, null, password, outputEncryption, limits, cancellationToken);
    }

    /// <summary>
    /// Copies embedded files from other documents with default budgets.
    /// </summary>
    public static ValueTask<QpdfAttachmentEditResult> CopyAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        IReadOnlyList<QpdfAttachmentCopy> sources,
        CancellationToken cancellationToken)
    {
        return CopyAttachmentsAsync(host, inputPath, outputPath, sources, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Copies embedded files from other documents with explicit budgets.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No source was supplied.</exception>
    public static ValueTask<QpdfAttachmentEditResult> CopyAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        IReadOnlyList<QpdfAttachmentCopy> sources,
        QpdfLimits limits,
        CancellationToken cancellationToken)
        => CopyAttachmentsAsync(host, inputPath, outputPath, sources, null, QpdfOutputEncryption.Preserve, limits, cancellationToken);

    /// <summary>Copies embedded files into an authenticated PDF with explicit output protection and budgets.</summary>
    /// <remarks>Each source keeps its own credential. Credentials are borrowed until completion; a null primary password tries empty.</remarks>
    public static ValueTask<QpdfAttachmentEditResult> CopyAttachmentsAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, IReadOnlyList<QpdfAttachmentCopy> sources, QpdfPassword? password,
        QpdfOutputEncryption outputEncryption, QpdfLimits limits, CancellationToken cancellationToken)
    {
        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one attachment source is required.", nameof(sources));
        }

        return EditAttachmentsAsync(host, inputPath, outputPath, null, null, sources, password, outputEncryption, limits, cancellationToken);
    }

    /// <summary>Executes a command invocation with default budgets.</summary>
    public static ValueTask<QpdfExecutionResult> ExecuteAsync(
        IQpdfHost host,
        QpdfCommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(host, invocation, new QpdfExecutionOptions(), cancellationToken);
    }

    /// <summary>Executes a command invocation with explicit budgets.</summary>
    /// <exception cref="ArgumentNullException">Host, invocation, or options are null.</exception>
    public static async ValueTask<QpdfExecutionResult> ExecuteAsync(
        IQpdfHost host,
        QpdfCommandInvocation invocation,
        QpdfExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (invocation is null)
        {
            throw new ArgumentNullException(nameof(invocation));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options.Limits.Validate();
        if (ExecutionResultAdmission.Reject(options.Limits, cancellationToken) is QpdfExecutionResult rejection) { return rejection; }
        ResourceGovernor governor = new(options.Limits, cancellationToken, host);
        if (!governor.TryExpectExecutionResult()) { return ExecutionResultAdmission.SharedMemoryFailure; }
        try
        {
            DiagnosticBag diagnostics = new(governor);
            ExecutionOutcome result;
            ExpandedArguments? expanded = null;
            try
            {
                expanded = await ResponseFileExpander.ExpandAsync(host, invocation.Arguments, options.Limits, governor, cancellationToken).ConfigureAwait(false);
                result = await RunExpandedInvocationAsync(host, expanded.Values, options, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            catch (QpdfException ex)
            {
                result = ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                result = ExecutionOutcome.Canceled([]);
            }
            finally
            {
                expanded?.Dispose();
                expanded = null;
            }

            return governor.FinishExecutionResult(result);
        }
        finally { governor.AbandonExecutionResult(); }
    }

    /// <summary>Executes a job-JSON invocation with default budgets.</summary>
    public static ValueTask<QpdfExecutionResult> ExecuteJobAsync(
        IQpdfHost host,
        string jobJson,
        CancellationToken cancellationToken)
    {
        return ExecuteJobAsync(host, jobJson, new QpdfExecutionOptions(), cancellationToken);
    }

    /// <summary>Executes a job-JSON invocation with explicit budgets.</summary>
    /// <exception cref="ArgumentNullException">Host, JSON, or options are null.</exception>
    public static async ValueTask<QpdfExecutionResult> ExecuteJobAsync(
        IQpdfHost host,
        string jobJson,
        QpdfExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (jobJson is null)
        {

            throw new ArgumentNullException(nameof(jobJson));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options.Limits.Validate();
        if (ExecutionResultAdmission.Reject(options.Limits, cancellationToken) is QpdfExecutionResult rejection) { return rejection; }
        ResourceGovernor governor = new(options.Limits, cancellationToken, host);
        if (!governor.TryExpectExecutionResult()) { return ExecutionResultAdmission.SharedMemoryFailure; }
        try
        {
            DiagnosticBag diagnostics = new(governor);
            JobJsonLoader.LoadedJob loaded;
            try
            {
                loaded = await JobJsonLoader.ParseAsync(host, jobJson, options.Limits, governor, cancellationToken).ConfigureAwait(false);
            }
            catch (QpdfException ex)
            {
                ExecutionOutcome failure = ToFailedResult(ex);
                return governor.FinishExecutionResult(failure);
            }
            catch (OperationCanceledException)
            {
                return governor.FinishExecutionResult(ExecutionOutcome.Canceled([]));
            }
            return governor.FinishExecutionResult(await RunLoadedJobAsync(host, loaded, options, governor, diagnostics, cancellationToken).ConfigureAwait(false));
        }
        finally { governor.AbandonExecutionResult(); }
    }

    /// <summary>
    /// Repairs an edited QDF file with default budgets, recomputing stream
    /// lengths, object-stream tables, cross-references, and startxref offsets.
    /// </summary>
    /// <exception cref="ArgumentNullException">Host or limits are null.</exception>
    public static ValueTask<QpdfExecutionResult> FixQdfAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        CancellationToken cancellationToken)
    {
        return FixQdfAsync(host, inputPath, outputPath, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Repairs an edited QDF file with explicit budgets. Both paths are
    /// explicit host paths; there is no standard input or output default,
    /// and the repaired bytes publish through a staged transaction.
    /// </summary>
    /// <remarks>Inconsistent stream/object/length framing fails before output commit.</remarks>
    /// <exception cref="ArgumentNullException">Host or limits are null.</exception>
    public static async ValueTask<QpdfExecutionResult> FixQdfAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        if (ExecutionResultAdmission.Reject(limits, cancellationToken) is QpdfExecutionResult rejection) { return rejection; }
        ResourceGovernor governor = new(limits, cancellationToken, host);
        if (!governor.TryExpectExecutionResult()) { return ExecutionResultAdmission.SharedMemoryFailure; }
        try
        {
            ExecutionOutcome result;
            try
            {
                using ParserMemoryScope working = ParserMemoryScope.Create(governor);
                IQpdfInput input = await GovernedHost.OpenInputAsync(host, inputPath, governor, cancellationToken).ConfigureAwait(false);
                byte[] bytes;
                try
                {
                    if (input.Length > limits.MaxInputBytes)
                    {
                        throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, limits.MaxInputBytes);
                    }

                    if (input.Length > int.MaxValue)
                    {
                        throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, int.MaxValue);
                    }

                    working.Reserve(checked(input.Length + GovernedBufferStream.ArrayOverheadBytes));
                    bytes = await QpdfInputReader.ReadAdmittedAsync(input, 0L, (int)input.Length, governor, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await input.DisposeAsync().ConfigureAwait(false);
                }

                await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, outputPath, governor, cancellationToken).ConfigureAwait(false);
                TransactionSink sink = new(transaction, governor);
                try
                {
                    QdfRepair repair = QdfRepair.Create(governor, sink, working);
                    await repair.RepairAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                catch (OverflowException ex)
                {
                    throw new QpdfParseException("QDF repair met an unrepresentable offset.", null, null, ex);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = ExecutionOutcome.Completed([]);
            }
            catch (QpdfException ex)
            {
                result = ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                result = ExecutionOutcome.Canceled([]);
            }
            return governor.FinishExecutionResult(result);
        }
        finally { governor.AbandonExecutionResult(); }
    }

    /// <summary>
    /// Runs the zlib-flate companion with default budgets: compresses raw
    /// bytes or uncompresses zlib-wrapped bytes between explicit host paths.
    /// </summary>
    /// <exception cref="ArgumentNullException">Host or options are null.</exception>
    public static ValueTask<QpdfExecutionResult> RunZlibFlateAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        QpdfZlibFlateOptions options,
        CancellationToken cancellationToken)
    {
        return RunZlibFlateAsync(host, inputPath, outputPath, options, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>
    /// Runs the zlib-flate companion with explicit budgets. Compression
    /// levels follow the governed buckets 1..9 and are only meaningful when
    /// compressing; anything else fails explicitly without publishing output.
    /// </summary>
    /// <exception cref="ArgumentNullException">Host, options, or limits are null.</exception>
    public static async ValueTask<QpdfExecutionResult> RunZlibFlateAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        QpdfZlibFlateOptions options,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        if (options.Mode != QpdfZlibFlateMode.Uncompress && options.Mode != QpdfZlibFlateMode.Compress)
        { return ExecutionResultAdmission.InvalidZlibMode; }
        if (options.CompressionLevel.HasValue && (options.Mode != QpdfZlibFlateMode.Compress || options.CompressionLevel.Value < 1 || options.CompressionLevel.Value > 9))
        { return ExecutionResultAdmission.InvalidZlibLevel; }
        if (ExecutionResultAdmission.Reject(limits, cancellationToken) is QpdfExecutionResult rejection)
        { return rejection; }
        ResourceGovernor governor = new(limits, cancellationToken, host);
        if (!governor.TryExpectExecutionResult()) { return ExecutionResultAdmission.SharedMemoryFailure; }
        try
        {
            ExecutionOutcome result;
            try
            {
                using ParserMemoryScope working = ParserMemoryScope.Create(governor);
                IQpdfInput input = await GovernedHost.OpenInputAsync(host, inputPath, governor, cancellationToken).ConfigureAwait(false);
                byte[] bytes;
                try
                {
                    if (input.Length > limits.MaxInputBytes)
                    {
                        throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, limits.MaxInputBytes);
                    }

                    if (input.Length > int.MaxValue)
                    {
                        throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, int.MaxValue);
                    }

                    working.Reserve(checked(input.Length + GovernedBufferStream.ArrayOverheadBytes));
                    bytes = await QpdfInputReader.ReadAdmittedAsync(input, 0L, (int)input.Length, governor, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await input.DisposeAsync().ConfigureAwait(false);
                }

                byte[] output = options.Mode == QpdfZlibFlateMode.Compress
                    ? FlateFilter.Encode(bytes, options.CompressionLevel, limits, governor)
                    : DecodeZlibFlate(bytes, limits, governor);
                working.AdoptReserved(output.Length == 0 ? 0L : checked(output.LongLength + GovernedBufferStream.ArrayOverheadBytes));
                if (output.Length > limits.MaxOutputBytes)
                {
                    throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), output.Length, limits.MaxOutputBytes);
                }

                await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, outputPath, governor, cancellationToken).ConfigureAwait(false);
                TransactionSink sink = new(transaction, governor);
                await sink.AppendAsync(output, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = ExecutionOutcome.Completed([]);
            }
            catch (QpdfException ex)
            {
                result = ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                result = ExecutionOutcome.Canceled([]);
            }
            return governor.FinishExecutionResult(result);
        }
        finally { governor.AbandonExecutionResult(); }
    }

    private static byte[] DecodeZlibFlate(byte[] bytes, QpdfLimits limits, ResourceGovernor governor)
    {
        try
        {
            return FlateFilter.Decode(bytes, null, limits, governor);
        }
        catch (QpdfFilterException ex)
        {
            throw new QpdfParseException("The zlib-flate input cannot be decoded.", null, null, ex);
        }
    }

}
