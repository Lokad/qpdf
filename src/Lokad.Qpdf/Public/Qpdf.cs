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

/// <summary>
/// Typed PDF operations over the shared engine. Each operation opens its
/// input, copies the selected graph once, serializes deterministically with a
/// counting pass, and publishes through a staged host transaction.
/// Every call builds a fresh per-job governor from the supplied or default
/// <see cref="QpdfLimits"/>; exhausted budgets fail explicitly without publication.
/// Calls hold no shared mutable state and are safe to run concurrently against
/// distinct hosts. Cancellation is cooperative: cancelled calls report
/// <see cref="QpdfExecutionStatus.Canceled"/> and never publish partial output,
/// and failed calls leave existing targets untouched.
/// </summary>
public static partial class Qpdf
    // Command adapters: argv and job JSON converge on the typed selection plan.
{
    /// <summary>
    /// Copies the selected pages into a standalone PDF with default budgets. Streams
    /// travel opaquely; inherited page attributes are materialized.
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
    public static async ValueTask<PdfPageSelectionResult> SelectPageAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
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
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(PdfPageSelectionResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            PageSelectionOutcome result = await SelectPageCoreAsync(host, inputPath, outputPath, options, limits, QpdfRecoveryMode.Strict, [], [], false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, cancellationToken).ConfigureAwait(false);
            PdfPageSelectionResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

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
    public static async ValueTask<PdfPageSelectionResult> SelectPageAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
        IReadOnlyList<PdfPageRotation> rotations,
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
        ArgumentNullException.ThrowIfNull(rotations);
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(PdfPageSelectionResult.StorageBytes);
        try
        {
            using TypedSelectionPreparation preparation = TypedSelectionPreparation.Create(governor);
            preparation.ConvertRotations(rotations);
            DiagnosticBag diagnostics = new(governor);
            PageSelectionOutcome result = await SelectPageCoreAsync(host, inputPath, outputPath, options, limits, QpdfRecoveryMode.Strict, preparation.Rotations, [], false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, cancellationToken).ConfigureAwait(false);
            preparation.Dispose();
            PdfPageSelectionResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>
    /// Collects the destination widget identifiers that stay reachable from the copied form
    /// registrations for appearance generation, mirroring the reference field traversal: only
    /// widgets in a surviving field subtree keep generation, with memberless fields counting
    /// only on whole-file paths. Returns null when no output registration list survives, in
    /// which case every widget stays untouched like the reference.
    /// </summary>
    private static HashSet<int>? CollectGenerateReached(IReadOnlyList<(IReadOnlyDictionary<int, QpdfObject> Pool, Content.CopiedForm? Part)> parts, HashSet<int> survivors, bool pruneUnreachableFields, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        if (survivors is null)
        {
            throw new ArgumentNullException(nameof(survivors));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        bool anyFields = false;
        HashSet<int> reached = new();
        foreach ((IReadOnlyDictionary<int, QpdfObject> pool, Content.CopiedForm? part) in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part is null || !part.HasFields || pool is null)
            {
                continue;
            }

            (List<PdfReference> kept, HashSet<int> closure) = Content.AcroFormCopy.CollectKeptClosure(pool, part.Fields, survivors, pruneUnreachableFields, limits, governor, cancellationToken);
            if (kept.Count == 0)
            {
                continue;
            }

            anyFields = true;
            foreach (int id in closure)
            {
                reached.Add(id);
            }
        }

        return anyFields ? reached : null;
    }

    /// <summary>
    /// Drops a generated /NeedAppearances flag from an assembled form, mirroring the reference
    /// clearing the condition once appearance generation has run.
    /// </summary>
    private static PdfDictionary? ClearNeedAppearances(PdfDictionary? form, ResourceGovernor governor)
    {
        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (form is null)
        {
            return null;
        }

        PdfName key = PdfName.FromAscii("NeedAppearances");
        if (!form.TryGet(key, out QpdfObject? current) || current is null || current is PdfNull)
        {
            return form;
        }

        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName name in form.Keys)
        {
            if (name.DisplayText == "NeedAppearances")
            {
                continue;
            }

            if (form.TryGet(name, out QpdfObject? value) && value is not null)
            {
                entries[name] = value;
            }
        }

        governor.ReserveMemory(64L);
        return new PdfDictionary(entries);
    }

    private static void ApplySourceContentTransforms(
        GraphCopier copier,
        IReadOnlyList<int> pageIds,
        bool coalesceContents,
        bool normalizeContent,
        bool externalizeInlineImages,
        long iiMinBytes,
        bool compress,
        int? level,
        byte filterOptions,
        Content.ContentPrunePolicy prune,
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        bool preserveUnreferenced)
    {
        bool pruneHere = prune.Enabled && (prune.Mode == "yes"
            || (prune.Mode is null or "auto" && Content.ContentTransforms.SharedResourcesHeuristic(engine, limits, governor, cancellationToken)));
        if (!coalesceContents && !normalizeContent && !pruneHere && !externalizeInlineImages)
        {
            return;
        }

        Content.ContentTransformRequest request = new(coalesceContents, normalizeContent, compress, level, externalizeInlineImages, iiMinBytes)
        { PreserveUnreferenced = preserveUnreferenced, FilterOptions = filterOptions };
        Content.ContentTransforms.ApplyPageTransforms(copier, pageIds, request, pruneHere, limits, governor, diagnostics, cancellationToken);
    }


    private static async ValueTask<PageSelectionOutcome> SelectPageCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        IReadOnlyList<OutputRotation> rotations,
        IReadOnlyList<PageLabelSpec> labels,
        bool removeLabels,
        string? minVersion,
        string? forceVersion,
        bool deterministicId,
        bool staticId,
        bool newlineBeforeEndstream,
        string? objectStreams,
        bool preserveUnreferenced,
        CompressionRequest compression,
        bool removeInfo,
        bool removeAcroform,
        bool removeStructure,
        bool removeMetadata,
        AttachmentEdits edits,
        bool coalesceContents,
        bool normalizeContent,
        bool externalizeInlineImages,
        long iiMinBytes,
        IReadOnlyList<OverlaySpec> underlays,
        IReadOnlyList<OverlaySpec> overlays,
        bool flattenRotation,
        string? flattenAnnotations,
        bool generateAppearances,
        Content.ImageOptimizeRequest optimize,
        Writing.QdfOptions qdf,
        Content.ContentPrunePolicy prune,
        bool preserveCatalog,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        bool linearize,
        QpdfPath? linearizePass1,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        await using QpdfDocument document = await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
        if (externalizeInlineImages || (optimize.Optimize && !optimize.KeepInline)) { document.PrepareInlinePageResources(cancellationToken); }
        int total = document.PageReferences.Count;
        IReadOnlyList<int> requested = options.Resolve(total, limits.MaxWork);
        if ((long)requested.Count > limits.MaxWork)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), requested.Count, limits.MaxWork);
        }

        foreach (int pageNumber in requested)
        {
            if (pageNumber > total)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"Page {pageNumber} exceeds the page count of {total}.");
            }
        }

        using ParserMemoryScope copyMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, 3, copyMemory);
        foreach (int pageNumber in requested)
        {
            copier.RegisterPageRoot(document.PageReferences[pageNumber - 1].Source?.ObjectNumber);
        }
        List<PdfReference> kids = new(requested.Count);
        List<(GraphCopier Copier, int Id, PdfDictionary Page)> occurrences = new(requested.Count);
        foreach (int pageNumber in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            PageLocation location = document.PageReferences[pageNumber - 1];
            Dictionary<PdfName, QpdfObject> extra = MaterializeAncestors(document.Engine, location, copier, governor, limits, cancellationToken, copyMemory);
            (int pageId, PdfDictionary page) = copier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra);
            RequireMediaBox(page);
            kids.Add(new PdfReference(pageId, 0));
            occurrences.Add((copier, pageId, page));
        }

        ApplyRotations(occurrences, ResolveRotationFolds(rotations, kids.Count, governor, limits, copyMemory), governor, copyMemory);
        if (preserveUnreferenced)
        {
            HashSet<int> excluded = PreservationExclusions(document.Engine, KeptLocations(document, requested), limits, governor, cancellationToken);
            copier.PreserveUnreferencedObjects(document.Engine, excluded, diagnostics, cancellationToken);
        }

        (PdfReference? singleInfo, PdfReference? singleMetadata) = CopyCatalogExtras(copier, document.Engine, removeInfo, removeMetadata, diagnostics);
        PdfReference? singleOutlines = Content.CatalogCopy.TryCopyOutlines(document.Engine, copier, preserveCatalog, linearize, governor, cancellationToken);
        (PdfReference? singleStructTree, QpdfObject? singleMarkInfo) = Content.CatalogCopy.TryCopyStructure(document.Engine, copier, preserveCatalog, removeStructure, governor, cancellationToken);
        Dictionary<GraphCopier, PdfDictionary?> singleFormResources = new();
        if (!removeAcroform)
        {
            PdfDictionary? singleDefaultResources = Content.AcroFormCopy.TryCopyFormDR(document.Engine, copier, governor, cancellationToken);
            if (singleDefaultResources is not null)
            {
                singleFormResources[copier] = singleDefaultResources;
            }
        }

        Dictionary<PdfName, QpdfObject> singleExtras = Content.CatalogCopy.TryCopyExtras(document.Engine, copier, preserveCatalog, removeAcroform, governor, cancellationToken);
        SortedDictionary<string, PdfReference> singleExisting = PreserveExistingAttachments(copier, document.Engine, edits, limits, governor, diagnostics, copyMemory, cancellationToken);
        string? singlePageMode = ReadInputPageMode(document.Engine);

        (bool singleCompress, string singleDecode, int? singleLevel) = ResolveCompression(compression, qdf.Enabled);
        if (singleCompress || singleDecode != "none" || (singleMetadata is not null && compression.CompressStreams is not null))
        {
            TransformCopierStreams(copier, document.Engine, singleCompress, singleDecode, compression.RecompressFlate, singleLevel, limits, governor, diagnostics, cancellationToken, singleMetadata);
        }

        List<int> occurrenceIds = new(occurrences.Count);
        foreach ((GraphCopier _, int occurrenceId, PdfDictionary _) in occurrences)
        {
            occurrenceIds.Add(occurrenceId);
        }

        ApplySourceContentTransforms(copier, occurrenceIds, coalesceContents, normalizeContent, externalizeInlineImages || (optimize.Optimize && !optimize.KeepInline), iiMinBytes, singleCompress, singleLevel, StreamTransformer.Options(singleDecode, compression.RecompressFlate), prune, document.Engine, limits, governor, diagnostics, cancellationToken, preserveUnreferenced);

        List<(GraphCopier Copier, int Id)> overlayOccurrences = new(occurrences.Count);
        foreach ((GraphCopier occurrenceCopier, int occurrenceId, PdfDictionary _) in occurrences)
        {
            overlayOccurrences.Add((occurrenceCopier, occurrenceId));
        }

        Content.OverlayApplication overlayApplied = await Content.Overlays.ApplyAsync(host, inputPath, overlayOccurrences, underlays, overlays, singleCompress, singleLevel, recovery, limits, governor, diagnostics, copyMemory, cancellationToken, copier.NextId, null).ConfigureAwait(false);

        int flattenAllocator = overlayApplied.NextId;
        bool needAppearances = (flattenAnnotations is not null || generateAppearances) && Content.FlattenAnnotations.NeedsAppearances(document.Engine);
        bool singleGenerated = generateAppearances && needAppearances;
        if (singleGenerated)
        {
            Content.CopiedForm? gateForm = Content.AcroFormCopy.TryCopySourceForm(document.Engine, copier, KeptLocations(document, requested), true, limits, governor, diagnostics, cancellationToken);
            HashSet<int> gateSurvivors = Content.AcroFormCopy.CollectAnnotRefs(overlayOccurrences, governor, cancellationToken);
            List<(IReadOnlyDictionary<int, QpdfObject> Pool, Content.CopiedForm? Part)> gateParts = new(1 + overlayApplied.FieldImports.Count)
            {
                (copier.CopiedObjects, gateForm),
            };
            foreach (Content.AcroFormCopy.OverlayFieldImport import in overlayApplied.FieldImports)
            {
                gateParts.Add((import.Copier.CopiedObjects, import.Part));
            }

            HashSet<int>? gateReached = CollectGenerateReached(gateParts, gateSurvivors, true, limits, governor, cancellationToken);
            flattenAllocator = Content.GenerateAppearances.Apply(overlayOccurrences, singleFormResources, singleCompress, singleLevel, limits, governor, diagnostics, cancellationToken, gateReached, false, flattenAllocator);
            needAppearances = false;
        }
        if (flattenAnnotations is not null)
        {
            flattenAllocator = Content.FlattenAnnotations.Apply(overlayOccurrences, flattenAnnotations, needAppearances, singleCompress, singleLevel, limits, governor, diagnostics, cancellationToken, flattenAllocator);
        }
        if (flattenRotation)
        {
            flattenAllocator = Content.FlattenRotation.Apply(overlayOccurrences, singleCompress, singleLevel, limits, governor, diagnostics, cancellationToken, flattenAllocator);
        }
        if (optimize.Optimize)
        {
            Content.ImageOptimizer.Apply(overlayOccurrences, optimize, limits, governor, cancellationToken);
        }


        (PdfVersion negotiated, int negotiatedExtension) = NegotiateVersion(document.OwnVersion(), minVersion, forceVersion, governor);
        string streamMode = ResolveObjectStreamMode(objectStreams);
        if (forceVersion is not null && CompareVersions(negotiated.Major, negotiated.Minor, 1, 5) < 0)
        {
            streamMode = "disable";
        }

        HashSet<int> compressedOrig = CollectCompressedOriginals(document.Engine);
        Dictionary<int, QpdfObject> singlePool = new(copier.CopiedObjects);
        foreach (GraphCopier overlayCopier in overlayApplied.Copiers)
        {
            foreach (KeyValuePair<int, QpdfObject> overlayEntry in overlayCopier.CopiedObjects)
            {
                singlePool[overlayEntry.Key] = overlayEntry.Value;
            }
        }
        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> singleSources = new(2)
        {
            (copier.SourceToDestination, compressedOrig),
        };
        AttachmentAssembly singleAttached = await AssembleAttachmentsAsync(host, singleExisting, singlePool, edits, singlePageMode, singleCompress, singleLevel, singleSources, recovery, limits, governor, diagnostics, copyMemory, cancellationToken).ConfigureAwait(false);
        HashSet<int> singleCompressed = ResolveCompressedSet(streamMode, singlePool, singleSources, governor);
        PdfVersion effective = negotiated;
        int extensionLevel = negotiatedExtension;
        if (singleCompressed.Count > 0 && CompareVersions(effective.Major, effective.Minor, 1, 5) < 0)
        {
            effective = governor.OwnVersion(1, 5);
            extensionLevel = 0;
        }

        PdfDictionary? singleLabels = null;
        if (labels.Count > 0)
        {
            singleLabels = BuildPageLabels(labels, kids.Count, governor);
        }
        else if (!removeLabels && Content.PageLabelMerge.HasLabels(document.Engine))
        {
            Content.SourceLabels singleSource = Content.PageLabelMerge.ReadSource(document.Engine, copier, limits, governor, diagnostics, cancellationToken);
            List<(Content.SourceLabels Source, int Page)> singleOccurrences = new(requested.Count);
            foreach (int pageNumber in requested)
            {
                singleOccurrences.Add((singleSource, pageNumber));
            }

            List<(int Index, PdfDictionary Label)> singleItems = Content.PageLabelMerge.Merge(singleOccurrences, limits, governor, cancellationToken, copyMemory);
            singleLabels = singleItems.Count == 0 ? null : NumbersDictionary(singleItems, governor);
        }

        byte[]? originalId = TryGetOriginalId(document.Engine);
        string countText = kids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PdfDictionary? singleAcroForm = removeAcroform
            ? null
            : Content.AcroFormCopy.BuildSingle(
                document.Engine,
                copier,
                KeptLocations(document, requested),
                true,
                overlayOccurrences,
                singlePool,
                overlayApplied.FieldImports,
                limits,
                governor,
                diagnostics,
                cancellationToken,
                false);
        if (singleGenerated)
        {
            singleAcroForm = ClearNeedAppearances(singleAcroForm, governor);
        }

        PdfDictionary catalog = NewCatalog(singleLabels, effective, extensionLevel, singleMetadata, singleAttached.NamesRef, singleAttached.PageMode, singleOutlines, singleAcroForm, singleStructTree, singleMarkInfo, singleExtras);
        PdfDictionary pages = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Pages"),
            [PdfName.FromAscii("Kids")] = new PdfArray([.. kids]),
            [PdfName.FromAscii("Count")] = new PdfInteger(countText, kids.Count),
        });
        List<NumberedObject> model = new()
        {
            new NumberedObject(1, catalog),
            new NumberedObject(2, pages),
        };
        List<int> ids = new(singlePool.Keys);
        ids.Sort();
        foreach (int id in ids)
        {
            model.Add(new NumberedObject(id, singlePool[id]));
        }

        List<List<int>> singleGroups = PlanObjectStreamGroups(singleCompressed, governor);
        IReadOnlyList<IReadOnlyList<int>>? singleStreams = singleGroups.Count == 0 ? null : singleGroups;
        List<IReadOnlyDictionary<int, int>> singleOriginalMaps = new() { copier.SourceToDestination };
        foreach (GraphCopier overlayCopier in overlayApplied.Copiers)
        {
            singleOriginalMaps.Add(overlayCopier.SourceToDestination);
        }

        Dictionary<int, int> singleOriginalIds = InvertOriginals(singleOriginalMaps);
        using OwnedAllocationBatch resultStorage = OwnedAllocationBatch.Create(governor, 2, cancellationToken);
        IReadOnlyList<int> sourcePages = ResultCollectionStorage.Copy(resultStorage, requested, sizeof(int));
        resultStorage.RetainWithPrepaidRetirement();
        await PublishModelAsync(host, effective, extensionLevel, model, singleStreams, singleCompress, singleLevel, singleInfo?.ObjectNumber, originalId, staticId, newlineBeforeEndstream, qdf, singleOriginalIds, linearize, linearizePass1, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
        return new PageSelectionOutcome(total, sourcePages, kids.Count, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
    }

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

        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(PdfMultiPageSelectionResult.StorageBytes);
        try
        {
            using TypedSelectionPreparation preparation = TypedSelectionPreparation.Create(governor);
            preparation.FreezeSources(sources);
            DiagnosticBag diagnostics = new(governor);
            MultiPageSelectionOutcome result = (await SelectPagesCoreAsync(host, preparation.Sources, outputPath, preparation.Sources[0].Input, true, limits, QpdfRecoveryMode.Strict, null, [], [], false, null, null, false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, false, null, cancellationToken).ConfigureAwait(false)).Result;
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

        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(PdfMultiPageSelectionResult.StorageBytes);
        try
        {
            using TypedSelectionPreparation preparation = TypedSelectionPreparation.Create(governor);
            preparation.FreezeSources(sources);
            preparation.ConvertRotations(rotations);
            DiagnosticBag diagnostics = new(governor);
            MultiPageSelectionOutcome result = (await SelectPagesCoreAsync(host, preparation.Sources, outputPath, preparation.Sources[0].Input, true, limits, QpdfRecoveryMode.Strict, null, preparation.Rotations, [], false, null, null, false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, false, null, cancellationToken).ConfigureAwait(false)).Result;
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
        ArgumentNullException.ThrowIfNull(mergeOptions.Rotations, "rotations");
        ArgumentNullException.ThrowIfNull(mergeOptions.Labels, "labels");
        if (sources.Count == 0) { throw new ArgumentException("At least one page source is required.", nameof(sources)); }

        ResourceGovernor governor = new(limits, cancellationToken);
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
            MultiPageSelectionOutcome result = (await SelectPagesCoreAsync(host, preparation.Sources, outputPath, preparation.Sources[0].Input, true, limits, QpdfRecoveryMode.Strict, mergeOptions.Collation, preparation.Rotations, preparation.Labels, false, null, mergeOptions.SplitPages, false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, false, null, cancellationToken).ConfigureAwait(false)).Result;
            preparation.Dispose();
            PdfMultiPageSelectionResult presented = result.Present(governor);
            governor.FinishTypedCounters();
            return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    private static async ValueTask<(MultiPageSelectionOutcome Result, int AttachmentCount)> SelectPagesCoreAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        QpdfPath primaryInput,
        bool pruneUnreachableFields,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        PdfPageCollation? collation,
        IReadOnlyList<OutputRotation> rotations,
        IReadOnlyList<PageLabelSpec> labels,
        bool removeLabels,
        QpdfObject? verbatimLabels,
        int? splitPages,
        bool replaceInput,
        string? minVersion,
        string? forceVersion,
        bool deterministicId,
        bool staticId,
        bool newlineBeforeEndstream,
        string? objectStreams,
        bool preserveUnreferenced,
        CompressionRequest compression,
        bool removeInfo,
        bool removeAcroform,
        bool removeStructure,
        bool removeMetadata,
        AttachmentEdits edits,
        bool coalesceContents,
        bool normalizeContent,
        bool externalizeInlineImages,
        long iiMinBytes,
        IReadOnlyList<OverlaySpec> underlays,
        IReadOnlyList<OverlaySpec> overlays,
        bool flattenRotation,
        string? flattenAnnotations,
        bool generateAppearances,
        Content.ImageOptimizeRequest optimize,
        Writing.QdfOptions qdf,
        Content.ContentPrunePolicy prune,
        bool preserveCatalog,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        bool linearize,
        QpdfPath? linearizePass1,
        bool emptyPrimary,
        QpdfDocument? preparedSource,
        CancellationToken cancellationToken)
    {
        if (preparedSource is not null
            && (sources.Count != 1 || preparedSource.Path.Value != sources[0].Input.Value))
        { throw new InvalidOperationException("A prepared document requires its single original source."); }
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        using ParserMemoryScope selectionMemory = ParserMemoryScope.Create(governor);
        List<int> sourceCounts = ParserContainers.CreateList<int>(selectionMemory);
        List<List<(GraphCopier Copier, int Id, PdfDictionary Page, int SourcePage)>> perSource = new(sources.Count);
        int nextId = 3;
        bool needAppearances = false;
        List<GraphCopier> copiers = new(sources.Count);
        PdfVersion? version = null;
        byte[]? originalId1 = null;
        List<HashSet<int>> sourceCompressed = new(sources.Count);
        HashSet<int> allPreserved = new();
        List<Content.CopiedForm?> multiForms = new(sources.Count);
        List<Content.CopiedForm?> gateForms = new(sources.Count);
        List<Content.SourceLabels?> multiLabels = new(sources.Count);
        QpdfObject? verbatimCopied = null;
        bool needSourceLabels = !removeLabels && (labels.Count == 0 || splitPages.HasValue);
        SortedDictionary<string, PdfReference> primaryExisting = new(StringComparer.Ordinal);
        string? primaryPageMode = null;
        int primaryIdx = 0;
        for (int p = 0; p < sources.Count; p++)
        {
            if (sources[p].Input.Value == primaryInput.Value)
            {
                primaryIdx = p;
                break;
            }
        }

        PdfReference? primaryInfo = null;
        PdfReference? primaryMetadata = null;
        PdfReference? primaryOutlines = null;
        PdfReference? primaryStructTree = null;
        QpdfObject? primaryMarkInfo = null;
        Dictionary<PdfName, QpdfObject> primaryExtras = new();
        HashSet<int> catalogExtraIds = new();
        long selected = 0L;
        (bool multiCompress, string multiDecode, int? multiLevel) = ResolveCompression(compression, qdf.Enabled);
        for (int s = 0; s < sources.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using QpdfDocument document = preparedSource
                ?? await QpdfDocument.OpenCore(host, sources[s].Input, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
            if (externalizeInlineImages || (optimize.Optimize && !optimize.KeepInline)) { document.PrepareInlinePageResources(cancellationToken); }
            ParserContainers.AddItem(selectionMemory, sourceCounts, document.PageReferences.Count, sizeof(int));
            PdfVersion sourceVersion = document.OwnVersion();
            if (version is null || IsNewerVersion(sourceVersion, version))
            {
                version = sourceVersion;
            }

            if (s == 0)
            {
                originalId1 = TryGetOriginalId(document.Engine);
            }

            sourceCompressed.Add(CollectCompressedOriginals(document.Engine));

            IReadOnlyList<int> requested = sources[s].Selection.Resolve(document.PageReferences.Count, limits.MaxWork);
            foreach (int pageNumber in requested)
            {
                if (pageNumber > document.PageReferences.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(sources), $"Source {s} page {pageNumber} exceeds the page count of {document.PageReferences.Count}.");
                }
            }

            selected += requested.Count;
            if (selected > limits.MaxWork)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), selected, limits.MaxWork);
            }

            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, nextId, selectionMemory);
            foreach (int pageNumber in requested)
            {
                copier.RegisterPageRoot(document.PageReferences[pageNumber - 1].Source?.ObjectNumber);
            }
            List<(GraphCopier Copier, int Id, PdfDictionary Page, int SourcePage)> taken = new(requested.Count);
            foreach (int pageNumber in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                PageLocation location = document.PageReferences[pageNumber - 1];
                Dictionary<PdfName, QpdfObject> extra = MaterializeAncestors(document.Engine, location, copier, governor, limits, cancellationToken, selectionMemory);
                (int pageId, PdfDictionary page) = copier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra);
                RequireMediaBox(page);
                taken.Add((copier, pageId, page, pageNumber));
            }

            if (preserveUnreferenced && s == primaryIdx && !emptyPrimary)
            {
                HashSet<int> excluded = PreservationExclusions(document.Engine, KeptLocations(document, requested), limits, governor, cancellationToken);
                foreach (int preserved in copier.PreserveUnreferencedObjects(document.Engine, excluded, diagnostics, cancellationToken))
                {
                    allPreserved.Add(preserved);
                }
            }

            Content.CopiedForm? sourceForm = removeAcroform
                ? null
                : Content.AcroFormCopy.TryCopySourceForm(
                    document.Engine,
                    copier,
                    KeptLocations(document, requested),
                    pruneUnreachableFields,
                    limits,
                    governor,
                    diagnostics,
                    cancellationToken);
            multiForms.Add(sourceForm);
            gateForms.Add(removeAcroform
                ? Content.AcroFormCopy.TryCopySourceForm(document.Engine, copier, KeptLocations(document, requested), pruneUnreachableFields, limits, governor, diagnostics, cancellationToken)
                : sourceForm);
            if (needSourceLabels)
            {
                multiLabels.Add(Content.PageLabelMerge.ReadSource(document.Engine, copier, limits, governor, diagnostics, cancellationToken));
            }
            else
            {
                multiLabels.Add(null);
            }

            if (verbatimLabels is not null && s == 0)
            {
                verbatimCopied = copier.CopyValue(verbatimLabels, 0);
            }

            if (s == primaryIdx && !emptyPrimary)
            {
                (primaryInfo, primaryMetadata) = CopyCatalogExtras(copier, document.Engine, removeInfo, removeMetadata, diagnostics);
                primaryOutlines = Content.CatalogCopy.TryCopyOutlines(document.Engine, copier, preserveCatalog, linearize, governor, cancellationToken);
                (primaryStructTree, primaryMarkInfo) = Content.CatalogCopy.TryCopyStructure(document.Engine, copier, preserveCatalog, removeStructure, governor, cancellationToken);
                primaryExtras = Content.CatalogCopy.TryCopyExtras(document.Engine, copier, preserveCatalog, removeAcroform, governor, cancellationToken);
                if (primaryInfo is not null)
                {
                    catalogExtraIds.Add(primaryInfo.ObjectNumber);
                }

                if (primaryMetadata is not null)
                {
                    catalogExtraIds.Add(primaryMetadata.ObjectNumber);
                }
                if (primaryOutlines is not null)
                {
                    catalogExtraIds.Add(primaryOutlines.ObjectNumber);
                }

                if (primaryStructTree is not null)
                {
                    catalogExtraIds.Add(primaryStructTree.ObjectNumber);
                }

                if (primaryMarkInfo is PdfReference primaryMarkRef)
                {
                    catalogExtraIds.Add(primaryMarkRef.ObjectNumber);
                }

                primaryExisting = PreserveExistingAttachments(copier, document.Engine, edits, limits, governor, diagnostics, selectionMemory, cancellationToken);
                primaryPageMode = ReadInputPageMode(document.Engine);
            }

            if (multiCompress || multiDecode != "none" || (s == primaryIdx && primaryMetadata is not null && compression.CompressStreams is not null))
            {
                TransformCopierStreams(copier, document.Engine, multiCompress, multiDecode, compression.RecompressFlate, multiLevel, limits, governor, diagnostics, cancellationToken, s == primaryIdx ? primaryMetadata : null);
            }

            List<int> takenIds = new(taken.Count);
            foreach ((GraphCopier _, int takenId, PdfDictionary _, int _) in taken)
            {
                takenIds.Add(takenId);
            }

            ApplySourceContentTransforms(copier, takenIds, coalesceContents, normalizeContent, externalizeInlineImages || (optimize.Optimize && !optimize.KeepInline), iiMinBytes, multiCompress, multiLevel, StreamTransformer.Options(multiDecode, compression.RecompressFlate), prune, document.Engine, limits, governor, diagnostics, cancellationToken, preserveUnreferenced);

            if ((flattenAnnotations is not null || generateAppearances) && Content.FlattenAnnotations.NeedsAppearances(document.Engine))
            {
                needAppearances = true;
            }

            nextId = copier.NextId;
            copiers.Add(copier);
            perSource.Add(taken);
            // Copied containers still borrow immutable source scalars/keys.
            // Keep their coarse admission until this complete job releases them.
            document.Engine.TransferExclusiveValuesTo(selectionMemory);
        }

        List<(int Source, int Slot)> order = CollateOrder(perSource, collation);
        List<PdfPageSelectionMapping> mapping = ParserContainers.CreateList<PdfPageSelectionMapping>(selectionMemory);
        List<PdfReference> kids = new(order.Count);
        List<(GraphCopier Copier, int Id, PdfDictionary Page)> occurrences = new(order.Count);
        foreach ((int source, int slot) in order)
        {
            (GraphCopier copier, int pageId, PdfDictionary page, int pageNumber) = perSource[source][slot];
            kids.Add(new PdfReference(pageId, 0));
            selectionMemory.Reserve(24L);
            ParserContainers.AddItem(selectionMemory, mapping, new PdfPageSelectionMapping(source, pageNumber), IntPtr.Size);
            occurrences.Add((copier, pageId, page));
        }

        ApplyRotations(occurrences, ResolveRotationFolds(rotations, kids.Count, governor, limits, selectionMemory), governor, selectionMemory);

        List<(GraphCopier Copier, int Id)> overlayOccurrences = new(occurrences.Count);
        foreach ((GraphCopier occurrenceCopier, int occurrenceId, PdfDictionary _) in occurrences)
        {
            overlayOccurrences.Add((occurrenceCopier, occurrenceId));
        }

        Content.OverlayApplication overlayApplied = await Content.Overlays.ApplyAsync(host, primaryInput, overlayOccurrences, underlays, overlays, multiCompress, multiLevel, recovery, limits, governor, diagnostics, selectionMemory, cancellationToken, nextId, null).ConfigureAwait(false);

        int multiFlattenAllocator = overlayApplied.NextId;
        bool multiGenerated = generateAppearances && needAppearances;
        if (multiGenerated)
        {
            Dictionary<GraphCopier, PdfDictionary?> multiFormResources = new();
            for (int s = 0; s < copiers.Count && s < multiForms.Count; s++)
            {
                if (multiForms[s]?.DefaultResources is PdfDictionary sourceResources)
                {
                    multiFormResources[copiers[s]] = sourceResources;
                }
            }

            HashSet<int> multiSurvivors = Content.AcroFormCopy.CollectAnnotRefs(overlayOccurrences, governor, cancellationToken);
            List<(IReadOnlyDictionary<int, QpdfObject> Pool, Content.CopiedForm? Part)> multiGateParts = new(copiers.Count + overlayApplied.FieldImports.Count);
            for (int s = 0; s < copiers.Count && s < gateForms.Count; s++)
            {
                multiGateParts.Add((copiers[s].CopiedObjects, gateForms[s]));
            }
            foreach (Content.AcroFormCopy.OverlayFieldImport import in overlayApplied.FieldImports)
            {
                multiGateParts.Add((import.Copier.CopiedObjects, import.Part));
            }

            HashSet<int>? multiReached = CollectGenerateReached(multiGateParts, multiSurvivors, pruneUnreachableFields, limits, governor, cancellationToken);
            multiFlattenAllocator = Content.GenerateAppearances.Apply(overlayOccurrences, multiFormResources, multiCompress, multiLevel, limits, governor, diagnostics, cancellationToken, multiReached, !pruneUnreachableFields, multiFlattenAllocator);
            needAppearances = false;
        }
        if (flattenAnnotations is not null)
        {
            multiFlattenAllocator = Content.FlattenAnnotations.Apply(overlayOccurrences, flattenAnnotations, needAppearances, multiCompress, multiLevel, limits, governor, diagnostics, cancellationToken, multiFlattenAllocator);
        }

        if (flattenRotation)
        {
            nextId = Content.FlattenRotation.Apply(overlayOccurrences, multiCompress, multiLevel, limits, governor, diagnostics, cancellationToken, multiFlattenAllocator);
        }
        if (optimize.Optimize)
        {
            Content.ImageOptimizer.Apply(overlayOccurrences, optimize, limits, governor, cancellationToken);
        }

        if (replaceInput && splitPages.HasValue)
        {
            throw new QpdfParseException("--split-pages may not be used with --replace-input.", null, null);
        }

        if (splitPages.HasValue && splitPages.Value < 1)
        {
            throw new QpdfParseException("--split-pages needs a positive group size.", null, null);
        }

        QpdfPath target = outputPath;
        if (replaceInput)
        {
            HashSet<string> distinct = new(StringComparer.Ordinal);
            foreach (PdfPageSource source in sources)
            {
                distinct.Add(source.Input.Value);
            }

            if (distinct.Count != 1)
            {
                throw new QpdfParseException("--replace-input needs a single distinct input.", null, null);
            }

            target = sources[0].Input;
        }
        else
        {
            foreach (PdfPageSource source in sources)
            {
                if (source.Input.Value == outputPath.Value)
                {
                    throw new QpdfParseException("Output aliases an input; use --replace-input to overwrite it.", null, null);
                }
            }
        }

        List<Content.CopiedForm?> renameForms = multiForms;
        List<GraphCopier> renameCopiers = copiers;
        if (!removeAcroform && overlayApplied.FieldImports.Count > 0)
        {
            renameForms = new List<Content.CopiedForm?>(multiForms);
            renameCopiers = new List<GraphCopier>(copiers);
            foreach (Content.AcroFormCopy.OverlayFieldImport import in overlayApplied.FieldImports)
            {
                renameForms.Add(import.Part);
                renameCopiers.Add(import.Copier);
            }
        }

        if (!removeAcroform)
        {
            Content.AcroFormCopy.RenameCollidingFields(renameForms, renameCopiers, limits, governor, cancellationToken);
        }

        Dictionary<int, QpdfObject> pool = new();
        foreach (GraphCopier copier in copiers)
        {
            List<int> ids = new(copier.CopiedObjects.Keys);
            ids.Sort();
            foreach (int id in ids)
            {
                pool[id] = copier.CopiedObjects[id];
            }
        }

        foreach (GraphCopier overlayCopier in overlayApplied.Copiers)
        {
            foreach (KeyValuePair<int, QpdfObject> overlayEntry in overlayCopier.CopiedObjects)
            {
                pool[overlayEntry.Key] = overlayEntry.Value;
            }
        }

        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> multiSources = new(copiers.Count + 1);
        for (int s = 0; s < copiers.Count; s++)
        {
            multiSources.Add((copiers[s].SourceToDestination, sourceCompressed[s]));
        }

        AttachmentAssembly multiAttached = await AssembleAttachmentsAsync(host, primaryExisting, pool, edits, primaryPageMode, multiCompress, multiLevel, multiSources, recovery, limits, governor, diagnostics, selectionMemory, cancellationToken).ConfigureAwait(false);

        List<(int Index, PdfDictionary Label)> explicitItems = labels.Count > 0
            ? ResolveLabelItems(labels, kids.Count, governor, selectionMemory)
            : new List<(int Index, PdfDictionary Label)>();
        List<(int Index, PdfDictionary Label)> labelItems = explicitItems;
        QpdfObject? catalogLabels = labelItems.Count == 0 ? null : NumbersDictionary(labelItems, governor);
        if (removeLabels)
        {
            labelItems = new List<(int Index, PdfDictionary Label)>();
            catalogLabels = null;
        }
        else if (labels.Count == 0 || splitPages.HasValue)
        {
            bool anyLabels = false;
            foreach (Content.SourceLabels? part in multiLabels)
            {
                if (part is not null && part.HasTree)
                {
                    anyLabels = true;
                    break;
                }
            }

            List<(Content.SourceLabels Source, int Page)> labelOccurrences = new(mapping.Count);
            if (anyLabels)
            {
                foreach (PdfPageSelectionMapping entry in mapping)
                {
                    Content.SourceLabels? part = multiLabels[entry.SourceIndex];
                    if (part is null)
                    {
                        throw new QpdfException("Page-label source is missing for a selected page.");
                    }

                    labelOccurrences.Add((part, entry.SourcePage));
                }

                labelItems = Content.PageLabelMerge.Merge(labelOccurrences, limits, governor, cancellationToken, selectionMemory);
            }
            else
            {
                labelItems = new List<(int Index, PdfDictionary Label)>();
            }
            if (splitPages.HasValue || verbatimCopied is null)
            {
                catalogLabels = labelItems.Count == 0 ? null : NumbersDictionary(labelItems, governor);
            }
            else
            {
                catalogLabels = verbatimCopied;
            }
        }
        PdfVersion inputMax = version ?? governor.OwnVersion(1, 4);
        (PdfVersion negotiatedMulti, int negotiatedMultiExtension) = NegotiateVersion(inputMax, minVersion, forceVersion, governor);
        string multiStreamMode = ResolveObjectStreamMode(objectStreams);
        if (forceVersion is not null && CompareVersions(negotiatedMulti.Major, negotiatedMulti.Minor, 1, 5) < 0)
        {
            multiStreamMode = "disable";
        }

        HashSet<int> multiCompressed = ResolveCompressedSet(multiStreamMode, pool, multiSources, governor);
        PdfVersion effective = negotiatedMulti;
        int extensionLevel = negotiatedMultiExtension;
        if (multiCompressed.Count > 0 && CompareVersions(effective.Major, effective.Minor, 1, 5) < 0)
        {
            effective = governor.OwnVersion(1, 5);
            extensionLevel = 0;
        }

        List<List<int>> multiGroups = PlanObjectStreamGroups(multiCompressed, governor);
        IReadOnlyList<IReadOnlyList<int>>? multiStreams = multiGroups.Count == 0 ? null : multiGroups;
        List<IReadOnlyDictionary<int, int>> multiOriginalMaps = new();
        foreach (GraphCopier sourceCopier in copiers)
        {
            multiOriginalMaps.Add(sourceCopier.SourceToDestination);
        }

        foreach (GraphCopier overlayCopier in overlayApplied.Copiers)
        {
            multiOriginalMaps.Add(overlayCopier.SourceToDestination);
        }

        Dictionary<int, int> multiOriginalIds = InvertOriginals(multiOriginalMaps);
        Content.CopiedForm? multiCombined = removeAcroform
            ? null
            : Content.AcroFormCopy.Combine(NonNullForms(renameForms));
        if (multiCombined is not null && overlayApplied.FieldImports.Count > 0)
        {
            (string? multiDestAppearance, int? multiDestQuadding) = Content.AcroFormCopy.ReadMergedDefaults(multiCombined);
            int importAllocator = 1;
            foreach (int id in pool.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (id >= importAllocator)
                {
                    checked
                    {
                        importAllocator = id + 1;
                    }
                }
            }

            multiCombined = Content.AcroFormCopy.ApplyOverlayImports(
                multiCombined,
                overlayApplied.FieldImports,
                multiDestAppearance,
                multiDestQuadding,
                overlayApplied.FieldImports[0].Copier,
                pool,
                ref importAllocator,
                limits,
                governor,
                diagnostics,
                cancellationToken);
        }
        PdfDictionary? multiAcroForm = multiCombined is null
            ? null
            : Content.AcroFormCopy.BuildCatalogForm(
                multiCombined,
                pool,
                Content.AcroFormCopy.CollectAnnotRefs(overlayOccurrences, governor, cancellationToken),
                pruneUnreachableFields,
                limits,
                governor,
                cancellationToken);
        if (multiGenerated)
        {
            multiAcroForm = ClearNeedAppearances(multiAcroForm, governor);
        }

        if (!splitPages.HasValue)
        {
            List<NumberedObject> model = new()
            {
                new NumberedObject(1, NewCatalog(catalogLabels, effective, extensionLevel, primaryMetadata, multiAttached.NamesRef, multiAttached.PageMode, primaryOutlines, multiAcroForm, primaryStructTree, primaryMarkInfo, primaryExtras)),
                new NumberedObject(2, PagesNode(kids)),
            };
            List<int> orderedPoolIds = new(pool.Keys);
            orderedPoolIds.Sort();
            foreach (int orderedPoolId in orderedPoolIds)
            {
                model.Add(new NumberedObject(orderedPoolId, pool[orderedPoolId]));
            }

            using OwnedAllocationBatch resultStorage = OwnedAllocationBatch.Create(governor, checked(mapping.Count + 6), cancellationToken);
            ResultCollectionStorage.AdoptMappings(resultStorage, selectionMemory, mapping);
            IReadOnlyList<PdfPageSelectionMapping> resultMapping = ResultCollectionStorage.Copy(resultStorage, mapping, IntPtr.Size);
            IReadOnlyList<int> resultCounts = ResultCollectionStorage.Copy(resultStorage, sourceCounts, sizeof(int));
            IReadOnlyList<QpdfPath> resultPaths = ResultCollectionStorage.Single(resultStorage, target, IntPtr.Size);
            resultStorage.RetainWithPrepaidRetirement();
            await PublishModelAsync(host, effective, extensionLevel, model, multiStreams, multiCompress, multiLevel, primaryInfo?.ObjectNumber, originalId1, staticId, newlineBeforeEndstream, qdf, multiOriginalIds, linearize, linearizePass1, target, limits, governor, cancellationToken).ConfigureAwait(false);
            return (new MultiPageSelectionOutcome(kids.Count, resultMapping, resultCounts, resultPaths, ResultWarningSnapshot.Read(resultWarnings, diagnostics)), multiAttached.FinalCount);
        }

        List<string> names = GenerateSplitNames(target.Value, kids.Count, splitPages.Value, selectionMemory, governor);
        HashSet<string> distinctInputs = new(StringComparer.Ordinal);
        foreach (PdfPageSource source in sources)
        {
            distinctInputs.Add(source.Input.Value);
        }

        HashSet<string> seenNames = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (name.Length > QpdfPath.MaxLength)
            {
                throw new QpdfParseException("A split output name exceeds the path budget.", null, null);
            }

            if (distinctInputs.Contains(name))
            {
                throw new QpdfParseException($"Split output '{name}' aliases an input.", null, null);
            }

            if (!seenNames.Add(name))
            {
                throw new QpdfParseException($"Split outputs collide on '{name}'.", null, null);
            }
        }

        List<(int First, int Last)> groups = PartitionGroups(kids.Count, splitPages.Value);
        using PreparedSplitPublications preparation = PreparedSplitPublications.Create(governor);
        ParserMemoryScope presentationMemory = preparation.Memory;
        List<PreparedSplitPublication> prepared = preparation.Items;
        for (int g = 0; g < groups.Count; g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int first, int last) = groups[g];
            List<PdfReference> groupKids = kids.GetRange(first - 1, last - first + 1);
            List<int> groupIds = new(groupKids.Count);
            foreach (PdfReference kid in groupKids)
            {
                groupIds.Add(kid.ObjectNumber);
            }

            PdfDictionary? groupAcroForm = null;
            if (multiCombined is not null)
            {
                List<(GraphCopier Copier, int Id)> groupOccurrences = new(last - first + 1);
                for (int slot = first - 1; slot <= last - 1; slot++)
                {
                    groupOccurrences.Add((occurrences[slot].Copier, occurrences[slot].Id));
                }

                HashSet<int> groupAnnots = Content.AcroFormCopy.CollectAnnotRefs(groupOccurrences, governor, cancellationToken);
                groupAcroForm = Content.AcroFormCopy.BuildCatalogForm(multiCombined, pool, groupAnnots, pruneUnreachableFields, limits, governor, cancellationToken);
                if (multiGenerated)
                {
                    groupAcroForm = ClearNeedAppearances(groupAcroForm, governor);
                }
            }

            PdfDictionary? slicedLabels = SlicePageLabels(labelItems, first, last, governor);
            List<int> roots = new(groupIds);
            if (groupAcroForm is not null)
            {
                roots.AddRange(Content.AcroFormCopy.CollectRefNumbers(groupAcroForm, governor, cancellationToken));
            }

            if (slicedLabels is not null)
            {
                roots.AddRange(Content.AcroFormCopy.CollectRefNumbers(slicedLabels, governor, cancellationToken));
            }

            roots.AddRange(allPreserved);
            roots.AddRange(catalogExtraIds);
            roots.AddRange(multiAttached.ObjectIds);
            using ParserMemoryScope reachabilityMemory = ParserMemoryScope.Create(governor);
            HashSet<int> reachable = ReachableIds(pool, roots, reachabilityMemory, governor);
            if (preserveUnreferenced)
            {
                foreach (int id in pool.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
                    ParserContainers.AddSetItem(reachabilityMemory, reachable, id, sizeof(int));
                }
            }
            List<int> kept = new(reachable.Count);
            foreach (KeyValuePair<int, QpdfObject> entry in pool)
            {
                if (reachable.Contains(entry.Key))
                {
                    kept.Add(entry.Key);
                }
            }

            kept.Sort();
            Dictionary<int, int> renumber = new(kept.Count);
            for (int r = 0; r < kept.Count; r++)
            {
                renumber[kept[r]] = 3 + r;
            }

            List<PdfReference> renumberedKids = new(groupKids.Count);
            foreach (PdfReference kid in groupKids)
            {
                renumberedKids.Add(new PdfReference(renumber[kid.ObjectNumber], 0));
            }

            PdfReference? groupMetadata = null;
            if (primaryMetadata is not null && renumber.TryGetValue(primaryMetadata.ObjectNumber, out int mappedMetadata))
            {
                groupMetadata = new PdfReference(mappedMetadata, 0);
            }

            PdfReference? groupNames = null;
            if (multiAttached.NamesRef is not null && renumber.TryGetValue(multiAttached.NamesRef.ObjectNumber, out int mappedNames))
            {
                groupNames = new PdfReference(mappedNames, 0);
            }
            PdfReference? groupOutlines = null;
            if (primaryOutlines is not null && renumber.TryGetValue(primaryOutlines.ObjectNumber, out int mappedOutlines))
            {
                groupOutlines = new PdfReference(mappedOutlines, 0);
            }

            PdfReference? groupStructTree = null;
            if (primaryStructTree is not null && renumber.TryGetValue(primaryStructTree.ObjectNumber, out int mappedStruct))
            {
                groupStructTree = new PdfReference(mappedStruct, 0);
            }

            QpdfObject? groupMarkInfo = primaryMarkInfo;
            if (primaryMarkInfo is PdfReference markRef && !renumber.ContainsKey(markRef.ObjectNumber))
            {
                groupMarkInfo = null;
            }
            else if (primaryMarkInfo is PdfReference markRef2 && renumber.TryGetValue(markRef2.ObjectNumber, out int mappedMark))
            {
                groupMarkInfo = new PdfReference(mappedMark, 0);
            }

            int? groupInfo = null;
            if (primaryInfo is not null && renumber.TryGetValue(primaryInfo.ObjectNumber, out int mappedInfo))
            {
                groupInfo = mappedInfo;
            }

            PdfDictionary? remappedAcroForm = null;
            if (groupAcroForm is not null)
            {
                if (Writing.ObjectRemap.RemapIds(groupAcroForm, renumber, governor) is not PdfDictionary remapped)
                {
                    throw new QpdfException("Split publication rebuilt a non-dictionary form entry.");
                }

                remappedAcroForm = remapped;
            }
            QpdfObject? remappedLabels = null;
            if (slicedLabels is not null)
            {
                if (Writing.ObjectRemap.RemapIds(slicedLabels, renumber, governor) is not PdfDictionary remappedLabelsDictionary)
                {
                    throw new QpdfException("Split publication rebuilt a non-dictionary label tree.");
                }

                remappedLabels = remappedLabelsDictionary;
            }
            List<NumberedObject> sub = ParserContainers.CreateList<NumberedObject>(presentationMemory);
            NumberedObject groupCatalog = new(1, NewCatalog(remappedLabels, effective, extensionLevel, groupMetadata, groupNames, multiAttached.PageMode, groupOutlines, remappedAcroForm, groupStructTree, groupMarkInfo, null));
            NumberedObject groupPages = new(2, PagesNode(renumberedKids));
            ParserContainers.AddItem(presentationMemory, sub, groupCatalog, 16L);
            ParserContainers.AddItem(presentationMemory, sub, groupPages, 16L);
            foreach (int id in kept)
            {
                NumberedObject item = new(renumber[id], Writing.ObjectRemap.RemapIds(pool[id], renumber, governor));
                ParserContainers.AddItem(presentationMemory, sub, item, 16L);
            }

            QpdfPath path = QpdfPath.From(names[g]);
            HashSet<int> groupCompressed = RemapCompressedSet(multiCompressed, renumber);
            List<List<int>> groupGroups = PlanObjectStreamGroups(groupCompressed, governor);
            IReadOnlyList<IReadOnlyList<int>>? groupStreams = groupGroups.Count == 0 ? null : groupGroups;
            Dictionary<int, int> groupOriginalIds = new();
            foreach (int keptId in kept)
            {
                if (renumber.TryGetValue(keptId, out int renamed) && multiOriginalIds.TryGetValue(keptId, out int source))
                {
                    groupOriginalIds[renamed] = source;
                }
            }

            PreparedSplitPublication publication = await PreparePresentationOutputAsync(effective, sub, groupStreams, multiCompress,
                multiLevel, groupInfo, originalId1, staticId, newlineBeforeEndstream, qdf, groupOriginalIds,
                linearize, linearizePass1, path, limits, governor, cancellationToken).ConfigureAwait(false);
            try { ParserContainers.AddItem(presentationMemory, prepared, publication, IntPtr.Size); }
            catch { publication.Dispose(); throw; }
        }

        long publicationUnits = 0L;
        foreach (PreparedSplitPublication publication in prepared)
        { governor.CheckCancellation(); governor.ChargeWork(1L); publicationUnits = checked(publicationUnits + publication.PublicationWork); }
        using PublicationWorkReservation publicationWork = PublicationWorkReservation.Create(governor, publicationUnits);
        using OwnedAllocationBatch splitResultStorage = OwnedAllocationBatch.Create(governor, checked(mapping.Count + 6 + names.Count), cancellationToken);
        ResultCollectionStorage.AdoptMappings(splitResultStorage, selectionMemory, mapping);
        IReadOnlyList<PdfPageSelectionMapping> splitMapping = ResultCollectionStorage.Copy(splitResultStorage, mapping, IntPtr.Size);
        IReadOnlyList<int> splitCounts = ResultCollectionStorage.Copy(splitResultStorage, sourceCounts, sizeof(int));
        List<QpdfPath> outputPaths = ParserContainers.CreateList<QpdfPath>(selectionMemory);
        foreach (string name in names)
        {
            long bytes = GovernedText.StringBytes(name.Length);
            selectionMemory.TransferTo(splitResultStorage.Memory, bytes);
            splitResultStorage.Add(name, bytes);
            ParserContainers.AddItem(selectionMemory, outputPaths, QpdfPath.From(name), IntPtr.Size);
        }
        IReadOnlyList<QpdfPath> splitPaths = ResultCollectionStorage.Copy(splitResultStorage, outputPaths, IntPtr.Size);
        splitResultStorage.RetainWithPrepaidRetirement();
        governor.PrepareExecutionPresentation();
        publicationWork.Begin();
        foreach (PreparedSplitPublication publication in prepared)
        {
            await publication.PublishAsync(host, governor, cancellationToken).ConfigureAwait(false);
        }

        return (new MultiPageSelectionOutcome(kids.Count, splitMapping, splitCounts, splitPaths, ResultWarningSnapshot.Read(resultWarnings, diagnostics)), multiAttached.FinalCount);
    }

    private static List<List<(PdfRotationMode Mode, int Angle)>> ResolveRotationFolds(
        IReadOnlyList<OutputRotation> rotations,
        int total,
        ResourceGovernor governor,
        QpdfLimits limits)
        => ResolveRotationFolds(rotations, total, governor, limits, null);

    private static List<List<(PdfRotationMode Mode, int Angle)>> ResolveRotationFolds(
        IReadOnlyList<OutputRotation> rotations, int total, ResourceGovernor governor,
        QpdfLimits limits, ParserMemoryScope? ownership)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        List<List<(PdfRotationMode Mode, int Angle)>> folds = ownership is null
            ? new(total) : ParserContainers.CreateList<List<(PdfRotationMode Mode, int Angle)>>(ownership);
        for (int i = 0; i < total; i++)
        {
            if (ownership is null) { folds.Add(new()); }
            else
            {
                governor.ChargeWork(1L);
                ParserContainers.AddItem(ownership, folds, ParserContainers.CreateList<(PdfRotationMode, int)>(ownership), IntPtr.Size);
            }
        }

        foreach (OutputRotation specification in rotations)
        {
            IReadOnlyList<int> positions = ownership is null ? specification.Expand(total, limits.MaxWork)
                : Pages.PageRanges.Expand(specification.Range, total, limits.MaxWork, governor, ownership);
            foreach (int position in positions)
            {
                governor.ChargeWork(1L);
                if (ownership is null) { folds[position - 1].Add((specification.Mode, specification.Angle)); }
                else { ParserContainers.AddItem(ownership, folds[position - 1], (specification.Mode, specification.Angle), 8L); }
            }
        }

        return folds;
    }

    private static void ApplyRotations(
        List<(GraphCopier Copier, int Id, PdfDictionary Page)> occurrences,
        List<List<(PdfRotationMode Mode, int Angle)>> folds,
        ResourceGovernor governor)
        => ApplyRotations(occurrences, folds, governor, null);

    private static void ApplyRotations(
        List<(GraphCopier Copier, int Id, PdfDictionary Page)> occurrences,
        List<List<(PdfRotationMode Mode, int Angle)>> folds,
        ResourceGovernor governor, ParserMemoryScope? ownership)
    {
        ownership?.Reserve(128L);
        PdfName rotateKey = PdfName.FromAscii("Rotate");
        for (int i = 0; i < occurrences.Count; i++)
        {
            List<(PdfRotationMode Mode, int Angle)> specifications = folds[i];
            if (specifications.Count == 0)
            {
                continue;
            }

            int current = 0;
            if (occurrences[i].Page.TryGet(rotateKey, out QpdfObject raw))
            {
                if (raw is PdfNull)
                {
                    current = 0;
                }
                else if (raw is PdfInteger inherited)
                {
                    current = NormalizeRotation(inherited.Value);
                }
                else
                {
                    throw new QpdfParseException($"Output page {i + 1} has an invalid /Rotate value.", null, null);
                }
            }

            foreach ((PdfRotationMode mode, int angle) in specifications)
            {
                if (ownership is not null) { governor.ChargeWork(1L); }
                current = mode switch
                {
                    PdfRotationMode.Set => angle,
                    PdfRotationMode.Add => NormalizeRotation((long)current + angle),
                    PdfRotationMode.Subtract => NormalizeRotation((long)current - angle),
                    _ => throw new QpdfException($"Unknown rotation mode {(int)mode}."),
                };
            }

            if (ownership is null) { governor.ReserveMemory(64L); }
            Dictionary<PdfName, QpdfObject> entries = ownership is null ? new()
                : ParserContainers.CreateDictionary<PdfName, QpdfObject>(ownership);
            foreach (PdfName key in occurrences[i].Page.Keys)
            {
                if (occurrences[i].Page.TryGet(key, out QpdfObject value))
                {
                    if (ownership is not null)
                    {
                        governor.ChargeWork(1L);
                        ParserContainers.EnsureEntryCapacity(ownership, entries, IntPtr.Size);
                    }
                    entries[key] = value;
                }
            }

            if (ownership is not null)
            {
                ownership.Reserve(112L);
                if (!entries.ContainsKey(rotateKey)) { ParserContainers.EnsureEntryCapacity(ownership, entries, IntPtr.Size); }
            }
            entries[rotateKey] = new PdfInteger(
                current.ToString(System.Globalization.CultureInfo.InvariantCulture),
                current);
            occurrences[i].Copier.ReplaceCopiedObject(occurrences[i].Id, ownership is null
                ? new PdfDictionary(entries) : PdfDictionary.FromOwnedEntries(entries));
        }
    }

    private static int NormalizeRotation(long value)
    {
        return (int)(((value % 360L) + 360L) % 360L);
    }

    private static List<(int Source, int Slot)> CollateOrder(
        List<List<(GraphCopier Copier, int Id, PdfDictionary Page, int SourcePage)>> perSource,
        PdfPageCollation? collation)
    {
        List<int> counts = new(perSource.Count);
        foreach (var source in perSource) { counts.Add(source.Count); }
        return CollatePageOrder(counts, collation, null, null);
    }

    private static PdfDictionary PagesNode(List<PdfReference> groupKids)
    {
        string text = groupKids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Pages"),
            [PdfName.FromAscii("Kids")] = new PdfArray([.. groupKids]),
            [PdfName.FromAscii("Count")] = new PdfInteger(text, groupKids.Count),
        });
    }

    private static readonly byte[] StaticIdBytes = new byte[] { 0x31, 0x41, 0x59, 0x26, 0x53, 0x58, 0x97, 0x93, 0x23, 0x84, 0x62, 0x64, 0x33, 0x83, 0x27, 0x95 };

    private static readonly byte[] PlaceholderIdBytes = new byte[16];

    private static PdfDictionary NewCatalog(QpdfObject? pageLabels, PdfVersion finalVersion, int extensionLevel, PdfReference? metadataRef, PdfReference? namesRef, string? pageMode, PdfReference? outlinesRef, PdfDictionary? acroForm, PdfReference? structTreeRef, QpdfObject? markInfo, IReadOnlyDictionary<PdfName, QpdfObject>? extras)
    {
        if (finalVersion is null)
        {
            throw new ArgumentNullException(nameof(finalVersion));
        }

        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Catalog"),
            [PdfName.FromAscii("Pages")] = new PdfReference(2, 0),
        };
        if (pageLabels is not null)
        {
            entries[PdfName.FromAscii("PageLabels")] = pageLabels;
        }

        if (metadataRef is not null)
        {
            entries[PdfName.FromAscii("Metadata")] = metadataRef;
        }

        if (namesRef is not null)
        {
            entries[PdfName.FromAscii("Names")] = namesRef;
        }

        if (pageMode is not null)
        {
            entries[PdfName.FromAscii("PageMode")] = PdfName.FromAscii(pageMode);
        }

        if (outlinesRef is not null)
        {
            entries[PdfName.FromAscii("Outlines")] = outlinesRef;
        }

        if (acroForm is not null)
        {
            entries[PdfName.FromAscii("AcroForm")] = acroForm;
        }

        if (structTreeRef is not null)
        {
            entries[PdfName.FromAscii("StructTreeRoot")] = structTreeRef;
        }

        if (markInfo is not null && markInfo is not PdfNull)
        {
            entries[PdfName.FromAscii("MarkInfo")] = markInfo;
        }

        if (extensionLevel > 0)
        {
            string baseVersion = finalVersion.Major.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + finalVersion.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture);
            PdfDictionary adbe = new(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("BaseVersion")] = PdfName.FromAscii(baseVersion),
                [PdfName.FromAscii("ExtensionLevel")] = new PdfInteger(extensionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), extensionLevel),
            });
            PdfDictionary extensions = new(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("ADBE")] = adbe,
            });
            entries[PdfName.FromAscii("Extensions")] = extensions;
        }
        if (extras is not null)
        {
            foreach (KeyValuePair<PdfName, QpdfObject> extra in extras)
            {
                // Imported fields replace an opaque primary form, as they do
                // in the reference; the preserved array must not overwrite it.
                if (acroForm is not null && extra.Key.RawSpan.SequenceEqual("AcroForm"u8)) { continue; }
                entries[extra.Key] = extra.Value;
            }
        }

        return new PdfDictionary(entries);
    }

    private static PdfDictionary? BuildPageLabels(
        IReadOnlyList<PageLabelSpec> labels,
        int total,
        ResourceGovernor governor)
    {
        if (labels.Count == 0) { return null; }
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<(int Index, PdfDictionary Label)> items = ResolveLabelItems(labels, total, governor, memory);
        if (items.Count == 0)
        {
            return null;
        }

        return NumbersDictionary(items, governor);
    }

    private static List<(int Index, PdfDictionary Label)> ResolveLabelItems(
        IReadOnlyList<PageLabelSpec> labels,
        int total,
        ResourceGovernor governor,
        ParserMemoryScope memory)
    {
        memory.CheckOwner(governor);
        List<(int Index, PdfDictionary Label)> items = ParserContainers.CreateList<(int Index, PdfDictionary Label)>(memory);
        if (labels.Count == 0)
        {
            return items;
        }

        if (total == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(labels), "Page labels need at least one output page.");
        }

        foreach (PageLabelSpec specification in labels)
        {
            governor.ChargeWork(1L);
            int index = specification.ResolveFirstPage(total) - 1;
            PdfDictionary label = specification.ToDictionary(governor, governor.CancellationToken);
            GovernedCollections.AddItem(memory, governor, items, (index, label), 16L);
        }

        if (items[0].Index != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(labels), "The first page label must start at page 1.");
        }

        for (int i = 1; i < items.Count; i++)
        {
            if (items[i].Index <= items[i - 1].Index)
            {
                throw new ArgumentOutOfRangeException(nameof(labels), "Page labels must start on increasing pages.");
            }
        }

        return items;
    }

    private static PdfDictionary NumbersDictionary(List<(int Index, PdfDictionary Label)> items, ResourceGovernor governor)
        => Content.PageLabelStorage.NumberTree(items, governor, governor.CancellationToken);

    private static PdfDictionary? SlicePageLabels(List<(int Index, PdfDictionary Label)> items, int first, int last,
        ResourceGovernor governor)
    {
        if (items.Count == 0) { return null; }
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        (int Index, PdfDictionary Label) carry = items[0];
        List<(int Index, PdfDictionary Label)> sliced = ParserContainers.CreateList<(int Index, PdfDictionary Label)>(memory);
        // Admit the carry slot before construction; update it without shifting a second list.
        GovernedCollections.AddItem(memory, governor, sliced, (0, carry.Label), 16L);
        foreach ((int index, PdfDictionary label) in items)
        {
            governor.CheckCancellation(); governor.ChargeWork(1L);
            if (index + 1 <= first) { carry = (index, label); }
            else if (index + 1 <= last)
            { GovernedCollections.AddItem(memory, governor, sliced, (index - (first - 1), label), 16L); }
            else { break; }
        }
        sliced[0] = (0, carry.Label);
        return NumbersDictionary(sliced, governor);
    }

    private static List<(int First, int Last)> PartitionGroups(int total, int groupSize)
    {
        List<(int First, int Last)> groups = new();
        for (int first = 1; first <= total; first += groupSize)
        {
            groups.Add((first, Math.Min(first + groupSize - 1, total)));
        }

        return groups;
    }

    private static List<string> GenerateSplitNames(string pattern, int total, int groupSize,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        int width = 1;
        for (int remaining = total; remaining >= 10; remaining /= 10) { width++; }
        governor.CheckCancellation();
        governor.ChargeWork(pattern.Length);
        int marker = pattern.IndexOf("%d", StringComparison.Ordinal);
        bool extension = marker < 0 && pattern.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        int prefix = marker >= 0 ? marker : extension ? pattern.Length - 4 : pattern.Length;
        int suffix = marker >= 0 ? marker + 2 : prefix;
        List<string> names = ParserContainers.CreateList<string>(memory);
        for (int first = 1; first <= total;)
        {
            governor.CheckCancellation();
            int last = (int)Math.Min(total, (long)first + groupSize - 1L);
            int length = checked(prefix + pattern.Length - suffix + (marker < 0 ? 1 : 0)
                + width + (first == last ? 0 : width + 1));
            if (length > QpdfPath.MaxLength)
            { throw new QpdfParseException("A split output name exceeds the path budget.", null, null); }
            memory.Reserve(GovernedText.StringBytes(length));
            string name = string.Create(length, (pattern, prefix, suffix, marker, first, last, width, governor),
                static (target, state) =>
                {
                    CopySplitNamePart(state.pattern.AsSpan(0, state.prefix), target, state.governor);
                    int position = state.prefix;
                    if (state.marker < 0) { target[position++] = '-'; }
                    WriteSplitNumber(target.Slice(position, state.width), state.first, state.governor);
                    position += state.width;
                    if (state.first != state.last)
                    {
                        target[position++] = '-';
                        WriteSplitNumber(target.Slice(position, state.width), state.last, state.governor);
                        position += state.width;
                    }
                    CopySplitNamePart(state.pattern.AsSpan(state.suffix), target[position..], state.governor);
                });
            ParserContainers.AddItem(memory, names, name, IntPtr.Size);
            first = checked(last + 1);
        }
        return names;
    }

    private static void CopySplitNamePart(ReadOnlySpan<char> source, Span<char> target, ResourceGovernor governor)
    {
        for (int offset = 0; offset < source.Length; offset += Math.Min(256, source.Length - offset))
        {
            int count = Math.Min(256, source.Length - offset);
            governor.CheckCancellation(); governor.ChargeWork(count);
            source.Slice(offset, count).CopyTo(target.Slice(offset, count));
        }
    }

    private static void WriteSplitNumber(Span<char> target, int value, ResourceGovernor governor)
    {
        governor.CheckCancellation(); governor.ChargeWork(target.Length);
        target.Fill('0');
        int digits = 1;
        for (int remaining = value; remaining >= 10; remaining /= 10) { digits++; }
        if (!value.TryFormat(target[(target.Length - digits)..], out _, provider: System.Globalization.CultureInfo.InvariantCulture))
        { throw new InvalidOperationException("Split page number exceeds its prepared width."); }
    }

    private static HashSet<int> ReachableIds(Dictionary<int, QpdfObject> pool, List<int> roots,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        List<QpdfObject> references = ParserContainers.CreateList(memory);
        foreach (int root in roots)
        {
            governor.ChargeWork(1L);
            memory.Reserve(24L);
            ParserContainers.AddItem(memory, references, new PdfReference(root, 0));
        }
        PdfArray rootArray = ParserContainers.CopyArray(memory, references);
        return GraphReachability.Find(new Content.PoolResolver(pool), rootArray, memory, governor);
    }

    private sealed class MemorySink : IByteSink
    {
        private readonly List<byte> _bytes = new();
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;

        internal MemorySink(QpdfLimits limits, ResourceGovernor governor)
        {
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        }

        public long Length => _bytes.Count;

        public byte[] ToArray()
        {
            return [.. _bytes];
        }

        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            if ((long)_bytes.Count + chunk.Length > _limits.MaxOutputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), (long)_bytes.Count + chunk.Length, _limits.MaxOutputBytes);
            }

            ReadOnlySpan<byte> span = chunk.Span;
            for (int i = 0; i < span.Length; i++)
            {
                _bytes.Add(span[i]);
            }

            return ValueTask.CompletedTask;
        }
    }

    private static Dictionary<int, int> InvertOriginals(IEnumerable<IReadOnlyDictionary<int, int>> maps)
    {
        Dictionary<int, int> inverted = new();
        foreach (IReadOnlyDictionary<int, int> map in maps)
        {
            foreach (KeyValuePair<int, int> entry in map)
            {
                inverted[entry.Value] = entry.Key;
            }
        }

        return inverted;
    }

    private readonly record struct PageImage(PdfName Name, int Number, int Generation, long Width, long Height);

    private static List<PageImage> ListPageImages(
        DocumentEngine engine,
        PageLocation location,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ParserMemoryScope memory)
    {
        memory.CheckOwner(governor);
        List<PageImage> images = ParserContainers.CreateList<PageImage>(memory);
        PdfDictionary? resources = EffectivePageResources(engine, location, limits, governor, cancellationToken);
        if (resources is null)
        {
            return images;
        }

        if (!resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjectsRaw) || xobjectsRaw is PdfNull)
        {
            return images;
        }

        PdfDictionary? xobjects = xobjectsRaw as PdfDictionary;
        if (xobjects is null && xobjectsRaw is PdfReference xobjectsReference
            && ((IIndirectResolver)engine).TryResolve(xobjectsReference, out QpdfObject? xobjectsResolved))
        {
            xobjects = xobjectsResolved as PdfDictionary;
        }

        if (xobjects is null)
        {
            return images;
        }

        List<PdfName> names = ParserContainers.CreateList<PdfName>(memory);
        foreach (PdfName name in xobjects.Keys)
        {
            governor.CheckCancellation();
            governor.ChargeWork(1L);
            if (names.Count == names.Capacity) { governor.ChargeWork(checked(3L * (names.Count + 4L))); }
            ParserContainers.AddItem(memory, names, name, IntPtr.Size);
        }
        GovernedCollections.SortNames(memory, governor, names);
        foreach (PdfName key in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (!xobjects.TryGet(key, out QpdfObject entry) || entry is not PdfReference reference)
            {
                continue;
            }

            if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? target) || target is not PdfStream stream)
            {
                continue;
            }

            if (!stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype) || subtype is not PdfName subtypeName || !subtypeName.RawSpan.SequenceEqual("Image"u8))
            {
                continue;
            }

            long width = ImageDimension(stream.Dictionary, "Width", reference, engine, diagnostics, governor, cancellationToken);
            long height = ImageDimension(stream.Dictionary, "Height", reference, engine, diagnostics, governor, cancellationToken);
            if (images.Count == images.Capacity) { governor.ChargeWork(checked(3L * (images.Count + 4L))); }
            ParserContainers.AddItem(memory, images, new PageImage(key, reference.ObjectNumber, reference.Generation, width, height), 32L);
        }

        return images;
    }

    private static PdfDictionary? EffectivePageResources(
        DocumentEngine engine,
        PageLocation location,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        HashSet<int> visited = ParserContainers.CreateSet<int>(memory);
        if (location.Source is not null)
        {
            GovernedCollections.AddNumber(memory, governor, visited, location.Source.ObjectNumber);
        }

        PdfDictionary current = location.Page;
        int depth = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resourcesRaw) && resourcesRaw is not PdfNull)
            {
                if (resourcesRaw is PdfDictionary direct)
                {
                    return direct;
                }

                if (resourcesRaw is PdfReference resourcesReference
                    && ((IIndirectResolver)engine).TryResolve(resourcesReference, out QpdfObject? resourcesResolved)
                    && resourcesResolved is PdfDictionary resolved)
                {
                    return resolved;
                }

                return null;
            }

            if (!current.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parentRaw) || parentRaw is PdfNull)
            {
                return null;
            }

            PdfDictionary parent;
            if (parentRaw is PdfReference parentReference)
            {
                if (!GovernedCollections.AddNumber(memory, governor, visited, parentReference.ObjectNumber))
                {
                    throw new QpdfParseException($"Page ancestry cycle at object {parentReference.ObjectNumber}.", null, parentReference.ObjectNumber);
                }

                if (!((IIndirectResolver)engine).TryResolve(parentReference, out QpdfObject? resolved) || resolved is not PdfDictionary resolvedParent)
                {
                    throw new QpdfParseException("Unresolvable page parent.", null, parentReference.ObjectNumber);
                }

                parent = resolvedParent;
            }
            else if (parentRaw is PdfDictionary directParent)
            {
                parent = directParent;
            }
            else
            {
                throw new QpdfParseException("Page /Parent must be a dictionary or a reference.", null, null);
            }

            depth += 1;
            if (depth > limits.MaxNestingDepth)
            {
                throw new QpdfParseException($"Page ancestry depth exceeds {limits.MaxNestingDepth}.", null, null);
            }

            governor.ChargeWork(1L);
            current = parent;
        }
    }

    private static long ImageDimension(
        PdfDictionary image,
        string key,
        PdfReference reference,
        DocumentEngine engine,
        DiagnosticBag diagnostics,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.ChargeWork(1L);
        if (image.TryGet(PdfName.FromAscii(key), out QpdfObject raw) && raw is not PdfNull)
        {
            if (raw is PdfInteger direct)
            {
                return direct.Value;
            }

            if (raw is PdfReference indirect
                && ((IIndirectResolver)engine).TryResolve(indirect, out QpdfObject? resolved)
                && resolved is PdfInteger target)
            {
                return target.Value;
            }
        }

        diagnostics.AddParts(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, ["Image ", reference.ObjectNumber, " is missing /", key, "; using 0."]);
        return 0L;
    }

    private static void CheckJsonSchema(
        string json,
        int version,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        string help = version == 1 ? HelpText.JsonHelpV1 : HelpText.JsonHelpV2;
        HashSet<string> described = new(StringComparer.Ordinal);
        using (System.Text.Json.JsonDocument helpDocument = System.Text.Json.JsonDocument.Parse(help))
        {
            foreach (System.Text.Json.JsonProperty property in helpDocument.RootElement.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                described.Add(property.Name);
            }
        }

        List<string> missing = new();
        using (System.Text.Json.JsonDocument generated = System.Text.Json.JsonDocument.Parse(json))
        {
            foreach (System.Text.Json.JsonProperty property in generated.RootElement.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (!described.Contains(property.Name))
                {
                    missing.Add(property.Name);
                }
            }
        }

        if (missing.Count > 0)
        {
            diagnostics.AddParts(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error,
                ["JSON output has keys missing from --json-help version ", version, ": ", string.Join(", ", missing), "."]);
            return;
        }

        diagnostics.AddParts(QpdfDiagnosticCode.JsonSchemaCheck, QpdfDiagnosticSeverity.Info,
            ["JSON schema check passed for version ", version, "."]);
    }

    private static readonly PdfName InspectionContents = PdfName.FromAscii("Contents");

    private static List<PdfReference> PageContentRefs(PdfDictionary page,
        ResourceGovernor governor, ParserMemoryScope memory)
    {
        List<PdfReference> refs = ParserContainers.CreateList<PdfReference>(memory);
        if (page.TryGet(InspectionContents, out QpdfObject? contents) && contents is not null)
        {
            if (contents is PdfReference single)
            {
                ParserContainers.AddItem(memory, refs, single, IntPtr.Size);
            }
            else if (contents is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    governor.ChargeWork(1L);
                    if (array[i] is PdfReference reference)
                    {
                        ParserContainers.AddItem(memory, refs, reference, IntPtr.Size);
                    }
                }
            }
        }

        return refs;
    }

    private static async ValueTask PublishModelAsync(
        IQpdfHost host,
        PdfVersion version,
        int extensionLevel,
        List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>>? objectStreamGroups,
        bool compressStreams,
        int? compressionLevel,
        int? infoRef,
        byte[]? originalId1,
        bool staticId,
        bool newlineBeforeEndstream,
        Writing.QdfOptions qdf,
        IReadOnlyDictionary<int, int>? originalIds,
        bool linearize,
        QpdfPath? linearizePass1,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (linearize)
        {
            await PublishLinearizedAsync(host, version, model, objectStreamGroups, infoRef, originalId1, staticId, newlineBeforeEndstream, compressStreams, compressionLevel, linearizePass1, path, limits, governor, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] id1;
        byte[] id2;
        if (staticId)
        {
            id2 = (byte[])StaticIdBytes.Clone();
            id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
            await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, qdf, originalIds, path, limits, governor, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] placeholderId1 = originalId1 is null ? (byte[])PlaceholderIdBytes.Clone() : (byte[])originalId1.Clone();
        byte[] placeholderId2 = (byte[])PlaceholderIdBytes.Clone();
        PdfSerializer probe = new(limits, governor);
        MemorySink memory = new(limits, governor);
        if (qdf.Enabled)
        {
            await probe.SerializeQdfAsync(version, model, objectStreamGroups, originalIds, infoRef, placeholderId1, placeholderId2, qdf, newlineBeforeEndstream, memory, cancellationToken).ConfigureAwait(false);
        }
        else if (objectStreamGroups is null || objectStreamGroups.Count == 0)
        {
            await probe.SerializeAsync(version, model, infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, memory, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await probe.SerializeStreamedAsync(version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, memory, cancellationToken).ConfigureAwait(false);
        }
        byte[] placeholderBytes = memory.ToArray();
        byte[] digest = MD5.HashData(placeholderBytes);
        id2 = digest;
        id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
        await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, qdf, originalIds, path, limits, governor, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PublishLinearizedAsync(
        IQpdfHost host,
        PdfVersion version,
        List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>>? objectStreamGroups,
        int? infoRef,
        byte[]? originalId1,
        bool staticId,
        bool newlineBeforeEndstream,
        bool compressHints,
        int? compressionLevel,
        QpdfPath? linearizePass1,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        Writing.LinearizedWriter writer = new(limits, governor);
        byte[] id1;
        byte[] id2;
        if (staticId)
        {
            id2 = (byte[])StaticIdBytes.Clone();
            id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
        }
        else
        {
            byte[] placeholderId1 = originalId1 is null ? (byte[])PlaceholderIdBytes.Clone() : (byte[])originalId1.Clone();
            byte[] placeholderId2 = (byte[])PlaceholderIdBytes.Clone();
            Writing.LinearizedWriter probe = new(limits, governor);
            MemorySink memory = new(limits, governor);
            if (objectStreamGroups is not null && objectStreamGroups.Count > 0)
            {
                await probe.WriteLinearizedStreamedAsync(version, model, objectStreamGroups, infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, compressHints, compressionLevel, memory, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await probe.WriteLinearizedAsync(version, model, infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, compressHints, compressionLevel, memory, null, cancellationToken).ConfigureAwait(false);
            }

            byte[] digest = MD5.HashData(memory.ToArray());
            id2 = digest;
            id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
        }

        await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
        TransactionSink sink = new(transaction, governor);
        if (objectStreamGroups is not null && objectStreamGroups.Count > 0)
        {
            if (linearizePass1 is null)
            {
                await writer.WriteLinearizedStreamedAsync(version, model, objectStreamGroups, infoRef, id1, id2, newlineBeforeEndstream, compressHints, compressionLevel, sink, null, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await using IQpdfOutputTransaction streamedPass1 = await GovernedHost.CreateOutputAsync(host, linearizePass1.Value, governor, cancellationToken).ConfigureAwait(false);
            TransactionSink streamedPass1Sink = new(streamedPass1, governor);
            await writer.WriteLinearizedStreamedAsync(version, model, objectStreamGroups, infoRef, id1, id2, newlineBeforeEndstream, compressHints, compressionLevel, sink, streamedPass1Sink, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await streamedPass1.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (linearizePass1 is null)
        {
            await writer.WriteLinearizedAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream, compressHints, compressionLevel, sink, null, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using IQpdfOutputTransaction pass1Transaction = await GovernedHost.CreateOutputAsync(host, linearizePass1.Value, governor, cancellationToken).ConfigureAwait(false);
        TransactionSink pass1Sink = new(pass1Transaction, governor);
        await writer.WriteLinearizedAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream, compressHints, compressionLevel, sink, pass1Sink, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await pass1Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PublishWithIdsAsync(
        IQpdfHost host,
        PdfVersion version,
        List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>>? objectStreamGroups,
        bool compressStreams,
        int? compressionLevel,
        int? infoRef,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        Writing.QdfOptions qdf,
        IReadOnlyDictionary<int, int>? originalIds,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        PdfSerializer serializer = new(limits, governor);
        CountingSink counter = new();
        long counted;
        if (qdf.Enabled)
        {
            counted = await serializer.SerializeQdfAsync(version, model, objectStreamGroups, originalIds, infoRef, id1, id2, qdf, newlineBeforeEndstream, counter, cancellationToken).ConfigureAwait(false);
        }
        else if (objectStreamGroups is null || objectStreamGroups.Count == 0)
        {
            counted = await serializer.SerializeAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream, counter, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            counted = await serializer.SerializeStreamedAsync(version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, counter, cancellationToken).ConfigureAwait(false);
        }
        if (counted > limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), counted, limits.MaxOutputBytes);
        }

        governor.ReserveMemory(counted);
        try
        {
            await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
            TransactionSink sink = new(transaction, governor);
            long written;
            if (qdf.Enabled)
            {
                written = await serializer.SerializeQdfAsync(version, model, objectStreamGroups, originalIds, infoRef, id1, id2, qdf, newlineBeforeEndstream, sink, cancellationToken).ConfigureAwait(false);
            }
            else if (objectStreamGroups is null || objectStreamGroups.Count == 0)
            {
                written = await serializer.SerializeAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream, sink, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                written = await serializer.SerializeStreamedAsync(version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, sink, cancellationToken).ConfigureAwait(false);
            }
            if (written != counted)
            {
                throw new QpdfException($"Writer replay mismatch: counted {counted} bytes but wrote {written}.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { governor.ReleaseMemory(counted); }
    }

    private static (int Major, int Minor, int Extension) ParseWriterVersion(string text, string option)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        string[] parts = text.Split((char)46);
        if (parts.Length < 2 || parts.Length > 3)
        {
            throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
        }

        if (parts[0].Length == 0 || parts[0].Length > 4 || parts[1].Length == 0 || parts[1].Length > 4)
        {
            throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
        }

        int major = ParseVersionComponent(parts[0], option);
        int minor = ParseVersionComponent(parts[1], option);
        int extension = 0;
        if (parts.Length == 3)
        {
            if (parts[2].Length == 0 || parts[2].Length > 6)
            {
                throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
            }

            extension = ParseVersionComponent(parts[2], option);
        }

        bool supported = (major == 1 && minor >= 0 && minor <= 7) || (major == 2 && minor == 0);
        if (!supported)
        {
            throw new QpdfParseException(option + " supports versions 1.0 through 1.7 and 2.0.", 0L, null);
        }

        if (extension < 0)
        {
            throw new QpdfParseException(option + " requires a non-negative extension level.", 0L, null);
        }

        return (major, minor, extension);
    }

    private static int ParseVersionComponent(string part, string option)
    {
        long value = 0L;
        foreach (char c in part)
        {
            if (c < (char)48 || c > (char)57)
            {
                throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
            }

            value = checked(value * 10L + (c - (char)48));
        }

        if (value > 999999L)
        {
            throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
        }

        return (int)value;
    }

    private static (PdfVersion Version, int Extension) NegotiateVersion(PdfVersion inputMax, string? minText, string? forceText, ResourceGovernor governor)
    {
        if (inputMax is null)
        {
            throw new ArgumentNullException(nameof(inputMax));
        }

        if (forceText is not null)
        {
            (int major, int minor, int ext) = ParseWriterVersion(forceText, "--force-version");
            return (governor.OwnVersion(major, minor), ext);
        }

        if (minText is null)
        {
            return (inputMax, 0);
        }

        (int minMajor, int minMinor, int minExt) = ParseWriterVersion(minText, "--min-version");
        int order = CompareVersions(inputMax.Major, inputMax.Minor, minMajor, minMinor);
        if (order > 0)
        {
            return (inputMax, 0);
        }

        if (order < 0)
        {
            return (governor.OwnVersion(minMajor, minMinor), minExt);
        }

        return (governor.OwnVersion(minMajor, minMinor), minExt);
    }

    private static int CompareVersions(int major1, int minor1, int major2, int minor2)
    {
        if (major1 != major2)
        {
            return major1 > major2 ? 1 : -1;
        }

        if (minor1 != minor2)
        {
            return minor1 > minor2 ? 1 : -1;
        }

        return 0;
    }

    private static byte[]? TryGetOriginalId(DocumentEngine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        PdfDictionary trailer = engine.Trailer;
        PdfName idKey = PdfName.FromAscii("ID");
        if (!trailer.TryGet(idKey, out QpdfObject raw) || raw is not PdfArray array || array.Count == 0)
        {
            return null;
        }

        if (array[0] is not PdfByteString first || first.Value.Length == 0 || first.Value.Length > 256)
        {
            return null;
        }

        return first.Value;
    }

    private static string ResolveObjectStreamMode(string? raw)
    {
        if (raw is null)
        {
            return "preserve";
        }

        if (raw == "disable" || raw == "preserve" || raw == "generate")
        {
            return raw;
        }

        throw new QpdfParseException("--object-streams mode must be disable, preserve, or generate.", 0L, null);
    }

    private static HashSet<int> CollectCompressedOriginals(DocumentEngine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        HashSet<int> compressed = new();
        foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            if (entry.Value.Kind == XrefEntryKind.Compressed)
            {
                compressed.Add(entry.Key);
            }
        }

        return compressed;
    }

    private static bool IsCompressibleValue(int number, QpdfObject value)
    {
        if (number == 1 || number == 2)
        {
            return false;
        }

        if (value is PdfStream)
        {
            return false;
        }

        if (value is PdfDictionary dictionary)
        {
            PdfName typeKey = PdfName.FromAscii("Type");
            PdfName byteRangeKey = PdfName.FromAscii("ByteRange");
            PdfName contentsKey = PdfName.FromAscii("Contents");
            if (dictionary.TryGet(typeKey, out QpdfObject typeRaw) && typeRaw is PdfName typeName && typeName.DisplayText == "Sig"
                && dictionary.TryGet(byteRangeKey, out QpdfObject byteRange) && byteRange is not PdfNull
                && dictionary.TryGet(contentsKey, out QpdfObject contents) && contents is not PdfNull)
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<int> ResolveCompressedSet(
        string mode,
        IReadOnlyDictionary<int, QpdfObject> pool,
        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> sources,
        ResourceGovernor governor)
    {
        if (mode is null)
        {
            throw new ArgumentNullException(nameof(mode));
        }

        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        HashSet<int> compressed = new();
        if (mode == "disable")
        {
            return compressed;
        }

        if (mode == "generate")
        {
            foreach (KeyValuePair<int, QpdfObject> entry in pool)
            {
                governor.ChargeWork(1L);
                if (IsCompressibleValue(entry.Key, entry.Value))
                {
                    compressed.Add(entry.Key);
                }
            }

            return compressed;
        }

        if (mode != "preserve")
        {
            throw new QpdfParseException("--object-streams mode must be disable, preserve, or generate.", 0L, null);
        }

        foreach ((IReadOnlyDictionary<int, int> map, HashSet<int> origs) in sources)
        {
            foreach (int orig in origs)
            {
                governor.ChargeWork(1L);
                if (map.TryGetValue(orig, out int dest)
                    && pool.TryGetValue(dest, out QpdfObject? value)
                    && value is not null
                    && IsCompressibleValue(dest, value))
                {
                    compressed.Add(dest);
                }
            }
        }

        return compressed;
    }

    private static List<List<int>> PlanObjectStreamGroups(HashSet<int> compressed, ResourceGovernor governor)
    {
        if (compressed is null)
        {
            throw new ArgumentNullException(nameof(compressed));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        List<int> ordered = new(compressed);
        ordered.Sort();
        List<List<int>> groups = new();
        if (ordered.Count == 0)
        {
            return groups;
        }

        int streamCount = (ordered.Count + 99) / 100;
        int perStream = (ordered.Count + streamCount - 1) / streamCount;
        for (int i = 0; i < ordered.Count; i += perStream)
        {
            governor.ChargeWork(1L);
            int take = Math.Min(perStream, ordered.Count - i);
            groups.Add(ordered.GetRange(i, take));
        }

        return groups;
    }

    private static HashSet<int> RemapCompressedSet(HashSet<int> compressed, Dictionary<int, int> renumber)
    {
        if (compressed is null)
        {
            throw new ArgumentNullException(nameof(compressed));
        }

        if (renumber is null)
        {
            throw new ArgumentNullException(nameof(renumber));
        }

        HashSet<int> remapped = new();
        foreach (int id in compressed)
        {
            if (renumber.TryGetValue(id, out int mapped))
            {
                remapped.Add(mapped);
            }
        }

        return remapped;
    }

    private static (bool Compress, string Decode, int? Level) ResolveCompression(CompressionRequest request, bool qdf)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.StreamData is not null && request.StreamData != "compress" && request.StreamData != "preserve" && request.StreamData != "uncompress")
        {
            throw new QpdfParseException("--stream-data must be compress, preserve, or uncompress.", 0L, null);
        }

        if (request.CompressStreams is not null && request.CompressStreams != "y" && request.CompressStreams != "n")
        {
            throw new QpdfParseException("--compress-streams must be y or n.", 0L, null);
        }

        if (request.DecodeLevel is not null && request.DecodeLevel != "none" && request.DecodeLevel != "generalized" && request.DecodeLevel != "specialized" && request.DecodeLevel != "all")
        {
            throw new QpdfParseException("--decode-level must be none, generalized, specialized, or all.", 0L, null);
        }

        int? level = request.CompressionLevel is null ? null : ParseCompressionLevel(request.CompressionLevel, "--compression-level");
        bool compress = request.CompressStreams is not null
            ? request.CompressStreams == "y"
            : request.StreamData == "compress";
        string decode = request.DecodeLevel
            ?? ((request.StreamData == "compress" || request.StreamData == "uncompress") ? "generalized" : (qdf ? "generalized" : "none"));
        // Compression asks the writer to filter generalized encodings even at decode level none.
        if (compress && decode == "none") { decode = "generalized"; }
        return (compress, decode, level);
    }

    private static int ParseCompressionLevel(string text, string option)
        => CompressionRequest.ParseLevel(text, option);

    private static bool StreamFilterInLevel(string name, string decode)
    {
        return Filters.FilterChain.NameInLevel(name, decode);
    }

    private static void TransformCopierStreams(
        GraphCopier copier,
        DocumentEngine engine,
        bool compress,
        string decode,
        bool recompressFlate,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
        => TransformCopierStreams(copier, engine, compress, decode, recompressFlate, level,
            limits, governor, diagnostics, cancellationToken, null);

    private static void TransformCopierStreams(
        GraphCopier copier,
        DocumentEngine engine,
        bool compress,
        string decode,
        bool recompressFlate,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        PdfReference? metadata)
    {
        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        Content.PoolResolver resolver = Content.PoolResolver.Create(scratch, copier.CopiedObjects);
        List<int> ids = ParserContainers.CreateList<int>(scratch);
        foreach (int id in copier.CopiedObjects.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            governor.ChargeWork(checked(1L + (ids.Count == ids.Capacity ? ids.Count : 0)));
            ParserContainers.AddItem(scratch, ids, id, sizeof(int));
        }
        foreach (int id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!copier.CopiedObjects.TryGetValue(id, out QpdfObject? value) || value is not PdfStream stream)
            {
                continue;
            }

            bool xmlMetadata = metadata?.ObjectNumber == id
                && PdfDictionaryLookup.TryGet(stream.Dictionary, "Type"u8, governor, cancellationToken, out QpdfObject type)
                && type is PdfName typeName && PdfDictionaryLookup.NameEquals(typeName, "Metadata"u8, governor, cancellationToken)
                && PdfDictionaryLookup.TryGet(stream.Dictionary, "Subtype"u8, governor, cancellationToken, out QpdfObject subtype)
                && subtype is PdfName subtypeName && PdfDictionaryLookup.NameEquals(subtypeName, "XML"u8, governor, cancellationToken);
            QpdfObject? replacement = StreamTransformer.Transform(stream, id, resolver, xmlMetadata ? false : compress,
                xmlMetadata ? "all" : decode, recompressFlate, level, limits, governor, diagnostics, cancellationToken);
            if (replacement is not null)
            {
                copier.ReplaceCopiedObject(id, replacement);
            }
        }
    }

    private static QpdfObject? TransformStream(
        PdfStream stream,
        int number,
        DocumentEngine engine,
        bool compress,
        string decode,
        bool recompressFlate,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics)
        => StreamTransformer.Transform(stream, number, engine, compress, decode, recompressFlate, level,
            limits, governor, diagnostics, governor.CancellationToken);

    private static PdfDictionary? TryGetInputCatalog(DocumentEngine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (!engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) || root is null || root is PdfNull)
        {
            return null;
        }

        if (root is PdfReference rootReference)
        {
            if (((IIndirectResolver)engine).TryResolve(rootReference, out QpdfObject? resolved) && resolved is PdfDictionary catalog)
            {
                return catalog;
            }

            return null;
        }

        return root as PdfDictionary;
    }

    private static List<Content.CopiedForm> NonNullForms(List<Content.CopiedForm?> parts)
    {
        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        List<Content.CopiedForm> present = new(parts.Count);
        foreach (Content.CopiedForm? part in parts)
        {
            if (part is not null)
            {
                present.Add(part);
            }
        }

        return present;
    }

    private static HashSet<int> PreservationExclusions(
        DocumentEngine engine,
        IReadOnlyList<PageLocation> kept,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (kept is null)
        {
            throw new ArgumentNullException(nameof(kept));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        HashSet<int> excluded = new();
        if (engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) && root is PdfReference rootReference)
        {
            excluded.Add(rootReference.ObjectNumber);
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        PdfName parentKey = PdfName.FromAscii("Parent");
        foreach (PageLocation location in kept)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            PdfReference? source = location.Source;
            if (source is null)
            {
                continue;
            }

            HashSet<int> visited = new() { source.ObjectNumber };
            excluded.Add(source.ObjectNumber);
            PdfReference current = source;
            int depth = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (!resolver.TryResolve(current, out QpdfObject? node) || node is not PdfDictionary parent)
                {
                    break;
                }

                if (!parent.TryGet(parentKey, out QpdfObject? raw) || raw is null || raw is PdfNull)
                {
                    break;
                }

                if (raw is not PdfReference next)
                {
                    break;
                }

                if (!visited.Add(next.ObjectNumber))
                {
                    break;
                }

                excluded.Add(next.ObjectNumber);
                depth += 1;
                if (depth > limits.MaxNestingDepth)
                {
                    break;
                }

                current = next;
            }
        }

        return excluded;
    }

    private static List<PageLocation> KeptLocations(QpdfDocument document, IReadOnlyList<int> requested)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (requested is null)
        {
            throw new ArgumentNullException(nameof(requested));
        }

        List<PageLocation> kept = new(requested.Count);
        foreach (int pageNumber in requested)
        {
            kept.Add(document.PageReferences[pageNumber - 1]);
        }

        return kept;
    }

    private static (PdfReference? InfoRef, PdfReference? MetadataRef) CopyCatalogExtras(
        GraphCopier copier,
        DocumentEngine engine,
        bool removeInfo,
        bool removeMetadata,
        DiagnosticBag diagnostics)
    {
        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        PdfReference? infoRef = null;
        PdfReference? metadataRef = null;
        // Upstream --remove-info also omits the catalog metadata stream.
        if (!removeMetadata && !removeInfo)
        {
            try
            {
                PdfDictionary? catalog = TryGetInputCatalog(engine);
                if (catalog is not null
                    && catalog.TryGet(PdfName.FromAscii("Metadata"), out QpdfObject? raw)
                    && raw is not null && raw is not PdfNull)
                {
                    metadataRef = copier.AddIndirectObject(raw);
                }
            }
            catch (QpdfException)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable document metadata.");
                metadataRef = null;
            }
        }

        if (!removeInfo)
        {
            try
            {
                if (engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject? raw)
                    && raw is not null && raw is not PdfNull)
                {
                    infoRef = copier.AddIndirectObject(raw);
                }
            }
            catch (QpdfException)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable document info.");
                infoRef = null;
            }
        }
        else
        {
            try
            {
                PdfDictionary? infoDict = null;
                if (engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject? raw)
                    && raw is not null && raw is not PdfNull)
                {
                    if (raw is PdfReference infoReference)
                    {
                        if (((IIndirectResolver)engine).TryResolve(infoReference, out QpdfObject? resolved) && resolved is PdfDictionary indirect)
                        {
                            infoDict = indirect;
                        }
                    }
                    else if (raw is PdfDictionary direct)
                    {
                        infoDict = direct;
                    }
                }

                if (infoDict is not null
                    && infoDict.TryGet(PdfName.FromAscii("ModDate"), out QpdfObject? modDate)
                    && modDate is not null && modDate is not PdfNull)
                {
                    PdfDictionary reduced = new(new Dictionary<PdfName, QpdfObject>
                    {
                        [PdfName.FromAscii("ModDate")] = modDate,
                    });
                    infoRef = copier.AddIndirectObject(reduced);
                }
            }
            catch (QpdfException)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable document info.");
                infoRef = null;
            }
        }

        return (infoRef, metadataRef);
    }

    private sealed record AttachmentAssembly(PdfReference? NamesRef, string? PageMode, HashSet<int> ObjectIds, int FinalCount);

    private static string? ReadInputPageMode(DocumentEngine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        PdfDictionary? catalog = TryGetInputCatalog(engine);
        if (catalog is not null
            && catalog.TryGet(PdfName.FromAscii("PageMode"), out QpdfObject? raw)
            && raw is PdfName name)
        {
            return name.DisplayText;
        }

        return null;
    }

    private static SortedDictionary<string, PdfReference> PreserveExistingAttachments(
        GraphCopier copier,
        DocumentEngine engine,
        AttachmentEdits edits,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
    {
        ownership.CheckOwner(governor);
        SortedDictionary<string, PdfReference> kept = new(StringComparer.Ordinal);
        if (edits.HasEdits)
        {
            List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(engine, limits, governor, diagnostics, cancellationToken, ownership);
            foreach (EmbeddedAttachment attachment in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PdfReference? link = copier.AddIndirectObject(attachment.Spec);
                    if (link is not null)
                    {
                        kept[attachment.Key] = link;
                    }
                }
                catch (QpdfException)
                {
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable attachment.");
                }
            }
        }

        return kept;
    }

    private static async ValueTask<AttachmentAssembly> AssembleAttachmentsAsync(
        IQpdfHost host,
        SortedDictionary<string, PdfReference> existing,
        Dictionary<int, QpdfObject> pool,
        AttachmentEdits edits,
        string? inputPageMode,
        bool compress,
        int? level,
        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> compressibleSources,
        QpdfRecoveryMode recovery,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
    {
        ownership.CheckOwner(governor);
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (existing is null)
        {
            throw new ArgumentNullException(nameof(existing));
        }

        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        if (edits is null)
        {
            throw new ArgumentNullException(nameof(edits));
        }

        if (compressibleSources is null)
        {
            throw new ArgumentNullException(nameof(compressibleSources));
        }

        HashSet<int> ids = new();
        if (!edits.HasEdits)
        {
            return new AttachmentAssembly(null, inputPageMode, ids, 0);
        }

        foreach (string key in edits.Removes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!existing.Remove(key))
            {
                throw new QpdfParseException("The requested attachment was not found.", 0L, null);
            }
        }

        int nextId = 3;
        foreach (int number in pool.Keys)
        {
            if (number >= nextId)
            {
                nextId = checked(number + 1);
            }
        }

        List<string> duplicates = new();
        foreach (AttachmentAdd add in edits.Adds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            string key = add.Key ?? AttachmentBuilder.Basename(add.File);
            string fileName = add.FileName ?? AttachmentBuilder.Basename(add.File);
            if (key.Length == 0)
            {
                throw new QpdfParseException("Attachment key must not be empty.", 0L, null);
            }

            if (fileName.Length == 0)
            {
                throw new QpdfParseException("Attachment filename must not be empty.", 0L, null);
            }

            if (!add.Replace && existing.ContainsKey(key))
            {
                duplicates.Add(key);
                continue;
            }

            byte[] data = await ReadAttachmentFileAsync(host, add.File, limits, governor, ownership, cancellationToken).ConfigureAwait(false);
            if (add.MimeType is not null && !IsAscii(add.MimeType))
            {
                throw new QpdfParseException("Attachment MIME type must be ASCII.", 0L, null);
            }

            byte[] checksum = AttachmentBuilder.Checksum(data);
            PdfDictionary streamDict = AttachmentBuilder.BuildStreamDictionary(data.Length, checksum, add.MimeType, add.CreationDate, add.ModDate, governor, ownership);
            byte[] stored = data;
            if (compress && data.Length > 0)
            {
                stored = FlateFilter.Encode(data, level, limits, governor, ownership, cancellationToken);
                streamDict = AttachmentBuilder.WithFilter(streamDict, governor, ownership);
            }

            int streamId = nextId;
            checked
            {
                nextId += 1;
            }

            ownership.Reserve(64L);
            pool[streamId] = new PdfStream(streamDict, stored);
            ids.Add(streamId);
            PdfDictionary spec = AttachmentBuilder.BuildFilespecDictionary(fileName, add.Description, streamId);
            int specId = nextId;
            checked
            {
                nextId += 1;
            }

            ownership.Reserve(128L);
            pool[specId] = spec;
            ids.Add(specId);
            existing[key] = new PdfReference(specId, 0);
        }

        if (duplicates.Count > 0)
        {
            throw new QpdfParseException("Attachments with conflicting keys already exist; use --replace to replace or --key to specify a different key.", 0L, null);
        }

        bool copiedAny = false;
        List<string> copyConflicts = new();
        foreach (AttachmentCopy copy in edits.Copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            await using QpdfDocument source = await QpdfDocument.OpenCore(host, SelectionPlan.CheckedPath(copy.File), limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
            List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(source.Engine, limits, governor, diagnostics, cancellationToken, ownership);
            GraphCopier copyCopier = GraphCopier.CreateOwned(source.Engine, governor, limits, nextId, ownership);
            foreach (EmbeddedAttachment attachment in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                string newKey = (copy.Prefix ?? string.Empty) + attachment.Key;
                if (existing.ContainsKey(newKey))
                {
                    if (!copyConflicts.Contains(copy.File))
                    {
                        copyConflicts.Add(copy.File);
                    }

                    continue;
                }

                PdfReference? link;
                try
                {
                    link = copyCopier.AddIndirectObject(attachment.Spec);
                }
                catch (QpdfException)
                {
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable attachment.");
                    continue;
                }

                if (link is null)
                {
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable attachment.");
                    continue;
                }

                existing[newKey] = link;
                copiedAny = true;
            }

            nextId = copyCopier.NextId;
            List<int> copiedIds = new(copyCopier.CopiedObjects.Keys);
            copiedIds.Sort();
            foreach (int id in copiedIds)
            {
                pool[id] = copyCopier.CopiedObjects[id];
            }

            compressibleSources.Add((copyCopier.SourceToDestination, CollectCompressedOriginals(source.Engine)));
            source.Engine.TransferExclusiveValuesTo(ownership);
        }

        if (copyConflicts.Count > 0)
        {
            string files = string.Join("', '", copyConflicts);
            throw new QpdfParseException("Attachments copied from '" + files + "' conflict with existing keys; use --prefix to specify a key prefix.", 0L, null);
        }

        if (existing.Count == 0)
        {
            return new AttachmentAssembly(null, inputPageMode, ids, 0);
        }

        List<KeyValuePair<string, PdfReference>> pairs = new(existing.Count);
        foreach (KeyValuePair<string, PdfReference> entry in existing)
        {
            pairs.Add(entry);
        }

        PdfDictionary tree = AttachmentBuilder.BuildNameTree(pairs, governor, ownership);
        ownership.Reserve(64L);
        PdfDictionary wrapper = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("EmbeddedFiles")] = tree,
        });
        int rootId = nextId;
        checked
        {
            nextId += 1;
        }

        ownership.Reserve(128L);
        pool[rootId] = wrapper;
        ids.Add(rootId);
        string? pageMode = edits.Adds.Count > 0 || copiedAny ? inputPageMode ?? "UseAttachments" : inputPageMode;
        return new AttachmentAssembly(new PdfReference(rootId, 0), pageMode, ids, existing.Count);
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }

    private static async ValueTask<byte[]> ReadAttachmentFileAsync(
        IQpdfHost host,
        string file,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
    {
        ownership.CheckOwner(governor);
        QpdfPath path = SelectionPlan.CheckedPath(file);
        IQpdfInput input = await GovernedHost.OpenInputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
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

            ownership.Reserve(checked(input.Length + GovernedBufferStream.ArrayOverheadBytes));
            byte[] bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ValueTask<QpdfDocument> OpenPlanInputAsync(
        IQpdfHost host,
        QpdfPath input,
        bool emptyDocument,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        CancellationToken cancellationToken)
        => OpenPlanInputAsync(host, input, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken, true);

    private static ValueTask<QpdfDocument> OpenPlanInputAsync(
        IQpdfHost host,
        QpdfPath input,
        bool emptyDocument,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        CancellationToken cancellationToken,
        bool valuesEscape)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        limits.Validate();
        if (emptyDocument)
        {
            return QpdfDocument.OpenEmptyDocument(input, limits, governor, diagnostics, recovery, cancellationToken, valuesEscape);
        }

        return QpdfDocument.OpenCore(host, input, limits, governor, diagnostics, recovery, cancellationToken, valuesEscape);
    }

    private static async ValueTask<(MultiPageSelectionOutcome Result, int AttachmentCount)> WholeFileCoreAsync(
        IQpdfHost host,
        WholeFilePlan plan,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        await using QpdfDocument document = await OpenPlanInputAsync(host, plan.Input, plan.EmptyDocument, limits, governor, diagnostics, plan.Recovery, cancellationToken, plan.EmptyDocument).ConfigureAwait(false);
        int total = document.PageReferences.Count;
        if (total <= 0)
        {
            if (!plan.EmptyDocument)
            {
                throw new QpdfParseException("The input document has no pages.", 0L, null);
            }

            return await PublishEmptyModelAsync(host, document, plan, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        if ((long)total > limits.MaxWork)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), total, limits.MaxWork);
        }

        List<int> pages = new(total);
        for (int page = 1; page <= total; page++)
        {
            pages.Add(page);
        }

        List<PdfPageSource> solo = new(1)
        {
            new PdfPageSource(plan.Input, PdfPageSelectionOptions.FromPages(pages)),
        };
        QpdfObject? verbatimTree = plan.Labels.Count == 0 && !plan.RemoveLabels && !plan.SplitPages.HasValue
            ? Content.PageLabelMerge.TryGetRawTree(document.Engine)
            : null;
        // The shared selection core consumes this same stable source snapshot,
        // transfers required values and closes its lease before publication.
        return await SelectPagesCoreAsync(
            host,
            solo,
            plan.Output,
            plan.Input,
            false,
            limits,
            plan.Recovery,
            null,
            plan.Rotations,
            plan.Labels,
            plan.RemoveLabels,
            verbatimTree,
            plan.SplitPages,
            plan.ReplaceInput,
            plan.MinVersion,
            plan.ForceVersion,
            plan.DeterministicId,
            plan.StaticId,
            plan.NewlineBeforeEndstream,
            plan.ObjectStreams,
            plan.PreserveUnreferenced,
            plan.Compression(),
            plan.RemoveInfo,
            plan.RemoveAcroform,
            plan.RemoveStructure,
            plan.RemoveMetadata,
            plan.Edits(),
            plan.CoalesceContents,
            plan.NormalizeContent,
            plan.ExternalizeInlineImages,
            plan.IiMinBytes,
            plan.Underlays,
            plan.Overlays,
            plan.FlattenRotation,
            plan.FlattenAnnotations,
            plan.GenerateAppearances,
            new Content.ImageOptimizeRequest(plan.OptimizeImages, plan.OiMinWidth, plan.OiMinHeight, plan.OiMinArea, plan.KeepInlineImages, plan.JpegQuality),
            new Writing.QdfOptions(plan.Qdf, plan.NoOriginalObjectIds),
            new Content.ContentPrunePolicy(plan.SplitPages.HasValue, plan.RemoveUnreferencedResources),
            true,
            governor,
            diagnostics,
            plan.Linearize,
            plan.LinearizePass1,
            false,
            document,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<(MultiPageSelectionOutcome Result, int AttachmentCount)> PublishEmptyModelAsync(
        IQpdfHost host,
        QpdfDocument document,
        WholeFilePlan plan,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        limits.Validate();
        (PdfVersion negotiated, int negotiatedExtension) = NegotiateVersion(document.Version, plan.MinVersion, plan.ForceVersion, governor);
        (bool emptyCompress, string _, int? emptyLevel) = ResolveCompression(plan.Compression(), plan.Qdf);
        PdfVersion effective = negotiated;
        int extensionLevel = negotiatedExtension;
        PdfDictionary catalog = NewCatalog(BuildPageLabels(plan.Labels, 0, governor), effective, extensionLevel, null, null, null, null, null, null, null, null);
        List<NumberedObject> model = new()
        {
            new NumberedObject(1, catalog),
            new NumberedObject(2, PagesNode(new List<PdfReference>())),
        };
        byte[]? originalId = TryGetOriginalId(document.Engine);
        await PublishModelAsync(host, effective, extensionLevel, model, null, emptyCompress, emptyLevel, null, originalId, plan.StaticId, plan.NewlineBeforeEndstream, new Writing.QdfOptions(plan.Qdf, plan.NoOriginalObjectIds), new Dictionary<int, int>(), plan.Linearize, plan.LinearizePass1, plan.Output, limits, governor, cancellationToken).ConfigureAwait(false);
        MultiPageSelectionOutcome result = new(0, [], [0], [plan.Output], ResultWarningSnapshot.Read(resultWarnings, diagnostics));
        return (result, 0);
    }

    private static bool IsNewerVersion(PdfVersion candidate, PdfVersion current)
    {
        if (candidate.Major != current.Major)
        {
            return candidate.Major > current.Major;
        }

        return candidate.Minor > current.Minor;
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
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(QpdfCheckResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            TypedCheckOutcome result = await CheckCoreAsync(host, inputPath, options, limits, QpdfRecoveryMode.Strict, false, false, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
    public static async ValueTask<IReadOnlyList<QpdfAttachmentInfo>> ListAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
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
        ResourceGovernor governor = new(limits, cancellationToken);
        DiagnosticBag diagnostics = new(governor);
        return await ListAttachmentsCoreAsync(host, inputPath, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
    /// <exception cref="ArgumentException">The key is empty.</exception>
    public static async ValueTask<QpdfAttachmentExtraction> ExtractAttachmentAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        string key,
        QpdfPath outputPath,
        QpdfLimits limits,
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

        if (key.Length == 0)
        {
            throw new ArgumentException("Attachment key must not be empty.", nameof(key));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(QpdfAttachmentExtraction.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            AttachmentExtractionOutcome result = await ExtractAttachmentCoreAsync(host, inputPath, key, outputPath, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(QpdfJsonResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            List<string> wanted;
            using (ConfigurationText? textStorage = options.Objects.Count == 0 ? null : ConfigurationText.Create(governor))
            { wanted = ValidateJsonObjects(options.Objects, 0L, textStorage); }
            Commands.JsonInspection inspection = new(options.Version, options.Keys, null, options.StreamData, options.StreamPrefix, wanted, options.MinimalOutput);
            JsonInspectionOutcome result = await JsonCoreAsync(host, inputPath, outputPath, inspection, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, false, true, cancellationToken).ConfigureAwait(false);
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
    public static async ValueTask<QpdfJsonImportResult> CreatePdfFromJsonAsync(
        IQpdfHost host,
        QpdfPath jsonPath,
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
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(QpdfJsonImportResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            JsonImportOutcome result = await ImportCreateCoreAsync(host, jsonPath, outputPath, null, null, false, false, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
    public static async ValueTask<QpdfJsonImportResult> UpdatePdfFromJsonAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath jsonPath,
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
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(QpdfJsonImportResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            JsonImportOutcome result = await ImportUpdateCoreAsync(host, inputPath, jsonPath, outputPath, null, null, false, false, limits, QpdfRecoveryMode.Strict, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
    {
        return EditAttachmentsAsync(host, inputPath, outputPath, ToAttachmentAdds(attachments, nameof(attachments)), [], [], limits, cancellationToken);
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
    /// <exception cref="ArgumentException">No key, or an empty key, was supplied.</exception>
    public static ValueTask<QpdfAttachmentEditResult> RemoveAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        IReadOnlyList<string> keys,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (keys is null)
        {
            throw new ArgumentNullException(nameof(keys));
        }

        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one attachment key is required.", nameof(keys));
        }

        List<string> removes = new(keys.Count);
        foreach (string key in keys)
        {
            if (key is null)
            {
                throw new ArgumentException("Attachment keys must not contain null entries.", nameof(keys));
            }

            if (key.Length == 0)
            {
                throw new ArgumentException("Attachment keys must not be empty.", nameof(keys));
            }

            removes.Add(key);
        }

        return EditAttachmentsAsync(host, inputPath, outputPath, [], removes, [], limits, cancellationToken);
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
    {
        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one attachment source is required.", nameof(sources));
        }

        List<AttachmentCopy> copies = new(sources.Count);
        foreach (QpdfAttachmentCopy source in sources)
        {
            if (source is null)
            {
                throw new ArgumentException("Attachment sources must not contain null entries.", nameof(sources));
            }

            copies.Add(new AttachmentCopy(source.Input.Value, source.Prefix));
        }

        return EditAttachmentsAsync(host, inputPath, outputPath, [], [], copies, limits, cancellationToken);
    }

    private static List<AttachmentAdd> ToAttachmentAdds(IReadOnlyList<QpdfAttachmentSpec> attachments, string name)
    {
        if (attachments is null)
        {
            throw new ArgumentNullException(name);
        }

        if (attachments.Count == 0)
        {
            throw new ArgumentException("At least one attachment is required.", name);
        }

        List<AttachmentAdd> adds = new(attachments.Count);
        foreach (QpdfAttachmentSpec spec in attachments)
        {
            if (spec is null)
            {
                throw new ArgumentException("Attachments must not contain null entries.", name);
            }

            if (spec.File.Length == 0)
            {
                throw new ArgumentException("Attachment files must not be empty.", name);
            }

            if (spec.Key is not null && spec.Key.Length == 0)
            {
                throw new ArgumentException("Attachment keys must not be empty.", name);
            }

            if (spec.FileName is not null && spec.FileName.Length == 0)
            {
                throw new ArgumentException("Attachment filenames must not be empty.", name);
            }

            adds.Add(new AttachmentAdd(
                spec.File,
                spec.Key,
                spec.FileName,
                spec.CreationDate is null || spec.CreationDate.Length == 0 ? null : spec.CreationDate,
                spec.ModDate is null || spec.ModDate.Length == 0 ? null : spec.ModDate,
                spec.MimeType is null || spec.MimeType.Length == 0 ? null : spec.MimeType,
                spec.Description is null || spec.Description.Length == 0 ? null : spec.Description,
                spec.Replace));
        }

        return adds;
    }

    private static async ValueTask<QpdfAttachmentEditResult> EditAttachmentsAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        List<AttachmentAdd> adds,
        List<string> removes,
        List<AttachmentCopy> copies,
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
        WholeFilePlan plan = new(inputPath, outputPath, QpdfRecoveryMode.Strict)
        {
            AttachmentsToAdd = adds,
            AttachmentsToRemove = removes,
            AttachmentsToCopy = copies,
        };
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.PrepareTypedResult(QpdfAttachmentEditResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            (MultiPageSelectionOutcome result, int attachmentCount) = await WholeFileCoreAsync(
                host, plan, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            QpdfAttachmentEditResult edited = new(attachmentCount, result.Warnings, governor.TypedCounters, true);
            edited.OwnStorage(governor.TakeTypedResultStorage());
            governor.FinishTypedCounters();
            return edited;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    private static async ValueTask<AttachmentExtractionOutcome> ExtractAttachmentCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        string key,
        QpdfPath outputPath,
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

        await using QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        byte[] data = ReadAttachmentData(document, key, limits, governor, diagnostics, cancellationToken);
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

    private static readonly string[] JsonKeys =
    [
        "acroform", "attachments", "encrypt", "objectinfo", "objects", "outlines", "pagelabels", "pages", "qpdf",
    ];

    internal static void ValidateJsonKeys(int version, IReadOnlyList<string> keys)
    {
        if (keys is null)
        {
            throw new ArgumentNullException(nameof(keys));
        }

        foreach (string key in keys)
        {
            if (key is null || Array.IndexOf(JsonKeys, key) < 0)
            {
                throw new QpdfParseException("Unknown JSON key '" + (key ?? string.Empty) + "'.", 0L, null);
            }

            if (version == 1 && string.Equals(key, "qpdf", StringComparison.Ordinal))
            {
                throw new QpdfParseException("JSON key 'qpdf' is only valid for JSON version 2.", 0L, null);
            }

            if (version == 2 && (string.Equals(key, "objects", StringComparison.Ordinal) || string.Equals(key, "objectinfo", StringComparison.Ordinal)))
            {
                throw new QpdfParseException("JSON keys 'objects' and 'objectinfo' are only valid for JSON version 1.", 0L, null);
            }
        }
    }

    internal static List<string> ValidateJsonObjects(IReadOnlyList<string> objects, long index)
        => ValidateJsonObjects(objects, index, null);

    internal static List<string> ValidateJsonObjects(IReadOnlyList<string> objects, long index, ConfigurationText? textStorage)
    {
        if (objects is null)
        {
            throw new ArgumentNullException(nameof(objects));
        }

        List<string> canonical = new(objects.Count);
        foreach (string spec in objects)
        {
            if (spec is null)
            {
                throw new QpdfParseException("JSON object selectors must not be null.", index, null);
            }

            if (string.Equals(spec, "trailer", StringComparison.Ordinal))
            {
                canonical.Add("trailer");
                continue;
            }

            int comma = spec.IndexOf(',');
            if (comma >= 0 && spec.AsSpan(comma + 1).Contains(','))
            {
                throw new QpdfParseException("JSON object '" + spec + "' must be trailer or obj[,gen].", index, null);
            }

            if (!TryParseObjectNumber(comma < 0 ? spec.AsSpan() : spec.AsSpan(0, comma), out int number))
            {
                throw new QpdfParseException("JSON object '" + spec + "' must be trailer or obj[,gen].", index, null);
            }

            int generation = 0;
            if (comma >= 0 && !TryParseObjectNumber(spec.AsSpan(comma + 1), out generation))
            {
                throw new QpdfParseException("JSON object '" + spec + "' must be trailer or obj[,gen].", index, null);
            }

            canonical.Add(ConfigurationText.ObjectSelector(textStorage, number, generation));
        }

        return canonical;
    }

    private static bool TryParseObjectNumber(ReadOnlySpan<char> text, out int number)
    {
        number = 0;
        if (text.Length == 0 || text.Length > 10)
        {
            return false;
        }

        long value = 0L;
        foreach (char c in text)
        {
            if (c < (char)48 || c > (char)57)
            {
                return false;
            }

            value = checked(value * 10L + (c - (char)48));
        }

        if (value > (long)int.MaxValue)
        {
            return false;
        }

        number = (int)value;
        return true;
    }

    private static async ValueTask<JsonInspectionOutcome> JsonCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath? outputPath,
        JsonInspection inspection,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        bool emptyDocument,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        bool testJsonSchema,
        bool escapeText,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        ValidateJsonKeys(inspection.Version, inspection.Keys);
        List<string> wanted = [.. inspection.Objects];
        if (outputPath.HasValue && string.Equals(inputPath.Value, outputPath.Value.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        string decodeLevel = inspection.DecodeLevel ?? (inspection.MinimalOutput ? "none" : "generalized");
        if (decodeLevel != "none" && decodeLevel != "generalized" && decodeLevel != "specialized" && decodeLevel != "all")
        {
            throw new QpdfParseException("JSON decode level must be one of: none, generalized, specialized, all.", 0L, null);
        }

        string? prefix = inspection.StreamPrefix;
        if (inspection.StreamData == "file" && prefix is null)
        {
            if (!outputPath.HasValue)
            {
                throw new QpdfParseException("JSON file stream data needs an output file or --json-stream-prefix.", 0L, null);
            }

            prefix = outputPath.Value.Value;
        }

        // JSON emits text and copied sidecar bytes. Its reader values remain
        // exclusive to this document until serialization completes.
        using DiagnosticStage? diagnosticStage = diagnostics.BeginStaging();
        try
        {
            await using QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
            Json.JsonRequest request = new(inspection.Version, inspection.Keys, decodeLevel, inspection.StreamData, prefix, wanted, inspection.MinimalOutput);
            using Json.PreparedJsonOutput output = Json.PreparedJsonOutput.Create(governor);
            using (DisplayBuffer builder = Json.DocumentJson.WriteDocument(document, request, output, limits, governor, diagnostics, cancellationToken))
            {
                if (builder.Utf8Bytes > limits.MaxOutputBytes)
                { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), builder.Utf8Bytes, limits.MaxOutputBytes); }
                // The chunks no longer refer to PDF values. Close the reader
                // before allocating immutable text and publication copies.
                if (!testJsonSchema) { await document.DisposeAsync().ConfigureAwait(false); }
                using GovernedText text = builder.Finish();
                if (testJsonSchema)
                {
                    // Preserve the schema note's acceptance before input close.
                    CheckJsonSchema(text.Value, inspection.Version, governor, diagnostics, cancellationToken);
                    await document.DisposeAsync().ConfigureAwait(false);
                }
                output.Prepare(text, outputPath.HasValue, escapeText, limits);
            }
            List<Json.JsonPayload> sidecars = output.Sidecars;
            long mainBytes = output.Main?.Data.Length ?? 0L;

            diagnosticStage?.Flush();
            if (governor.DeferredPlainHistory)
            {
                _ = ExecutionPresentation.Create(governor, diagnostics, false, false, false, false, false, cancellationToken);
            }
            long anticipatedBytes = mainBytes;
            long publicationUnits = outputPath.HasValue ? 1L + (mainBytes + 32767L) / 32768L : 0L;
            foreach (Json.JsonPayload sidecar in sidecars)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
                if (string.Equals(sidecar.Name, inputPath.Value, StringComparison.Ordinal))
                { throw new QpdfParseException("A JSON stream file must differ from the input file.", 0L, null); }
                _ = QpdfPath.From(sidecar.Name);
                anticipatedBytes = checked(anticipatedBytes + sidecar.Data.Length);
                publicationUnits = checked(publicationUnits + 1L + (sidecar.Data.Length + 32767L) / 32768L);
            }
            governor.ChargeOutputBytes(anticipatedBytes);
            using PublicationWorkReservation publicationWork = PublicationWorkReservation.Create(governor, publicationUnits);
            governor.PrepareExecutionPresentation();
            publicationWork.Begin();
            if (outputPath.HasValue)
            { await PublishPreparedBytesAsync(host, outputPath.Value, output.Main ?? throw new InvalidOperationException("Missing JSON publication."), governor, cancellationToken).ConfigureAwait(false); }
            foreach (Json.JsonPayload sidecar in sidecars)
            { await PublishPreparedBytesAsync(host, QpdfPath.From(sidecar.Name), sidecar, governor, cancellationToken).ConfigureAwait(false); }
            return new JsonInspectionOutcome(output.TransferText(), ResultWarningSnapshot.Read(resultWarnings, diagnostics));
        }
        catch (Exception ex) when (ex is QpdfException or OperationCanceledException)
        {
            // The document's await-using closes first. Export only previously
            // accepted warnings with their held work; summaries are abandoned.
            governor.AbandonPresentationCount();
            diagnosticStage?.FlushAcceptedAfterFailure();
            throw;
        }
    }

    private static async ValueTask<JsonImportOutcome> ImportCreateCoreAsync(
        IQpdfHost host,
        QpdfPath jsonPath,
        QpdfPath outputPath,
        string? minVersion,
        string? forceVersion,
        bool staticId,
        bool newlineBeforeEndstream,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        if (string.Equals(jsonPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        System.Text.Json.JsonDocument parsed = await Json.JsonImport.ReadJsonAsync(host, jsonPath, limits, governor, cancellationToken).ConfigureAwait(false);
        using (parsed)
        {
            Json.ImportedModel model = await Json.JsonImport.BuildCreateModelAsync(host, parsed, limits, governor, cancellationToken).ConfigureAwait(false);
            Json.JsonImport.RenumberedModel renumbered = Json.JsonImport.Renumber(model, limits, governor, cancellationToken);
            (PdfVersion effective, int extensionLevel) = NegotiateVersion(model.HeaderVersion, minVersion, forceVersion, governor);
            await PublishModelAsync(host, effective, extensionLevel, renumbered.Model, null, false, null, renumbered.InfoNumber, null, staticId, newlineBeforeEndstream, Writing.QdfOptions.Disabled, null, false, null, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
            return new JsonImportOutcome(renumbered.PageCount, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
        }
    }

    private static async ValueTask<JsonImportOutcome> ImportUpdateCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath jsonPath,
        QpdfPath outputPath,
        string? minVersion,
        string? forceVersion,
        bool staticId,
        bool newlineBeforeEndstream,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        if (string.Equals(inputPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        if (string.Equals(jsonPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the JSON file.", 0L, null);
        }

        await using QpdfDocument document = await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        System.Text.Json.JsonDocument parsed = await Json.JsonImport.ReadJsonAsync(host, jsonPath, limits, governor, cancellationToken).ConfigureAwait(false);
        using (parsed)
        {
            Json.ImportedModel model = await Json.JsonImport.BuildUpdateModelAsync(host, parsed, document, limits, governor, cancellationToken).ConfigureAwait(false);
            Json.JsonImport.RenumberedModel renumbered = Json.JsonImport.Renumber(model, limits, governor, cancellationToken);
            byte[]? originalId1 = TryGetOriginalId(document.Engine);
            (PdfVersion effective, int extensionLevel) = NegotiateVersion(model.HeaderVersion, minVersion, forceVersion, governor);
            await PublishModelAsync(host, effective, extensionLevel, renumbered.Model, null, false, null, renumbered.InfoNumber, originalId1, staticId, newlineBeforeEndstream, Writing.QdfOptions.Disabled, null, false, null, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
            return new JsonImportOutcome(renumbered.PageCount, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
        }
    }

    private static async ValueTask<TypedCheckOutcome> CheckCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfCheckOptions options,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        bool emptyDocument,
        bool showNpages,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        // Native checking traverses pages without warning about their declared count.
        // The sink belongs to this job, including any following linearization inspection.
        diagnostics.ReportPageCountMismatch = false;
        List<QpdfCheckOutcome> checks;
        int pageCount;
        PdfVersion version;
        await using (QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false))
        {
            checks = DocumentCheck.Run(document, options, limits, governor, diagnostics, false, cancellationToken);
            pageCount = document.PageCount;
            version = document.Version;
            if (showNpages && !checks.Exists(static outcome => outcome.Status == QpdfCheckStatus.Failed))
            {
                AddPageCountDiagnostic(document, inputPath, diagnostics);
            }
        }
        using OwnedAllocationBatch resultStorage = OwnedAllocationBatch.Create(governor, 4, cancellationToken);
        IReadOnlyList<QpdfCheckOutcome> resultChecks = ResultCollectionStorage.Copy(resultStorage, checks, IntPtr.Size);
        IReadOnlyList<QpdfDiagnostic> resultDiagnostics = ResultCollectionStorage.Copy(resultStorage, diagnostics.Entries, IntPtr.Size);
        resultStorage.RetainWithPrepaidRetirement();
        return new TypedCheckOutcome(pageCount, version, resultChecks, resultDiagnostics);
    }

    private static void AddPageCountDiagnostic(QpdfDocument document, QpdfPath input, DiagnosticBag diagnostics)
    {
        long count = document.ReadDeclaredPageCount();
        diagnostics.AddParts(QpdfDiagnosticCode.PageCount, QpdfDiagnosticSeverity.Info, [input.Value, ": ", DiagnosticPart.Invariant(count)]);
    }

    private static async ValueTask InspectLinearizationAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        bool emptyDocument,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        await using QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        InspectLinearization(document, inputPath, limits, governor, diagnostics, cancellationToken);
    }

    private static async ValueTask ShowLinearizationAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        bool emptyDocument,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        await using QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        ShowLinearization(document, inputPath, limits, governor, diagnostics, cancellationToken);
    }

    private static readonly PdfName[] WriterInherited =
        [PdfName.FromAscii("Resources"), PdfName.FromAscii("MediaBox"), PdfName.FromAscii("CropBox"), PdfName.FromAscii("Rotate")];

    private static Dictionary<PdfName, QpdfObject> MaterializeAncestors(
        DocumentEngine engine, PageLocation location, GraphCopier copier,
        ResourceGovernor governor, QpdfLimits limits, CancellationToken cancellationToken)
        => MaterializeAncestors(engine, location, copier, governor, limits, cancellationToken, null);

    private static Dictionary<PdfName, QpdfObject> MaterializeAncestors(
        DocumentEngine engine, PageLocation location, GraphCopier copier,
        ResourceGovernor governor, QpdfLimits limits, CancellationToken cancellationToken,
        ParserMemoryScope? ownership)
    {
        Dictionary<PdfName, QpdfObject> extra = ownership is null ? new()
            : ParserContainers.CreateDictionary<PdfName, QpdfObject>(ownership);
        foreach (PdfName key in WriterInherited)
        {
            governor.CheckCancellation();
            if (location.Page.TryGet(key, out QpdfObject present)
                && PageInheritance.Resolve(engine, present, limits, governor) is not PdfNull) { continue; }
            QpdfObject? value = PageInheritance.Find(engine, location, key, limits, governor, cancellationToken);
            if (value is null) { continue; }
            QpdfObject copied = copier.CopyValue(value, 0);
            if (copied is PdfNull) { continue; }
            if (ownership is not null) { ParserContainers.EnsureEntryCapacity(ownership, extra, IntPtr.Size); }
            extra[key] = copied;
        }
        return extra;
    }

    private static void RequireMediaBox(PdfDictionary page)
    {
        if (!page.TryGet(PdfName.FromAscii("MediaBox"), out QpdfObject box) || box is PdfNull)
        {
            throw new QpdfParseException("Selected page has no /MediaBox in its ancestry.", null, null);
        }
    }

    private sealed class TransactionSink : IByteSink
    {
        private readonly IQpdfOutputTransaction _transaction;
        private readonly ResourceGovernor _governor;
        private long _length;

        internal TransactionSink(IQpdfOutputTransaction transaction, ResourceGovernor governor)
        {
            _transaction = transaction;
            _governor = governor;
        }

        public long Length => _length;

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            _governor.ChargeOutputBytes(chunk.Length);
            await _transaction.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            checked
            {
                _length += chunk.Length;
            }
        }
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
        ResourceGovernor governor = new(options.Limits, cancellationToken);
        governor.ExpectExecutionResult();
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

    private static async ValueTask<ExecutionOutcome> RunExpandedInvocationAsync(
        IQpdfHost host, IReadOnlyList<string> expanded, QpdfExecutionOptions options,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {

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

        if (JobJsonLoader.HasJobFile(expanded)
            || expanded.Any(token => token == "--check" || token.StartsWith("--check=", StringComparison.Ordinal)
                || token == "--raw-stream-data" || token.StartsWith("--raw-stream-data=", StringComparison.Ordinal)
                || token == "--filtered-stream-data" || token.StartsWith("--filtered-stream-data=", StringComparison.Ordinal)
                || token == "--show-attachment" || token.StartsWith("--show-attachment=", StringComparison.Ordinal)
                || token == "--show-object" || token.StartsWith("--show-object=", StringComparison.Ordinal)
                || token == "--show-npages" || token.StartsWith("--show-npages=", StringComparison.Ordinal)
                || token == "--show-pages" || token.StartsWith("--show-pages=", StringComparison.Ordinal)
                || token == "--show-xref" || token.StartsWith("--show-xref=", StringComparison.Ordinal)
                || token == "--list-attachments" || token.StartsWith("--list-attachments=", StringComparison.Ordinal)
                || token == "--check-linearization" || token.StartsWith("--check-linearization=", StringComparison.Ordinal)
                || token == "--show-linearization" || token.StartsWith("--show-linearization=", StringComparison.Ordinal)
                || token == "--set-page-labels" || token.StartsWith("--set-page-labels=", StringComparison.Ordinal)
                || token == "--remove-page-labels" || token.StartsWith("--remove-page-labels=", StringComparison.Ordinal)))
        {
            JobJsonLoader.LoadedJob loaded;
            try
            {
                loaded = await JobJsonLoader.ParseArgvAsync(host, expanded, options.Limits, governor, cancellationToken).ConfigureAwait(false);
            }
            catch (QpdfException ex)
            {
                ExecutionOutcome failure = ToFailedResult(ex);
                return failure;
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }

            return await RunLoadedJobAsync(host, loaded, options, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        JobPlan plan;
        try
        {
            plan = ArgvParser.Parse(expanded, governor);
        }
        catch (QpdfException ex)
        {
            ExecutionOutcome failure = ToFailedResult(ex);
            return failure;
        }
        catch (OperationCanceledException)
        {
            return ExecutionOutcome.Canceled([]);
        }

        try
        { return await RunPlanAsync(host, plan, options.Limits, options.ExplicitLimits, governor, diagnostics, cancellationToken).ConfigureAwait(false); }
        finally { plan.CollationStorage?.Dispose(); }
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
        ResourceGovernor governor = new(options.Limits, cancellationToken);
        governor.ExpectExecutionResult();
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

    private static async ValueTask<ExecutionOutcome> RunLoadedJobAsync(
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

    private static async ValueTask<IReadOnlyList<QpdfAttachmentInfo>> ListAttachmentsCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        bool emptyDocument,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        await using QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        return ListAttachments(document, inputPath, limits, governor, diagnostics, cancellationToken);
    }

    private static QpdfLimits ResolveParserLimits(JobPlan plan, QpdfLimits host, bool hostExplicit)
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

    private static void RequireParserCeiling(string name, long requested, long hostCeiling, bool hostExplicit)
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

    private static async ValueTask ShowObjectCoreAsync(
        IQpdfHost host,
        ShowObjectPlan showObject,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        await using QpdfDocument document = await OpenPlanInputAsync(host, showObject.Input, showObject.EmptyDocument, limits, governor, diagnostics, showObject.Recovery, cancellationToken).ConfigureAwait(false);
        await ShowObjectAsync(host, document, showObject, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ExecutionOutcome> RunPlanAsync(
        IQpdfHost host,
        JobPlan plan,
        QpdfLimits limits,
        bool hostExplicit,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        PresentationCountReservation? reservation = null;
        ExecutionPresentation? presentation = null;
        try
        {
            limits = ResolveParserLimits(plan, limits, hostExplicit);
            reservation = PresentationCountReservation.Create(governor, plan.Verbose,
                plan.ReportMemoryUsage, plan.Progress, cancellationToken);
            if (plan is JsonPlan && !plan.Verbose && !plan.ReportMemoryUsage && !plan.Progress && !plan.NoWarn)
            { governor.DeferredPlainHistory = true; }
            else
            {
                presentation = ExecutionPresentation.Create(governor, diagnostics, plan.Verbose,
                    plan.ReportMemoryUsage, plan.Progress, plan.NoWarn, plan.WarningExitZero, cancellationToken);
            }
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
        finally { (presentation ?? governor.ExecutionPresentation)?.Dispose(); reservation?.Dispose(); }
    }

    private static async ValueTask<ExecutionOutcome> RunPlanCoreAsync(IQpdfHost host, JobPlan plan,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        if (plan.IgnoreXrefStreams)
        {
            limits = limits with { IgnoreXrefStreams = true };
        }

        if (plan is CheckPlan check)
        {
            return await RunCheckPlanAsync(host, check, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        if (plan is ShowXrefPlan showXref)
        {
            try
            {
                await using QpdfDocument document = await OpenPlanInputAsync(host, showXref.Input, showXref.EmptyDocument, limits, governor, diagnostics, showXref.Recovery, cancellationToken).ConfigureAwait(false);
                ShowXref(document, plan, limits, governor, diagnostics, cancellationToken);

                return CompleteWithPresentation(diagnostics.Entries, plan, governor);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }
        }

        if (plan is ShowObjectPlan showObject)
        {
            try
            {
                await ShowObjectCoreAsync(host, showObject, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                return CompleteWithPresentation(diagnostics.Entries, plan, governor);
            }
            catch (QpdfException ex)
            {
                ExecutionOutcome failure = ToFailedResult(ex);
                return new ExecutionOutcome(failure.Status, failure.ExitCode, failure.Diagnostics);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }
        }

        if (plan is ShowPagesPlan showPages)
        {
            try
            {
                await using QpdfDocument document = await OpenPlanInputAsync(host, showPages.Input, showPages.EmptyDocument, limits, governor, diagnostics, showPages.Recovery, cancellationToken).ConfigureAwait(false);
                ShowPages(document, plan, limits, governor, diagnostics, cancellationToken);

                return CompleteWithPresentation(diagnostics.Entries, plan, governor);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }
        }

        if (plan is ShowNpagesPlan npages)
        {
            try
            {
                diagnostics.ReportPageCountMismatch = false;
                await using (QpdfDocument document = await OpenPlanInputAsync(host, npages.Input, npages.EmptyDocument, limits, governor, diagnostics, npages.Recovery, cancellationToken).ConfigureAwait(false))
                {
                    AddPageCountDiagnostic(document, npages.Input, diagnostics);
                }
                return CompleteWithPresentation(diagnostics.Entries, plan, governor);
            }
            catch (QpdfException ex)
            {
                ExecutionOutcome failure = ToFailedResult(ex);
                return new ExecutionOutcome(failure.Status, failure.ExitCode, failure.Diagnostics);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }
        }

        if (plan is LinearizationPlan linearization)
        {
            try
            {
                if (linearization.Check)
                {
                    await InspectLinearizationAsync(host, linearization.Input, limits, linearization.Recovery, linearization.EmptyDocument, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                }

                if (linearization.Show)
                {
                    await ShowLinearizationAsync(host, linearization.Input, limits, linearization.Recovery, linearization.EmptyDocument, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                }

                return CompleteWithPresentation(diagnostics.Entries, plan, governor);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }
        }

        if (plan is AttachmentPlan attachments)
        {
            try
            {
                if (attachments.List)
                {
                    await ListAttachmentsCoreAsync(host, attachments.Input, limits, attachments.Recovery, attachments.EmptyDocument, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                }

                if (attachments.Key is not null)
                {
                    QpdfPath output = attachments.Output ?? throw new QpdfParseException("An output file is required after --.", 0L, null);
                    await ExtractAttachmentCoreAsync(host, attachments.Input, attachments.Key, output, limits, attachments.Recovery, attachments.EmptyDocument, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                }

                return CompleteWithPresentation(diagnostics.Entries, plan, governor);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }
        }

        if (plan is JsonPlan json)
        {
            try
            {
                Commands.JsonInspection inspection = new(json.Version, json.Keys, json.DecodeLevel, json.StreamDataMode, json.StreamPrefix, json.Objects, json.MinimalOutput);
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
                return CompleteWithPresentation(inspected.Warnings, plan, governor).WithOutputText(json.Output.HasValue ? null : inspected.Json);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }
        }

        if (plan is JsonInputPlan jsonInput)
        {
            try
            {
                JsonImportOutcome imported = await ImportCreateCoreAsync(
                    host,
                    jsonInput.Input,
                    jsonInput.Output,
                    jsonInput.MinVersion,
                    jsonInput.ForceVersion,
                    jsonInput.StaticId,
                    jsonInput.NewlineBeforeEndstream,
                    limits,
                    governor,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                return CompleteWithPresentation(imported.Warnings, plan, governor);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }
        }

        if (plan is JsonUpdatePlan jsonUpdate)
        {
            try
            {
                JsonImportOutcome updated = await ImportUpdateCoreAsync(
                    host,
                    jsonUpdate.Input,
                    jsonUpdate.JsonPath,
                    jsonUpdate.Output,
                    jsonUpdate.MinVersion,
                    jsonUpdate.ForceVersion,
                    jsonUpdate.StaticId,
                    jsonUpdate.NewlineBeforeEndstream,
                    limits,
                    jsonUpdate.Recovery,
                    governor,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                return CompleteWithPresentation(updated.Warnings, plan, governor);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
            }
        }

        if (plan is WholeFilePlan whole)
        {
            try
            {
                MultiPageSelectionOutcome result = (await WholeFileCoreAsync(host, whole, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false)).Result;
                return CompleteWithPresentation(result.Warnings, plan, governor);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return ExecutionOutcome.Failed(
                    [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)]);
            }
            catch (OperationCanceledException)
            {
                return ExecutionOutcome.Canceled([]);
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
                        resolved.Add(new PdfPageSource(path, source.Selection));
                    }

                    MultiPageSelectionOutcome merged = (await SelectPagesCoreAsync(
                        host,
                        resolved,
                        merge.Output,
                        merge.Input,
                        true,
                        limits,
                        merge.Recovery,
                        merge.Collation,
                        merge.Rotations,
                        merge.Labels,
                        merge.RemoveLabels,
                        null,
                        merge.SplitPages,
                        merge.ReplaceInput,
                        merge.MinVersion,
                        merge.ForceVersion,
                        merge.DeterministicId,
                        merge.StaticId,
                        merge.NewlineBeforeEndstream,
                        merge.ObjectStreams,
                        merge.PreserveUnreferenced,
                        merge.Compression(),
                        merge.RemoveInfo,
                        merge.RemoveAcroform,
                        merge.RemoveStructure,
                        merge.RemoveMetadata,
                        merge.Edits(),
                        merge.CoalesceContents,
                        merge.NormalizeContent,
                        merge.ExternalizeInlineImages,
                        merge.IiMinBytes,
                        merge.Underlays,
                        merge.Overlays,
                        merge.FlattenRotation,
                        merge.FlattenAnnotations,
                        merge.GenerateAppearances,
                        new Content.ImageOptimizeRequest(merge.OptimizeImages, merge.OiMinWidth, merge.OiMinHeight, merge.OiMinArea, merge.KeepInlineImages, merge.JpegQuality),
                        new Writing.QdfOptions(merge.Qdf, merge.NoOriginalObjectIds),
                        new Content.ContentPrunePolicy(true, merge.RemoveUnreferencedResources),
                        true,
                        governor,
                        diagnostics,
                        merge.Linearize,
                        merge.LinearizePass1,
                        merge.EmptyInput,
                        null,
                        cancellationToken).ConfigureAwait(false)).Result;
                    return CompleteWithPresentation(merged.Warnings, plan, governor);
                }
                catch (QpdfException ex)
                {
                    return ToFailedResult(ex);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return ExecutionOutcome.Failed(
                        [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)]);
                }
                catch (OperationCanceledException)
                {
                    return ExecutionOutcome.Canceled([]);
                }
            }

            SelectionPlan selection = (SelectionPlan)plan;
            if (selection.SplitPages.HasValue || selection.ReplaceInput)
            {
                try
                {
                    List<PdfPageSource> solo = new(1)
                    {
                        new PdfPageSource(selection.Input, new PdfPageSelectionOptions(selection.Page)),
                    };
                    QpdfPath target = selection.ReplaceInput ? selection.Input : selection.Output;
                    MultiPageSelectionOutcome merged = (await SelectPagesCoreAsync(
                        host,
                        solo,
                        target,
                        selection.Input,
                        true,
                        limits,
                        selection.Recovery,
                        null,
                        selection.Rotations,
                        selection.Labels,
                        selection.RemoveLabels,
                        null,
                        selection.SplitPages,
                        selection.ReplaceInput,
                        selection.MinVersion,
                        selection.ForceVersion,
                        selection.DeterministicId,
                        selection.StaticId,
                        selection.NewlineBeforeEndstream,
                        selection.ObjectStreams,
                        selection.PreserveUnreferenced,
                        selection.Compression(),
                        selection.RemoveInfo,
                        selection.RemoveAcroform,
                        selection.RemoveStructure,
                        selection.RemoveMetadata,
                        selection.Edits(),
                        selection.CoalesceContents,
                        selection.NormalizeContent,
                        selection.ExternalizeInlineImages,
                        selection.IiMinBytes,
                        selection.Underlays,
                        selection.Overlays,
                        selection.FlattenRotation,
                        selection.FlattenAnnotations,
                        selection.GenerateAppearances,
                        new Content.ImageOptimizeRequest(selection.OptimizeImages, selection.OiMinWidth, selection.OiMinHeight, selection.OiMinArea, selection.KeepInlineImages, selection.JpegQuality),
                        new Writing.QdfOptions(selection.Qdf, selection.NoOriginalObjectIds),
                        new Content.ContentPrunePolicy(true, selection.RemoveUnreferencedResources),
                        true,
                        governor,
                        diagnostics,
                        selection.Linearize,
                        selection.LinearizePass1,
                        false,
                        null,
                        cancellationToken).ConfigureAwait(false)).Result;
                    return CompleteWithPresentation(merged.Warnings, plan, governor);
                }
                catch (QpdfException ex)
                {
                    return ToFailedResult(ex);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return ExecutionOutcome.Failed(
                        [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)]);
                }
                catch (OperationCanceledException)
                {
                    return ExecutionOutcome.Canceled([]);
                }
            }

            PageSelectionOutcome selected = await SelectPageCoreAsync(
                host,
                selection.Input,
                selection.Output,
                new PdfPageSelectionOptions(selection.Page),
                limits,
                selection.Recovery,
                selection.Rotations,
                selection.Labels,
                selection.RemoveLabels,
                selection.MinVersion,
                selection.ForceVersion,
                selection.DeterministicId,
                selection.StaticId,
                selection.NewlineBeforeEndstream,
                selection.ObjectStreams,
                selection.PreserveUnreferenced,
                selection.Compression(),
                selection.RemoveInfo,
                selection.RemoveAcroform,
                selection.RemoveStructure,
                selection.RemoveMetadata,
                selection.Edits(),
                selection.CoalesceContents,
                selection.NormalizeContent,
                selection.ExternalizeInlineImages,
                selection.IiMinBytes,
                selection.Underlays,
                selection.Overlays,
                selection.FlattenRotation,
                selection.FlattenAnnotations,
                selection.GenerateAppearances,
                new Content.ImageOptimizeRequest(selection.OptimizeImages, selection.OiMinWidth, selection.OiMinHeight, selection.OiMinArea, selection.KeepInlineImages, selection.JpegQuality),
                new Writing.QdfOptions(selection.Qdf, selection.NoOriginalObjectIds),
                new Content.ContentPrunePolicy(true, selection.RemoveUnreferencedResources),
                true,
                governor,
                diagnostics,
                selection.Linearize,
                selection.LinearizePass1,
                cancellationToken).ConfigureAwait(false);
            return CompleteWithPresentation(selected.Warnings, plan, governor);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ExecutionOutcome.Failed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)]);
        }
        catch (OperationCanceledException)
        {
            return ExecutionOutcome.Canceled([]);
        }
    }

    private static ExecutionOutcome CompleteWithPresentation(
        IReadOnlyList<QpdfDiagnostic> diagnostics,
        JobPlan plan,
        ResourceGovernor governor)
    {
        if (governor.CompleteExecutionPresentation() is PresentedDiagnostics owned)
        {
            return new ExecutionOutcome(owned.ExitCode == QpdfExitCode.Error ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed,
                owned.ExitCode, owned.Diagnostics);
        }
        ExecutionCounterValues counters = governor.CounterValues;
        governor.PresentDiagnosticCount();
        List<QpdfDiagnostic> effective = new();
        foreach (QpdfDiagnostic diagnostic in diagnostics)
        {
            if (plan.NoWarn && diagnostic.Severity == QpdfDiagnosticSeverity.Warning)
            {
                continue;
            }

            effective.Add(diagnostic);
        }

        if (plan.Verbose)
        {
            effective.Add(new QpdfDiagnostic(QpdfDiagnosticCode.VerboseSummary, QpdfDiagnosticSeverity.Info, $"Verbose summary: {counters.TotalOutputBytes} output bytes, {counters.TotalDecodedBytes} decoded bytes, peak {counters.PeakMemoryBytes} bytes (estimated)."));
        }

        if (plan.ReportMemoryUsage)
        {
            effective.Add(new QpdfDiagnostic(QpdfDiagnosticCode.MemoryUsage, QpdfDiagnosticSeverity.Info, $"Peak memory usage: {counters.PeakMemoryBytes} bytes (estimated)."));
        }

        if (plan.Progress && counters.TotalOutputBytes > 0L)
        {
            effective.Add(new QpdfDiagnostic(QpdfDiagnosticCode.ProgressReport, QpdfDiagnosticSeverity.Info, $"Write progress: 25% of {counters.TotalOutputBytes} bytes."));
            effective.Add(new QpdfDiagnostic(QpdfDiagnosticCode.ProgressReport, QpdfDiagnosticSeverity.Info, $"Write progress: 50% of {counters.TotalOutputBytes} bytes."));
            effective.Add(new QpdfDiagnostic(QpdfDiagnosticCode.ProgressReport, QpdfDiagnosticSeverity.Info, $"Write progress: 75% of {counters.TotalOutputBytes} bytes."));
            effective.Add(new QpdfDiagnostic(QpdfDiagnosticCode.ProgressReport, QpdfDiagnosticSeverity.Info, $"Write progress: 100% of {counters.TotalOutputBytes} bytes."));
        }

        bool error = false;
        foreach (QpdfDiagnostic diagnostic in effective)
        {
            if (diagnostic.Severity == QpdfDiagnosticSeverity.Error)
            {
                error = true;
                break;
            }
        }

        if (error)
        {
            return ExecutionOutcome.Failed(effective);
        }

        if (plan.WarningExitZero)
        {
            return new ExecutionOutcome(QpdfExecutionStatus.Completed, QpdfExitCode.Success, effective);
        }

        bool warning = false;
        foreach (QpdfDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == QpdfDiagnosticSeverity.Warning)
            {
                warning = true;
                break;
            }
        }

        return new ExecutionOutcome(QpdfExecutionStatus.Completed, warning ? QpdfExitCode.Warning : QpdfExitCode.Success, effective);
    }

    private static ExecutionOutcome CryptoFailed(string option)
    {
        QpdfDiagnostic diagnostic = new(
            QpdfDiagnosticCode.UnsupportedCryptoOperation,
            QpdfDiagnosticSeverity.Error,
            $"Cryptographic option '{option}' is permanently out of scope.");
        return ExecutionOutcome.Failed([diagnostic]);
    }

    private static ExecutionOutcome ToFailedResult(QpdfException ex)
    {
        if (ex is QpdfCryptoException crypto)
        {
            return CryptoFailed(crypto.Option);
        }

        if (ex is QpdfEncryptedInputException)
        {
            return Failed(QpdfDiagnosticCode.UnsupportedCryptoOperation, ex.Message);
        }

        if (ex is QpdfLimitException)
        {
            return Failed(QpdfDiagnosticCode.BudgetExceeded, ex.Message);
        }

        if (ex is QpdfFilterException)
        {
            return Failed(QpdfDiagnosticCode.UnsupportedFeature, ex.Message);
        }

        if (ex is QpdfParseException)
        {
            return Failed(QpdfDiagnosticCode.InvalidArguments, ex.Message);
        }

        return Failed(QpdfDiagnosticCode.HostOperationFailed, ex.Message);
    }

    private static ExecutionOutcome Failed(QpdfDiagnosticCode code, string message)
    {
        QpdfDiagnostic diagnostic = new(code, QpdfDiagnosticSeverity.Error, message);
        return ExecutionOutcome.Failed([diagnostic]);
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
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.ExpectExecutionResult();
        ExecutionOutcome result;
        try
        {
            await using IQpdfInput input = await GovernedHost.OpenInputAsync(host, inputPath, governor, cancellationToken).ConfigureAwait(false);
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

                governor.ReserveMemory(input.Length);
                bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }

            await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, outputPath, governor, cancellationToken).ConfigureAwait(false);
            TransactionSink sink = new(transaction, governor);
            try
            {
                QdfRepair repair = new(governor, sink);
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
        if (ExecutionResultAdmission.Reject(limits, cancellationToken) is QpdfExecutionResult rejection)
        {
            // Companion validation precedes cancellation/budget rejection, as
            // it does for admitted jobs. These messages contain no job data.
            if (options.Mode != QpdfZlibFlateMode.Uncompress && options.Mode != QpdfZlibFlateMode.Compress)
            { return ExecutionResultAdmission.InvalidZlibMode; }
            if (options.CompressionLevel.HasValue && (options.Mode != QpdfZlibFlateMode.Compress || options.CompressionLevel.Value < 1 || options.CompressionLevel.Value > 9))
            { return ExecutionResultAdmission.InvalidZlibLevel; }
            return rejection;
        }
        ResourceGovernor governor = new(limits, cancellationToken);
        governor.ExpectExecutionResult();
        if (options.Mode != QpdfZlibFlateMode.Uncompress && options.Mode != QpdfZlibFlateMode.Compress)
        {
            return governor.FinishExecutionResult(ToFailedResult(new QpdfParseException(ExecutionResultAdmission.InvalidZlibModeMessage, null, null)));
        }

        if (options.CompressionLevel.HasValue && (options.Mode != QpdfZlibFlateMode.Compress || options.CompressionLevel.Value < 1 || options.CompressionLevel.Value > 9))
        {
            return governor.FinishExecutionResult(ToFailedResult(new QpdfParseException(ExecutionResultAdmission.InvalidZlibLevelMessage, null, null)));
        }

        ExecutionOutcome result;
        try
        {
            await using IQpdfInput input = await GovernedHost.OpenInputAsync(host, inputPath, governor, cancellationToken).ConfigureAwait(false);
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

                governor.ReserveMemory(input.Length);
                bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }

            byte[] output = options.Mode == QpdfZlibFlateMode.Compress
                ? FlateFilter.Encode(bytes, options.CompressionLevel, limits, governor)
                : DecodeZlibFlate(bytes, limits, governor);
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
