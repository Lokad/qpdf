using System.Security.Cryptography;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    private static IReadOnlyList<QpdfAttachmentInfo> ListAttachments(
        QpdfDocument document,
        QpdfPath inputPath,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(document.Engine, limits, governor, diagnostics, cancellationToken);
        List<QpdfAttachmentInfo> infos = new(found.Count);
        foreach (EmbeddedAttachment attachment in found)
        {
            infos.Add(new QpdfAttachmentInfo(attachment.Key, attachment.FileName, attachment.Description, attachment.MimeType, attachment.CreationDate, attachment.ModDate, attachment.Checksum, attachment.StreamObjectNumber, attachment.Size));
        }

        if (infos.Count == 0)
        {
            diagnostics.Add(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, $"{inputPath.Value} has no embedded files");
            return infos;
        }

        foreach (QpdfAttachmentInfo info in infos)
        {
            int generation = 0;
            if (info.StreamObjectNumber >= 0
                && document.Engine.XRefEntries.TryGetValue(info.StreamObjectNumber, out XrefEntry entry)
                && entry.Kind == XrefEntryKind.Uncompressed)
            {
                generation = entry.Second;
            }

            diagnostics.Add(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, $"{info.Key} -> {info.StreamObjectNumber},{generation}");
            if (info.FileName.Length > 0)
            {
                diagnostics.Add(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, $"  filename: {info.FileName}");
            }

            if (info.Description.Length > 0)
            {
                diagnostics.Add(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, $"  description: {info.Description}");
            }

            if (info.MimeType.Length > 0)
            {
                diagnostics.Add(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, $"  mime type: {info.MimeType}");
            }

            if (info.CreationDate.Length > 0)
            {
                diagnostics.Add(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, $"  creation date: {info.CreationDate}");
            }

            if (info.ModDate.Length > 0)
            {
                diagnostics.Add(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, $"  modification date: {info.ModDate}");
            }

            if (info.Checksum.Length > 0)
            {
                diagnostics.Add(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, $"  checksum: {info.Checksum}");
            }

            if (info.Size >= 0L)
            {
                diagnostics.Add(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, $"  size: {info.Size}");
            }
        }

        return infos;
    }

    private static void InspectLinearization(
        QpdfDocument document,
        QpdfPath inputPath,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        LinearizationInspection inspection = LinearizationCheck.Inspect(document.Engine, document.PageCount, limits, governor, cancellationToken);
        if (inspection.Status == LinearizationStatus.NotLinearized)
        {
            diagnostics.Add(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, $"{inputPath.Value} is not linearized");
            return;
        }

        if (inspection.Status == LinearizationStatus.Valid)
        {
            diagnostics.Add(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, $"{inputPath.Value}: no linearization errors");
            return;
        }

        diagnostics.Add(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, $"Check 'linearization' failed: {inspection.Detail}");
    }

    private static void ShowLinearization(
        QpdfDocument document,
        QpdfPath inputPath,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        LinearizationData? data;
        try
        {
            data = LinearizationCheck.TryReadData(document.Engine, document.PageCount, limits, governor, cancellationToken);
        }
        catch (QpdfException ex)
        {
            diagnostics.Add(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, $"{inputPath.Value}: error encountered while checking linearization data: {ex.Message}");
            return;
        }

        if (data is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, $"{inputPath.Value} is not linearized");
            return;
        }

        ClassifiedGraph graph;
        try
        {
            System.Collections.Generic.HashSet<int> excluded = new() { data.LindictObject, data.HintObject };
            graph = LinearizationReport.ClassifyDocument(document.Engine, excluded, governor, cancellationToken);
        }
        catch (QpdfException ex)
        {
            diagnostics.Add(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, ex.Message);
            return;
        }

        foreach (string line in LinearizationReport.Build(inputPath.Value, data, graph))
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, line);
        }
    }

    private static async ValueTask<byte[]?> ShowObjectAsync(
        IQpdfHost host,
        QpdfDocument document,
        ShowObjectPlan showObject,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        (bool trailer, int number, int generation) = SelectionPlan.ParseInspectionSelector(showObject.Selector, 0);
        if (!trailer && number <= 0)
        {
            if (showObject.Output is not null) { throw new QpdfParseException("The output file is only used for stream data.", 0L, null); }
            return null;
        }
        QpdfObject? target = null;
        if (trailer)
        {
            target = document.Engine.Trailer;
        }
        else if (generation >= 0 && ((IIndirectResolver)document.Engine).TryResolve(new PdfReference(number, generation), out QpdfObject? resolved))
        {
            target = resolved;
        }

        bool wantsStreamData = showObject.RawStreamData || showObject.FilteredStreamData;
        if (target is PdfStream dataStream && wantsStreamData)
        {
            if (showObject.RejectStreamAfterInfo)
            {
                throw new QpdfException("Binary stream output cannot follow informational output in this inspection.");
            }
            QpdfPath? output = showObject.Output;
            if (output.HasValue && string.Equals(showObject.Input.Value, output.Value.Value, StringComparison.Ordinal))
            {
                throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
            }

            byte[] payload = dataStream.Data;
            if (showObject.FilteredStreamData)
            {
                using FilterPlan stages = FilterChain.Resolve(dataStream.Dictionary, (IIndirectResolver)document.Engine, governor, cancellationToken);
                payload = FilterChain.Decode(dataStream.Data, stages, limits, governor);
                stages.Dispose();
                if (showObject.NormalizeContent)
                {
                    payload = Content.ContentTransforms.NormalizeContentBytes(payload, limits, governor,
                        cancellationToken, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes));
                }
            }

            if (payload.Length > limits.MaxOutputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), payload.Length, limits.MaxOutputBytes);
            }

            if (!output.HasValue) { return payload; }
            await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, output.Value, governor, cancellationToken).ConfigureAwait(false);
            TransactionSink sink = new(transaction, governor);
            await sink.AppendAsync(payload, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (showObject.Output is not null)
        {
            throw new QpdfParseException("The output file is only used for stream data.", 0L, null);
        }

        if (target is null || target is PdfNull)
        {
            diagnostics.Add(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, "null");
        }
        else if (target is PdfStream stream)
        {
            diagnostics.Add(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, "Object is stream.  Dictionary:");
            CanonicalDisplay.AddDiagnostic(stream.Dictionary, limits, governor, document.Engine, diagnostics);
        }
        else
        {
            CanonicalDisplay.AddDiagnostic(target, limits, governor, document.Engine, diagnostics);
        }

        return null;
    }

    private static void ShowXref(QpdfDocument document, JobPlan plan, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<int> numbers = ParserContainers.CreateList<int>(memory);
        foreach (int number in document.Engine.XRefEntries.Keys)
        {
            governor.CheckCancellation();
            governor.ChargeWork(1L);
            ParserContainers.AddItem(memory, numbers, number, sizeof(int));
        }
        try
        {
            numbers.Sort((left, right) =>
            {
                governor.CheckCancellation();
                governor.ChargeWork(1L);
                return left.CompareTo(right);
            });
        }
        catch (InvalidOperationException ex) when (ex.InnerException is QpdfException or OperationCanceledException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
        }
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            Reading.XrefEntry entry = document.Engine.XRefEntries[number];
            if (entry.Kind == Reading.XrefEntryKind.Free)
            {
                continue;
            }

            string line = entry.Kind == Reading.XrefEntryKind.Compressed
                ? $"{number}/0: compressed; stream = {entry.First}, index = {entry.Second}"
                : $"{number}/{entry.Second}: uncompressed; offset = {entry.First}";
            diagnostics.Add(QpdfDiagnosticCode.XrefListing, QpdfDiagnosticSeverity.Info, line);
        }
    }

    private static void ShowPages(QpdfDocument document, JobPlan plan, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        int index = 1;
        foreach (Reading.PageLocation location in document.PageReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (location.Source is null)
            {
                throw new QpdfException("Page dictionary is not an indirect object.");
            }

            diagnostics.Add(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, $"page {index}: {location.Source.ObjectNumber} {location.Source.Generation} R");
            if (plan.WithImages)
            {
                List<PageImage> images = ListPageImages(document.Engine, location, limits, governor, diagnostics, cancellationToken);
                if (images.Count > 0)
                {
                    diagnostics.Add(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, "  images:");
                    foreach (PageImage image in images)
                    {
                        diagnostics.Add(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, $"    /{image.Name}: {image.Number} {image.Generation} R, {image.Width} x {image.Height}");
                    }
                }
            }

            diagnostics.Add(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, "  content:");
            using ParserMemoryScope contentMemory = ParserMemoryScope.Create(governor);
            foreach (PdfReference content in PageContentRefs(location.Page, governor, contentMemory))
            {
                governor.ChargeWork(1L);
                diagnostics.Add(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, $"    {content.ObjectNumber} {content.Generation} R");
            }

            index = checked(index + 1);
        }
    }
}
