using System.Runtime.ExceptionServices;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Operations.InspectionGraphPreparation;
using static Lokad.Qpdf.Operations.PageAssembly;
using static Lokad.Qpdf.Runtime.ExecutionFailures;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;

namespace Lokad.Qpdf.Operations;

internal static class InspectionExecution
{
    internal readonly record struct PageImage(PdfName Name, int Number, int Generation, long Width, long Height);

    internal static List<PageImage> ListPageImages(
        IIndirectResolver resolver,
        PageLocation location,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ParserMemoryScope memory,
        PageImageBindings.Cache? imageCache)
    {
        memory.CheckOwner(governor);
        List<PageImage> images = ParserContainers.CreateList<PageImage>(memory);
        PageImageBindings bindings = PageImageBindings.Create(resolver, location, limits, memory, governor, cancellationToken, imageCache);
        while (bindings.TryReadNext(out PageImageBindings.Binding image))
        {
            PdfReference reference = image.Reference;
            PdfStream stream = image.Stream;
            long width = ImageDimension(stream.Dictionary, "Width", reference, resolver, diagnostics, governor, cancellationToken);
            long height = ImageDimension(stream.Dictionary, "Height", reference, resolver, diagnostics, governor, cancellationToken);
            if (images.Count == images.Capacity) { governor.NoteOperation(); }
            ParserContainers.AddItem(memory, images, new PageImage(image.Name, reference.ObjectNumber, reference.Generation, width, height), 32L);
        }

        return images;
    }

    internal static long ImageDimension(
        PdfDictionary image,
        string key,
        PdfReference reference,
        IIndirectResolver resolver,
        DiagnosticBag diagnostics,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        if (image.TryGet(PdfName.FromAscii(key), out QpdfObject raw) && raw is not PdfNull)
        {
            if (raw is PdfInteger direct)
            {
                return direct.Value;
            }

            if (raw is PdfReference indirect
                && resolver.TryResolve(indirect, out QpdfObject? resolved)
                && resolved is PdfInteger target)
            {
                return target.Value;
            }
        }

        diagnostics.AddParts(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, ["Image ", reference.ObjectNumber, " is missing /", key, "; using 0."]);
        return 0L;
    }

