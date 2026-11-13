using Lokad.Qpdf.Writing;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;
using static Lokad.Qpdf.Operations.InspectionExecution;
using static Lokad.Qpdf.Operations.MultiPageSelection;

namespace Lokad.Qpdf.Operations;

internal static class AttachmentExecution
{
    internal static IReadOnlyList<AttachmentAdd> ToAttachmentAdds(IReadOnlyList<QpdfAttachmentSpec> attachments,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        // Exact cardinality funds the list backing and all projected records in
        // one request; cancellation is independent of this capacity admission.
        int count = attachments.Count;
        memory.Reserve(checked(64L + 88L * count));
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        List<AttachmentAdd> adds = new(count);
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            QpdfAttachmentSpec spec = attachments[index];
            if (spec is null) { throw new ArgumentException("Attachments must not contain null entries.", nameof(attachments)); }
            if (spec.File.Length == 0) { throw new ArgumentException("Attachment files must not be empty.", nameof(attachments)); }
            if (spec.Key is not null && spec.Key.Length == 0) { throw new ArgumentException("Attachment keys must not be empty.", nameof(attachments)); }
            if (spec.FileName is not null && spec.FileName.Length == 0) { throw new ArgumentException("Attachment filenames must not be empty.", nameof(attachments)); }
            adds.Add(new AttachmentAdd(spec.File, spec.Key, spec.FileName,
                string.IsNullOrEmpty(spec.CreationDate) ? null : spec.CreationDate,
                string.IsNullOrEmpty(spec.ModDate) ? null : spec.ModDate,
                string.IsNullOrEmpty(spec.MimeType) ? null : spec.MimeType,
                string.IsNullOrEmpty(spec.Description) ? null : spec.Description, spec.Replace));
        }
        return adds;
    }

    internal static async ValueTask<QpdfAttachmentEditResult> EditAttachmentsAsync(
        IQpdfHost host, QpdfPath inputPath, QpdfPath outputPath,
        IReadOnlyList<QpdfAttachmentSpec>? attachments, IReadOnlyList<string>? keys, IReadOnlyList<QpdfAttachmentCopy>? sources,
        QpdfPassword? password, QpdfOutputEncryption outputEncryption,
        QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputEncryption);
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(limits); limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        try
        {
            governor.PrepareTypedResult(QpdfAttachmentEditResult.StorageBytes);
            MultiPageSelectionOutcome result; int attachmentCount;
            using (ParserMemoryScope requests = ParserMemoryScope.Create(governor))
            {
                requests.Reserve(1024L); // Bounded plan/configuration metadata.
                IReadOnlyList<AttachmentAdd> adds = attachments is null ? [] : ToAttachmentAdds(attachments, requests, governor, cancellationToken);
                List<string>? removes = null;
                if (keys is not null)
                {
                    int count = keys.Count;
                    requests.Reserve(checked(64L + IntPtr.Size * (long)count));
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    removes = new(count);
                    for (int index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        string key = keys[index];
                        if (key is null) { throw new ArgumentException("Attachment keys must not contain null entries.", nameof(keys)); }
                        removes.Add(key);
                    }
                }
                List<AttachmentCopy>? copies = null;
                if (sources is not null)
                {
                    int count = sources.Count;
                    requests.Reserve(checked(64L + 56L * count));
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    copies = new(count);
                    for (int index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        QpdfAttachmentCopy source = sources[index];
                        if (source is null) { throw new ArgumentException("Attachment sources must not contain null entries.", nameof(sources)); }
                        copies.Add(new AttachmentCopy(source.Input.Value, source.Prefix) { Password = source.Password });
                    }
                }
                WholeFilePlan plan = new(inputPath, outputPath, QpdfRecoveryMode.Strict)
                {
                    OutputEncryption = outputEncryption,
                    AttachmentsToAdd = adds, AttachmentsToRemove = removes is null ? [] : removes,
                    AttachmentsToCopy = copies is null ? [] : copies,
                };
                DiagnosticBag diagnostics = new(governor);
                (result, attachmentCount) = await WholeFileCoreAsync(host, plan, password, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            QpdfAttachmentEditResult edited = new(attachmentCount, result.Warnings, governor.TypedCounters, true);
            governor.AcceptTypedResult(); governor.FinishTypedCounters(); return edited;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    internal static async ValueTask<AttachmentExtractionOutcome> ExtractAttachmentCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        string key,
        QpdfPath outputPath,
        QpdfPassword? password,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        bool emptyDocument,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        if (string.Equals(inputPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        await using QpdfDocument document = emptyDocument
            ? await QpdfDocument.OpenEmptyDocument(inputPath, limits, governor, diagnostics, recovery, cancellationToken, true).ConfigureAwait(false)
            : await QpdfDocument.OpenInspectionCore(host, inputPath, password, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        using ParserMemoryScope attachmentMemory = ParserMemoryScope.Create(governor);
        ReadOnlyMemory<byte> data = EmbeddedFiles.ExtractAttachment(document.Engine, key, limits, governor, diagnostics, attachmentMemory, cancellationToken);
        if (data.Length > limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), data.Length, limits.MaxOutputBytes);
        }

        await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, outputPath, governor, cancellationToken).ConfigureAwait(false);
        TransactionSink sink = new(transaction, governor);
        await sink.AppendAsync(data, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AttachmentExtractionOutcome(key, data.Length, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
    }

    internal static async ValueTask<IReadOnlyList<QpdfAttachmentInfo>> ListAttachmentsCoreAsync(
        IQpdfHost host, QpdfPath inputPath, QpdfPassword? password, QpdfLimits limits, QpdfRecoveryMode recovery, bool emptyDocument,
        ResourceGovernor governor, DiagnosticBag diagnostics, bool returnMetadata, CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using ParserMemoryScope? results = returnMetadata ? ParserMemoryScope.Create(governor) : null;
        await using QpdfDocument document = emptyDocument
            ? await QpdfDocument.OpenEmptyDocument(inputPath, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false)
            : await QpdfDocument.OpenInspectionCore(host, inputPath, password, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
        IReadOnlyList<QpdfAttachmentInfo> infos = ListAttachments(document, inputPath, limits, governor, diagnostics,
            memory, results, cancellationToken);
        await document.DisposeAsync().ConfigureAwait(false);
        if (results is not null) { results.RetainCallerStorage(results.ReservedBytes); }
        return infos;
    }

}
