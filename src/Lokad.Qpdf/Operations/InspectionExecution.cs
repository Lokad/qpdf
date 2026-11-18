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

    internal static async ValueTask ShowObjectCoreAsync(
        IQpdfHost host,
        ShowObjectPlan showObject,
        QpdfPassword? password,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        await using QpdfDocument document = showObject.EmptyDocument
            ? await QpdfDocument.OpenEmptyDocument(showObject.Input, limits, governor, diagnostics, showObject.Recovery, cancellationToken, true).ConfigureAwait(false)
            : await QpdfDocument.OpenInspectionCore(host, showObject.Input, password, limits, governor, diagnostics, showObject.Recovery, cancellationToken).ConfigureAwait(false);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        await ShowObjectAsync(host, document, showObject, limits, governor, diagnostics, memory, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<QpdfAttachmentInfo> ListAttachments(
        QpdfDocument document,
        QpdfPath inputPath,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope memory,
        ParserMemoryScope? results,
        CancellationToken cancellationToken)
    {
        List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(document.Engine, limits, governor, diagnostics, memory, cancellationToken);
        if (found.Count == 0)
        {
            if (results is null) { diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, [inputPath.Value, " has no embedded files"]); }
            return Array.Empty<QpdfAttachmentInfo>();
        }

        if (results is null)
        {
            foreach (EmbeddedAttachment info in found)
            {
                int generation = 0;
                if (info.StreamObjectNumber >= 0
                    && document.Engine.XRefEntries.TryGetValue(info.StreamObjectNumber, out XrefEntry entry)
                    && entry.Kind == XrefEntryKind.Uncompressed)
                {
                    generation = entry.Second;
                }

                diagnostics.AddParts(QpdfDiagnosticCode.AttachmentListing, QpdfDiagnosticSeverity.Info, [info.Key, " -> ", info.StreamObjectNumber, ",", generation]);
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

    internal static void InspectLinearization(
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

    internal static void ShowLinearization(
        QpdfDocument document,
        QpdfPath inputPath,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        LinearizationDataOwner? owner;
        try
        {
            owner = LinearizationCheck.TryReadOwnedData(document.Engine, document.PageCount, limits, governor, cancellationToken);
        }
        catch (QpdfFilterException ex) when (ex.Reason is QpdfFilterFailureReason.ExternalData or QpdfFilterFailureReason.TooManyFilters)
        {
            diagnostics.AddParts(QpdfDiagnosticCode.CheckSkipped, QpdfDiagnosticSeverity.Warning,
                ["Skipped check 'linearization': ", ex.Message]);
            return;
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            diagnostics.AddParts(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, [inputPath.Value, ": error encountered while checking linearization data: ", ex.Message]);
            return;
        }

        using (owner)
        {
            if (owner is null)
            {
                diagnostics.AddParts(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, [inputPath.Value, " is not linearized"]);
                return;
            }

            LinearizationData data = owner.Data;
            using ParserMemoryScope classificationMemory = ParserMemoryScope.Create(governor);
            ClassifiedGraph graph;
            try
            {
                graph = LinearizationGraph.Classify(document.Engine, data.LindictObject, data.HintObject,
                    classificationMemory, governor, cancellationToken);
            }
            catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
            {
                diagnostics.Add(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, ex.Message);
                return;
            }

            using (graph)
            {
                try
                {
                    LinearizationPhysical.Validate(document.Engine, data, graph, limits, governor, cancellationToken);
                }
                catch (QpdfParseException ex)
                {
                    diagnostics.AddParts(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error,
                        ["Check 'linearization' failed: ", ex.Message]);
                }
                LinearizationReport.Write(inputPath.Value, data, graph, governor, diagnostics, cancellationToken);
            }
        }
    }

    internal static async ValueTask<ReadOnlyMemory<byte>?> ShowObjectAsync(
        IQpdfHost host,
        QpdfDocument document,
        ShowObjectPlan showObject,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
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

            ReadOnlyMemory<byte> payload = dataStream.RawMemory;
            if (showObject.FilteredStreamData)
            {
                FilterChain.RequireEmbeddedData(dataStream, document.Engine, governor, cancellationToken);
                using FilterPlan stages = FilterChain.Resolve(dataStream.Dictionary, (IIndirectResolver)document.Engine, governor, cancellationToken);
                byte[]? decoded = null;
                if (stages.Count > 0)
                {
                    decoded = FilterChain.Decode(dataStream.ArrayData(memory, governor, cancellationToken), stages, limits, governor, memory, cancellationToken);
                    payload = decoded;
                }
                if (showObject.NormalizeContent)
                {
                    QpdfObject? resources = PdfDictionaryLookup.TryGet(dataStream.Dictionary, "Resources"u8, governor, cancellationToken, out QpdfObject rawResources)
                        ? rawResources : null;
                    payload = Content.ContentTransforms.NormalizeContentBytesOwned(decoded ?? dataStream.ArrayData(memory, governor, cancellationToken), resources,
                        document.Engine, limits, governor, diagnostics, cancellationToken, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes), memory, out _);
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

        ShowObjectValue(target, document.Engine, null, limits, governor, diagnostics);
        return null;
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
            // Native reports declared counts separately from quiet traversal.
            // Partial counting validates declarations before accepting ranges.
            diagnostics.ReportPageCountMismatch = false;
            if (plan.Import is ImportedCheckInput imported)
            {
                await RunImportedCheckAsync(host, plan, imported, password, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                return CompleteWithPresentation(governor);
            }
            if (plan.PayloadOutput is QpdfPath target && target == plan.Input)
            { throw new QpdfParseException("The output file must differ from the input file.", 0L, null); }
            if (plan.ShowNpages && !plan.EmptyDocument && !plan.RunCheck && !plan.ShowEncryption
                && !plan.ShowPages && !plan.ShowXref && plan.ObjectSelector is null && !plan.ListAttachments
                && plan.AttachmentKey is null && !plan.CheckLinearization && !plan.ShowLinearization
                && plan.Sources.Count == 0 && plan.Rotations.Count == 0 && plan.Overlays.Count == 0 && plan.Underlays.Count == 0)
            {
                long count = await PageCountInspection.ReadAsync(host, plan.Input, password, plan.Recovery, limits,
                    governor, diagnostics, true, cancellationToken).ConfigureAwait(false);
                AddPageCountDiagnostic(count, plan.Input, diagnostics);
                return CompleteWithPresentation(governor);
            }
            using GovernedBufferStream output = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes));
            using ParserMemoryScope? payloadOwner = plan.PayloadOutput.HasValue ? ParserMemoryScope.Create(governor) : null;
            ReadOnlyMemory<byte>? directPayload = null;
            bool hasPayload = false;
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
                List<QpdfCheckOutcome> checks = plan.RunCheck
                    ? DocumentCheck.Run(document, new QpdfCheckOptions(), limits, governor, diagnostics, true, cancellationToken)
                    : [];
                if (!checks.Exists(static outcome => outcome.Status == QpdfCheckStatus.Failed))
                {
                    // This order is independent of argv position and JSON key order.
                    if (plan.ShowNpages) { AddPageCountDiagnostic(document, plan.Input, diagnostics); }
                    if (plan.ShowEncryption) { ShowEncryption(security, diagnostics); }
                    if (plan.CheckLinearization) { InspectLinearization(document, plan.Input, limits, governor, diagnostics, cancellationToken); }
                    if (plan.ShowLinearization) { ShowLinearization(document, plan.Input, limits, governor, diagnostics, cancellationToken); }
                    if (plan.ShowXref) { ShowXref(document, governor, diagnostics, cancellationToken); }
                    if (plan.ObjectSelector is not null)
                    {
                        bool priorInfo = plan.RunCheck || plan.ShowNpages || plan.ShowEncryption || plan.CheckLinearization || plan.ShowLinearization || plan.ShowXref;
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
                    if (plan.ListAttachments)
                    {
                        using ParserMemoryScope listingMemory = ParserMemoryScope.Create(governor);
                        _ = ListAttachments(document, plan.Input, limits, governor, diagnostics, listingMemory, null, cancellationToken);
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
            if (hasPayload && plan.PayloadOutput is QpdfPath payloadTarget)
            {
                IReadOnlyList<QpdfDiagnostic> reports = diagnostics.Entries;
                for (int index = 0; index < reports.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (reports[index].Severity == QpdfDiagnosticSeverity.Error) { return CompleteWithPresentation(governor); }
                }
                ReadOnlyMemory<byte> payload = directPayload ?? output.WrittenMemory;
                using PublicationReservation publication = PublicationReservation.Create(governor, true, payload.Length);
                publication.Begin();
                await using (IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, payloadTarget, governor, cancellationToken).ConfigureAwait(false))
                {
                    GovernedHost.PrepareOutputCapacity(transaction, payload.Length, cancellationToken);
                    // Extraction already charged output bytes; publishing must not charge them twice.
                    await transaction.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                return CompleteWithPresentation(governor);
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

    internal static void AppendInspectionBytes(GovernedBufferStream output, ReadOnlyMemory<byte> payload, ResourceGovernor governor)
    {
        governor.ChargeOutputBytes(payload.Length);
        output.Write(payload.Span);
    }

    private static async ValueTask RunImportedCheckAsync(IQpdfHost host, CheckPlan plan,
        ImportedCheckInput imported, QpdfPassword? password, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken token)
    {
        await using IQpdfInput? input = imported.Update
            ? await GovernedHost.OpenInputAsync(host, plan.Input, governor, token).ConfigureAwait(false) : null;
        (DocumentEngine? Engine, QpdfDocument.VersionParts Header, EncryptionInspection Security) source = input is null
            ? default : await QpdfDocument.ReadAuthenticatedPatchSourceAsync(input, password, limits, governor,
                diagnostics, plan.Recovery, token).ConfigureAwait(false);
        using DocumentEngine? engine = source.Engine;
        using ParserMemoryScope graph = ParserMemoryScope.Create(governor);
        using System.Text.Json.JsonDocument json = await Json.JsonImport.ReadJsonAsync(host, imported.Json,
            limits, governor, graph, token).ConfigureAwait(false);
        Json.ImportedModel model = engine is null
            ? await Json.JsonImport.BuildCreateModelAsync(host, json, true, limits, governor, graph, token).ConfigureAwait(false)
            : await Json.JsonImport.BuildUpdateModelAsync(host, json, engine, source.Header, true, false, true,
                limits, governor, graph, token).ConfigureAwait(false);
        Json.JsonImport.CheckObjectCount(model, limits);
        if (PdfDictionaryLookup.TryGet(model.Trailer, "Encrypt"u8, governor, token, out QpdfObject declaration)
            && declaration is not PdfNull) { throw new QpdfCryptoException("/Encrypt"); }

        using ParserMemoryScope inspection = ParserMemoryScope.Create(governor);
        Content.PoolResolver resolver = Content.PoolResolver.Create(inspection, model.Objects);
        if (!PdfDictionaryLookup.TryGet(model.Trailer, "Root"u8, governor, token, out QpdfObject root)
            || Content.CatalogCopy.ResolveDictionary(root, resolver, governor, token) is not PdfDictionary catalog)
        { throw new QpdfParseException("Imported trailer /Root must resolve to a dictionary.", 0L, null); }
        Json.JsonImport.ValidateCatalogVersion(catalog, resolver, governor, token);
        List<PageLocation> pages = PageTree.Read(resolver, catalog, diagnostics, governor, limits, inspection, token);
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds = OriginalObjectIds.PrepareImportedIds(
            model.OriginalPairs, null, inspection, governor, token);
        if (DocumentCheck.CheckImported(resolver, model.Trailer, pages, model.Objects, originalIds,
            limits, governor, diagnostics, token))
        {
            if (plan.ShowNpages)
            {
                PdfDictionary pagesRoot = PageTree.ReadRoot(resolver, catalog, governor, token);
                AddPageCountDiagnostic(PageTree.ReadCount(resolver, pagesRoot, governor, token)?.Value ?? pages.Count,
                    plan.Input, diagnostics);
            }
            if (plan.ShowEncryption) { ShowEncryption(source.Security, diagnostics); }
            if (plan.ObjectSelector is string selector)
            {
                (bool trailer, int number, int generation) = SelectionPlan.ParseInspectionSelector(selector, 0);
                if (trailer || number > 0)
                {
                    QpdfObject? target = trailer ? model.Trailer : null;
                    if (!trailer && model.OriginalPairs is not null && model.OriginalPairs.TryGetValue((number, generation), out int bound))
                    { target = model.Objects.GetValueOrDefault(bound); }
                    if (target is PdfStream && (plan.RawStreamData || plan.FilteredStreamData))
                    { throw new QpdfException("Binary stream output cannot follow informational output in this inspection."); }
                    ShowObjectValue(target, resolver, originalIds, limits, governor, diagnostics);
                }
            }
            if (plan.ShowPages) { ShowPages(resolver, pages, originalIds, plan, limits, governor, diagnostics, token); }
        }
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