    internal static async ValueTask<TypedCheckOutcome> CheckCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfCheckOptions options,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        // Native checking traverses pages without warning about their declared count.
        // The sink belongs to this job, including any following linearization inspection.
        diagnostics.ReportPageCountMismatch = false;
        List<QpdfCheckOutcome> checks;
        int pageCount;
        QpdfDocument.VersionParts versionParts;
        await using (QpdfDocument document = await QpdfDocument.OpenInspectionCore(host, inputPath, options.Password,
            limits, governor, diagnostics, options.RecoveryMode, cancellationToken).ConfigureAwait(false))
        {
            checks = DocumentCheck.Run(document, options, limits, governor, diagnostics, false, cancellationToken);
            pageCount = document.PageCount;
            versionParts = document.EffectiveVersionParts;
        }
        using OwnedAllocationBatch resultStorage = OwnedAllocationBatch.Create(governor, cancellationToken);
        resultStorage.Memory.Reserve(PdfVersion.StorageBytes);
        PdfVersion version = new(versionParts.Major, versionParts.Minor); resultStorage.Add(PdfVersion.StorageBytes);
        IReadOnlyList<QpdfCheckOutcome> resultChecks = ResultCollectionStorage.Copy(resultStorage, checks, IntPtr.Size);
        IReadOnlyList<QpdfDiagnostic> resultDiagnostics = ResultCollectionStorage.Copy(resultStorage, diagnostics.Entries, IntPtr.Size);
        if (governor.HasTypedResult) { resultStorage.RetainResult(); }
        else { resultStorage.Retain(); }
        return new TypedCheckOutcome(pageCount, version, resultChecks, resultDiagnostics);
    }

    internal static void AddPageCountDiagnostic(QpdfDocument document, QpdfPath input, DiagnosticBag diagnostics)
        => AddPageCountDiagnostic(document.ReadDeclaredPageCount(), input, diagnostics);

    private static void AddPageCountDiagnostic(long count, QpdfPath input, DiagnosticBag diagnostics)
    {
        diagnostics.AddParts(QpdfDiagnosticCode.PageCount, QpdfDiagnosticSeverity.Info, [input.Value, ": ", DiagnosticPart.Invariant(count)]);
    }

    internal static IReadOnlyList<QpdfAttachmentInfo> ListAttachments(
        IIndirectResolver resolver,
        PdfDictionary trailer,
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds,
        QpdfPath inputPath,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope memory,
        ParserMemoryScope? results,
        CancellationToken cancellationToken)
    {
        List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(resolver, trailer, limits, governor, diagnostics, memory, cancellationToken);
        if (found.Count == 0)
        {
            if (results is null) { diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, [inputPath.Value, " has no embedded files"]); }
            return Array.Empty<QpdfAttachmentInfo>();
        }

        if (results is null)
        {
            foreach (EmbeddedAttachment info in found)
            {
                int number = info.StreamObjectNumber;
                int generation = resolver is DocumentEngine engine && engine.XRefEntries.TryGetValue(number, out XrefEntry entry)
                    && entry.Kind == XrefEntryKind.Uncompressed ? entry.Second : 0;
                (number, generation) = OriginalObjectIds.DisplayPair(number, generation, originalIds);
                diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, [info.Key, " -> ", number, ",", generation]);
                if (info.FileName.Length > 0)
                {
                    diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, ["  filename: ", info.FileName]);
                }

                if (info.Description.Length > 0)
                {
                    diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, ["  description: ", info.Description]);
                }

                if (info.MimeType.Length > 0)
                {
                    diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, ["  mime type: ", info.MimeType]);
                }

                if (info.CreationDate.Length > 0)
                {
                    diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, ["  creation date: ", info.CreationDate]);
                }

                if (info.ModDate.Length > 0)
                {
                    diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, ["  modification date: ", info.ModDate]);
                }

                if (info.Checksum.Length > 0)
                {
                    diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, ["  checksum: ", info.Checksum]);
                }

                if (info.Size >= 0L)
                {
                    diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, ["  size: ", info.Size]);
                }
            }
            return Array.Empty<QpdfAttachmentInfo>();
        }

        results.Reserve(checked(48L + IntPtr.Size * (long)found.Count));
        QpdfAttachmentInfo[] infos = new QpdfAttachmentInfo[found.Count];
        long textBytes = 0L;
        for (int i = 0; i < found.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            EmbeddedAttachment info = found[i]; results.Reserve(96L);
            infos[i] = new(info.Key, info.FileName, info.Description, info.MimeType, info.CreationDate, info.ModDate,
                info.Checksum, info.StreamObjectNumber, info.Size);
            textBytes = checked(textBytes + GovernedText.StringBytes(info.Key.Length) + GovernedText.StringBytes(info.FileName.Length)
                + GovernedText.StringBytes(info.Description.Length) + GovernedText.StringBytes(info.MimeType.Length)
                + GovernedText.StringBytes(info.CreationDate.Length) + GovernedText.StringBytes(info.ModDate.Length)
                + GovernedText.StringBytes(info.Checksum.Length));
        }
        IReadOnlyList<QpdfAttachmentInfo> view = Array.AsReadOnly(infos);
        // These decoded strings are independent of the PDF graph and remain alive with the returned records.
        memory.TransferTo(results, textBytes);
        return view;
    }

    private static void InspectLinearization(
        LinearizationInspection inspection,
        QpdfPath inputPath,
        DiagnosticBag diagnostics)
    {
        if (inspection.Status == LinearizationStatus.NotLinearized)
        {
            diagnostics.AddParts(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, [inputPath.Value, " is not linearized"]);
            return;
        }

        if (inspection.Status == LinearizationStatus.Valid)
        {
            diagnostics.AddParts(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, [inputPath.Value, ": no linearization errors"]);
            return;
        }
        if (inspection.Status == LinearizationStatus.Unavailable)
        {
            diagnostics.AddParts(QpdfDiagnosticCode.CheckSkipped, QpdfDiagnosticSeverity.Warning,
                ["Skipped check 'linearization': ", inspection.ErrorMessage]);
            return;
        }

        diagnostics.AddParts(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, ["Check 'linearization' failed: ", inspection.ErrorMessage]);
    }

    private static void ShowLinearization(
        LinearizationAnalysis analysis,
        QpdfPath inputPath,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (analysis.Inspection.Status == LinearizationStatus.NotLinearized)
        {
            diagnostics.AddParts(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, [inputPath.Value, " is not linearized"]);
            return;
        }

        if (analysis.Failure is QpdfException failure)
        {
            switch (analysis.Stage)
            {
                case LinearizationStage.Read:
                    if (analysis.Inspection.Status == LinearizationStatus.Unavailable)
                    {
                        diagnostics.AddParts(QpdfDiagnosticCode.CheckSkipped, QpdfDiagnosticSeverity.Warning,
                            ["Skipped check 'linearization': ", failure.Message]);
                    }
                    else
                    {
                        diagnostics.AddParts(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error,
                            [inputPath.Value, ": error encountered while checking linearization data: ", failure.Message]);
                    }
                    return;
                case LinearizationStage.Classify:
                    diagnostics.Add(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, failure.Message);
                    return;
                case LinearizationStage.Validate:
                    if (failure is not QpdfParseException) { ExceptionDispatchInfo.Throw(failure); }
                    diagnostics.AddParts(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error,
                        ["Check 'linearization' failed: ", failure.Message]);
                    break;
            }
        }

        LinearizationReport.Write(inputPath.Value, analysis.Data, analysis.Graph, governor, diagnostics, cancellationToken);
    }

    private static bool CheckAndReportLinearization(QpdfDocument document, CheckPlan plan, EncryptionInspection security,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        (List<QpdfCheckOutcome>? checks, LinearizationAnalysis checkAnalysis) = plan.RunCheck
            ? DocumentCheck.RunWithLinearization(document, new QpdfCheckOptions(), limits, governor, diagnostics,
                true, plan.ShowLinearization, cancellationToken)
            : (null, default);
        using (checkAnalysis)
        {
            if (checks?.Exists(static outcome => outcome.Status == QpdfCheckStatus.Failed) == true) { return false; }
            // This order is independent of argv position and JSON key order.
            if (plan.ShowNpages) { AddPageCountDiagnostic(document, plan.Input, diagnostics); }
            if (plan.ShowEncryption) { ShowEncryption(security, diagnostics); }
            if (plan.CheckLinearization || plan.ShowLinearization)
            {
                using LinearizationAnalysis dedicated = plan.RunCheck ? default
                    : LinearizationCheck.Analyze(document.Engine, document.PhysicalPageCount, limits, governor,
                        plan.ShowLinearization, cancellationToken);
                LinearizationAnalysis analysis = plan.RunCheck ? checkAnalysis : dedicated;
                if (plan.CheckLinearization) { InspectLinearization(analysis.Inspection, plan.Input, diagnostics); }
                if (plan.ShowLinearization) { ShowLinearization(analysis, plan.Input, governor, diagnostics, cancellationToken); }
            }
            return true;
        }
    }

    private static ReadOnlyMemory<byte>? ShowObject(QpdfDocument document, CheckPlan plan, InspectionObjectSelector selector,
        PdfReference? replacedCatalog, bool rejectStreamAfterInfo,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        (bool trailer, int number, int generation) = selector;
        if (!trailer && number <= 0)
        {
            if (plan.PublishesObjectStream) { throw StreamOutputRequired(); }
            return null;
        }
        QpdfObject? target = null;
        if (trailer)
        {
            target = document.Engine.Trailer;
        }
        else if (generation >= 0)
        {
            PdfReference reference = new(number, generation);
            if (replacedCatalog is PdfReference former && former.ObjectNumber == number && former.Generation == generation
                && PdfDictionaryLookup.TryGet(document.Engine.Trailer, "Root"u8, governor, cancellationToken, out QpdfObject root)
                && root is PdfReference active)
            {
                // The selector addresses the input catalog role. Other edges
                // still resolve the original catalog with its stored values.
                reference = active;
            }
            if (((IIndirectResolver)document.Engine).TryResolve(reference, out QpdfObject? resolved)) { target = resolved; }
        }

        return InspectObject(target, document.Engine, plan, null, rejectStreamAfterInfo,
            limits, governor, diagnostics, memory, cancellationToken);
    }

    internal static ReadOnlyMemory<byte>? InspectObject(QpdfObject? target, IIndirectResolver resolver, CheckPlan plan,
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds, bool rejectStreamAfterInfo,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, ParserMemoryScope memory, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (target is PdfStream dataStream && (plan.RawStreamData || plan.FilteredStreamData))
        {
            if (rejectStreamAfterInfo)
            {
                throw new QpdfException("Binary stream output cannot follow informational output in this inspection.");
            }
            ReadOnlyMemory<byte> payload = ReadObjectPayload(dataStream, resolver, plan.FilteredStreamData,
                plan.NormalizeContent, limits, governor, diagnostics, memory, token);

            if (payload.Length > limits.MaxOutputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), payload.Length, limits.MaxOutputBytes);
            }

            return payload;
        }

        if (plan.PublishesObjectStream) { throw StreamOutputRequired(); }
        ShowObjectValue(target, resolver, originalIds, limits, governor, diagnostics);
        return null;
    }

    private static QpdfParseException StreamOutputRequired()
        => new("The output file is only used for stream data.", 0L, null);

    private static ReadOnlyMemory<byte> ReadObjectPayload(PdfStream stream, IIndirectResolver resolver, bool filtered,
        bool normalize, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope memory, CancellationToken token)
    {
        if (!filtered) { return stream.RawMemory; }
        FilterChain.RequireEmbeddedData(stream, resolver, governor, token);
        using FilterPlan stages = FilterChain.Resolve(stream.Dictionary, resolver, governor, token);
        byte[]? decoded = stages.Count == 0 ? null
            : FilterChain.Decode(stream.ArrayData(memory, governor, token), stages, limits, governor, memory, token);
        if (!normalize) { return decoded is null ? stream.RawMemory : decoded; }

        QpdfObject? resources = PdfDictionaryLookup.TryGet(stream.Dictionary, "Resources"u8, governor, token, out QpdfObject rawResources)
            ? rawResources : null;
        return Content.ContentTransforms.NormalizeContentBytesOwned(decoded ?? stream.ArrayData(memory, governor, token), resources,
            resolver, limits, governor, diagnostics, token, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes), memory, out _);
    }

    private static void ShowObjectValue(QpdfObject? target, IIndirectResolver resolver,
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics)
    {
        if (target is null || target is PdfNull)
        {
            diagnostics.Add(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, "null");
        }
        else if (target is PdfStream stream)
        {
            diagnostics.Add(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, "Object is stream.  Dictionary:");
            CanonicalDisplay.AddDiagnostic(stream.Dictionary, limits, governor, resolver, originalIds, diagnostics);
        }
        else
        {
            CanonicalDisplay.AddDiagnostic(target, limits, governor, resolver, originalIds, diagnostics);
        }
    }

    internal static void ShowXref(QpdfDocument document, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<int> numbers = ParserContainers.CreateList<int>(memory);
        foreach (int number in document.Engine.XRefEntries.Keys)
        {
            governor.CheckCancellation();
            ParserContainers.AddItem(memory, numbers, number, sizeof(int));
        }
        GovernedCollections.SortNumbers(memory, governor, numbers);
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            Reading.XrefEntry entry = document.Engine.XRefEntries[number];
            if (entry.Kind == Reading.XrefEntryKind.Free)
            {
                continue;
            }

            if (entry.Kind == Reading.XrefEntryKind.Compressed)
            {
                diagnostics.AddParts(QpdfDiagnosticCode.XrefListing, QpdfDiagnosticSeverity.Info,
                    [number, "/0: compressed; stream = ", entry.First, ", index = ", entry.Second]);
            }
            else
            {
                diagnostics.AddParts(QpdfDiagnosticCode.XrefListing, QpdfDiagnosticSeverity.Info,
                    [number, "/", entry.Second, ": uncompressed; offset = ", entry.First]);
            }
        }
    }

    internal static void ShowPages(QpdfDocument document, JobPlan plan, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
        => ShowPages(document.Engine, document.PageReferences, null, plan, limits, governor, diagnostics, cancellationToken);

    private static void ShowPages(IIndirectResolver resolver, IReadOnlyList<PageLocation> pages,
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds, JobPlan plan, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        using PageImageBindings.Cache? imageCache = plan.WithImages && pages.Count > 1
            ? PageImageBindings.Cache.Create(governor, diagnostics) : null;
        int index = 1;
        foreach (PageLocation location in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (location.Source is PdfReference page)
            {
                (int number, int generation) = OriginalObjectIds.DisplayPair(page.ObjectNumber, page.Generation, originalIds);
                diagnostics.AddParts(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, ["page ", index, ": ", number, " ", generation, " R"]);
            }
            else if (originalIds is not null)
            { diagnostics.AddParts(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, ["page ", index, ": direct"]); }
            else { throw new QpdfException("Page dictionary is not an indirect object."); }
            if (plan.WithImages)
            {
                using ParserMemoryScope imageMemory = ParserMemoryScope.Create(governor);
                List<PageImage> images = ListPageImages(resolver, location, limits, governor, diagnostics, cancellationToken, imageMemory, imageCache);
                if (images.Count > 0)
                {
                    diagnostics.Add(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, "  images:");
                    foreach (PageImage image in images)
                    {
                        (int number, int generation) = OriginalObjectIds.DisplayPair(image.Number, image.Generation, originalIds);
                        diagnostics.AddParts(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, ["    /", DiagnosticPart.Name(image.Name), ": ", number, " ", generation, " R, ", image.Width, " x ", image.Height]);
                    }
                }
            }

            diagnostics.Add(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, "  content:");
            if (location.Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) && contents is not null)
            {
                if (contents is PdfReference root)
                {
                    QpdfObject? indirect = resolver is DocumentEngine engine ? engine.ResolveArrayOrNull(root)
                        : resolver.TryResolve(root, out QpdfObject? found) ? found : null;
                    if (indirect is PdfArray or PdfNull) { contents = indirect; }
                }
                PdfArray? array = contents as PdfArray;
                int count = array?.Count ?? 1;
                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.NoteOperation();
                    if ((array is null ? contents : array[i]) is PdfReference content)
                    {
                        (int number, int generation) = OriginalObjectIds.DisplayPair(content.ObjectNumber, content.Generation, originalIds);
                        diagnostics.AddParts(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, ["    ", number, " ", generation, " R"]);
                    }
                }
            }

            index = checked(index + 1);
        }
    }

    internal static async ValueTask<ExecutionOutcome> RunCheckPlanAsync(
        IQpdfHost host, CheckPlan plan, QpdfPassword? password, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            // Count reports use the declaration and keep traversal quiet.
            // Physical named extraction retains the reader's warnings.
            diagnostics.ReportPageCountMismatch = plan.PublishesObjectStream && plan.Import is null;
            if (plan.PayloadOutput is QpdfPath target
                && (target == plan.Input || target == plan.Import?.Json || target == plan.Import?.FollowingPatch))
            { throw new QpdfParseException("The output file must differ from the input file.", 0L, null); }
            using PublicationInputAliases? ownedAliases = plan.PayloadOutput.HasValue ? PublicationInputAliases.BeginIfNeeded(governor) : null;
            if (plan.Import is null && plan.ShowNpages && !plan.EmptyDocument && !plan.RunCheck && !plan.ShowEncryption
                && !plan.ShowPages && !plan.ShowXref && plan.ObjectSelector is null && !plan.ListAttachments
                && plan.AttachmentKey is null && !plan.CheckLinearization && !plan.ShowLinearization
                && !plan.HasAttachmentEdits && !plan.PublicationPolicy.HasRemovals && plan.Labels.Count == 0
                && plan.FlattenAnnotations is null
                && !plan.CoalesceContents && !plan.ExternalizeInlineImages && !plan.OptimizeImages
                && plan.Sources.Count == 0 && plan.Rotations.Count == 0
                && plan.Overlays.Count == 0 && plan.Underlays.Count == 0)
            {
                long count = await PageCountInspection.ReadAsync(host, plan.Input, password, plan.Recovery, limits,
                    governor, diagnostics, true, cancellationToken).ConfigureAwait(false);
                AddPageCountDiagnostic(count, plan.Input, diagnostics);
                return CompleteWithPresentation(governor);
            }
            using GovernedBufferStream output = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes));
            using ParserMemoryScope? payloadOwner = plan.Import is null && plan.PayloadOutput.HasValue ? ParserMemoryScope.Create(governor) : null;
            ReadOnlyMemory<byte>? directPayload = null;
            bool hasPayload = false;
            if (plan.Import is ImportedInspectionInput imported)
            {
                hasPayload = await InspectImportedAsync(host, plan, imported, password, limits, governor, diagnostics,
                    output, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Checking and all following inspections use this same document.
                (QpdfDocument? opened, EncryptionInspection security) = plan.ShowEncryption && !plan.EmptyDocument
                    ? await QpdfDocument.OpenSecurityInspectionCore(host, plan.Input, password, limits, governor,
                        diagnostics, plan.Recovery, cancellationToken, plan.NeedsDocument).ConfigureAwait(false)
                    : (plan.EmptyDocument
                        ? await QpdfDocument.OpenEmptyDocument(plan.Input, limits, governor, diagnostics, plan.Recovery, cancellationToken, false).ConfigureAwait(false)
                        : await QpdfDocument.OpenInspectionCore(host, plan.Input, password,
                            limits, governor, diagnostics, plan.Recovery, cancellationToken).ConfigureAwait(false), default);
                await using (QpdfDocument? document = opened)
                {
                    if (document is null)
                    {
                        ShowEncryption(security, diagnostics);
                        if (plan.NeedsDocument)
                        { diagnostics.Add(QpdfDiagnosticCode.CheckSkipped, QpdfDiagnosticSeverity.Info, "Other inspections were skipped because the password did not authenticate."); }
                        return CompleteWithPresentation(governor);
                    }
                    using InspectionGraph? inspection = await PrepareInspectionGraphAsync(host, document, plan,
                        limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                    if (CheckAndReportLinearization(document, plan, security, limits, governor, diagnostics, cancellationToken))
                    {
                        if (plan.ShowXref) { ShowXref(document, governor, diagnostics, cancellationToken); }
                        if (plan.ObjectSelector is InspectionObjectSelector selector)
                        {
                            bool priorInfo = plan.RunCheck || plan.ShowNpages || plan.ShowEncryption || plan.CheckLinearization || plan.ShowLinearization || plan.ShowXref;
                            using ParserMemoryScope objectMemory = ParserMemoryScope.Create(governor);
                            ReadOnlyMemory<byte>? payload = ShowObject(document, plan, selector,
                                inspection?.ReplacedCatalog, priorInfo && plan.AttachmentKey is null,
                                limits, governor, diagnostics, objectMemory, cancellationToken);
                            if (payload is ReadOnlyMemory<byte> objectBytes)
                            {
                                if (plan.PublishesObjectStream)
                                {
                                    if (payloadOwner is not null && inspection is null)
                                    {
                                        objectMemory.TransferTo(payloadOwner);
                                        document.Engine.TransferExclusiveValuesTo(payloadOwner);
                                        directPayload = objectBytes;
                                    }
                                    else { output.Write(objectBytes.Span); }
                                }
                                else { AppendInspectionBytes(output, objectBytes, governor); }
                                hasPayload = true;
                            }
                        }
                        if (plan.ShowPages) { ShowPages(document, plan, limits, governor, diagnostics, cancellationToken); }
                        if (plan.ListAttachments)
                        {
                            using ParserMemoryScope listingMemory = ParserMemoryScope.Create(governor);
                            _ = ListAttachments(document.Engine, document.Engine.Trailer, null, plan.Input, limits, governor,
                                diagnostics, listingMemory, null, cancellationToken);
                        }
                        if (plan.AttachmentKey is not null)
                        {
                            using ParserMemoryScope attachmentMemory = ParserMemoryScope.Create(governor);
                            ReadOnlyMemory<byte> payload = EmbeddedFiles.ExtractAttachment(document.Engine, plan.AttachmentKey, limits, governor, diagnostics, attachmentMemory, cancellationToken);
                            if (payloadOwner is not null && !hasPayload && inspection is null)
                            {
                                // Keep raw/decrypted/decoded backing funded across input close and publication.
                                governor.ChargeOutputBytes(payload.Length);
                                attachmentMemory.TransferTo(payloadOwner);
                                document.Engine.TransferExclusiveValuesTo(payloadOwner);
                                directPayload = payload;
                            }
                            else { AppendInspectionBytes(output, payload, governor); }
                            hasPayload = true;
                        }
                    }
                }
            }
            if (hasPayload && plan.PayloadOutput is QpdfPath payloadTarget)
            {
                return await PublishInspectionPayloadAsync(host, payloadTarget, directPayload ?? output.WrittenMemory,
                    plan.PublishesObjectStream, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            ReadOnlyMemory<byte>? bytes = null;
            if (hasPayload)
            {
                bytes = new ReadOnlyMemory<byte>(output.ToArray());
            }
            output.Dispose();
            ExecutionOutcome result = CompleteWithPresentation(governor);
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

    private static async ValueTask<ExecutionOutcome> PublishInspectionPayloadAsync(IQpdfHost host, QpdfPath target,
        ReadOnlyMemory<byte> payload, bool chargeOnWrite, ResourceGovernor governor, DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<QpdfDiagnostic> reports = diagnostics.Entries;
        for (int index = 0; index < reports.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (reports[index].Severity == QpdfDiagnosticSeverity.Error) { return CompleteWithPresentation(governor); }
        }
        await PublicationInputAliases.Active(governor).ValidateTargetAsync(host, target, null, "Binary", cancellationToken).ConfigureAwait(false);
        using PublicationReservation publication = PublicationReservation.Create(governor, true, payload.Length);
        publication.Begin();
        await using (IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, target, governor, cancellationToken).ConfigureAwait(false))
        {
            GovernedHost.PrepareOutputCapacity(transaction, payload.Length, cancellationToken);
            // Buffered and attachment extraction already charge output bytes on capture.
            if (chargeOnWrite) { governor.ChargeOutputBytes(payload.Length); }
            await transaction.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return CompleteWithPresentation(governor);
    }

    internal static void AppendInspectionBytes(GovernedBufferStream output, ReadOnlyMemory<byte> payload, ResourceGovernor governor)
    {
        governor.ChargeOutputBytes(payload.Length);
        output.Write(payload.Span);
    }

    private static async ValueTask<bool> InspectImportedAsync(IQpdfHost host, CheckPlan plan,
        ImportedInspectionInput imported, QpdfPassword? password, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, GovernedBufferStream output, CancellationToken token)
    {
        await using IQpdfInput? input = imported.Update
            ? await GovernedHost.OpenInputAsync(host, plan.Input, governor, token).ConfigureAwait(false) : null;
        (DocumentEngine? Engine, QpdfDocument.VersionParts Header, EncryptionInspection Security) source = input is null
            ? default : await QpdfDocument.ReadAuthenticatedPatchSourceAsync(input, password, limits, governor,
                diagnostics, plan.Recovery, token).ConfigureAwait(false);
        using DocumentEngine? engine = source.Engine;
        using ParserMemoryScope graph = ParserMemoryScope.Create(governor);
        Json.ImportedModel model = await JsonExecution.BindInspectionModelAsync(host, imported, engine, source.Header,
            limits, governor, graph, token).ConfigureAwait(false);
        model = ImportedPublication.Apply(model, plan.PublicationPolicy, limits, graph, governor, token,
            out PdfReference? replacedCatalog);

        using ParserMemoryScope inspection = ParserMemoryScope.Create(governor);
        Content.PoolResolver resolver = Content.PoolResolver.Create(inspection, model.Objects);
        Dictionary<int, (int Number, int Generation)>? originalIds = OriginalObjectIds.PrepareImportedIds(
            model.OriginalPairs, null, inspection, governor, token);
        List<PageLocation>? prepared = null;
        if (plan.CoalesceContents)
        {
            (PdfDictionary trailer, List<PageLocation> locations) = JsonExecution.PrepareBoundPages(model, resolver, originalIds, true,
                limits, graph, governor, diagnostics, token);
            graph.Reserve(Json.ImportedModel.AllocationBytes);
            model = model with { Trailer = trailer };
            prepared = locations;
        }
        PdfDictionary catalog = JsonExecution.ReadBoundCatalog(model, resolver, governor, token);
        IReadOnlyList<PageLocation> pages = prepared ?? (plan.RunCheck || plan.ShowPages || plan.ShowNpages
            ? PageTree.Read(resolver, catalog, diagnostics, governor, limits, inspection, token) : []);
        if (plan.RunCheck && !DocumentCheck.CheckImported(resolver, model.Trailer, pages, model.Objects, originalIds,
            limits, governor, diagnostics, token))
        { return false; }

        if (plan.ShowNpages)
        {
            PdfDictionary pagesRoot = PageTree.ReadRoot(resolver, catalog, governor, token);
            AddPageCountDiagnostic(PageTree.ReadCount(resolver, pagesRoot, governor, token)?.Value ?? pages.Count,
                plan.Input, diagnostics);
        }
        if (plan.ShowEncryption) { ShowEncryption(source.Security, diagnostics); }
        bool hasPayload = false;
        if (plan.ObjectSelector is InspectionObjectSelector selector)
        {
            (bool trailer, int number, int generation) = selector;
            if (trailer || number > 0)
            {
                QpdfObject? target = trailer ? model.Trailer : null;
                if (!trailer && model.OriginalPairs is not null && model.OriginalPairs.TryGetValue((number, generation), out int bound))
                { target = replacedCatalog?.ObjectNumber == bound ? catalog : model.Objects.GetValueOrDefault(bound); }
                bool priorInfo = plan.RunCheck || plan.ShowNpages || plan.ShowEncryption;
                using ParserMemoryScope streamMemory = ParserMemoryScope.Create(governor);
                ReadOnlyMemory<byte>? payload = InspectObject(target, resolver, plan, originalIds,
                    priorInfo && plan.AttachmentKey is null, limits, governor, diagnostics, streamMemory, token);
                if (payload is ReadOnlyMemory<byte> bytes)
                {
                    if (plan.PublishesObjectStream) { output.Write(bytes.Span); }
                    else { AppendInspectionBytes(output, bytes, governor); }
                    hasPayload = true;
                }
            }
            else if (plan.PublishesObjectStream) { throw StreamOutputRequired(); }
        }
        if (plan.ShowPages) { ShowPages(resolver, pages, originalIds, plan, limits, governor, diagnostics, token); }
        if (plan.ListAttachments)
        {
            using ParserMemoryScope listingMemory = ParserMemoryScope.Create(governor);
            _ = ListAttachments(resolver, model.Trailer, originalIds, plan.Input, limits, governor, diagnostics, listingMemory, null, token);
        }
        if (plan.AttachmentKey is string key)
        {
            using ParserMemoryScope attachmentMemory = ParserMemoryScope.Create(governor);
            ReadOnlyMemory<byte> payload = EmbeddedFiles.ExtractAttachment(resolver, model.Trailer, key, limits,
                governor, diagnostics, attachmentMemory, token);
            AppendInspectionBytes(output, payload, governor);
            hasPayload = true;
        }
        return hasPayload;
    }

    internal static void ShowEncryption(EncryptionInspection inspection, DiagnosticBag diagnostics)
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
        Security.EncryptionPermissions permissions = new(parameters.Revision, parameters.Permissions);
        Permission("extract for accessibility: ", permissions.Accessibility);
        Permission("extract for any purpose: ", permissions.Extract);
        Permission("print low resolution: ", permissions.PrintLow);
        Permission("print high resolution: ", permissions.PrintHigh);
        Permission("modify document assembly: ", permissions.Assemble);
        Permission("modify forms: ", permissions.Forms);
        Permission("modify annotations: ", permissions.Annotations);
        Permission("modify other: ", permissions.ModifyOther);
        Permission("modify anything: ", permissions.ModifyAll);
        if (parameters.Version >= 4)
        {
            diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info,
                ["stream encryption method: ", EncryptionParameters.CipherName(parameters.Streams)]);
            diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info,
                ["string encryption method: ", EncryptionParameters.CipherName(parameters.Strings)]);
            diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport, QpdfDiagnosticSeverity.Info,
                ["file encryption method: ", EncryptionParameters.CipherName(parameters.Files)]);
        }

        void Permission(string label, bool allowed) => diagnostics.AddParts(QpdfDiagnosticCode.EncryptionReport,
            QpdfDiagnosticSeverity.Info, [label, allowed ? "allowed" : "not allowed"]);
    }

    internal static async ValueTask<ExecutionOutcome> RunSecurityPlanAsync(IQpdfHost host, SecurityPlan plan,
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
