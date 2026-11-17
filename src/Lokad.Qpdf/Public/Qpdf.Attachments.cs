using Lokad.Qpdf.Operations;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    /// <summary>Lists embedded files with an input credential, explicit recovery and budgets, retaining reader warnings.</summary>
    /// <remarks>A null password tries empty. Metadata and warnings remain valid after input cleanup.</remarks>
    public static async ValueTask<QpdfAttachmentListResult> ListAttachmentsAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPassword? password, QpdfRecoveryMode recovery, QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(limits);
        if (recovery is not (QpdfRecoveryMode.Strict or QpdfRecoveryMode.BoundedRepair))
        { throw new ArgumentOutOfRangeException(nameof(recovery)); }
        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfAttachmentListResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            IReadOnlyList<QpdfAttachmentInfo> attachments = await AttachmentExecution.ListAttachmentsCoreAsync(host,
                inputPath, password, limits, recovery, false, governor, diagnostics, true, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<QpdfDiagnostic> warnings;
            using (OwnedAllocationBatch storage = OwnedAllocationBatch.Create(governor, cancellationToken))
            {
                warnings = ResultCollectionStorage.Copy(storage, diagnostics.Entries, IntPtr.Size);
                storage.RetainResult();
            }
            QpdfAttachmentListResult result = new(attachments, warnings, governor.TypedCounters);
            governor.AcceptTypedResult(); governor.FinishTypedCounters(); return result;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>Extracts an embedded file with an input credential, explicit recovery and budgets.</summary>
    /// <remarks>A null password tries empty. An explicitly empty key identifies an existing empty name-tree key.</remarks>
    public static async ValueTask<QpdfAttachmentExtraction> ExtractAttachmentAsync(IQpdfHost host, QpdfPath inputPath,
        string key, QpdfPath outputPath, QpdfPassword? password, QpdfRecoveryMode recovery, QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(limits);
        if (recovery is not (QpdfRecoveryMode.Strict or QpdfRecoveryMode.BoundedRepair))
        { throw new ArgumentOutOfRangeException(nameof(recovery)); }
        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfAttachmentExtraction.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            AttachmentExtractionOutcome result = await AttachmentExecution.ExtractAttachmentCoreAsync(host,
                inputPath, key, outputPath, password, limits, recovery, false, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            QpdfAttachmentExtraction presented = result.Present(governor);
            governor.FinishTypedCounters(); return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>Adds embedded files with explicit credentials, output protection, recovery and budgets.</summary>
    /// <remarks>Recovery applies to the input and any encryption template. Credentials are borrowed until completion.</remarks>
    public static ValueTask<QpdfAttachmentEditResult> AddAttachmentsAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, IReadOnlyList<QpdfAttachmentSpec> attachments, QpdfPassword? password,
        QpdfOutputEncryption outputEncryption, QpdfRecoveryMode recovery, QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0) { throw new ArgumentException("At least one attachment is required.", nameof(attachments)); }
        return AttachmentExecution.EditAttachmentsAsync(host, inputPath, outputPath, attachments, null, null,
            password, outputEncryption, recovery, limits, cancellationToken);
    }

    /// <summary>Removes embedded files with explicit credentials, output protection, recovery and budgets.</summary>
    /// <remarks>Recovery applies to the input and any encryption template. Credentials are borrowed until completion.</remarks>
    public static ValueTask<QpdfAttachmentEditResult> RemoveAttachmentsAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, IReadOnlyList<string> keys, QpdfPassword? password,
        QpdfOutputEncryption outputEncryption, QpdfRecoveryMode recovery, QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) { throw new ArgumentException("At least one attachment key is required.", nameof(keys)); }
        return AttachmentExecution.EditAttachmentsAsync(host, inputPath, outputPath, null, keys, null,
            password, outputEncryption, recovery, limits, cancellationToken);
    }

    /// <summary>Copies embedded files with explicit credentials, output protection, recovery and budgets.</summary>
    /// <remarks>Recovery applies to the primary, copy sources and any encryption template. Each source retains its own credential.</remarks>
    public static ValueTask<QpdfAttachmentEditResult> CopyAttachmentsAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath outputPath, IReadOnlyList<QpdfAttachmentCopy> sources, QpdfPassword? password,
        QpdfOutputEncryption outputEncryption, QpdfRecoveryMode recovery, QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) { throw new ArgumentException("At least one attachment source is required.", nameof(sources)); }
        return AttachmentExecution.EditAttachmentsAsync(host, inputPath, outputPath, null, null, sources,
            password, outputEncryption, recovery, limits, cancellationToken);
    }
}
