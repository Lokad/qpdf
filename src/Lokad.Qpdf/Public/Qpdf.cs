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
public static class Qpdf
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
        DiagnosticBag diagnostics = new(governor);
        return await SelectPageCoreAsync(host, inputPath, outputPath, options, limits, QpdfRecoveryMode.Strict, [], [], false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, cancellationToken).ConfigureAwait(false);
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
        List<OutputRotation> specifications = ConvertRotations(rotations);
        ResourceGovernor governor = new(limits, cancellationToken);
        DiagnosticBag diagnostics = new(governor);
        return await SelectPageCoreAsync(host, inputPath, outputPath, options, limits, QpdfRecoveryMode.Strict, specifications, [], false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, cancellationToken).ConfigureAwait(false);
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
        Content.ContentPrunePolicy prune,
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        bool pruneHere = prune.Enabled && (prune.Mode == "yes"
            || (prune.Mode is null && Content.ContentTransforms.SharedResourcesHeuristic(engine, limits, governor, cancellationToken)));
        if (!coalesceContents && !normalizeContent && !pruneHere && !externalizeInlineImages)
        {
            return;
        }

        Content.ContentTransformRequest request = new(coalesceContents, normalizeContent, compress, level, externalizeInlineImages, iiMinBytes);
        Content.ContentTransforms.ApplyPageTransforms(copier, pageIds, request, pruneHere, limits, governor, diagnostics, cancellationToken);
    }


    private static async ValueTask<PdfPageSelectionResult> SelectPageCoreAsync(
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
        await using QpdfDocument document = await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
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

        GraphCopier copier = new(document.Engine, governor, limits, 3);
        List<PdfReference> kids = new(requested.Count);
        List<(GraphCopier Copier, int Id, PdfDictionary Page)> occurrences = new(requested.Count);
        foreach (int pageNumber in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            PageLocation location = document.PageReferences[pageNumber - 1];
            Dictionary<PdfName, QpdfObject> extra = MaterializeAncestors(document.Engine, location, copier, governor, limits, cancellationToken);
            (int pageId, PdfDictionary page) = copier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra);
            RequireMediaBox(page);
            kids.Add(new PdfReference(pageId, 0));
            occurrences.Add((copier, pageId, page));
        }

        ApplyRotations(occurrences, ResolveRotationFolds(rotations, kids.Count, governor, limits), governor);
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

        Dictionary<PdfName, QpdfObject> singleExtras = Content.CatalogCopy.TryCopyExtras(document.Engine, copier, preserveCatalog, governor, cancellationToken);
        SortedDictionary<string, PdfReference> singleExisting = PreserveExistingAttachments(copier, document.Engine, edits, limits, governor, diagnostics, cancellationToken);
        string? singlePageMode = ReadInputPageMode(document.Engine);

        (bool singleCompress, string singleDecode, int? singleLevel) = ResolveCompression(compression, qdf.Enabled);
        if (singleCompress || singleDecode != "none")
        {
            TransformCopierStreams(copier, document.Engine, singleCompress, singleDecode, compression.RecompressFlate, singleLevel, limits, governor, diagnostics, cancellationToken);
        }

        List<int> occurrenceIds = new(occurrences.Count);
        foreach ((GraphCopier _, int occurrenceId, PdfDictionary _) in occurrences)
        {
            occurrenceIds.Add(occurrenceId);
        }

        ApplySourceContentTransforms(copier, occurrenceIds, coalesceContents, normalizeContent, externalizeInlineImages || (optimize.Optimize && !optimize.KeepInline), iiMinBytes, singleCompress, singleLevel, prune, document.Engine, limits, governor, diagnostics, cancellationToken);

        List<(GraphCopier Copier, int Id)> overlayOccurrences = new(occurrences.Count);
        foreach ((GraphCopier occurrenceCopier, int occurrenceId, PdfDictionary _) in occurrences)
        {
            overlayOccurrences.Add((occurrenceCopier, occurrenceId));
        }

        Content.OverlayApplication overlayApplied = await Content.Overlays.ApplyAsync(host, inputPath, overlayOccurrences, underlays, overlays, singleCompress, singleLevel, recovery, limits, governor, diagnostics, cancellationToken, copier.NextId).ConfigureAwait(false);

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


        (PdfVersion negotiated, int negotiatedExtension) = NegotiateVersion(document.Version, minVersion, forceVersion);
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
        AttachmentAssembly singleAttached = await AssembleAttachmentsAsync(host, singleExisting, singlePool, edits, singlePageMode, singleCompress, singleLevel, singleSources, recovery, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        HashSet<int> singleCompressed = ResolveCompressedSet(streamMode, singlePool, singleSources, governor);
        PdfVersion effective = negotiated;
        int extensionLevel = negotiatedExtension;
        if (singleCompressed.Count > 0 && CompareVersions(effective.Major, effective.Minor, 1, 5) < 0)
        {
            effective = new PdfVersion(1, 5);
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

            List<(int Index, PdfDictionary Label)> singleItems = Content.PageLabelMerge.Merge(singleOccurrences, limits, governor, cancellationToken);
            singleLabels = singleItems.Count == 0 ? null : NumbersDictionary(singleItems);
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
                cancellationToken);
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
        await PublishModelAsync(host, effective, extensionLevel, model, singleStreams, singleCompress, singleLevel, singleInfo?.ObjectNumber, originalId, staticId, newlineBeforeEndstream, qdf, singleOriginalIds, linearize, linearizePass1, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
        return new PdfPageSelectionResult(total, [.. requested], kids.Count, [.. diagnostics.Entries], governor.Snapshot());
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
        List<PdfPageSource> frozen = new(sources.Count);
        foreach (PdfPageSource source in sources)
        {
            if (source is null)
            {
                throw new ArgumentException("Page sources must not contain null entries.", nameof(sources));
            }

            frozen.Add(source);
        }

        if (frozen.Count == 0)
        {
            throw new ArgumentException("At least one page source is required.", nameof(sources));
        }

        ResourceGovernor governor = new(limits, cancellationToken);
        DiagnosticBag diagnostics = new(governor);
        return (await SelectPagesCoreAsync(host, frozen, outputPath, frozen[0].Input, true, limits, QpdfRecoveryMode.Strict, null, [], [], false, null, null, false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, false, cancellationToken).ConfigureAwait(false)).Result;
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
        List<PdfPageSource> frozen = new(sources.Count);
        foreach (PdfPageSource source in sources)
        {
            if (source is null)
            {
                throw new ArgumentException("Page sources must not contain null entries.", nameof(sources));
            }

            frozen.Add(source);
        }

        if (frozen.Count == 0)
        {
            throw new ArgumentException("At least one page source is required.", nameof(sources));
        }

        List<OutputRotation> specifications = ConvertRotations(rotations);
        ResourceGovernor governor = new(limits, cancellationToken);
        DiagnosticBag diagnostics = new(governor);
        return (await SelectPagesCoreAsync(host, frozen, outputPath, frozen[0].Input, true, limits, QpdfRecoveryMode.Strict, null, specifications, [], false, null, null, false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, false, cancellationToken).ConfigureAwait(false)).Result;
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
        List<PdfPageSource> frozen = new(sources.Count);
        foreach (PdfPageSource source in sources)
        {
            if (source is null)
            {
                throw new ArgumentException("Page sources must not contain null entries.", nameof(sources));
            }

            frozen.Add(source);
        }

        if (frozen.Count == 0)
        {
            throw new ArgumentException("At least one page source is required.", nameof(sources));
        }

        if (mergeOptions.Collation is not null
            && mergeOptions.Collation.GroupSizes.Count != 1
            && mergeOptions.Collation.GroupSizes.Count != frozen.Count)
        {
            throw new ArgumentException("Collation group sizes must hold one value or one per source.", nameof(mergeOptions));
        }

        if (mergeOptions.SplitPages.HasValue && mergeOptions.SplitPages.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeOptions), "Split pages must be a positive group size.");
        }

        List<OutputRotation> specifications = ConvertRotations(mergeOptions.Rotations);
        List<PageLabelSpec> labels = ConvertLabels(mergeOptions.Labels);
        ResourceGovernor governor = new(limits, cancellationToken);
        DiagnosticBag diagnostics = new(governor);
        return (await SelectPagesCoreAsync(host, frozen, outputPath, frozen[0].Input, true, limits, QpdfRecoveryMode.Strict, mergeOptions.Collation, specifications, labels, false, null, mergeOptions.SplitPages, false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, false, false, AttachmentEdits.Empty, false, false, false, 1024L, [], [], false, null, false, Content.ImageOptimizeRequest.Disabled, Writing.QdfOptions.Disabled, new Content.ContentPrunePolicy(false, null), false, governor, diagnostics, false, null, false, cancellationToken).ConfigureAwait(false)).Result;
    }

    private static async ValueTask<(PdfMultiPageSelectionResult Result, int AttachmentCount)> SelectPagesCoreAsync(
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
        CancellationToken cancellationToken)
    {
        List<int> sourceCounts = new(sources.Count);
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
            await using QpdfDocument document = await QpdfDocument.OpenCore(host, sources[s].Input, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
            sourceCounts.Add(document.PageReferences.Count);
            if (version is null || IsNewerVersion(document.Version, version))
            {
                version = document.Version;
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

            GraphCopier copier = new(document.Engine, governor, limits, nextId);
            List<(GraphCopier Copier, int Id, PdfDictionary Page, int SourcePage)> taken = new(requested.Count);
            foreach (int pageNumber in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                PageLocation location = document.PageReferences[pageNumber - 1];
                Dictionary<PdfName, QpdfObject> extra = MaterializeAncestors(document.Engine, location, copier, governor, limits, cancellationToken);
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
                primaryExtras = Content.CatalogCopy.TryCopyExtras(document.Engine, copier, preserveCatalog, governor, cancellationToken);
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

                primaryExisting = PreserveExistingAttachments(copier, document.Engine, edits, limits, governor, diagnostics, cancellationToken);
                primaryPageMode = ReadInputPageMode(document.Engine);
            }

            if (multiCompress || multiDecode != "none")
            {
                TransformCopierStreams(copier, document.Engine, multiCompress, multiDecode, compression.RecompressFlate, multiLevel, limits, governor, diagnostics, cancellationToken);
            }

            List<int> takenIds = new(taken.Count);
            foreach ((GraphCopier _, int takenId, PdfDictionary _, int _) in taken)
            {
                takenIds.Add(takenId);
            }

            ApplySourceContentTransforms(copier, takenIds, coalesceContents, normalizeContent, externalizeInlineImages || (optimize.Optimize && !optimize.KeepInline), iiMinBytes, multiCompress, multiLevel, prune, document.Engine, limits, governor, diagnostics, cancellationToken);

            if ((flattenAnnotations is not null || generateAppearances) && Content.FlattenAnnotations.NeedsAppearances(document.Engine))
            {
                needAppearances = true;
            }

            nextId = copier.NextId;
            copiers.Add(copier);
            perSource.Add(taken);
        }

        List<(int Source, int Slot)> order = CollateOrder(perSource, collation);
        List<PdfPageSelectionMapping> mapping = new(order.Count);
        List<PdfReference> kids = new(order.Count);
        List<(GraphCopier Copier, int Id, PdfDictionary Page)> occurrences = new(order.Count);
        foreach ((int source, int slot) in order)
        {
            (GraphCopier copier, int pageId, PdfDictionary page, int pageNumber) = perSource[source][slot];
            kids.Add(new PdfReference(pageId, 0));
            mapping.Add(new PdfPageSelectionMapping(source, pageNumber));
            occurrences.Add((copier, pageId, page));
        }

        ApplyRotations(occurrences, ResolveRotationFolds(rotations, kids.Count, governor, limits), governor);

        List<(GraphCopier Copier, int Id)> overlayOccurrences = new(occurrences.Count);
        foreach ((GraphCopier occurrenceCopier, int occurrenceId, PdfDictionary _) in occurrences)
        {
            overlayOccurrences.Add((occurrenceCopier, occurrenceId));
        }

        Content.OverlayApplication overlayApplied = await Content.Overlays.ApplyAsync(host, primaryInput, overlayOccurrences, underlays, overlays, multiCompress, multiLevel, recovery, limits, governor, diagnostics, cancellationToken, nextId).ConfigureAwait(false);

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

        AttachmentAssembly multiAttached = await AssembleAttachmentsAsync(host, primaryExisting, pool, edits, primaryPageMode, multiCompress, multiLevel, multiSources, recovery, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);

        List<(int Index, PdfDictionary Label)> explicitItems = labels.Count > 0
            ? ResolveLabelItems(labels, kids.Count, governor)
            : new List<(int Index, PdfDictionary Label)>();
        List<(int Index, PdfDictionary Label)> labelItems = explicitItems;
        QpdfObject? catalogLabels = labelItems.Count == 0 ? null : NumbersDictionary(labelItems);
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

                labelItems = Content.PageLabelMerge.Merge(labelOccurrences, limits, governor, cancellationToken);
            }
            else
            {
                labelItems = new List<(int Index, PdfDictionary Label)>();
            }
            if (splitPages.HasValue || verbatimCopied is null)
            {
                catalogLabels = labelItems.Count == 0 ? null : NumbersDictionary(labelItems);
            }
            else
            {
                catalogLabels = verbatimCopied;
            }
        }
        PdfVersion inputMax = version ?? new PdfVersion(1, 4);
        (PdfVersion negotiatedMulti, int negotiatedMultiExtension) = NegotiateVersion(inputMax, minVersion, forceVersion);
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
            effective = new PdfVersion(1, 5);
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

        List<QpdfPath> outputPaths = new();
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

            await PublishModelAsync(host, effective, extensionLevel, model, multiStreams, multiCompress, multiLevel, primaryInfo?.ObjectNumber, originalId1, staticId, newlineBeforeEndstream, qdf, multiOriginalIds, linearize, linearizePass1, target, limits, governor, cancellationToken).ConfigureAwait(false);
            outputPaths.Add(target);
            return (new PdfMultiPageSelectionResult(kids.Count, mapping, sourceCounts, outputPaths, [.. diagnostics.Entries], governor.Snapshot()), multiAttached.FinalCount);
        }

        List<string> names = GenerateSplitNames(target.Value, kids.Count, splitPages.Value);
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

            PdfDictionary? slicedLabels = SlicePageLabels(labelItems, first, last);
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
            HashSet<int> reachable = ReachableIds(pool, roots, governor);
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
            List<NumberedObject> sub = new()
            {
                new NumberedObject(1, NewCatalog(remappedLabels, effective, extensionLevel, groupMetadata, groupNames, multiAttached.PageMode, groupOutlines, remappedAcroForm, groupStructTree, groupMarkInfo, null)),
                new NumberedObject(2, PagesNode(renumberedKids)),
            };
            foreach (int id in kept)
            {
                sub.Add(new NumberedObject(renumber[id], Writing.ObjectRemap.RemapIds(pool[id], renumber, governor)));
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

            await PublishModelAsync(host, effective, extensionLevel, sub, groupStreams, multiCompress, multiLevel, groupInfo, originalId1, staticId, newlineBeforeEndstream, qdf, groupOriginalIds, linearize, linearizePass1, path, limits, governor, cancellationToken).ConfigureAwait(false);
            outputPaths.Add(path);
        }

        return (new PdfMultiPageSelectionResult(kids.Count, mapping, sourceCounts, outputPaths, [.. diagnostics.Entries], governor.Snapshot()), multiAttached.FinalCount);
    }

    private static List<OutputRotation> ConvertRotations(IReadOnlyList<PdfPageRotation>? rotations)
    {
        if (rotations is null)
        {
            throw new ArgumentNullException(nameof(rotations));
        }

        List<OutputRotation> specifications = new(rotations.Count);
        foreach (PdfPageRotation rotation in rotations)
        {
            specifications.Add(OutputRotation.FromPages(rotation));
        }

        return specifications;
    }

    private static List<List<(PdfRotationMode Mode, int Angle)>> ResolveRotationFolds(
        IReadOnlyList<OutputRotation> rotations,
        int total,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        List<List<(PdfRotationMode Mode, int Angle)>> folds = new(total);
        for (int i = 0; i < total; i++)
        {
            folds.Add(new List<(PdfRotationMode Mode, int Angle)>());
        }

        foreach (OutputRotation specification in rotations)
        {
            foreach (int position in specification.Expand(total, limits.MaxWork))
            {
                governor.ChargeWork(1L);
                folds[position - 1].Add((specification.Mode, specification.Angle));
            }
        }

        return folds;
    }

    private static void ApplyRotations(
        List<(GraphCopier Copier, int Id, PdfDictionary Page)> occurrences,
        List<List<(PdfRotationMode Mode, int Angle)>> folds,
        ResourceGovernor governor)
    {
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
                current = mode switch
                {
                    PdfRotationMode.Set => angle,
                    PdfRotationMode.Add => NormalizeRotation((long)current + angle),
                    PdfRotationMode.Subtract => NormalizeRotation((long)current - angle),
                    _ => throw new QpdfException($"Unknown rotation mode {(int)mode}."),
                };
            }

            governor.ReserveMemory(64L);
            Dictionary<PdfName, QpdfObject> entries = new();
            foreach (PdfName key in occurrences[i].Page.Keys)
            {
                if (occurrences[i].Page.TryGet(key, out QpdfObject value))
                {
                    entries[key] = value;
                }
            }

            entries[rotateKey] = new PdfInteger(
                current.ToString(System.Globalization.CultureInfo.InvariantCulture),
                current);
            occurrences[i].Copier.ReplaceCopiedObject(occurrences[i].Id, new PdfDictionary(entries));
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
        List<(int Source, int Slot)> order = new();
        if (collation is not null && collation.GroupSizes.Count != 1 && collation.GroupSizes.Count != perSource.Count)
        {
            throw new ArgumentException("Collation group sizes must hold one value or one per source.", nameof(collation));
        }

        if (collation is null)
        {
            for (int source = 0; source < perSource.Count; source++)
            {
                for (int slot = 0; slot < perSource[source].Count; slot++)
                {
                    order.Add((source, slot));
                }
            }

            return order;
        }

        List<int> sizes = new(perSource.Count);
        for (int source = 0; source < perSource.Count; source++)
        {
            sizes.Add(collation.GroupSizes.Count == 1 ? collation.GroupSizes[0] : collation.GroupSizes[source]);
        }

        int[] cursors = new int[perSource.Count];
        bool progress = true;
        while (progress)
        {
            progress = false;
            for (int source = 0; source < perSource.Count; source++)
            {
                for (int j = 0; j < sizes[source] && cursors[source] < perSource[source].Count; j++)
                {
                    order.Add((source, cursors[source]));
                    cursors[source] += 1;
                    progress = true;
                }
            }
        }

        return order;
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

    private static PdfDictionary NewCatalog(QpdfObject? pageLabels)
    {
        return NewCatalog(pageLabels, new PdfVersion(1, 4), 0, null, null, null, null, null, null, null, null);
    }

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
        List<(int Index, PdfDictionary Label)> items = ResolveLabelItems(labels, total, governor);
        if (items.Count == 0)
        {
            return null;
        }

        return NumbersDictionary(items);
    }

    private static List<(int Index, PdfDictionary Label)> ResolveLabelItems(
        IReadOnlyList<PageLabelSpec> labels,
        int total,
        ResourceGovernor governor)
    {
        List<(int Index, PdfDictionary Label)> items = new(labels.Count);
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
            items.Add((specification.ResolveFirstPage(total) - 1, specification.ToDictionary()));
            governor.ReserveMemory(64L);
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

    private static PdfDictionary NumbersDictionary(List<(int Index, PdfDictionary Label)> items)
    {
        List<QpdfObject> numbers = new(items.Count * 2);
        foreach ((int index, PdfDictionary label) in items)
        {
            numbers.Add(new PdfInteger(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                index));
            numbers.Add(label);
        }

        return new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Nums")] = new PdfArray(numbers),
        });
    }

    private static PdfDictionary? SlicePageLabels(List<(int Index, PdfDictionary Label)> items, int first, int last)
    {
        if (items.Count == 0)
        {
            return null;
        }

        (int Index, PdfDictionary Label) carry = items[0];
        List<(int Index, PdfDictionary Label)> sliced = new();
        foreach ((int index, PdfDictionary label) in items)
        {
            if (index + 1 <= first)
            {
                carry = (index, label);
            }
            else if (index + 1 <= last)
            {
                sliced.Add((index - (first - 1), label));
            }
            else
            {
                break;
            }
        }

        sliced.Insert(0, (0, carry.Label));
        return NumbersDictionary(sliced);
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

    private static string PadGroupNumber(int value, int width)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0');
    }

    private static List<string> GenerateSplitNames(string pattern, int total, int groupSize)
    {
        int width = total.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        List<string> names = new();
        foreach ((int first, int last) in PartitionGroups(total, groupSize))
        {
            string range = first == last
                ? PadGroupNumber(first, width)
                : PadGroupNumber(first, width) + "-" + PadGroupNumber(last, width);
            int marker = pattern.IndexOf("%d", StringComparison.Ordinal);
            if (marker >= 0)
            {
                names.Add(pattern.Substring(0, marker) + range + pattern.Substring(marker + 2));
                continue;
            }

            if (pattern.Length >= 4 && pattern.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(pattern.Substring(0, pattern.Length - 4) + "-" + range + pattern.Substring(pattern.Length - 4));
                continue;
            }

            names.Add(pattern + "-" + range);
        }

        return names;
    }

   private static HashSet<int> ReachableIds(Dictionary<int, QpdfObject> pool, List<int> roots, ResourceGovernor governor)
    {
        HashSet<int> seen = new();
        Queue<int> queue = new();
        foreach (int root in roots)
        {
            queue.Enqueue(root);
        }

        while (queue.TryDequeue(out int id))
        {
            if (!pool.TryGetValue(id, out QpdfObject? value) || !seen.Add(id))
            {
                continue;
            }

            governor.ChargeWork(1L);
            Stack<QpdfObject> stack = new();
            stack.Push(value);
            while (stack.TryPop(out QpdfObject? current))
            {
                if (current is PdfReference reference)
                {
                    if (!seen.Contains(reference.ObjectNumber))
                    {
                        queue.Enqueue(reference.ObjectNumber);
                    }
                }
                else if (current is PdfArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        stack.Push(array[i]);
                    }
                }
                else if (current is PdfDictionary dictionary)
                {
                    foreach (PdfName key in dictionary.Keys)
                    {
                        if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null)
                        {
                            stack.Push(entry);
                        }
                    }
                }
                else if (current is PdfStream stream)
                {
                    stack.Push(stream.Dictionary);
                }
            }
        }

        return seen;
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

    private sealed record PageImage(string Name, int Number, int Generation, long Width, long Height);

    private static List<PageImage> ListPageImages(
        DocumentEngine engine,
        PageLocation location,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        List<PageImage> images = new();
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

        List<PdfName> names = new(xobjects.Keys);
        names.Sort(static (left, right) => string.CompareOrdinal(left.DisplayText, right.DisplayText));
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

            if (!stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype) || subtype is not PdfName subtypeName || subtypeName.DisplayText != "Image")
            {
                continue;
            }

            long width = ImageDimension(stream.Dictionary, "Width", reference, engine, diagnostics, governor, cancellationToken);
            long height = ImageDimension(stream.Dictionary, "Height", reference, engine, diagnostics, governor, cancellationToken);
            images.Add(new PageImage(key.DisplayText, reference.ObjectNumber, reference.Generation, width, height));
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
        HashSet<int> visited = new();
        if (location.Source is not null)
        {
            visited.Add(location.Source.ObjectNumber);
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
                if (!visited.Add(parentReference.ObjectNumber))
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

        diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, $"Image {reference.ObjectNumber} is missing /{key}; using 0.");
        return 0L;
    }

    private static QpdfDiagnostic CheckJsonSchema(
        string json,
        int version,
        ResourceGovernor governor,
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
            return new QpdfDiagnostic(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, $"JSON output has keys missing from --json-help version {version}: {string.Join(", ", missing)}.");
        }

        return new QpdfDiagnostic(QpdfDiagnosticCode.JsonSchemaCheck, QpdfDiagnosticSeverity.Info, $"JSON schema check passed for version {version}.");
    }

    private static List<int> PageContentRefs(Objects.PdfDictionary page)
    {
        List<int> refs = new();
        if (page.TryGet(Objects.PdfName.FromAscii("Contents"), out QpdfObject? contents) && contents is not null)
        {
            if (contents is Objects.PdfReference single)
            {
                refs.Add(single.ObjectNumber);
            }
            else if (contents is Objects.PdfArray array)
            {
                foreach (QpdfObject item in array.Items)
                {
                    if (item is Objects.PdfReference reference)
                    {
                        refs.Add(reference.ObjectNumber);
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

        governor.NoteHostOperation();
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(path, cancellationToken).ConfigureAwait(false);
        TransactionSink sink = new(transaction, governor);
        if (objectStreamGroups is not null && objectStreamGroups.Count > 0)
        {
            if (linearizePass1 is null)
            {
                await writer.WriteLinearizedStreamedAsync(version, model, objectStreamGroups, infoRef, id1, id2, newlineBeforeEndstream, compressHints, compressionLevel, sink, null, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await using IQpdfOutputTransaction streamedPass1 = await host.CreateOutputAsync(linearizePass1.Value, cancellationToken).ConfigureAwait(false);
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

        await using IQpdfOutputTransaction pass1Transaction = await host.CreateOutputAsync(linearizePass1.Value, cancellationToken).ConfigureAwait(false);
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
        governor.NoteHostOperation();
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(path, cancellationToken).ConfigureAwait(false);
        TransactionSink sink = new(transaction, governor);
        IByteSink emit = sink;
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

    private static (PdfVersion Version, int Extension) NegotiateVersion(PdfVersion inputMax, string? minText, string? forceText)
    {
        if (inputMax is null)
        {
            throw new ArgumentNullException(nameof(inputMax));
        }

        if (forceText is not null)
        {
            (int major, int minor, int ext) = ParseWriterVersion(forceText, "--force-version");
            return (new PdfVersion(major, minor), ext);
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
            return (new PdfVersion(minMajor, minMinor), minExt);
        }

        return (new PdfVersion(minMajor, minMinor), minExt);
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
        return (compress, decode, level);
    }

    private static int ParseCompressionLevel(string text, string option)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException(option + " must be an integer 1..9.", 0L, null);
        }

        long value = 0L;
        foreach (char c in text)
        {
            if (c < (char)48 || c > (char)57)
            {
                throw new QpdfParseException(option + " must be an integer 1..9.", 0L, null);
            }

            value = checked(value * 10L + (c - (char)48));
        }

        if (value < 1L || value > 9L)
        {
            throw new QpdfParseException(option + " must be an integer 1..9.", 0L, null);
        }

        return (int)value;
    }

    private static bool StreamFilterInLevel(string name, string decode)
    {
        return Filters.FilterChain.AllStagesInLevel([new Filters.FilterStage(name, null, null)], decode);
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
    {
        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        List<int> ids = new(copier.CopiedObjects.Keys);
        foreach (int id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!copier.CopiedObjects.TryGetValue(id, out QpdfObject? value) || value is not PdfStream stream)
            {
                continue;
            }

            QpdfObject? replacement = TransformStream(stream, id, engine, compress, decode, recompressFlate, level, limits, governor, diagnostics);
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
    {
        IReadOnlyList<FilterStage> stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, (IIndirectResolver)engine);
        }
        catch (QpdfParseException)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, $"Stream {number} kept opaque: unsupported filter.");
            return null;
        }

        if (stages.Count == 0)
        {
            if (!compress)
            {
                return null;
            }

            byte[] recompressed = FlateFilter.Encode(stream.Data, level, limits, governor);
            return new PdfStream(FlateDictionary(stream.Dictionary, governor), recompressed);
        }

        bool alreadyFlate = stages[0].Name == FlateFilter.FilterName;
        if (alreadyFlate && !recompressFlate && compress)
        {
            return null;
        }

        foreach (FilterStage stage in stages)
        {
            if (StreamFilterInLevel(stage.Name, decode))
            {
                continue;
            }

            if (decode == "all")
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, $"Stream {number} kept opaque: unsupported filter.");
            }

            return null;
        }

        byte[] decoded;
        try
        {
            decoded = FilterChain.Decode(stream.Data, stages, limits, governor);
        }
        catch (QpdfFilterException)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, $"Stream {number} kept opaque: decode failed.");
            return null;
        }

        if (!compress || decoded.Length == 0)
        {
            return new PdfStream(StrippedDictionary(stream.Dictionary, governor), decoded);
        }

        byte[] encoded = FlateFilter.Encode(decoded, level, limits, governor);
        return new PdfStream(FlateDictionary(stream.Dictionary, governor), encoded);
    }

    private static PdfDictionary StrippedDictionary(PdfDictionary dictionary, ResourceGovernor governor)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in dictionary.Keys)
        {
            if (key.DisplayText == "Filter" || key.DisplayText == "DecodeParms")
            {
                continue;
            }

            if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null)
            {
                governor.ReserveMemory(32L);
                entries[key] = entry;
            }
        }

        return new PdfDictionary(entries);
    }

    private static PdfDictionary FlateDictionary(PdfDictionary dictionary, ResourceGovernor governor)
    {
        PdfDictionary stripped = StrippedDictionary(dictionary, governor);
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in stripped.Keys)
        {
            if (stripped.TryGet(key, out QpdfObject? entry) && entry is not null)
            {
                entries[key] = entry;
            }
        }

        governor.ReserveMemory(64L);
        entries[PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode");
        return new PdfDictionary(entries);
    }

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
        CancellationToken cancellationToken)
    {
        SortedDictionary<string, PdfReference> kept = new(StringComparer.Ordinal);
        if (edits.HasEdits)
        {
            List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(engine, limits, governor, diagnostics, cancellationToken);
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
        CancellationToken cancellationToken)
    {
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

            byte[] data = await ReadAttachmentFileAsync(host, add.File, limits, governor, cancellationToken).ConfigureAwait(false);
            if (add.MimeType is not null && !IsAscii(add.MimeType))
            {
                throw new QpdfParseException("Attachment MIME type must be ASCII.", 0L, null);
            }

            byte[] checksum = AttachmentBuilder.Checksum(data);
            PdfDictionary streamDict = AttachmentBuilder.BuildStreamDictionary(data.Length, checksum, add.MimeType, add.CreationDate, add.ModDate, governor);
            byte[] stored = data;
            if (compress && data.Length > 0)
            {
                stored = FlateFilter.Encode(data, level, limits, governor);
                streamDict = AttachmentBuilder.WithFilter(streamDict, governor);
            }

            int streamId = nextId;
            checked
            {
                nextId += 1;
            }

            governor.ReserveMemory(64L);
            pool[streamId] = new PdfStream(streamDict, stored);
            ids.Add(streamId);
            PdfDictionary spec = AttachmentBuilder.BuildFilespecDictionary(fileName, add.Description, streamId);
            int specId = nextId;
            checked
            {
                nextId += 1;
            }

            governor.ReserveMemory(128L);
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
            await using QpdfDocument source = await QpdfDocument.OpenCore(host, SelectionPlan.CheckedPath(copy.File), limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
            List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(source.Engine, limits, governor, diagnostics, cancellationToken);
            GraphCopier copyCopier = new(source.Engine, governor, limits, nextId);
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

        PdfDictionary tree = AttachmentBuilder.BuildNameTree(pairs, governor);
        governor.ReserveMemory(64L);
        PdfDictionary wrapper = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("EmbeddedFiles")] = tree,
        });
        int rootId = nextId;
        checked
        {
            nextId += 1;
        }

        governor.ReserveMemory(128L);
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
        CancellationToken cancellationToken)
    {
        QpdfPath path = SelectionPlan.CheckedPath(file);
        IQpdfInput input = await host.OpenInputAsync(path, cancellationToken).ConfigureAwait(false);
        governor.NoteHostOperation();
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
            byte[] bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            governor.NoteHostOperation();
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
            return QpdfDocument.OpenEmptyDocument(input, limits, governor, diagnostics, recovery, cancellationToken);
        }

        return QpdfDocument.OpenCore(host, input, limits, governor, diagnostics, recovery, cancellationToken);
    }

    private static async ValueTask<(PdfMultiPageSelectionResult Result, int AttachmentCount)> WholeFileCoreAsync(
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

        await using QpdfDocument document = await OpenPlanInputAsync(host, plan.Input, plan.EmptyDocument, limits, governor, diagnostics, plan.Recovery, cancellationToken).ConfigureAwait(false);
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
            false,
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
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<(PdfMultiPageSelectionResult Result, int AttachmentCount)> PublishEmptyModelAsync(
        IQpdfHost host,
        QpdfDocument document,
        WholeFilePlan plan,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
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
        (PdfVersion negotiated, int negotiatedExtension) = NegotiateVersion(document.Version, plan.MinVersion, plan.ForceVersion);
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
        PdfMultiPageSelectionResult result = new(0, [], [0], [plan.Output], [.. diagnostics.Entries], governor.Snapshot());
        return (result, 0);
    }

    private static List<PageLabelSpec> ConvertLabels(IReadOnlyList<PdfPageLabel>? labels)
    {
        if (labels is null)
        {
            throw new ArgumentNullException(nameof(labels));
        }

        List<PageLabelSpec> specifications = new(labels.Count);
        foreach (PdfPageLabel label in labels)
        {
            if (label is null)
            {
                throw new ArgumentException("Page labels must not contain null entries.", nameof(labels));
            }

            specifications.Add(PageLabelSpec.FromLabel(label));
        }

        return specifications;
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
        DiagnosticBag diagnostics = new(governor);
        return await CheckCoreAsync(host, inputPath, options, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        DiagnosticBag diagnostics = new(governor);
        return await ExtractAttachmentCoreAsync(host, inputPath, key, outputPath, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        DiagnosticBag diagnostics = new(governor);
        List<string> wanted = ValidateJsonObjects(options.Objects, 0L);
        Commands.JsonInspection inspection = new(options.Version, options.Keys, null, options.StreamData, options.StreamPrefix, wanted, options.MinimalOutput);
        return await JsonCoreAsync(host, inputPath, outputPath, inspection, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        DiagnosticBag diagnostics = new(governor);
        return await ImportCreateCoreAsync(host, jsonPath, outputPath, null, null, false, false, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        DiagnosticBag diagnostics = new(governor);
        return await ImportUpdateCoreAsync(host, inputPath, jsonPath, outputPath, null, null, false, false, limits, QpdfRecoveryMode.Strict, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        DiagnosticBag diagnostics = new(governor);
        (PdfMultiPageSelectionResult result, int attachmentCount) = await WholeFileCoreAsync(
            host, plan, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        return new QpdfAttachmentEditResult(attachmentCount, result.Warnings, result.Counters);
    }

    private static async ValueTask<QpdfAttachmentExtraction> ExtractAttachmentCoreAsync(
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
        if (string.Equals(inputPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        await using QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(document.Engine, limits, governor, diagnostics, cancellationToken);
        EmbeddedAttachment? match = null;
        foreach (EmbeddedAttachment attachment in found)
        {
            if (string.Equals(attachment.Key, key, StringComparison.Ordinal))
            {
                match = attachment;
                break;
            }
        }

        if (match is null)
        {
            throw new QpdfParseException("The requested attachment was not found.", 0L, null);
        }

        byte[] data = EmbeddedFiles.ExtractData(match.Stream, match.StreamObjectNumber, document.Engine, limits, governor, diagnostics);
        if (data.Length > limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), data.Length, limits.MaxOutputBytes);
        }

        governor.NoteHostOperation();
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(outputPath, cancellationToken).ConfigureAwait(false);
        TransactionSink sink = new(transaction, governor);
        await sink.AppendAsync(data, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new QpdfAttachmentExtraction(match.Key, data.Length, [.. diagnostics.Entries], governor.Snapshot());
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

            string[] parts = spec.Split((char)44);
            if (parts.Length > 2)
            {
                throw new QpdfParseException("JSON object '" + spec + "' must be trailer or obj[,gen].", index, null);
            }

            if (!TryParseObjectNumber(parts[0], out int number))
            {
                throw new QpdfParseException("JSON object '" + spec + "' must be trailer or obj[,gen].", index, null);
            }

            int generation = 0;
            if (parts.Length == 2 && !TryParseObjectNumber(parts[1], out generation))
            {
                throw new QpdfParseException("JSON object '" + spec + "' must be trailer or obj[,gen].", index, null);
            }

            canonical.Add("obj:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + " R");
        }

        return canonical;
    }

    private static bool TryParseObjectNumber(string text, out int number)
    {
        number = 0;
        if (text is null || text.Length == 0 || text.Length > 10)
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

    private static async ValueTask<QpdfJsonResult> JsonCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath? outputPath,
        JsonInspection inspection,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        bool emptyDocument,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
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

        await using QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        Json.JsonRequest request = new(inspection.Version, inspection.Keys, decodeLevel, inspection.StreamData, prefix, wanted, inspection.MinimalOutput);
        List<Json.JsonSidecar> sidecars = new();
        string json = Json.DocumentJson.WriteDocument(document, request, sidecars, limits, governor, diagnostics, cancellationToken);
        byte[] data = System.Text.Encoding.UTF8.GetBytes(json);
        if (data.Length > limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), data.Length, limits.MaxOutputBytes);
        }

        if (outputPath.HasValue)
        {
            governor.NoteHostOperation();
            await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(outputPath.Value, cancellationToken).ConfigureAwait(false);
            TransactionSink sink = new(transaction, governor);
            await sink.AppendAsync(data, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (Json.JsonSidecar sidecar in sidecars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(sidecar.Name, inputPath.Value, StringComparison.Ordinal))
            {
                throw new QpdfParseException("A JSON stream file must differ from the input file.", 0L, null);
            }

            QpdfPath sidecarPath = QpdfPath.From(sidecar.Name);
            governor.NoteHostOperation();
            await using IQpdfOutputTransaction staging = await host.CreateOutputAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
            TransactionSink sidecarSink = new(staging, governor);
            await sidecarSink.AppendAsync(sidecar.Data, cancellationToken).ConfigureAwait(false);
            await staging.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new QpdfJsonResult(json, [.. diagnostics.Entries], governor.Snapshot());
    }

    private static async ValueTask<QpdfJsonImportResult> ImportCreateCoreAsync(
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
        if (string.Equals(jsonPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        System.Text.Json.JsonDocument parsed = await Json.JsonImport.ReadJsonAsync(host, jsonPath, limits, governor, cancellationToken).ConfigureAwait(false);
        using (parsed)
        {
            Json.ImportedModel model = await Json.JsonImport.BuildCreateModelAsync(host, parsed, limits, governor, cancellationToken).ConfigureAwait(false);
            Json.JsonImport.RenumberedModel renumbered = Json.JsonImport.Renumber(model, limits, governor, cancellationToken);
            (PdfVersion effective, int extensionLevel) = NegotiateVersion(model.HeaderVersion, minVersion, forceVersion);
            await PublishModelAsync(host, effective, extensionLevel, renumbered.Model, null, false, null, renumbered.InfoNumber, null, staticId, newlineBeforeEndstream, Writing.QdfOptions.Disabled, null, false, null, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
            return new QpdfJsonImportResult(renumbered.PageCount, [.. diagnostics.Entries], governor.Snapshot());
        }
    }

    private static async ValueTask<QpdfJsonImportResult> ImportUpdateCoreAsync(
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
            (PdfVersion effective, int extensionLevel) = NegotiateVersion(model.HeaderVersion, minVersion, forceVersion);
            await PublishModelAsync(host, effective, extensionLevel, renumbered.Model, null, false, null, renumbered.InfoNumber, originalId1, staticId, newlineBeforeEndstream, Writing.QdfOptions.Disabled, null, false, null, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
            return new QpdfJsonImportResult(renumbered.PageCount, [.. diagnostics.Entries], governor.Snapshot());
        }
    }

    private static async ValueTask<QpdfCheckResult> CheckCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfCheckOptions options,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        bool emptyDocument,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        await using QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        List<QpdfCheckOutcome> checks = DocumentCheck.Run(document, options, limits, governor, diagnostics, cancellationToken);
        return new QpdfCheckResult(document.PageCount, document.Version, checks, [.. diagnostics.Entries], governor.Snapshot());
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


    private static Dictionary<PdfName, QpdfObject> MaterializeAncestors(
        DocumentEngine engine,
        PageLocation location,
        GraphCopier copier,
        ResourceGovernor governor,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        string[] inheritable = ["Resources", "MediaBox", "CropBox", "Rotate"];
        Dictionary<PdfName, QpdfObject> extra = new();
        foreach (string name in inheritable)
        {
            PdfName key = PdfName.FromAscii(name);
            if (location.Page.TryGet(key, out QpdfObject present) && present is not PdfNull)
            {
                continue;
            }

            extra[key] = PdfNull.Instance;
        }

        if (extra.Count == 0)
        {
            return extra;
        }

        HashSet<int> visited = new();
        if (location.Source is not null)
        {
            visited.Add(location.Source.ObjectNumber);
        }

        PdfDictionary current = location.Page;
        PdfName parentKey = PdfName.FromAscii("Parent");
        int depth = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!current.TryGet(parentKey, out QpdfObject parentRaw) || parentRaw is PdfNull)
            {
                break;
            }

            PdfDictionary parent;
            if (parentRaw is PdfReference parentReference)
            {
                if (!visited.Add(parentReference.ObjectNumber))
                {
                    throw new QpdfParseException($"Page ancestry cycle at object {parentReference.ObjectNumber}.", null, parentReference.ObjectNumber);
                }

                if (!((IIndirectResolver)engine).TryResolve(parentReference, out QpdfObject? resolved) || resolved is not PdfDictionary resolvedParent)
                {
                    throw new QpdfParseException("Unresolvable page parent.", null, parentReference.ObjectNumber);
                }

                parent = resolvedParent;
            }
            else if (parentRaw is PdfDictionary direct)
            {
                parent = direct;
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
            foreach (KeyValuePair<PdfName, QpdfObject> pending in extra)
            {
                if (pending.Value is not PdfNull)
                {
                    continue;
                }

                if (parent.TryGet(pending.Key, out QpdfObject inherited))
                {
                    extra[pending.Key] = copier.CopyValue(inherited, 0);
                }
            }

            bool complete = true;
            foreach (KeyValuePair<PdfName, QpdfObject> pending in extra)
            {
                if (pending.Value is PdfNull)
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                break;
            }

            current = parent;
        }

        Dictionary<PdfName, QpdfObject> materialized = new();
        foreach (KeyValuePair<PdfName, QpdfObject> pending in extra)
        {
            if (pending.Value is not PdfNull)
            {
                materialized[pending.Key] = pending.Value;
            }
        }

        return materialized;
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
        ResourceGovernor governor = new(options.Limits, cancellationToken);
        DiagnosticBag diagnostics = new(governor);
        IReadOnlyList<string> expanded;
        try
        {
            expanded = await ResponseFileExpander.ExpandAsync(host, invocation.Arguments, options.Limits, governor, cancellationToken).ConfigureAwait(false);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        catch (OperationCanceledException)
        {
            return QpdfExecutionResult.Canceled([], ZeroCounters());
        }

        if (HelpText.TryMatchHelp(expanded, out string help))
        {
            return QpdfExecutionResult.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, help)],
                governor.Snapshot());
        }

        if (HelpText.TryMatchVersion(expanded, out string version))
        {
            return QpdfExecutionResult.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, version)],
                governor.Snapshot());
        }

        if (HelpText.TryMatchCopyright(expanded, out string copyright))
        {
            return QpdfExecutionResult.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, copyright)],
                governor.Snapshot());
        }

        if (HelpText.TryMatchJobJsonHelp(expanded, out string jobJsonHelp))
        {
            return QpdfExecutionResult.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, jobJsonHelp)],
                governor.Snapshot());
        }

        if (HelpText.TryMatchJsonHelp(expanded, out string jsonHelp))
        {
            return QpdfExecutionResult.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, jsonHelp)],
                governor.Snapshot());
        }

        if (HelpText.TryMatchCompletionBash(expanded, out string completionBash))
        {
            return QpdfExecutionResult.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, completionBash)],
                governor.Snapshot());
        }

        if (HelpText.TryMatchCompletionZsh(expanded, out string completionZsh))
        {
            return QpdfExecutionResult.Completed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.HelpText, QpdfDiagnosticSeverity.Info, completionZsh)],
                governor.Snapshot());
        }

        if (HelpText.TryMatchZopfli(expanded, out string zopfli))
        {
            return QpdfExecutionResult.Failed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Error, zopfli)],
                governor.Snapshot());
        }

        if (TryTakeJobJsonFile(expanded, out string? jobPath, out QpdfException? jobError))
        {
            if (jobError is not null)
            {
                return ToFailedResult(jobError);
            }

            if (jobPath is null)
            {
                return ToFailedResult(new QpdfParseException("Null argument.", 1, null));
            }

            string jobJson;
            try
            {
                jobJson = await ResponseFileExpander.ReadTextFileAsync(host, SelectionPlan.CheckedPath(jobPath), options.Limits, governor, cancellationToken).ConfigureAwait(false);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
            }

            JobPlan jobPlan;
            try
            {
                jobPlan = JobJsonParser.Parse(jobJson, options.Limits);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }

            return await RunPlanAsync(host, jobPlan, options.Limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        JobPlan plan;
        try
        {
            plan = ArgvParser.Parse(expanded);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }

        return await RunPlanAsync(host, plan, options.Limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryTakeJobJsonFile(IReadOnlyList<string> argv, out string? jobPath, out QpdfException? error)
    {
        jobPath = null;
        error = null;
        int found = -1;
        for (int i = 0; i < argv.Count; i++)
        {
            if (argv[i] == "--job-json-file")
            {
                if (found >= 0)
                {
                    error = new QpdfParseException("Duplicate --job-json-file is outside the narrow profile.", i, null);
                    return true;
                }

                found = i;
            }
        }

        if (found < 0)
        {
            return false;
        }

        if (argv.Count != 2)
        {
            error = new QpdfParseException("--job-json-file takes exactly one file and no other operation.", found, null);
            return true;
        }

        jobPath = argv[1];
        if (jobPath is null)
        {
            error = new QpdfParseException("Null argument.", 1, null);
            return true;
        }

        return true;
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
        ResourceGovernor governor = new(options.Limits, cancellationToken);
        DiagnosticBag diagnostics = new(governor);
        JobPlan plan;
        try
        {
            plan = JobJsonParser.Parse(jobJson, options.Limits);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        return await RunPlanAsync(host, plan, options.Limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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

    private static QpdfLimits ResolveParserLimits(JobPlan plan, QpdfLimits host)
    {
        QpdfLimits limits = host;
        if (plan.MaxStreamFilters is long maxStreamFilters)
        {
            RequireHostCeiling(nameof(QpdfLimits.MaxStreamFilters), maxStreamFilters, host.MaxStreamFilters, QpdfLimits.Default.MaxStreamFilters);
            limits = limits with { MaxStreamFilters = maxStreamFilters };
        }

        if (plan.ParserMaxNesting is long parserMaxNesting)
        {
            RequireHostCeiling(nameof(QpdfLimits.MaxNestingDepth), parserMaxNesting, host.MaxNestingDepth, QpdfLimits.Default.MaxNestingDepth);
            limits = limits with { MaxNestingDepth = parserMaxNesting };
        }

        if (plan.MaxContainerSize is long maxContainerSize)
        {
            RequireHostCeiling(nameof(QpdfLimits.MaxContainerSize), maxContainerSize, host.MaxContainerSize, QpdfLimits.Default.MaxContainerSize);
            limits = limits with { MaxContainerSize = maxContainerSize };
        }

        if (plan.MaxContainerSizeDamaged is long maxContainerSizeDamaged)
        {
            RequireHostCeiling(nameof(QpdfLimits.MaxContainerSizeDamaged), maxContainerSizeDamaged, host.MaxContainerSizeDamaged, QpdfLimits.Default.MaxContainerSizeDamaged);
            limits = limits with { MaxContainerSizeDamaged = maxContainerSizeDamaged };
        }

        if (plan.ParserMaxErrors is long parserMaxErrors)
        {
            RequireHostCeiling(nameof(QpdfLimits.MaxParserErrors), parserMaxErrors, host.MaxParserErrors, QpdfLimits.Default.MaxParserErrors);
            limits = limits with { MaxParserErrors = parserMaxErrors };
        }

        if (plan.NoDefaultLimits)
        {
            // Compatible parser defaults (filter and container counts) lift when the
            // host left them at library defaults. Explicit invocation values and
            // host-customized ceilings always stand; nesting and error budgets stay.
            if (plan.MaxStreamFilters is null && host.MaxStreamFilters == QpdfLimits.Default.MaxStreamFilters)
            {
                limits = limits with { MaxStreamFilters = long.MaxValue };
            }

            if (plan.MaxContainerSize is null && host.MaxContainerSize == QpdfLimits.Default.MaxContainerSize)
            {
                limits = limits with { MaxContainerSize = long.MaxValue };
            }

            if (plan.MaxContainerSizeDamaged is null && host.MaxContainerSizeDamaged == QpdfLimits.Default.MaxContainerSizeDamaged)
            {
                limits = limits with { MaxContainerSizeDamaged = long.MaxValue };
            }
        }

        return limits;
    }

    private static void RequireHostCeiling(string name, long requested, long hostCeiling, long libraryDefault)
    {
        // Invocation parser knobs tighten behavior within the embedding host's
        // hard ceilings; they never raise a ceiling the host customized.
        // An untouched host ceiling still carries the library default, in which
        // case invocation values within their own absolute bounds apply.
        if (hostCeiling != libraryDefault && requested > hostCeiling)
        {
            throw new QpdfLimitException(name, requested, hostCeiling);
        }
    }

    private static async ValueTask<QpdfExecutionResult> RunPlanAsync(
        IQpdfHost host,
        JobPlan plan,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            limits = ResolveParserLimits(plan, limits);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        catch (OperationCanceledException)
        {
            return QpdfExecutionResult.Canceled([], ZeroCounters());
        }

        if (plan.IgnoreXrefStreams)
        {
            limits = limits with { IgnoreXrefStreams = true };
        }

        if (plan is CheckPlan check)
        {
            try
            {
                QpdfCheckResult inspected = await CheckCoreAsync(
                    host,
                    check.Input,
                    new QpdfCheckOptions(),
                    limits,
                    check.Recovery,
                    check.EmptyDocument,
                    governor,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                if (check.CheckLinearization)
                {
                    await InspectLinearizationAsync(host, check.Input, limits, check.Recovery, check.EmptyDocument, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                }

                if (check.ShowLinearization)
                {
                    await ShowLinearizationAsync(host, check.Input, limits, check.Recovery, check.EmptyDocument, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                }

                return CompleteWithPresentation(diagnostics.Entries, inspected.Counters, plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
            }
        }

        if (plan is ShowXrefPlan showXref)
        {
            try
            {
                await using QpdfDocument document = await OpenPlanInputAsync(host, showXref.Input, showXref.EmptyDocument, limits, governor, diagnostics, showXref.Recovery, cancellationToken).ConfigureAwait(false);
                List<int> numbers = new(document.Engine.XRefEntries.Keys);
                numbers.Sort();
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

                return CompleteWithPresentation(diagnostics.Entries, governor.Snapshot(), plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
            }
        }

        if (plan is ShowObjectPlan showObject)
        {
            try
            {
                (bool trailer, int number, int generation) = SelectionPlan.ParseObjectSelector(showObject.Selector);
                await using QpdfDocument document = await OpenPlanInputAsync(host, showObject.Input, showObject.EmptyDocument, limits, governor, diagnostics, showObject.Recovery, cancellationToken).ConfigureAwait(false);
                QpdfObject? target = null;
                if (trailer)
                {
                    target = document.Engine.Trailer;
                }
                else if (((IIndirectResolver)document.Engine).TryResolve(new PdfReference(number, generation), out QpdfObject? resolved))
                {
                    target = resolved;
                }

                bool wantsStreamData = showObject.RawStreamData || showObject.FilteredStreamData;
                if (target is PdfStream dataStream && wantsStreamData)
                {
                    QpdfPath output = showObject.Output ?? throw new QpdfParseException("An output file is required after --.", 0L, null);
                    if (string.Equals(showObject.Input.Value, output.Value, StringComparison.Ordinal))
                    {
                        throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
                    }

                    byte[] payload = dataStream.Data;
                    if (showObject.FilteredStreamData)
                    {
                        IReadOnlyList<FilterStage> stages = FilterChain.Resolve(dataStream.Dictionary, (IIndirectResolver)document.Engine);
                        payload = FilterChain.Decode(dataStream.Data, stages, limits, governor);
                    }

                    if (payload.Length > limits.MaxOutputBytes)
                    {
                        throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), payload.Length, limits.MaxOutputBytes);
                    }

                    governor.NoteHostOperation();
                    await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(output, cancellationToken).ConfigureAwait(false);
                    TransactionSink sink = new(transaction, governor);
                    await sink.AppendAsync(payload, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return CompleteWithPresentation(diagnostics.Entries, governor.Snapshot(), plan);
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
                    diagnostics.Add(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, CanonicalDisplay.Render(stream.Dictionary, limits, governor));
                }
                else
                {
                    diagnostics.Add(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, CanonicalDisplay.Render(target, limits, governor));
                }

                return CompleteWithPresentation(diagnostics.Entries, governor.Snapshot(), plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
            }
        }

        if (plan is ShowPagesPlan showPages)
        {
            try
            {
                await using QpdfDocument document = await OpenPlanInputAsync(host, showPages.Input, showPages.EmptyDocument, limits, governor, diagnostics, showPages.Recovery, cancellationToken).ConfigureAwait(false);
                int index = 1;
                foreach (Reading.PageLocation location in document.PageReferences)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.ChargeWork(1L);
                    if (location.Source is null)
                    {
                        throw new QpdfException("Page dictionary is not an indirect object.");
                    }

                    diagnostics.Add(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, $"page {index}: {location.Source.ObjectNumber} 0 R");
                    if (showPages.WithImages)
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
                    foreach (int content in PageContentRefs(location.Page))
                    {
                        diagnostics.Add(QpdfDiagnosticCode.PageListing, QpdfDiagnosticSeverity.Info, $"    {content} 0 R");
                    }

                    index = checked(index + 1);
                }

                return CompleteWithPresentation(diagnostics.Entries, governor.Snapshot(), plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
            }
        }

        if (plan is ShowNpagesPlan npages)
        {
            try
            {
                await using QpdfDocument document = await OpenPlanInputAsync(host, npages.Input, npages.EmptyDocument, limits, governor, diagnostics, npages.Recovery, cancellationToken).ConfigureAwait(false);
                int count = document.PageCount;
                diagnostics.Add(QpdfDiagnosticCode.PageCount, QpdfDiagnosticSeverity.Info, $"{npages.Input.Value}: {count}");
                return CompleteWithPresentation(diagnostics.Entries, governor.Snapshot(), plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
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

                return CompleteWithPresentation(diagnostics.Entries, governor.Snapshot(), plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
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

                return CompleteWithPresentation(diagnostics.Entries, governor.Snapshot(), plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
            }
        }

        if (plan is JsonPlan json)
        {
            try
            {
                Commands.JsonInspection inspection = new(json.Version, json.Keys, json.DecodeLevel, json.StreamDataMode, json.StreamPrefix, json.Objects, json.MinimalOutput);
                QpdfJsonResult inspected = await JsonCoreAsync(
                    host,
                    json.Input,
                    json.Output,
                    inspection,
                    limits,
                    json.Recovery,
                    json.EmptyDocument,
                    governor,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                if (json.TestJsonSchema)
                {
                    QpdfDiagnostic schema = CheckJsonSchema(inspected.Json, json.Version, governor, cancellationToken);
                    List<QpdfDiagnostic> warnings = new(inspected.Warnings)
                    {
                        schema,
                    };

                    return CompleteWithPresentation(warnings, inspected.Counters, plan);
                }

                return CompleteWithPresentation(inspected.Warnings, inspected.Counters, plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
            }
        }

        if (plan is JsonInputPlan jsonInput)
        {
            try
            {
                QpdfJsonImportResult imported = await ImportCreateCoreAsync(
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
                return CompleteWithPresentation(imported.Warnings, imported.Counters, plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
            }
        }

        if (plan is JsonUpdatePlan jsonUpdate)
        {
            try
            {
                QpdfJsonImportResult updated = await ImportUpdateCoreAsync(
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
                return CompleteWithPresentation(updated.Warnings, updated.Counters, plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
            }
        }

        if (plan is WholeFilePlan whole)
        {
            try
            {
                PdfMultiPageSelectionResult result = (await WholeFileCoreAsync(host, whole, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false)).Result;
                return CompleteWithPresentation(result.Warnings, result.Counters, plan);
            }
            catch (QpdfException ex)
            {
                return ToFailedResult(ex);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return QpdfExecutionResult.Failed(
                    [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)],
                    ZeroCounters());
            }
            catch (OperationCanceledException)
            {
                return QpdfExecutionResult.Canceled([], ZeroCounters());
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

                    PdfMultiPageSelectionResult merged = (await SelectPagesCoreAsync(
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
                        cancellationToken).ConfigureAwait(false)).Result;
                    return CompleteWithPresentation(merged.Warnings, merged.Counters, plan);
                }
                catch (QpdfException ex)
                {
                    return ToFailedResult(ex);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return QpdfExecutionResult.Failed(
                        [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)],
                        ZeroCounters());
                }
                catch (OperationCanceledException)
                {
                    return QpdfExecutionResult.Canceled([], ZeroCounters());
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
                    PdfMultiPageSelectionResult merged = (await SelectPagesCoreAsync(
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
                        cancellationToken).ConfigureAwait(false)).Result;
                    return CompleteWithPresentation(merged.Warnings, merged.Counters, plan);
                }
                catch (QpdfException ex)
                {
                    return ToFailedResult(ex);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return QpdfExecutionResult.Failed(
                        [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)],
                        ZeroCounters());
                }
                catch (OperationCanceledException)
                {
                    return QpdfExecutionResult.Canceled([], ZeroCounters());
                }
            }

            PdfPageSelectionResult selected = await SelectPageCoreAsync(
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
            return CompleteWithPresentation(selected.Warnings, selected.Counters, plan);
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return QpdfExecutionResult.Failed(
                [new QpdfDiagnostic(QpdfDiagnosticCode.InvalidArguments, QpdfDiagnosticSeverity.Error, ex.Message)],
                ZeroCounters());
        }
        catch (OperationCanceledException)
        {
            return QpdfExecutionResult.Canceled([], ZeroCounters());
        }
    }

    private static QpdfExecutionResult CompleteWithPresentation(
        IReadOnlyList<QpdfDiagnostic> diagnostics,
        QpdfExecutionCounters counters,
        JobPlan plan)
    {
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
            effective.Add(new QpdfDiagnostic(QpdfDiagnosticCode.VerboseSummary, QpdfDiagnosticSeverity.Info, $"Verbose summary: {counters.TotalOutputBytes} output bytes, {counters.TotalDecodedBytes} decoded bytes, peak {counters.PeakMemoryBytes} bytes."));
        }

        if (plan.ReportMemoryUsage)
        {
            effective.Add(new QpdfDiagnostic(QpdfDiagnosticCode.MemoryUsage, QpdfDiagnosticSeverity.Info, $"Peak memory usage: {counters.PeakMemoryBytes} bytes."));
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
            return QpdfExecutionResult.Failed(effective, counters);
        }

        if (plan.WarningExitZero)
        {
            return new QpdfExecutionResult(QpdfExecutionStatus.Completed, QpdfExitCode.Success, effective, counters);
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

        return new QpdfExecutionResult(QpdfExecutionStatus.Completed, warning ? QpdfExitCode.Warning : QpdfExitCode.Success, effective, counters);
    }

    private static QpdfExecutionResult CryptoFailed(string option)
    {
        QpdfDiagnostic diagnostic = new(
            QpdfDiagnosticCode.UnsupportedCryptoOperation,
            QpdfDiagnosticSeverity.Error,
            $"Cryptographic option '{option}' is permanently out of scope.");
        return QpdfExecutionResult.Failed([diagnostic], ZeroCounters());
    }

    private static QpdfExecutionResult ToFailedResult(QpdfException ex)
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

    private static QpdfExecutionResult Failed(QpdfDiagnosticCode code, string message)
    {
        QpdfDiagnostic diagnostic = new(code, QpdfDiagnosticSeverity.Error, message);
        return QpdfExecutionResult.Failed([diagnostic], ZeroCounters());
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
        ResourceGovernor governor = new(limits, cancellationToken);
        try
        {
            await using IQpdfInput input = await host.OpenInputAsync(inputPath, cancellationToken).ConfigureAwait(false);
            governor.NoteHostOperation();
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
                governor.NoteHostOperation();
            }
            finally
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }

            governor.NoteHostOperation();
            await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(outputPath, cancellationToken).ConfigureAwait(false);
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
            return QpdfExecutionResult.Completed([], governor.Snapshot());
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        catch (OperationCanceledException)
        {
            return QpdfExecutionResult.Canceled([], ZeroCounters());
        }
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
        ResourceGovernor governor = new(limits, cancellationToken);
        if (options.Mode != QpdfZlibFlateMode.Uncompress && options.Mode != QpdfZlibFlateMode.Compress)
        {
            return ToFailedResult(new QpdfParseException("The zlib-flate mode is not supported.", null, null));
        }

        if (options.CompressionLevel.HasValue && (options.Mode != QpdfZlibFlateMode.Compress || options.CompressionLevel.Value < 1 || options.CompressionLevel.Value > 9))
        {
            return ToFailedResult(new QpdfParseException("The zlib-flate compression level must be 1..9 while compressing.", null, null));
        }

        try
        {
            await using IQpdfInput input = await host.OpenInputAsync(inputPath, cancellationToken).ConfigureAwait(false);
            governor.NoteHostOperation();
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
                governor.NoteHostOperation();
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

            governor.NoteHostOperation();
            await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(outputPath, cancellationToken).ConfigureAwait(false);
            TransactionSink sink = new(transaction, governor);
            await sink.AppendAsync(output, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return QpdfExecutionResult.Completed([], governor.Snapshot());
        }
        catch (QpdfException ex)
        {
            return ToFailedResult(ex);
        }
        catch (OperationCanceledException)
        {
            return QpdfExecutionResult.Canceled([], ZeroCounters());
        }
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

    private static QpdfExecutionCounters ZeroCounters()
    {
        return new QpdfExecutionCounters(0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L);
    }
}
