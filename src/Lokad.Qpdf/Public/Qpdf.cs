using System.Runtime.InteropServices;
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
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(PdfPageSelectionResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            PageSelectionOutcome result = await SelectPageCoreAsync(
                host: host,
                inputPath: inputPath,
                outputPath: outputPath,
                options: options,
                limits: limits,
                recovery: QpdfRecoveryMode.Strict,
                settings: SelectionSettings.Typed,
                governor: governor,
                diagnostics: diagnostics,
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(PdfPageSelectionResult.StorageBytes);
        try
        {
            using TypedSelectionPreparation preparation = TypedSelectionPreparation.Create(governor);
            preparation.ConvertRotations(rotations);
            DiagnosticBag diagnostics = new(governor);
            PageSelectionOutcome result = await SelectPageCoreAsync(
                host: host,
                inputPath: inputPath,
                outputPath: outputPath,
                options: options,
                limits: limits,
                recovery: QpdfRecoveryMode.Strict,
                settings: SelectionSettings.Typed with { Rotations = preparation.Rotations },
                governor: governor,
                diagnostics: diagnostics,
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
    private static HashSet<int>? CollectGenerateReached(IReadOnlyList<(IReadOnlyDictionary<int, QpdfObject> Pool, Content.CopiedForm? Part)> parts, HashSet<int> survivors, bool pruneUnreachableFields, QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken cancellationToken)
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
        HashSet<int> reached = ParserContainers.CreateSet<int>(memory);
        foreach ((IReadOnlyDictionary<int, QpdfObject> pool, Content.CopiedForm? part) in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part is null || !part.HasFields || pool is null)
            {
                continue;
            }

            using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
            (List<PdfReference> kept, HashSet<int> closure) = Content.AcroFormCopy.CollectKeptClosure(pool, part.Fields, survivors, pruneUnreachableFields, limits, governor, scratch, cancellationToken);
            if (kept.Count == 0)
            {
                continue;
            }

            anyFields = true;
            foreach (int id in closure)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddSetItem(memory, reached, id, sizeof(int));
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
            if (name.RawSpan.SequenceEqual("NeedAppearances"u8))
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

    private static Content.ContentTransformRequest? PrepareSourceContentTransforms(
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
        CancellationToken cancellationToken,
        bool preserveUnreferenced,
        out bool pruneHere)
    {
        pruneHere = prune.Enabled && (prune.Mode == "yes"
            || (prune.Mode is null or "auto" && Content.ContentTransforms.SharedResourcesHeuristic(engine, limits, governor, cancellationToken)));
        if (!coalesceContents && !normalizeContent && !pruneHere && !externalizeInlineImages)
        {
            return null;
        }

        return new Content.ContentTransformRequest(coalesceContents, normalizeContent, compress, level, externalizeInlineImages, iiMinBytes)
        { PreserveUnreferenced = preserveUnreferenced, FilterOptions = filterOptions };
    }


    private static async ValueTask<PageSelectionOutcome> SelectPageCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath outputPath,
        PdfPageSelectionOptions options,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        SelectionSettings settings,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        bool selectiveCandidate = host is not Hosting.InMemoryQpdfHost
            && !settings.PreserveUnreferenced && !settings.Linearize && settings.LinearizePass1 is null && !settings.Qdf.Enabled
            && !settings.CoalesceContents && !settings.NormalizeContent && !settings.ExternalizeInlineImages && !settings.Optimize.Optimize
            && settings.Underlays.Count == 0 && settings.Overlays.Count == 0 && !settings.FlattenRotation
            && settings.FlattenAnnotations is null && !settings.GenerateAppearances;
        IQpdfInput? stableInput = null;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                bool retryParse = false;
                try
                {
                    using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
                    using ParserMemoryScope resultStorage = ParserMemoryScope.Create(governor);
                    if (selectiveCandidate && stableInput is null)
                    { stableInput = await GovernedHost.OpenInputAsync(host, inputPath, governor, cancellationToken).ConfigureAwait(false); }
                    await using QpdfDocument document = stableInput is null
                        ? await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false)
                        : await QpdfDocument.OpenSelectionInputAsync(stableInput, inputPath, limits, governor, diagnostics,
                            recovery, attempt == 0, cancellationToken).ConfigureAwait(false);
                    // This private reader remains alive through publication.
                    document.Engine.BorrowImmutableStreamData = !document.Engine.HasSelectiveInput;
                    retryParse = document.Engine.HasSelectiveInput;
                    if (settings.ExternalizeInlineImages || (settings.Optimize.Optimize && !settings.Optimize.KeepInline)) { document.PrepareInlinePageResources(cancellationToken); }
                    int total = document.PageReferences.Count;
                    using ParserMemoryScope copyMemory = ParserMemoryScope.Create(governor);
                    IReadOnlyList<int> requested = options.Resolve(total, limits.MaxContainerSize, governor, copyMemory);
                    if ((long)requested.Count > limits.MaxContainerSize)
                    {
                        throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), requested.Count, limits.MaxContainerSize);
                    }

                    foreach (int pageNumber in requested)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        if (pageNumber > total)
                        {
                            throw new ArgumentOutOfRangeException(nameof(options), $"Page {pageNumber} exceeds the page count of {total}.");
                        }
                    }

                    IReadOnlyList<PageLocation> kept = KeptLocations(document, requested, copyMemory, governor, cancellationToken);
                    GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, 3, copyMemory);
                    foreach (int pageNumber in requested)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        copier.RegisterPageRoot(document.PageReferences[pageNumber - 1].Source?.ObjectNumber);
                    }
                    copier.HintPageCapacity(requested.Count);
                    List<PdfReference> kids = ParserContainers.CreateList<PdfReference>(copyMemory, requested.Count, IntPtr.Size);
                    copyMemory.Reserve(checked(24L * requested.Count));
                    List<(GraphCopier Copier, int Id)> occurrences = ParserContainers.CreateList<(GraphCopier, int)>(copyMemory, requested.Count, 16L);
                    foreach (int pageNumber in requested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        governor.NoteOperation();
                        PageLocation location = document.PageReferences[pageNumber - 1];
                        IReadOnlyDictionary<PdfName, QpdfObject> extra = MaterializeAncestors(document.Engine, location, copier, governor, limits, cancellationToken, copyMemory);
                        (int pageId, PdfDictionary page) = copier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra);
                        RequireMediaBox(page);
                        kids.Add(new PdfReference(pageId, 0));
                        occurrences.Add((copier, pageId));
                    }

                    if (settings.Rotations.Count != 0)
                    {
                        ApplyRotations(occurrences, ResolveRotationFolds(settings.Rotations, kids.Count, governor, limits, copyMemory), governor, copyMemory);
                    }
                    if (settings.PreserveUnreferenced)
                    {
                        using ParserMemoryScope preservation = ParserMemoryScope.Create(governor);
                        HashSet<int> excluded = PreservationExclusions(document.Engine, kept, limits, governor, preservation, cancellationToken);
                        copier.PreserveUnreferencedObjects(document.Engine, excluded, diagnostics, null, cancellationToken);
                    }

                    (PdfReference? singleInfo, PdfReference? singleMetadata) = CopyCatalogExtras(copier, document.Engine, settings.RemoveInfo, settings.RemoveMetadata, diagnostics);
                    PdfReference? singleOutlines = Content.CatalogCopy.TryCopyOutlines(document.Engine, copier, settings.PreserveCatalog, settings.Linearize, governor, cancellationToken);
                    (PdfReference? singleStructTree, QpdfObject? singleMarkInfo) = Content.CatalogCopy.TryCopyStructure(document.Engine, copier, settings.PreserveCatalog, settings.RemoveStructure, governor, cancellationToken);
                    IReadOnlyDictionary<PdfName, QpdfObject> singleExtras = Content.CatalogCopy.TryCopyExtras(document.Engine, copier, settings.PreserveCatalog, settings.RemoveAcroform, governor, copyMemory, cancellationToken);
                    SortedDictionary<string, PdfReference> singleExisting = PreserveExistingAttachments(copier, document.Engine, settings.Edits, settings.PreserveAttachments, limits, governor, diagnostics, copyMemory, cancellationToken);
                    string? singlePageMode = ReadInputPageMode(document.Engine);

                    (bool singleCompress, string singleDecode, int? singleLevel) = ResolveCompression(settings.Compression, settings.Qdf.Enabled);
                    if (singleCompress || singleDecode != "none" || (singleMetadata is not null && settings.Compression.CompressStreams is not null))
                    {
                        TransformCopierStreams(copier, document.Engine, singleCompress, singleDecode, settings.Compression.RecompressFlate, singleLevel, limits, governor, diagnostics, cancellationToken, singleMetadata);
                    }

                    Content.ContentTransformRequest? singleTransforms = PrepareSourceContentTransforms(settings.CoalesceContents, settings.NormalizeContent, settings.ExternalizeInlineImages || (settings.Optimize.Optimize && !settings.Optimize.KeepInline), settings.IiMinBytes, singleCompress, singleLevel, StreamTransformer.Options(singleDecode, settings.Compression.RecompressFlate), settings.Prune, document.Engine, limits, governor, cancellationToken, settings.PreserveUnreferenced, out bool singlePrune);
                    if (singleTransforms is not null)
                    {
                        List<int> occurrenceIds = ParserContainers.CreateList<int>(copyMemory, occurrences.Count, sizeof(int));
                        foreach ((GraphCopier _, int occurrenceId) in occurrences)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                            occurrenceIds.Add(occurrenceId);
                        }
                        Content.ContentTransforms.ApplyPageTransforms(copier, occurrenceIds, singleTransforms, singlePrune, limits, governor, diagnostics, cancellationToken);
                    }

                    Content.OverlayApplication overlayApplied = await Content.Overlays.ApplyAsync(host, inputPath, occurrences, settings.Underlays, settings.Overlays, singleCompress, singleLevel, recovery, limits, governor, diagnostics, copyMemory, cancellationToken, copier.NextId, null).ConfigureAwait(false);
                    copier.AdvanceNextId(overlayApplied.NextId);

                    int flattenAllocator = overlayApplied.NextId;
                    bool needAppearances = (settings.FlattenAnnotations is not null || settings.GenerateAppearances) && Content.FlattenAnnotations.NeedsAppearances(document.Engine, governor, cancellationToken);
                    bool singleGenerated = settings.GenerateAppearances && needAppearances;
                    if (singleGenerated)
                    {
                        using ParserMemoryScope gateMemory = ParserMemoryScope.Create(governor);
                        Content.CopiedForm? gateForm = Content.AcroFormCopy.TryCopySourceForm(document.Engine, copier, kept, true, limits, governor, gateMemory, cancellationToken);
                        HashSet<int> gateSurvivors = Content.AcroFormCopy.CollectAnnotRefs(occurrences, governor, gateMemory, cancellationToken);
                        List<(IReadOnlyDictionary<int, QpdfObject> Pool, Content.CopiedForm? Part)> gateParts = ParserContainers.CreateList<(IReadOnlyDictionary<int, QpdfObject>, Content.CopiedForm?)>(gateMemory);
                        ParserContainers.AddItem(gateMemory, gateParts, (copier.CopiedObjects, gateForm), 16L);
                        foreach (Content.AcroFormCopy.OverlayFieldImport import in overlayApplied.FieldImports)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                            ParserContainers.AddItem(gateMemory, gateParts, (import.Copier.CopiedObjects, import.Part), 16L);
                        }

                        HashSet<int>? gateReached = CollectGenerateReached(gateParts, gateSurvivors, true, limits, governor, gateMemory, cancellationToken);
                        Dictionary<GraphCopier, Content.CopiedForm?> singleForms = ParserContainers.CreateDictionary<GraphCopier, Content.CopiedForm?>(gateMemory);
                        ParserContainers.EnsureEntryCapacity(gateMemory, singleForms, IntPtr.Size); singleForms.Add(copier, gateForm);
                        flattenAllocator = Math.Max(flattenAllocator, copier.NextId);
                        flattenAllocator = Content.GenerateAppearances.Apply(occurrences, singleForms, limits, governor, diagnostics, cancellationToken, gateReached, false, flattenAllocator);
                        needAppearances = false;
                    }
                    if (settings.FlattenAnnotations is not null)
                    {
                        flattenAllocator = Content.FlattenAnnotations.Apply(occurrences, settings.FlattenAnnotations, needAppearances, singleCompress, singleLevel, limits, governor, diagnostics, cancellationToken, flattenAllocator);
                    }
                    if (settings.FlattenRotation)
                    {
                        flattenAllocator = Content.FlattenRotation.Apply(occurrences, singleCompress, singleLevel, settings.PreserveUnreferenced, limits, governor, diagnostics, cancellationToken, flattenAllocator);
                    }
                    if (settings.Optimize.Optimize)
                    {
                        Content.ImageOptimizer.Apply(occurrences, settings.Optimize, limits, governor, cancellationToken);
                    }
                    copier.AdvanceNextId(flattenAllocator);

                    int inputExtension = Content.CatalogCopy.ReadExtensionLevel(document.Engine, governor, cancellationToken);
                    (PdfVersion negotiated, int negotiatedExtension) = NegotiateVersion(document.OwnVersion(), inputExtension, settings.MinVersion, settings.ForceVersion, governor);
                    string streamMode = ResolveObjectStreamMode(settings.ObjectStreams);
                    if (settings.ForceVersion is not null && CompareVersions(negotiated.Major, negotiated.Minor, 1, 5) < 0)
                    {
                        streamMode = "disable";
                    }

                    PdfDictionary? singleLabels = null;
                    if (settings.Labels.Count > 0)
                    {
                        singleLabels = BuildPageLabels(settings.Labels, kids.Count, governor);
                    }
                    else if (!settings.RemoveLabels && Content.PageLabelMerge.HasLabels(document.Engine))
                    {
                        Content.SourceLabels singleSource = Content.PageLabelMerge.ReadSource(document.Engine, copier, limits, governor, cancellationToken);
                        List<(Content.SourceLabels Source, int Page)> singleOccurrences = ParserContainers.CreateList<(Content.SourceLabels, int)>(copyMemory, requested.Count, 16L);
                        foreach (int pageNumber in requested)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                            singleOccurrences.Add((singleSource, pageNumber));
                        }

                        List<(int Index, PdfDictionary Label)> singleItems = Content.PageLabelMerge.Merge(singleOccurrences, limits, governor, cancellationToken, copyMemory);
                        singleLabels = singleItems.Count == 0 ? null : NumbersDictionary(singleItems, governor);
                    }

                    Dictionary<int, QpdfObject> singlePool = ParserContainers.CreateDictionary<int, QpdfObject>(copyMemory);
                    ParserContainers.HintEntryCapacity(copyMemory, singlePool, copier.CopiedObjects.Count, IntPtr.Size, governor);
                    foreach (KeyValuePair<int, QpdfObject> entry in copier.CopiedObjects)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        ParserContainers.EnsureEntryCapacity(copyMemory, singlePool, IntPtr.Size);
                        singlePool.Add(entry.Key, entry.Value);
                    }
                    foreach (GraphCopier overlayCopier in overlayApplied.Copiers)
                    {
                        foreach (KeyValuePair<int, QpdfObject> overlayEntry in overlayCopier.CopiedObjects)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                            ParserContainers.EnsureEntryCapacity(copyMemory, singlePool, IntPtr.Size);
                            singlePool.Add(overlayEntry.Key, overlayEntry.Value);
                        }
                    }
                    byte[]? originalId = TryGetOriginalId(document.Engine);
                    PdfDictionary? singleAcroForm = settings.RemoveAcroform
                        ? null
                        : Content.AcroFormCopy.BuildSingle(
                            document.Engine,
                            copier,
                            kept,
                            true,
                            occurrences,
                            singlePool,
                            overlayApplied.FieldImports,
                            limits,
                            governor,
                            diagnostics,
                            copyMemory,
                            cancellationToken,
                            false);
                    if (singleGenerated)
                    {
                        singleAcroForm = ClearNeedAppearances(singleAcroForm, governor);
                    }

                    // Finish source copying before attachments allocate from the assembled pool.
                    HashSet<int> compressedOrig = CollectCompressedOriginals(document.Engine, streamMode == "preserve", copyMemory, governor, cancellationToken);
                    List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> singleSources = new(2)
                    {
                        (copier.SourceToDestination, compressedOrig),
                    };
                    AttachmentAssembly singleAttached = await AssembleAttachmentsAsync(host, singleExisting, singlePool, settings.Edits, singlePageMode, singleCompress, singleLevel, streamMode == "preserve", singleSources, recovery, limits, governor, diagnostics, copyMemory, cancellationToken).ConfigureAwait(false);
                    HashSet<int> singleCompressed = ResolveCompressedSet(streamMode, singlePool, singleSources, copyMemory, governor, cancellationToken);
                    PdfVersion effective = negotiated;
                    int extensionLevel = negotiatedExtension;
                    if (singleCompressed.Count > 0 && CompareVersions(effective.Major, effective.Minor, 1, 5) < 0)
                    {
                        effective = governor.OwnVersion(1, 5);
                        extensionLevel = 0;
                    }

                    PdfDictionary catalog = NewCatalog(singleLabels, effective, extensionLevel, singleMetadata, singleAttached.NamesRef, singleAttached.PageMode, singleOutlines, singleAcroForm, singleStructTree, singleMarkInfo, singleExtras, singlePool, governor, cancellationToken);
                    PdfDictionary pages = PagesNode(kids, copyMemory, governor, cancellationToken);
                    int modelCount = checked(singlePool.Count + 2);
                    copyMemory.Reserve(checked(64L + 16L * modelCount));
                    List<NumberedObject> model = new(modelCount)
                    {
                        new NumberedObject(1, catalog),
                        new NumberedObject(2, pages),
                    };
                    foreach (KeyValuePair<int, QpdfObject> entry in singlePool)
                    {
                        model.Add(new NumberedObject(entry.Key, entry.Value));
                    }
                    GovernedSort.Sort(CollectionsMarshal.AsSpan(model)[2..], new WriterObjectOrder(), governor, cancellationToken);

                    List<List<int>> singleGroups = PlanObjectStreamGroups(singleCompressed, copyMemory, governor, cancellationToken);
                    IReadOnlyList<IReadOnlyList<int>>? singleStreams = singleGroups.Count == 0 ? null : singleGroups;
                    IReadOnlyDictionary<int, int>? singleOriginalIds = null;
                    if (settings.Qdf.Enabled && !settings.Qdf.NoOriginalIds)
                    {
                        List<IReadOnlyDictionary<int, int>> singleOriginalMaps = ParserContainers.CreateList<IReadOnlyDictionary<int, int>>(copyMemory);
                        ParserContainers.AddItem(copyMemory, singleOriginalMaps, copier.SourceToDestination, IntPtr.Size);
                        foreach (GraphCopier overlayCopier in overlayApplied.Copiers)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                            ParserContainers.AddItem(copyMemory, singleOriginalMaps, overlayCopier.SourceToDestination, IntPtr.Size);
                        }
                        singleOriginalIds = InvertOriginals(singleOriginalMaps, copyMemory, governor, cancellationToken);
                    }
                    // Metadata admission stays with this operation through publication.
                    // Returned immutable arrays do not retain an execution owner.
                    IReadOnlyList<int> sourcePages = ResultCollectionStorage.Copy(resultStorage, requested, sizeof(int), governor, cancellationToken);
                    // All source resolution is finished. The writer receives only its
                    // immutable prepared graph, so fallback cannot follow publication.
                    document.Engine.AcceptSelectiveInput();
                    retryParse = false;
                    await PublishModelAsync(host, effective, model, singleStreams, singleCompress, singleLevel, singleInfo?.ObjectNumber, originalId, settings.StaticId, settings.NewlineBeforeEndstream, settings.Qdf, singleOriginalIds, settings.Linearize, settings.LinearizePass1, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
                    return new PageSelectionOutcome(total, sourcePages, kids.Count, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
                }
                catch (CompleteInputRequiredException) when (attempt == 0 && stableInput is not null) { }
                catch (QpdfParseException) when (attempt == 0 && retryParse) { }
            }
        }
        finally
        {
            if (stableInput is not null)
            {
                try { await stableInput.DisposeAsync().ConfigureAwait(false); }
                finally { governor.FinishDocumentCounters(); }
            }
        }
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
                settings: SelectionSettings.Typed,
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
                settings: SelectionSettings.Typed with { Rotations = preparation.Rotations },
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
        ArgumentNullException.ThrowIfNull(mergeOptions.Rotations, "rotations");
        ArgumentNullException.ThrowIfNull(mergeOptions.Labels, "labels");
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
                recovery: QpdfRecoveryMode.Strict,
                settings: SelectionSettings.Typed with { Rotations = preparation.Rotations, Labels = preparation.Labels },
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

    private static async ValueTask<(MultiPageSelectionOutcome Result, int AttachmentCount)> SelectPagesCoreAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        QpdfPath primaryInput,
        bool pruneUnreachableFields,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        SelectionSettings settings,
        PdfPageCollation? collation,
        QpdfObject? verbatimLabels,
        int? splitPages,
        bool replaceInput,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        bool emptyPrimary,
        QpdfDocument? preparedSource,
        CancellationToken cancellationToken)
    {
        if (preparedSource is not null
            && (sources.Count != 1 || preparedSource.Path.Value != sources[0].Input.Value))
        { throw new InvalidOperationException("A prepared document requires its single original source."); }
        // A pages block uses the multisource copier even with one source. Keep
        // that catalog/selection behavior, retrying its complete preparation on
        // the same lease if a bounded read cannot establish the original result.
        if (preparedSource is null && sources.Count == 1 && host is not Hosting.InMemoryQpdfHost
            && !emptyPrimary && !replaceInput && !splitPages.HasValue
            && !settings.PreserveUnreferenced && !settings.Linearize && settings.LinearizePass1 is null && !settings.Qdf.Enabled
            && !settings.CoalesceContents && !settings.NormalizeContent && !settings.ExternalizeInlineImages && !settings.Optimize.Optimize
            && settings.Underlays.Count == 0 && settings.Overlays.Count == 0 && !settings.FlattenRotation
            && settings.FlattenAnnotations is null && !settings.GenerateAppearances)
        {
            IQpdfInput input = await GovernedHost.OpenInputAsync(host, sources[0].Input,
                governor, cancellationToken).ConfigureAwait(false);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await using QpdfDocument document = await QpdfDocument.OpenSelectionInputAsync(input,
                            sources[0].Input, limits, governor, diagnostics, recovery, attempt == 0,
                            cancellationToken).ConfigureAwait(false);
                        return await SelectPagesCoreAsync(
                            host: host,
                            sources: sources,
                            outputPath: outputPath,
                            primaryInput: primaryInput,
                            pruneUnreachableFields: pruneUnreachableFields,
                            limits: limits,
                            recovery: recovery,
                            settings: settings,
                            collation: collation,
                            verbatimLabels: verbatimLabels,
                            splitPages: splitPages,
                            replaceInput: replaceInput,
                            governor: governor,
                            diagnostics: diagnostics,
                            emptyPrimary: emptyPrimary,
                            preparedSource: document,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (CompleteInputRequiredException) when (attempt == 0) { }
                }
            }
            finally
            {
                try { await input.DisposeAsync().ConfigureAwait(false); }
                finally { governor.FinishDocumentCounters(); }
            }
        }
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        using ParserMemoryScope selectionMemory = ParserMemoryScope.Create(governor);
        List<int> sourceCounts = ParserContainers.CreateList<int>(selectionMemory);
        List<List<(GraphCopier Copier, int Id, int SourcePage)>> perSource = ParserContainers.CreateList<List<(GraphCopier, int, int)>>(selectionMemory, sources.Count, IntPtr.Size);
        int nextId = 3;
        bool needAppearances = false;
        List<GraphCopier> copiers = ParserContainers.CreateList<GraphCopier>(selectionMemory, sources.Count, IntPtr.Size);
        PdfVersion? version = null;
        int inputExtension = 0;
        byte[]? originalId1 = null;
        List<HashSet<int>> sourceCompressed = ParserContainers.CreateList<HashSet<int>>(selectionMemory, sources.Count, IntPtr.Size);
        HashSet<int>? allPreserved = settings.PreserveUnreferenced && splitPages.HasValue
            ? ParserContainers.CreateSet<int>(selectionMemory) : null;
        List<Content.CopiedForm?> multiForms = ParserContainers.CreateList<Content.CopiedForm?>(selectionMemory, sources.Count, IntPtr.Size);
        List<Content.CopiedForm?> gateForms = ParserContainers.CreateList<Content.CopiedForm?>(selectionMemory, sources.Count, IntPtr.Size);
        List<Content.SourceLabels?> multiLabels = ParserContainers.CreateList<Content.SourceLabels?>(selectionMemory, sources.Count, IntPtr.Size);
        QpdfObject? verbatimCopied = null;
        bool needSourceLabels = !settings.RemoveLabels && (settings.Labels.Count == 0 || splitPages.HasValue);
        selectionMemory.Reserve(96L);
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
        IReadOnlyDictionary<PdfName, QpdfObject>? primaryExtras = null;
        HashSet<int> catalogExtraIds = ParserContainers.CreateSet<int>(selectionMemory);
        long selected = 0L;
        (bool multiCompress, string multiDecode, int? multiLevel) = ResolveCompression(settings.Compression, settings.Qdf.Enabled);
        for (int s = 0; s < sources.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using QpdfDocument document = preparedSource
                // Multisource graphs survive each reader. Independent payloads
                // let mostly unused inputs retire before the next source opens.
                ?? await QpdfDocument.OpenCore(host, sources[s].Input, limits, governor, diagnostics, recovery, cancellationToken,
                    false, sources.Count == 1).ConfigureAwait(false);
            try
            {
                // The existing source transfer funds a single complete input
                // until publication. Multiple inputs and ranged buffers remain
                // independent; retaining output sinks receive isolated payloads.
                document.Engine.BorrowImmutableStreamData = sources.Count == 1 && !document.Engine.HasSelectiveInput;
                if (settings.ExternalizeInlineImages || (settings.Optimize.Optimize && !settings.Optimize.KeepInline)) { document.PrepareInlinePageResources(cancellationToken); }
                ParserContainers.AddItem(selectionMemory, sourceCounts, document.PageReferences.Count, sizeof(int));
                PdfVersion sourceVersion = document.OwnVersion();
                if (version is null || IsNewerVersion(sourceVersion, version))
                {
                    version = sourceVersion;
                    inputExtension = Content.CatalogCopy.ReadExtensionLevel(document.Engine, governor, cancellationToken);
                }
                else if (sourceVersion == version)
                { inputExtension = Math.Max(inputExtension, Content.CatalogCopy.ReadExtensionLevel(document.Engine, governor, cancellationToken)); }

                if (s == 0)
                {
                    originalId1 = TryGetOriginalId(document.Engine);
                }

                sourceCompressed.Add(CollectCompressedOriginals(document.Engine, settings.ObjectStreams is not ("disable" or "generate"), selectionMemory, governor, cancellationToken));

                using ParserMemoryScope sourcePlan = ParserMemoryScope.Create(governor);
                IReadOnlyList<int> requested = sources[s].Selection.Resolve(document.PageReferences.Count, limits.MaxContainerSize, governor, sourcePlan);
                foreach (int pageNumber in requested)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (pageNumber > document.PageReferences.Count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(sources), $"Source {s} page {pageNumber} exceeds the page count of {document.PageReferences.Count}.");
                    }
                }

                selected += requested.Count;
                if (selected > limits.MaxContainerSize)
                {
                    throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), selected, limits.MaxContainerSize);
                }

                IReadOnlyList<PageLocation> kept = KeptLocations(document, requested, sourcePlan, governor, cancellationToken);
                GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, nextId, selectionMemory);
                foreach (int pageNumber in requested)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    copier.RegisterPageRoot(document.PageReferences[pageNumber - 1].Source?.ObjectNumber);
                }
                copier.HintPageCapacity(requested.Count);
                List<(GraphCopier Copier, int Id, int SourcePage)> taken = ParserContainers.CreateList<(GraphCopier, int, int)>(selectionMemory, requested.Count, 16L);
                foreach (int pageNumber in requested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    PageLocation location = document.PageReferences[pageNumber - 1];
                    IReadOnlyDictionary<PdfName, QpdfObject> extra = MaterializeAncestors(document.Engine, location, copier, governor, limits, cancellationToken, selectionMemory);
                    (int pageId, PdfDictionary page) = copier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra);
                    RequireMediaBox(page);
                    taken.Add((copier, pageId, pageNumber));
                }

                if (settings.PreserveUnreferenced && s == primaryIdx && !emptyPrimary)
                {
                    using ParserMemoryScope preservation = ParserMemoryScope.Create(governor);
                    HashSet<int> excluded = PreservationExclusions(document.Engine, kept, limits, governor, preservation, cancellationToken);
                    copier.PreserveUnreferencedObjects(document.Engine, excluded, diagnostics, allPreserved, cancellationToken);
                }

                Content.CopiedForm? sourceForm = settings.RemoveAcroform
                    ? null
                    : Content.AcroFormCopy.TryCopySourceForm(
                        document.Engine,
                        copier,
                        kept,
                        pruneUnreachableFields,
                        limits,
                        governor,
                        selectionMemory,
                        cancellationToken);
                multiForms.Add(sourceForm);
                gateForms.Add(settings.RemoveAcroform
                    ? Content.AcroFormCopy.TryCopySourceForm(document.Engine, copier, kept, pruneUnreachableFields, limits, governor, selectionMemory, cancellationToken)
                    : sourceForm);
                if (needSourceLabels)
                {
                    multiLabels.Add(Content.PageLabelMerge.ReadSource(document.Engine, copier, limits, governor, cancellationToken));
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
                    (primaryInfo, primaryMetadata) = CopyCatalogExtras(copier, document.Engine, settings.RemoveInfo, settings.RemoveMetadata, diagnostics);
                    primaryOutlines = Content.CatalogCopy.TryCopyOutlines(document.Engine, copier, settings.PreserveCatalog, settings.Linearize, governor, cancellationToken);
                    (primaryStructTree, primaryMarkInfo) = Content.CatalogCopy.TryCopyStructure(document.Engine, copier, settings.PreserveCatalog, settings.RemoveStructure, governor, cancellationToken);
                    primaryExtras = Content.CatalogCopy.TryCopyExtras(document.Engine, copier, settings.PreserveCatalog, settings.RemoveAcroform, governor, selectionMemory, cancellationToken);
                    if (primaryInfo is not null)
                    {
                        ParserContainers.AddSetItem(selectionMemory, catalogExtraIds, primaryInfo.ObjectNumber, sizeof(int));
                    }

                    if (primaryMetadata is not null)
                    {
                        ParserContainers.AddSetItem(selectionMemory, catalogExtraIds, primaryMetadata.ObjectNumber, sizeof(int));
                    }
                    if (primaryOutlines is not null)
                    {
                        ParserContainers.AddSetItem(selectionMemory, catalogExtraIds, primaryOutlines.ObjectNumber, sizeof(int));
                    }

                    if (primaryStructTree is not null)
                    {
                        ParserContainers.AddSetItem(selectionMemory, catalogExtraIds, primaryStructTree.ObjectNumber, sizeof(int));
                    }

                    if (primaryMarkInfo is PdfReference primaryMarkRef)
                    {
                        ParserContainers.AddSetItem(selectionMemory, catalogExtraIds, primaryMarkRef.ObjectNumber, sizeof(int));
                    }

                    primaryExisting = PreserveExistingAttachments(copier, document.Engine, settings.Edits, settings.PreserveAttachments, limits, governor, diagnostics, selectionMemory, cancellationToken);
                    primaryPageMode = ReadInputPageMode(document.Engine);
                }

                if (multiCompress || multiDecode != "none" || (s == primaryIdx && primaryMetadata is not null && settings.Compression.CompressStreams is not null))
                {
                    TransformCopierStreams(copier, document.Engine, multiCompress, multiDecode, settings.Compression.RecompressFlate, multiLevel, limits, governor, diagnostics, cancellationToken, s == primaryIdx ? primaryMetadata : null);
                }

                Content.ContentTransformRequest? sourceTransforms = PrepareSourceContentTransforms(settings.CoalesceContents, settings.NormalizeContent, settings.ExternalizeInlineImages || (settings.Optimize.Optimize && !settings.Optimize.KeepInline), settings.IiMinBytes, multiCompress, multiLevel, StreamTransformer.Options(multiDecode, settings.Compression.RecompressFlate), settings.Prune, document.Engine, limits, governor, cancellationToken, settings.PreserveUnreferenced, out bool sourcePrune);
                if (sourceTransforms is not null)
                {
                    List<int> takenIds = ParserContainers.CreateList<int>(selectionMemory, taken.Count, sizeof(int));
                    foreach ((GraphCopier _, int takenId, int _) in taken)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        takenIds.Add(takenId);
                    }
                    Content.ContentTransforms.ApplyPageTransforms(copier, takenIds, sourceTransforms, sourcePrune, limits, governor, diagnostics, cancellationToken);
                }

                if ((settings.FlattenAnnotations is not null || settings.GenerateAppearances) && Content.FlattenAnnotations.NeedsAppearances(document.Engine, governor, cancellationToken))
                {
                    needAppearances = true;
                }

                nextId = copier.NextId;
                copiers.Add(copier);
                perSource.Add(taken);
                // Copied containers still borrow immutable source scalars/keys.
                // Keep their coarse admission until this complete job releases them.
                document.Engine.TransferExclusiveValuesTo(selectionMemory);
                // No reader is consulted by the remaining graph preparation or
                // publication. A later writer failure must never retry output.
                document.AcceptSelectionInput();
            }
            catch (QpdfParseException) when (document.Engine.HasSelectiveInput)
            { throw new CompleteInputRequiredException(); }
        }

        List<(int Source, int Slot)> order = CollateOrder(perSource, collation, governor, selectionMemory);
        List<PdfPageSelectionMapping> mapping = ParserContainers.CreateList<PdfPageSelectionMapping>(selectionMemory);
        List<PdfReference> kids = ParserContainers.CreateList<PdfReference>(selectionMemory, order.Count, IntPtr.Size);
        selectionMemory.Reserve(checked(24L * order.Count));
        List<(GraphCopier Copier, int Id)> occurrences = ParserContainers.CreateList<(GraphCopier, int)>(selectionMemory, order.Count, 16L);
        foreach ((int source, int slot) in order)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            (GraphCopier copier, int pageId, int pageNumber) = perSource[source][slot];
            kids.Add(new PdfReference(pageId, 0));
            selectionMemory.Reserve(24L);
            ParserContainers.AddItem(selectionMemory, mapping, new PdfPageSelectionMapping(source, pageNumber), IntPtr.Size);
            occurrences.Add((copier, pageId));
        }

        if (settings.Rotations.Count != 0)
        {
            ApplyRotations(occurrences, ResolveRotationFolds(settings.Rotations, kids.Count, governor, limits, selectionMemory), governor, selectionMemory);
        }

        Content.OverlayApplication overlayApplied = await Content.Overlays.ApplyAsync(host, primaryInput, occurrences, settings.Underlays, settings.Overlays, multiCompress, multiLevel, recovery, limits, governor, diagnostics, selectionMemory, cancellationToken, nextId, null).ConfigureAwait(false);

        int multiFlattenAllocator = overlayApplied.NextId;
        bool multiGenerated = settings.GenerateAppearances && needAppearances;
        if (multiGenerated)
        {
            using ParserMemoryScope gateMemory = ParserMemoryScope.Create(governor);
            Dictionary<GraphCopier, Content.CopiedForm?> generationForms = ParserContainers.CreateDictionary<GraphCopier, Content.CopiedForm?>(gateMemory);
            for (int s = 0; s < copiers.Count && s < multiForms.Count; s++)
            {
                ParserContainers.EnsureEntryCapacity(gateMemory, generationForms, IntPtr.Size);
                generationForms.Add(copiers[s], multiForms[s]);
            }

            HashSet<int> multiSurvivors = Content.AcroFormCopy.CollectAnnotRefs(occurrences, governor, gateMemory, cancellationToken);
            List<(IReadOnlyDictionary<int, QpdfObject> Pool, Content.CopiedForm? Part)> multiGateParts = ParserContainers.CreateList<(IReadOnlyDictionary<int, QpdfObject>, Content.CopiedForm?)>(gateMemory);
            for (int s = 0; s < copiers.Count && s < gateForms.Count; s++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(gateMemory, multiGateParts, (copiers[s].CopiedObjects, gateForms[s]), 16L);
            }
            foreach (Content.AcroFormCopy.OverlayFieldImport import in overlayApplied.FieldImports)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(gateMemory, multiGateParts, (import.Copier.CopiedObjects, import.Part), 16L);
            }

            HashSet<int>? multiReached = CollectGenerateReached(multiGateParts, multiSurvivors, pruneUnreachableFields, limits, governor, gateMemory, cancellationToken);
            multiFlattenAllocator = Content.GenerateAppearances.Apply(occurrences, generationForms, limits, governor, diagnostics, cancellationToken, multiReached, !pruneUnreachableFields, multiFlattenAllocator);
            needAppearances = false;
        }
        if (settings.FlattenAnnotations is not null)
        {
            multiFlattenAllocator = Content.FlattenAnnotations.Apply(occurrences, settings.FlattenAnnotations, needAppearances, multiCompress, multiLevel, limits, governor, diagnostics, cancellationToken, multiFlattenAllocator);
        }

        if (settings.FlattenRotation)
        {
            nextId = Content.FlattenRotation.Apply(occurrences, multiCompress, multiLevel, settings.PreserveUnreferenced, limits, governor, diagnostics, cancellationToken, multiFlattenAllocator);
        }
        if (settings.Optimize.Optimize)
        {
            Content.ImageOptimizer.Apply(occurrences, settings.Optimize, limits, governor, cancellationToken);
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
        if (!settings.RemoveAcroform && overlayApplied.FieldImports.Count > 0)
        {
            int count = checked(multiForms.Count + overlayApplied.FieldImports.Count);
            renameForms = ParserContainers.CreateList<Content.CopiedForm?>(selectionMemory, count, IntPtr.Size);
            renameCopiers = ParserContainers.CreateList<GraphCopier>(selectionMemory, count, IntPtr.Size);
            for (int index = 0; index < multiForms.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                renameForms.Add(multiForms[index]); renameCopiers.Add(copiers[index]);
            }
            foreach (Content.AcroFormCopy.OverlayFieldImport import in overlayApplied.FieldImports)
            {
                renameForms.Add(import.Part);
                renameCopiers.Add(import.Copier);
            }
        }

        if (!settings.RemoveAcroform)
        {
            Content.AcroFormCopy.RenameCollidingFields(renameForms, renameCopiers, limits, governor, cancellationToken);
        }

        Dictionary<int, QpdfObject> pool = ParserContainers.CreateDictionary<int, QpdfObject>(selectionMemory);
        foreach (GraphCopier copier in copiers)
        {
            // Final writer models sort by object number; insertion order need not
            // require a second sorted snapshot of every source's keys.
            foreach (KeyValuePair<int, QpdfObject> entry in copier.CopiedObjects)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (!pool.ContainsKey(entry.Key)) { ParserContainers.EnsureEntryCapacity(selectionMemory, pool, IntPtr.Size); }
                pool[entry.Key] = entry.Value;
            }
        }

        foreach (GraphCopier overlayCopier in overlayApplied.Copiers)
        {
            foreach (KeyValuePair<int, QpdfObject> overlayEntry in overlayCopier.CopiedObjects)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (!pool.ContainsKey(overlayEntry.Key)) { ParserContainers.EnsureEntryCapacity(selectionMemory, pool, IntPtr.Size); }
                pool[overlayEntry.Key] = overlayEntry.Value;
            }
        }

        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> multiSources = ParserContainers.CreateList<(IReadOnlyDictionary<int, int>, HashSet<int>)>(selectionMemory, copiers.Count, 16L);
        for (int s = 0; s < copiers.Count; s++)
        {
            multiSources.Add((copiers[s].SourceToDestination, sourceCompressed[s]));
        }

        AttachmentAssembly multiAttached = await AssembleAttachmentsAsync(host, primaryExisting, pool, settings.Edits, primaryPageMode, multiCompress, multiLevel, settings.ObjectStreams is not ("disable" or "generate"), multiSources, recovery, limits, governor, diagnostics, selectionMemory, cancellationToken).ConfigureAwait(false);

        List<(int Index, PdfDictionary Label)> explicitItems = settings.Labels.Count > 0
            ? ResolveLabelItems(settings.Labels, kids.Count, governor, selectionMemory)
            : new List<(int Index, PdfDictionary Label)>();
        List<(int Index, PdfDictionary Label)> labelItems = explicitItems;
        QpdfObject? catalogLabels = labelItems.Count == 0 ? null : NumbersDictionary(labelItems, governor);
        if (settings.RemoveLabels)
        {
            labelItems = new List<(int Index, PdfDictionary Label)>();
            catalogLabels = null;
        }
        else if (settings.Labels.Count == 0 || splitPages.HasValue)
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

            List<(Content.SourceLabels Source, int Page)> labelOccurrences = ParserContainers.CreateList<(Content.SourceLabels, int)>(selectionMemory, anyLabels ? mapping.Count : 0, 16L);
            if (anyLabels)
            {
                foreach (PdfPageSelectionMapping entry in mapping)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
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
        (PdfVersion negotiatedMulti, int negotiatedMultiExtension) = NegotiateVersion(inputMax, inputExtension, settings.MinVersion, settings.ForceVersion, governor);
        string multiStreamMode = ResolveObjectStreamMode(settings.ObjectStreams);
        if (settings.ForceVersion is not null && CompareVersions(negotiatedMulti.Major, negotiatedMulti.Minor, 1, 5) < 0)
        {
            multiStreamMode = "disable";
        }

        HashSet<int> multiCompressed = ResolveCompressedSet(multiStreamMode, pool, multiSources, selectionMemory, governor, cancellationToken);
        PdfVersion effective = negotiatedMulti;
        int extensionLevel = negotiatedMultiExtension;
        if (multiCompressed.Count > 0 && CompareVersions(effective.Major, effective.Minor, 1, 5) < 0)
        {
            effective = governor.OwnVersion(1, 5);
            extensionLevel = 0;
        }

        IReadOnlyDictionary<int, int>? multiOriginalIds = null;
        if (settings.Qdf.Enabled && !settings.Qdf.NoOriginalIds)
        {
            List<IReadOnlyDictionary<int, int>> multiOriginalMaps = ParserContainers.CreateList<IReadOnlyDictionary<int, int>>(selectionMemory);
            foreach (GraphCopier sourceCopier in copiers)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(selectionMemory, multiOriginalMaps, sourceCopier.SourceToDestination, IntPtr.Size);
            }
            foreach (GraphCopier overlayCopier in overlayApplied.Copiers)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(selectionMemory, multiOriginalMaps, overlayCopier.SourceToDestination, IntPtr.Size);
            }
            multiOriginalIds = InvertOriginals(multiOriginalMaps, selectionMemory, governor, cancellationToken);
        }
        Content.CopiedForm? multiCombined = settings.RemoveAcroform
            ? null
            : Content.AcroFormCopy.Combine(renameForms, governor, selectionMemory, cancellationToken);
        if (multiCombined is not null && overlayApplied.FieldImports.Count > 0)
        {
            (PdfByteString? multiDestAppearance, int? multiDestQuadding) = Content.AcroFormCopy.ReadMergedDefaults(multiCombined, pool, governor, cancellationToken);
            int importAllocator = 1;
            foreach (int id in pool.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
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
                Content.AcroFormCopy.CollectAnnotRefs(occurrences, governor, selectionMemory, cancellationToken),
                pruneUnreachableFields,
                limits,
                governor,
                cancellationToken);
        if (multiGenerated)
        {
            multiAcroForm = ClearNeedAppearances(multiAcroForm, governor);
        }

        if (governor.HasTypedResult) { selectionMemory.PrepareResultStorage(checked(24L * mapping.Count)); }
        if (!splitPages.HasValue)
        {
            List<List<int>> multiGroups = PlanObjectStreamGroups(multiCompressed, selectionMemory, governor, cancellationToken);
            IReadOnlyList<IReadOnlyList<int>>? multiStreams = multiGroups.Count == 0 ? null : multiGroups;
            int modelCount = checked(pool.Count + 2);
            selectionMemory.Reserve(checked(64L + 16L * modelCount));
            List<NumberedObject> model = new(modelCount)
            {
                new NumberedObject(1, NewCatalog(catalogLabels, effective, extensionLevel, primaryMetadata, multiAttached.NamesRef, multiAttached.PageMode, primaryOutlines, multiAcroForm, primaryStructTree, primaryMarkInfo, primaryExtras, pool, governor, cancellationToken)),
                new NumberedObject(2, PagesNode(kids, selectionMemory, governor, cancellationToken)),
            };
            foreach (KeyValuePair<int, QpdfObject> entry in pool)
            {
                model.Add(new NumberedObject(entry.Key, entry.Value));
            }
            GovernedSort.Sort(CollectionsMarshal.AsSpan(model)[2..], new WriterObjectOrder(), governor, cancellationToken);

            IReadOnlyList<PdfPageSelectionMapping> resultMapping = ResultCollectionStorage.Copy(selectionMemory, mapping, IntPtr.Size, governor, cancellationToken);
            IReadOnlyList<int> resultCounts = ResultCollectionStorage.Copy(selectionMemory, sourceCounts, sizeof(int), governor, cancellationToken);
            IReadOnlyList<QpdfPath> resultPaths = ResultCollectionStorage.Single(selectionMemory, target, IntPtr.Size, governor, cancellationToken);
            await PublishModelAsync(host, effective, model, multiStreams, multiCompress, multiLevel, primaryInfo?.ObjectNumber, originalId1, settings.StaticId, settings.NewlineBeforeEndstream, settings.Qdf, multiOriginalIds, settings.Linearize, settings.LinearizePass1, target, limits, governor, cancellationToken).ConfigureAwait(false);
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
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
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

            if (settings.Linearize && string.Equals(settings.LinearizePass1?.Value, name, StringComparison.Ordinal))
            {
                throw new QpdfParseException("The first-pass output must differ from every main PDF output.", null, null);
            }
        }

        List<(int First, int Last)> groups = PartitionGroups(kids.Count, splitPages.Value, selectionMemory, governor);
        using PreparedSplitPublications preparation = PreparedSplitPublications.Create(governor);
        ParserMemoryScope presentationMemory = preparation.Memory;
        List<PreparedSplitPublication> prepared = preparation.Items;
        for (int g = 0; g < groups.Count; g++)
        {
            using ParserMemoryScope groupMemory = ParserMemoryScope.Create(governor);
            cancellationToken.ThrowIfCancellationRequested();
            (int first, int last) = groups[g];
            List<int> roots = ParserContainers.CreateList<int>(groupMemory);
            for (int slot = first - 1; slot < last; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(groupMemory, roots, kids[slot].ObjectNumber, sizeof(int));
            }

            PdfDictionary? groupAcroForm = null;
            if (multiCombined is not null)
            {
                List<(GraphCopier Copier, int Id)> groupOccurrences = ParserContainers.CreateList<(GraphCopier, int)>(groupMemory);
                for (int slot = first - 1; slot < last; slot++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    ParserContainers.AddItem(groupMemory, groupOccurrences, (occurrences[slot].Copier, occurrences[slot].Id), 16L);
                }

                HashSet<int> groupAnnots = Content.AcroFormCopy.CollectAnnotRefs(groupOccurrences, governor, groupMemory, cancellationToken);
                groupAcroForm = Content.AcroFormCopy.BuildCatalogForm(multiCombined, pool, groupAnnots, pruneUnreachableFields, limits, governor, cancellationToken);
                if (multiGenerated)
                {
                    groupAcroForm = ClearNeedAppearances(groupAcroForm, governor);
                }
            }

            PdfDictionary? slicedLabels = SlicePageLabels(labelItems, first, last, governor);
            if (groupAcroForm is not null)
            {
                foreach (int id in Content.AcroFormCopy.CollectRefNumbers(groupAcroForm, governor, groupMemory, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    ParserContainers.AddItem(groupMemory, roots, id, sizeof(int));
                }
            }

            if (slicedLabels is not null)
            {
                foreach (int id in Content.AcroFormCopy.CollectRefNumbers(slicedLabels, governor, groupMemory, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    ParserContainers.AddItem(groupMemory, roots, id, sizeof(int));
                }
            }

            if (allPreserved is not null)
            {
                foreach (int id in allPreserved)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    ParserContainers.AddItem(groupMemory, roots, id, sizeof(int));
                }
            }
            foreach (int id in catalogExtraIds)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(groupMemory, roots, id, sizeof(int));
            }
            foreach (int id in multiAttached.ObjectIds)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(groupMemory, roots, id, sizeof(int));
            }
            using ParserMemoryScope reachabilityMemory = ParserMemoryScope.Create(governor);
            HashSet<int> reachable = ReachableIds(pool, roots, reachabilityMemory, governor);
            if (settings.PreserveUnreferenced)
            {
                foreach (int id in pool.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    ParserContainers.AddSetItem(reachabilityMemory, reachable, id, sizeof(int));
                }
            }
            groupMemory.Reserve(checked(64L + sizeof(int) * (long)reachable.Count));
            List<int> kept = new(reachable.Count);
            foreach (int id in reachable)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (pool.ContainsKey(id))
                {
                    kept.Add(id);
                }
            }

            GovernedSort.Sort(CollectionsMarshal.AsSpan(kept), new WriterNumberOrder(), governor, cancellationToken);
            Dictionary<int, int> renumber = ParserContainers.CreateDictionary<int, int>(groupMemory);
            ParserContainers.HintEntryCapacity(groupMemory, renumber, kept.Count, sizeof(int), governor);
            for (int r = 0; r < kept.Count; r++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                GovernedCollections.Assign(groupMemory, governor, renumber, kept[r], 3 + r, sizeof(int));
            }

            List<PdfReference> renumberedKids = ParserContainers.CreateList<PdfReference>(groupMemory);
            for (int slot = first - 1; slot < last; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                groupMemory.Reserve(32L);
                ParserContainers.AddItem(groupMemory, renumberedKids, new PdfReference(renumber[kids[slot].ObjectNumber], 0), IntPtr.Size);
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
                if (Writing.ObjectRemap.RemapIds(groupAcroForm, renumber, groupMemory, governor, cancellationToken) is not PdfDictionary remapped)
                {
                    throw new QpdfException("Split publication rebuilt a non-dictionary form entry.");
                }

                remappedAcroForm = remapped;
            }
            QpdfObject? remappedLabels = null;
            if (slicedLabels is not null)
            {
                if (Writing.ObjectRemap.RemapIds(slicedLabels, renumber, groupMemory, governor, cancellationToken) is not PdfDictionary remappedLabelsDictionary)
                {
                    throw new QpdfException("Split publication rebuilt a non-dictionary label tree.");
                }

                remappedLabels = remappedLabelsDictionary;
            }
            List<NumberedObject> sub = ParserContainers.CreateList<NumberedObject>(groupMemory);
            NumberedObject groupCatalog = new(1, NewCatalog(remappedLabels, effective, extensionLevel, groupMetadata, groupNames, multiAttached.PageMode, groupOutlines, remappedAcroForm, groupStructTree, groupMarkInfo, null, pool, governor, cancellationToken));
            NumberedObject groupPages = new(2, PagesNode(renumberedKids, groupMemory, governor, cancellationToken));
            ParserContainers.AddItem(groupMemory, sub, groupCatalog, 16L);
            ParserContainers.AddItem(groupMemory, sub, groupPages, 16L);
            foreach (int id in kept)
            {
                NumberedObject item = new(renumber[id], Writing.ObjectRemap.RemapIds(pool[id], renumber, groupMemory, governor, cancellationToken));
                ParserContainers.AddItem(groupMemory, sub, item, 16L);
            }

            QpdfPath path = QpdfPath.From(names[g]);
            HashSet<int> groupCompressed = RemapCompressedSet(multiCompressed, renumber, groupMemory, governor, cancellationToken);
            List<List<int>> groupGroups = PlanObjectStreamGroups(groupCompressed, groupMemory, governor, cancellationToken);
            IReadOnlyList<IReadOnlyList<int>>? groupStreams = groupGroups.Count == 0 ? null : groupGroups;
            Dictionary<int, int>? groupOriginalIds = null;
            if (multiOriginalIds is not null)
            {
                groupOriginalIds = ParserContainers.CreateDictionary<int, int>(groupMemory);
                foreach (int keptId in kept)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (multiOriginalIds.TryGetValue(keptId, out int source))
                    {
                        GovernedCollections.Assign(groupMemory, governor, groupOriginalIds, renumber[keptId], source, sizeof(int));
                    }
                }
            }

            PreparedSplitPublication publication = await PreparePresentationOutputAsync(effective, sub, groupStreams, multiCompress,
                multiLevel, groupInfo, originalId1, settings.StaticId, settings.NewlineBeforeEndstream, settings.Qdf, groupOriginalIds,
                settings.Linearize, settings.LinearizePass1, path, limits, governor, cancellationToken).ConfigureAwait(false);
            try { ParserContainers.AddItem(presentationMemory, prepared, publication, IntPtr.Size); }
            catch { publication.Dispose(); throw; }
        }

        long publicationOperations = 0L, publicationCapacity = 0L;
        foreach (PreparedSplitPublication publication in prepared)
        {
            governor.CheckCancellation();
            publicationOperations = checked(publicationOperations + publication.PublicationOperations);
            publicationCapacity = Math.Max(publicationCapacity, publication.PublicationCapacity);
        }
        using PublicationReservation publicationAdmission = PublicationReservation.Create(governor, publicationOperations, publicationCapacity);
        IReadOnlyList<PdfPageSelectionMapping> splitMapping = ResultCollectionStorage.Copy(selectionMemory, mapping, IntPtr.Size, governor, cancellationToken);
        IReadOnlyList<int> splitCounts = ResultCollectionStorage.Copy(selectionMemory, sourceCounts, sizeof(int), governor, cancellationToken);
        List<QpdfPath> outputPaths = ParserContainers.CreateList<QpdfPath>(selectionMemory);
        foreach (string name in names)
        {
            ParserContainers.AddItem(selectionMemory, outputPaths, QpdfPath.From(name), IntPtr.Size);
            if (governor.HasTypedResult) { selectionMemory.PrepareResultStorage(GovernedText.StringBytes(name.Length)); }
        }
        IReadOnlyList<QpdfPath> splitPaths = ResultCollectionStorage.Copy(selectionMemory, outputPaths, IntPtr.Size, governor, cancellationToken);
        governor.PrepareExecutionPresentation();
        publicationAdmission.Begin();
        foreach (PreparedSplitPublication publication in prepared)
        {
            await publication.PublishAsync(host, governor, cancellationToken).ConfigureAwait(false);
        }

        return (new MultiPageSelectionOutcome(kids.Count, splitMapping, splitCounts, splitPaths, ResultWarningSnapshot.Read(resultWarnings, diagnostics)), multiAttached.FinalCount);
    }

    private static List<List<(PdfRotationMode Mode, int Angle)>> ResolveRotationFolds(
        IReadOnlyList<OutputRotation> rotations, int total, ResourceGovernor governor,
        QpdfLimits limits, ParserMemoryScope ownership)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ownership.CheckOwner(governor);
        List<List<(PdfRotationMode Mode, int Angle)>> folds = ParserContainers.CreateList<List<(PdfRotationMode Mode, int Angle)>>(ownership);
        for (int i = 0; i < total; i++)
        {
            governor.CheckCancellation();
            ParserContainers.AddItem(ownership, folds, ParserContainers.CreateList<(PdfRotationMode, int)>(ownership), IntPtr.Size);
        }

        foreach (OutputRotation specification in rotations)
        {
            governor.CheckCancellation();
            IReadOnlyList<int> positions = specification.ResolvePositions(total, limits.MaxContainerSize, governor, ownership);
            foreach (int position in positions)
            {
                governor.CheckCancellation();
                ParserContainers.AddItem(ownership, folds[position - 1], (specification.Mode, specification.Angle), 8L);
            }
        }

        return folds;
    }

    private static void ApplyRotations(
        List<(GraphCopier Copier, int Id)> occurrences,
        List<List<(PdfRotationMode Mode, int Angle)>> folds,
        ResourceGovernor governor, ParserMemoryScope ownership)
    {
        ownership.CheckOwner(governor);
        for (int i = 0; i < occurrences.Count; i++)
        {
            governor.CheckCancellation();
            if (folds[i].Count == 0) { continue; }
            (GraphCopier copier, int id) = occurrences[i];
            if (!copier.CopiedObjects.TryGetValue(id, out QpdfObject? value) || value is not PdfDictionary page)
            {
                throw new QpdfException("Rotation reached a missing copied page dictionary.");
            }
            ApplyRotation(copier, id, page, folds[i], i + 1, governor, ownership);
        }
    }

    private static void ApplyRotation(GraphCopier copier, int id, PdfDictionary page,
        IReadOnlyList<(PdfRotationMode Mode, int Angle)> specifications, int pageNumber,
        ResourceGovernor governor, ParserMemoryScope ownership)
    {
        using ParserMemoryScope header = ParserMemoryScope.Create(governor);
        header.Reserve(176L);
        PdfName rotateKey = PdfName.FromAscii("Rotate");
        int current = 0;
        if (page.TryGet(rotateKey, out QpdfObject raw))
        {
            if (raw is PdfReference reference && PdfObjectLookup.TryResolve(reference, copier.CopiedObjects,
                governor, governor.CancellationToken, out QpdfObject? resolved))
            { raw = resolved ?? raw; }
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
                throw new QpdfParseException($"Output page {pageNumber} has an invalid /Rotate value.", null, null);
            }
        }

        foreach ((PdfRotationMode mode, int angle) in specifications)
        {
            governor.CheckCancellation();
            current = mode switch
            {
                PdfRotationMode.Set => angle,
                PdfRotationMode.Add => NormalizeRotation((long)current + angle),
                PdfRotationMode.Subtract => NormalizeRotation((long)current - angle),
                _ => throw new QpdfException($"Unknown rotation mode {(int)mode}."),
            };
        }

        Dictionary<PdfName, QpdfObject> entries = ParserContainers.CreateDictionary<PdfName, QpdfObject>(header);
        using PdfDictionary.EntryEnumerator source = page.GetEntryEnumerator();
        while (source.MoveNext(governor, governor.CancellationToken))
        {
            ParserContainers.EnsureEntryCapacity(header, entries, IntPtr.Size);
            entries[source.Current.Key] = source.Current.Value;
        }
        if (!entries.ContainsKey(rotateKey)) { ParserContainers.EnsureEntryCapacity(header, entries, IntPtr.Size); }
        entries[rotateKey] = new PdfInteger(current.ToString(System.Globalization.CultureInfo.InvariantCulture), current);
        PdfDictionary rotated = PdfDictionary.FromOwnedEntries(entries);
        header.TransferTo(ownership); copier.ReplaceCopiedObject(id, rotated);
    }

    private static int NormalizeRotation(long value)
    {
        return (int)(((value % 360L) + 360L) % 360L);
    }

    private static List<(int Source, int Slot)> CollateOrder(
        List<List<(GraphCopier Copier, int Id, int SourcePage)>> perSource,
        PdfPageCollation? collation, ResourceGovernor governor, ParserMemoryScope memory)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<int> counts = ParserContainers.CreateList<int>(scratch);
        foreach (var source in perSource)
        {
            governor.CheckCancellation();
            ParserContainers.AddItem(scratch, counts, source.Count, sizeof(int));
        }
        return CollatePageOrder(counts, collation, governor, memory);
    }

    private static PdfDictionary PagesNode(IReadOnlyList<PdfReference> groupKids,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        // Fixed bounded names, three-entry table and count token; child backing
        // is admitted exactly and adopted without a second array copy.
        memory.Reserve(checked(1024L + IntPtr.Size * (long)groupKids.Count));
        QpdfObject[] children = new QpdfObject[groupKids.Count];
        for (int index = 0; index < groupKids.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); children[index] = groupKids[index];
        }
        string text = groupKids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return PdfDictionary.FromOwnedEntries(new Dictionary<PdfName, QpdfObject>(3)
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Pages"),
            [PdfName.FromAscii("Kids")] = PdfArray.FromOwnedItems(children),
            [PdfName.FromAscii("Count")] = new PdfInteger(text, groupKids.Count),
        });
    }

    private static readonly byte[] StaticIdBytes = new byte[] { 0x31, 0x41, 0x59, 0x26, 0x53, 0x58, 0x97, 0x93, 0x23, 0x84, 0x62, 0x64, 0x33, 0x83, 0x27, 0x95 };

    private static readonly byte[] PlaceholderIdBytes = new byte[16];

    private static PdfDictionary NewCatalog(QpdfObject? pageLabels, PdfVersion finalVersion, int extensionLevel, PdfReference? metadataRef, PdfReference? namesRef, string? pageMode, PdfReference? outlinesRef, PdfDictionary? acroForm, PdfReference? structTreeRef, QpdfObject? markInfo, IReadOnlyDictionary<PdfName, QpdfObject>? extras, IReadOnlyDictionary<int, QpdfObject> pool, ResourceGovernor governor, CancellationToken token)
    {
        if (finalVersion is null)
        {
            throw new ArgumentNullException(nameof(finalVersion));
        }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        void Set(ReadOnlySpan<byte> key, QpdfObject value)
            => entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, key), value, token);
        Set("Type"u8, PdfDictionaryRebuild.CreateName(graph, "Catalog"u8));
        graph.Memory.Reserve(24L); graph.Add(24L);
        Set("Pages"u8, new PdfReference(2, 0));
        if (pageLabels is not null)
        {
            Set("PageLabels"u8, pageLabels);
        }

        if (metadataRef is not null)
        {
            Set("Metadata"u8, metadataRef);
        }

        if (namesRef is not null)
        {
            Set("Names"u8, namesRef);
        }

        if (pageMode is not null)
        {
            long bytes = checked(56L + pageMode.Length);
            graph.Memory.Reserve(bytes);
            PdfName mode = PdfName.FromOwnedBytes(System.Text.Encoding.ASCII.GetBytes(pageMode)); graph.Add(bytes);
            Set("PageMode"u8, mode);
        }

        if (outlinesRef is not null)
        {
            Set("Outlines"u8, outlinesRef);
        }

        if (acroForm is not null)
        {
            Set("AcroForm"u8, acroForm);
        }

        if (structTreeRef is not null)
        {
            Set("StructTreeRoot"u8, structTreeRef);
        }

        if (markInfo is not null && markInfo is not PdfNull)
        {
            Set("MarkInfo"u8, markInfo);
        }

        QpdfObject? originalExtensions = null;
        if (extras is not null)
        {
            foreach (KeyValuePair<PdfName, QpdfObject> extra in extras)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                // Imported fields replace an opaque primary form, as they do
                // in the reference; the preserved array must not overwrite it.
                if (acroForm is not null && extra.Key.RawSpan.SequenceEqual("AcroForm"u8)) { continue; }
                if (extra.Key.RawSpan.SequenceEqual("Extensions"u8)) { originalExtensions = extra.Value; continue; }
                if (namesRef is not null && extra.Key.RawSpan.SequenceEqual("Names"u8))
                {
                    PdfDictionary names = extra.Value as PdfDictionary ?? throw new InvalidOperationException("Copied name categories must be a dictionary.");
                    PdfDictionary attachments = pool[namesRef.ObjectNumber] as PdfDictionary ?? throw new InvalidOperationException("Attachment names must be a dictionary.");
                    if (!PdfDictionaryLookup.TryGet(attachments, "EmbeddedFiles"u8, governor, token, out QpdfObject files))
                    { throw new InvalidOperationException("Attachment names must contain EmbeddedFiles."); }
                    Set("Names"u8, PdfDictionaryRebuild.ReplaceEntry(graph, names, "EmbeddedFiles"u8, files));
                    continue;
                }
                entries.Assign(graph.Memory, governor, extra.Key, extra.Value, token);
            }
        }

        if (originalExtensions is not null || extensionLevel > 0)
        {
            IIndirectResolver resolver = Content.PoolResolver.Create(graph.Memory, pool);
            QpdfObject? extensions = PrepareExtensions(originalExtensions, resolver, finalVersion, extensionLevel, graph);
            if (extensions is not null) { Set("Extensions"u8, extensions); }
        }

        PdfDictionary catalog = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Retain(); return catalog;
    }

    private static QpdfObject? PrepareExtensions(QpdfObject? original, IIndirectResolver resolver, PdfVersion version, int extensionLevel, OwnedAllocationBatch graph)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        PdfDictionary? extensions = Content.CatalogCopy.ResolveDictionary(original, resolver, governor, token);
        QpdfObject raw = PdfNull.Instance;
        bool hasAdobe = extensions is not null && PdfDictionaryLookup.TryGet(extensions, "ADBE"u8, governor, token, out raw);
        if (extensionLevel == 0)
        {
            if (!hasAdobe) { return original; }
            PdfDictionary remaining = PdfDictionaryRebuild.RemoveEntry(graph, extensions ?? throw new InvalidOperationException(), "ADBE"u8);
            return remaining.Count == 0 ? null : remaining;
        }

        // Keep an unchanged declaration, including private fields and aliases.
        PdfDictionary? adbe = Content.CatalogCopy.ResolveDictionary(raw, resolver, governor, token);
        if (adbe is not null
            && PdfDictionaryLookup.TryGet(adbe, "BaseVersion"u8, governor, token, out QpdfObject baseRaw)
            && Content.CatalogCopy.ResolveValue(baseRaw, resolver, governor, token) is PdfName baseName
            && baseName.RawSpan.Length == 3 && baseName.RawSpan[0] == (byte)('0' + version.Major)
            && baseName.RawSpan[1] == (byte)'.' && baseName.RawSpan[2] == (byte)('0' + version.Minor)
            && PdfDictionaryLookup.TryGet(adbe, "ExtensionLevel"u8, governor, token, out QpdfObject levelRaw)
            && Content.CatalogCopy.ResolveValue(levelRaw, resolver, governor, token) is PdfInteger oldLevel && oldLevel.Value == extensionLevel)
        { return original; }

        graph.Memory.Reserve(512L);
        string baseVersion = version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + version.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string levelText = extensionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PdfName name = PdfName.FromOwnedBytes(System.Text.Encoding.ASCII.GetBytes(baseVersion));
        PdfInteger level = new(levelText, extensionLevel);
        graph.Add(checked(96L + baseVersion.Length + GovernedText.StringBytes(levelText.Length)));
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "BaseVersion"u8), name, token);
        entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "ExtensionLevel"u8), level, token);
        PdfDictionary replacement = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        if (extensions is not null) { return PdfDictionaryRebuild.ReplaceEntry(graph, extensions, "ADBE"u8, replacement); }
        entries = PdfDictionaryRebuild.CreateEntries(graph, out allocation);
        entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "ADBE"u8), replacement, token);
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
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
            governor.NoteOperation();
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
            governor.CheckCancellation();
            if (index + 1 <= first) { carry = (index, label); }
            else if (index + 1 <= last)
            { GovernedCollections.AddItem(memory, governor, sliced, (index - (first - 1), label), 16L); }
            else { break; }
        }
        sliced[0] = (0, carry.Label);
        return NumbersDictionary(sliced, governor);
    }

    private static List<(int First, int Last)> PartitionGroups(int total, int groupSize,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        memory.CheckOwner(governor);
        List<(int First, int Last)> groups = ParserContainers.CreateList<(int, int)>(memory);
        for (int first = 1; first <= total;)
        {
            governor.CheckCancellation();
            int last = (int)Math.Min(total, (long)first + groupSize - 1L);
            ParserContainers.AddItem(memory, groups, (first, last), 8L);
            if (last == total) { break; }
            first = last + 1;
        }

        return groups;
    }

    private static List<string> GenerateSplitNames(string pattern, int total, int groupSize,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        int width = 1;
        for (int remaining = total; remaining >= 10; remaining /= 10) { width++; }
        governor.CheckCancellation();
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
            if (last == total) { break; }
            first = last + 1;
        }
        return names;
    }

    private static void CopySplitNamePart(ReadOnlySpan<char> source, Span<char> target, ResourceGovernor governor)
    {
        for (int offset = 0; offset < source.Length; offset += Math.Min(256, source.Length - offset))
        {
            int count = Math.Min(256, source.Length - offset);
            governor.CheckCancellation();
            source.Slice(offset, count).CopyTo(target.Slice(offset, count));
        }
    }

    private static void WriteSplitNumber(Span<char> target, int value, ResourceGovernor governor)
    {
        governor.CheckCancellation();
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
            governor.NoteOperation();
            memory.Reserve(24L);
            ParserContainers.AddItem(memory, references, new PdfReference(root, 0));
        }
        PdfArray rootArray = ParserContainers.CopyArray(memory, references);
        return GraphReachability.Find(new Content.PoolResolver(pool), rootArray, memory, governor);
    }

    private sealed class IdentifierHashSink : ISynchronousByteSink, IDisposable
    {
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;
        private ParserMemoryScope? _memory;
        private IncrementalHash? _hash;
        private long _length;

        private IdentifierHashSink(QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, IncrementalHash hash)
        {
            _limits = limits; _governor = governor; _memory = memory; _hash = hash;
        }

        internal static IdentifierHashSink Create(QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(governor);
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            IncrementalHash? hash = null;
            try
            {
                // A fixed allowance for the hash context and its small result.
                memory.Reserve(192L);
                hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                return new(limits, governor, memory, hash);
            }
            catch { hash?.Dispose(); memory.Dispose(); throw; }
        }

        public long Length => _length;

        public bool CanAppendSynchronously => true;

        internal byte[] Complete(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            return (_hash ?? throw new ObjectDisposedException(nameof(IdentifierHashSink))).GetHashAndReset();
        }

        internal void AdmitOutputMirror()
            => (_memory ?? throw new ObjectDisposedException(nameof(IdentifierHashSink))).Reserve(64L);

        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            Append(chunk.Span, cancellationToken);
            return ValueTask.CompletedTask;
        }

        public void Append(ReadOnlySpan<byte> chunk, CancellationToken cancellationToken)
        {
            IncrementalHash hash = _hash ?? throw new ObjectDisposedException(nameof(IdentifierHashSink));
            cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            long next = checked(_length + chunk.Length);
            if (next > _limits.MaxOutputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), next, _limits.MaxOutputBytes);
            }
            for (int offset = 0; offset < chunk.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
                int count = Math.Min(32768, chunk.Length - offset);
                _governor.NoteOperation();
                hash.AppendData(chunk.Slice(offset, count));
                offset += count;
            }
            _length = next;
        }

        public void Dispose()
        {
            IncrementalHash? hash = _hash; ParserMemoryScope? memory = _memory;
            _hash = null; _memory = null;
            try { hash?.Dispose(); } finally { memory?.Dispose(); }
        }
    }

    private sealed class IdentifierOutputSink(TransactionSink destination, IdentifierHashSink hash)
        : ISynchronousByteSink, IOutputCapacityHint
    {
        private bool _hashing = true;
        public long Length => destination.Length;
        public bool CanAppendSynchronously => destination.CanAppendSynchronously;
        internal void FinishPrefix() => _hashing = false;

        void IOutputCapacityHint.EnsureCapacity(long length, CancellationToken token)
            => ((IOutputCapacityHint)destination).EnsureCapacity(length, token);

        public void Append(ReadOnlySpan<byte> chunk, CancellationToken token)
        {
            if (_hashing) { hash.Append(chunk, token); }
            destination.Append(chunk, token);
        }

        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            if (_hashing) { hash.Append(chunk.Span, token); }
            return destination.AppendAsync(chunk, token);
        }
    }

    private static Dictionary<int, int> InvertOriginals(IEnumerable<IReadOnlyDictionary<int, int>> maps,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        Dictionary<int, int> inverted = ParserContainers.CreateDictionary<int, int>(candidate);
        foreach (IReadOnlyDictionary<int, int> map in maps)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            foreach (KeyValuePair<int, int> entry in map)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                GovernedCollections.Assign(candidate, governor, inverted, entry.Value, entry.Key, sizeof(int));
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
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
            if (names.Count == names.Capacity) { governor.NoteOperation(); }
            ParserContainers.AddItem(memory, names, name, IntPtr.Size);
        }
        GovernedCollections.SortNames(memory, governor, names);
        foreach (PdfName key in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!xobjects.TryGet(key, out QpdfObject entry) || entry is not PdfReference reference)
            {
                continue;
            }

            if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? target) || target is not PdfStream stream)
            {
                continue;
            }

            if (!PdfDictionaryLookup.HasName(stream.Dictionary, "Subtype"u8, "Image"u8, engine, governor, cancellationToken))
            {
                continue;
            }

            long width = ImageDimension(stream.Dictionary, "Width", reference, engine, diagnostics, governor, cancellationToken);
            long height = ImageDimension(stream.Dictionary, "Height", reference, engine, diagnostics, governor, cancellationToken);
            if (images.Count == images.Capacity) { governor.NoteOperation(); }
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

            governor.NoteOperation();
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
        governor.NoteOperation();
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
        long utf8Bytes,
        int version,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(80L);
        using GovernedBufferStream bytes = new(governor, Array.MaxLength, nameof(QpdfLimits.MaxOutputBytes), cancellationToken, 1024);
        List<string> described = ParserContainers.CreateList<string>(memory);
        EncodeSchemaJson(version == 1 ? HelpText.JsonHelpV1 : HelpText.JsonHelpV2, bytes, governor, cancellationToken);
        System.Text.Json.Utf8JsonReader help = new(bytes.WrittenSpan);
        while (help.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (help.TokenType == System.Text.Json.JsonTokenType.PropertyName && help.CurrentDepth == 1)
            {
                ParserContainers.AddItem(memory, described, ReadSchemaKey(ref help, memory), IntPtr.Size);
            }
        }

        bytes.ResetWrittenBytes();
        if (utf8Bytes > Array.MaxLength)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), utf8Bytes, Array.MaxLength); }
        // The serializer already counted UTF-8 bytes. Admit the final capacity
        // once instead of growing through copies of the complete output.
        bytes.PrepareCapacity(checked((int)utf8Bytes), cancellationToken);
        EncodeSchemaJson(json, bytes, governor, cancellationToken);
        if (bytes.Length != utf8Bytes) { throw new InvalidOperationException("JSON UTF-8 sizing disagrees with encoding."); }
        List<string> missing = ParserContainers.CreateList<string>(memory);
        System.Text.Json.Utf8JsonReader generated = new(bytes.WrittenSpan);
        if (!generated.Read() || generated.TokenType != System.Text.Json.JsonTokenType.StartObject)
        { throw new System.Text.Json.JsonException("JSON schema checking requires an object."); }
        while (generated.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (generated.TokenType != System.Text.Json.JsonTokenType.PropertyName || generated.CurrentDepth != 1) { continue; }
            bool found = false;
            foreach (string key in described)
            {
                // A help key is ASCII. Even fully escaped, a matching token has
                // at most six bytes per character; skip long unmatched names
                // before ValueTextEquals might materialize unescape scratch.
                if (generated.ValueSpan.Length <= 6L * key.Length && generated.ValueTextEquals(key)) { found = true; break; }
            }
            if (!found) { ParserContainers.AddItem(memory, missing, ReadSchemaKey(ref generated, memory), IntPtr.Size); }
        }

        if (missing.Count > 0)
        {
            using DisplayBuffer message = DisplayBuffer.Create(governor, true);
            message.Append("JSON output has keys missing from --json-help version "); message.AppendInteger(version); message.Append(": ");
            for (int index = 0; index < missing.Count; index++)
            {
                if (index != 0) { message.Append(", "); }
                message.Append(missing[index]);
            }
            message.Append('.');
            using GovernedText text = message.Finish();
            diagnostics.AddOwned(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, text);
            return;
        }
        diagnostics.AddParts(QpdfDiagnosticCode.JsonSchemaCheck, QpdfDiagnosticSeverity.Info,
            ["JSON schema check passed for version ", version, "."]);
    }

    private static string ReadSchemaKey(ref System.Text.Json.Utf8JsonReader reader, ParserMemoryScope memory)
    {
        // Fund the decoded UTF-16 string and any UTF-8 unescape scratch before
        // GetString. Names in the fixed help inventory are tiny; unknown output
        // keys remain subject to memory and diagnostic bounds.
        memory.Reserve(checked(GovernedText.StringBytes(reader.ValueSpan.Length) + 32L + 2L * reader.ValueSpan.Length));
        return reader.GetString() ?? string.Empty;
    }

    private static void EncodeSchemaJson(string text, GovernedBufferStream bytes,
        ResourceGovernor governor, CancellationToken token)
    {
        Span<byte> encoded = stackalloc byte[1024];
        ReadOnlySpan<char> remaining = text;
        while (!remaining.IsEmpty)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int count = Math.Min(256, remaining.Length);
            if (count < remaining.Length && char.IsHighSurrogate(remaining[count - 1]) && char.IsLowSurrogate(remaining[count])) { count--; }
            int length = System.Text.Encoding.UTF8.GetBytes(remaining[..count], encoded);
            bytes.Write(encoded[..length], token); remaining = remaining[count..];
        }
    }

    private static async ValueTask PublishModelAsync(
        IQpdfHost host,
        PdfVersion version,
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
            await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, qdf, originalIds, path, limits, governor, cancellationToken, null, false).ConfigureAwait(false);
            return;
        }

        byte[] placeholderId1 = originalId1 is null ? (byte[])PlaceholderIdBytes.Clone() : (byte[])originalId1.Clone();
        byte[] placeholderId2 = (byte[])PlaceholderIdBytes.Clone();
        if (!qdf.Enabled && (objectStreamGroups is null || objectStreamGroups.Count == 0))
        {
            using IdentifierHashSink identifiers = IdentifierHashSink.Create(limits, governor, cancellationToken);
            identifiers.AdmitOutputMirror();
            await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel,
                infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, qdf, originalIds, path, limits,
                governor, cancellationToken, identifiers, originalId1 is null).ConfigureAwait(false);
            return;
        }
        PdfSerializer probe = new(limits, governor);
        using IdentifierHashSink memory = IdentifierHashSink.Create(limits, governor, cancellationToken);
        using BatchingSink hashed = BatchingSink.Create(memory, limits, governor, cancellationToken);
        if (qdf.Enabled)
        {
            await probe.SerializeQdfAsync(version, model, objectStreamGroups, originalIds, infoRef, placeholderId1, placeholderId2, qdf, newlineBeforeEndstream, hashed, cancellationToken).ConfigureAwait(false);
        }
        else if (objectStreamGroups is null || objectStreamGroups.Count == 0)
        {
            await probe.SerializeAsync(version, model, infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, hashed, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await probe.SerializeStreamedAsync(version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, hashed, cancellationToken).ConfigureAwait(false);
        }
        await hashed.FlushAsync(cancellationToken).ConfigureAwait(false);
        hashed.Dispose();
        byte[] digest = memory.Complete(cancellationToken);
        memory.Dispose();
        id2 = digest;
        id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
        await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, qdf, originalIds, path, limits, governor, cancellationToken, null, false).ConfigureAwait(false);
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
        if (linearizePass1 == path)
        {
            throw new QpdfParseException("The first-pass output must differ from every main PDF output.", null, null);
        }

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
            using IdentifierHashSink memory = IdentifierHashSink.Create(limits, governor, cancellationToken);
            using BatchingSink hashed = BatchingSink.Create(memory, limits, governor, cancellationToken);
            if (objectStreamGroups is not null && objectStreamGroups.Count > 0)
            {
                await probe.WriteLinearizedStreamedAsync(version, model, objectStreamGroups, infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, compressHints, compressionLevel, hashed, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await probe.WriteLinearizedAsync(version, model, infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, compressHints, compressionLevel, hashed, null, cancellationToken).ConfigureAwait(false);
            }
            await hashed.FlushAsync(cancellationToken).ConfigureAwait(false);
            byte[] digest = memory.Complete(cancellationToken);
            id2 = digest;
            id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
        }

        using ParserMemoryScope staging = ParserMemoryScope.Create(governor);
        await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
        TransactionSink destination = new(transaction, governor, staging, limits);
        using BatchingSink sink = BatchingSink.Create(destination, limits, governor, cancellationToken);
        if (objectStreamGroups is not null && objectStreamGroups.Count > 0)
        {
            if (linearizePass1 is null)
            {
                await writer.WriteLinearizedStreamedAsync(version, model, objectStreamGroups, infoRef, id1, id2, newlineBeforeEndstream, compressHints, compressionLevel, sink, null, cancellationToken).ConfigureAwait(false);
                await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await using IQpdfOutputTransaction streamedPass1 = await GovernedHost.CreateOutputAsync(host, linearizePass1.Value, governor, cancellationToken).ConfigureAwait(false);
            TransactionSink streamedPass1Destination = new(streamedPass1, governor);
            using BatchingSink streamedPass1Sink = BatchingSink.Create(streamedPass1Destination, limits, governor, cancellationToken);
            await writer.WriteLinearizedStreamedAsync(version, model, objectStreamGroups, infoRef, id1, id2, newlineBeforeEndstream, compressHints, compressionLevel, sink, streamedPass1Sink, cancellationToken).ConfigureAwait(false);
            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            await streamedPass1Sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await streamedPass1.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (linearizePass1 is null)
        {
            await writer.WriteLinearizedAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream, compressHints, compressionLevel, sink, null, cancellationToken).ConfigureAwait(false);
            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using IQpdfOutputTransaction pass1Transaction = await GovernedHost.CreateOutputAsync(host, linearizePass1.Value, governor, cancellationToken).ConfigureAwait(false);
        TransactionSink pass1Destination = new(pass1Transaction, governor);
        using BatchingSink pass1Sink = BatchingSink.Create(pass1Destination, limits, governor, cancellationToken);
        await writer.WriteLinearizedAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream, compressHints, compressionLevel, sink, pass1Sink, cancellationToken).ConfigureAwait(false);
        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        await pass1Sink.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        IdentifierHashSink? identifiers,
        bool deriveFirstId)
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

        governor.ReserveHostMemory(counted);
        try
        {
            await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
            TransactionSink destination = new(transaction, governor);
            IByteSink target = identifiers is null ? destination : new IdentifierOutputSink(destination, identifiers);
            using BatchingSink sink = BatchingSink.Create(target, limits, governor, cancellationToken);
            ((IOutputCapacityHint)sink).EnsureCapacity(counted, cancellationToken);
            long written;
            if (identifiers is not null)
            {
                (int size, long xrefOffset) = await serializer.SerializeClassicBodyAsync(version, model,
                    newlineBeforeEndstream, sink, cancellationToken).ConfigureAwait(false);
                // Include the bounded pending prefix without forcing an extra
                // host write. The real footer keeps the same lengths/offsets.
                sink.AppendPendingTo(identifiers, cancellationToken);
                ((IdentifierOutputSink)target).FinishPrefix();
                await serializer.AppendClassicTrailerAsync(size, infoRef, id1, id2, xrefOffset,
                    identifiers, cancellationToken).ConfigureAwait(false);
                if (identifiers.Length != counted)
                { throw new QpdfException($"Identifier replay mismatch: counted {counted} bytes but hashed {identifiers.Length}."); }
                byte[] digest = identifiers.Complete(cancellationToken);
                digest.CopyTo(id2, 0);
                if (deriveFirstId) { digest.CopyTo(id1, 0); }
                await serializer.AppendClassicTrailerAsync(size, infoRef, id1, id2, xrefOffset,
                    sink, cancellationToken).ConfigureAwait(false);
                written = sink.Length;
            }
            else if (qdf.Enabled)
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

            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { governor.ReleaseHostMemory(counted); }
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

    private static (PdfVersion Version, int Extension) NegotiateVersion(PdfVersion inputMax, int inputExtension, string? minText, string? forceText, ResourceGovernor governor)
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
            return (inputMax, inputExtension);
        }

        (int minMajor, int minMinor, int minExt) = ParseWriterVersion(minText, "--min-version");
        int order = CompareVersions(inputMax.Major, inputMax.Minor, minMajor, minMinor);
        if (order > 0)
        {
            return (inputMax, inputExtension);
        }

        if (order < 0)
        {
            return (governor.OwnVersion(minMajor, minMinor), minExt);
        }

        return (inputMax, Math.Max(inputExtension, minExt));
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

        if (array[0] is not PdfByteString first || first.ValueMemory.Length == 0 || first.ValueMemory.Length > 256)
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

    private static HashSet<int> CollectCompressedOriginals(DocumentEngine engine, bool preserve,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        HashSet<int> compressed = ParserContainers.CreateSet<int>(candidate);
        if (preserve)
        {
            foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (entry.Value.Kind == XrefEntryKind.Compressed)
                {
                    ParserContainers.AddSetItem(candidate, compressed, entry.Key, sizeof(int));
                }
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return compressed;
    }

    private static bool IsCompressibleValue(int number, QpdfObject value, ResourceGovernor governor, CancellationToken token)
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
            if (PdfDictionaryLookup.TryGet(dictionary, "Type"u8, governor, token, out QpdfObject typeRaw)
                && typeRaw is PdfName typeName && typeName.RawSpan.SequenceEqual("Sig"u8)
                && PdfDictionaryLookup.TryGet(dictionary, "ByteRange"u8, governor, token, out QpdfObject byteRange)
                && byteRange is not PdfNull
                && PdfDictionaryLookup.TryGet(dictionary, "Contents"u8, governor, token, out QpdfObject contents)
                && contents is not PdfNull)
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
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
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

        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        HashSet<int> compressed = ParserContainers.CreateSet<int>(candidate);
        if (mode == "disable")
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            candidate.TransferTo(ownership);
            return compressed;
        }

        if (mode == "generate")
        {
            foreach (KeyValuePair<int, QpdfObject> entry in pool)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (IsCompressibleValue(entry.Key, entry.Value, governor, token))
                {
                    ParserContainers.AddSetItem(candidate, compressed, entry.Key, sizeof(int));
                }
            }

            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            candidate.TransferTo(ownership);
            return compressed;
        }

        if (mode != "preserve")
        {
            throw new QpdfParseException("--object-streams mode must be disable, preserve, or generate.", 0L, null);
        }

        foreach ((IReadOnlyDictionary<int, int> map, HashSet<int> origs) in sources)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            foreach (int orig in origs)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (map.TryGetValue(orig, out int dest)
                    && pool.TryGetValue(dest, out QpdfObject? value)
                    && value is not null
                    && IsCompressibleValue(dest, value, governor, token))
                {
                    ParserContainers.AddSetItem(candidate, compressed, dest, sizeof(int));
                }
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return compressed;
    }

    private static List<List<int>> PlanObjectStreamGroups(HashSet<int> compressed,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        if (compressed is null)
        {
            throw new ArgumentNullException(nameof(compressed));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        List<List<int>> groups = ParserContainers.CreateList<List<int>>(candidate);
        if (compressed.Count == 0)
        {
            candidate.TransferTo(ownership);
            return groups;
        }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(64L + sizeof(int) * (long)compressed.Count);
        List<int> ordered = new(compressed.Count);
        foreach (int number in compressed)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); ordered.Add(number);
        }
        GovernedSort.Sort(CollectionsMarshal.AsSpan(ordered), new WriterNumberOrder(), governor, token);
        int streamCount = 1 + (ordered.Count - 1) / 100;
        int perStream = 1 + (ordered.Count - 1) / streamCount;
        for (int i = 0; i < ordered.Count; i += perStream)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int take = Math.Min(perStream, ordered.Count - i);
            candidate.Reserve(64L + sizeof(int) * (long)take);
            List<int> group = new(take);
            for (int index = i; index < i + take; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation(); group.Add(ordered[index]);
            }
            ParserContainers.AddItem(candidate, groups, group, IntPtr.Size);
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return groups;
    }

    private static HashSet<int> RemapCompressedSet(HashSet<int> compressed, Dictionary<int, int> renumber,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        if (compressed is null)
        {
            throw new ArgumentNullException(nameof(compressed));
        }

        if (renumber is null)
        {
            throw new ArgumentNullException(nameof(renumber));
        }

        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        HashSet<int> remapped = ParserContainers.CreateSet<int>(candidate);
        foreach (int id in compressed)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (renumber.TryGetValue(id, out int mapped))
            {
                ParserContainers.AddSetItem(candidate, remapped, mapped, sizeof(int));
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
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

    private static HashSet<int> PreservationExclusions(
        DocumentEngine engine,
        IReadOnlyList<PageLocation> kept,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
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
        memory.CheckOwner(governor);
        HashSet<int> excluded = ParserContainers.CreateSet<int>(memory);
        HashSet<int> visited = ParserContainers.CreateSet<int>(memory);
        if (engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) && root is PdfReference rootReference)
        {
            ParserContainers.AddSetItem(memory, excluded, rootReference.ObjectNumber, sizeof(int));
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        PdfName parentKey = PdfName.FromAscii("Parent");
        foreach (PageLocation location in kept)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            PdfReference? source = location.Source;
            if (source is null)
            {
                continue;
            }

            visited.Clear(); ParserContainers.AddSetItem(memory, visited, source.ObjectNumber, sizeof(int));
            ParserContainers.AddSetItem(memory, excluded, source.ObjectNumber, sizeof(int));
            PdfReference current = source;
            int depth = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
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

                if (!ParserContainers.AddSetItem(memory, visited, next.ObjectNumber, sizeof(int)))
                {
                    break;
                }

                ParserContainers.AddSetItem(memory, excluded, next.ObjectNumber, sizeof(int));
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

    private static List<PageLocation> KeptLocations(QpdfDocument document, IReadOnlyList<int> requested,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (requested is null)
        {
            throw new ArgumentNullException(nameof(requested));
        }

        memory.CheckOwner(governor); cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        List<PageLocation> kept = ParserContainers.CreateList<PageLocation>(memory, requested.Count, 16L);
        foreach (int pageNumber in requested)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
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
            catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException))
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
            catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException))
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
            catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException))
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
        bool preserve,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
    {
        ownership.CheckOwner(governor);
        ownership.Reserve(96L);
        SortedDictionary<string, PdfReference> kept = new(StringComparer.Ordinal);
        if (edits.HasEdits || preserve)
        {
            List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(engine, limits, governor, diagnostics, ownership, cancellationToken);
            foreach (EmbeddedAttachment attachment in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PdfReference? link = copier.AddIndirectObject(attachment.Spec);
                    if (link is not null)
                    {
                        if (!kept.ContainsKey(attachment.Key)) { ownership.Reserve(64L); }
                        kept[attachment.Key] = link;
                    }
                }
                catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException))
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
        bool preserveObjectStreams,
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

        HashSet<int> ids = ParserContainers.CreateSet<int>(ownership);
        if (!edits.HasEdits && existing.Count == 0)
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

        bool duplicateKeys = false;
        foreach (AttachmentAdd add in edits.Adds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
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
                duplicateKeys = true;
                continue;
            }

            byte[] data = await ReadAttachmentFileAsync(host, add.File, limits, governor, ownership, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (add.MimeType is not null && !IsAscii(add.MimeType, governor, cancellationToken))
            {
                throw new QpdfParseException("Attachment MIME type must be ASCII.", 0L, null);
            }

            byte[] checksum = AttachmentBuilder.Checksum(data);
            byte[] stored = data;
            if (compress && data.Length > 0)
            {
                stored = FlateFilter.Encode(data, level, limits, governor, ownership, cancellationToken);
            }

            PdfDictionary streamDict = AttachmentBuilder.BuildStreamDictionary(data.Length, checksum, add.MimeType, add.CreationDate, add.ModDate, compress && data.Length > 0, governor, ownership, cancellationToken);
            int streamId = nextId;
            checked
            {
                nextId += 1;
            }

            ownership.Reserve(64L);
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size);
            pool[streamId] = PdfStream.FromOwnedData(streamDict, stored);
            ParserContainers.AddSetItem(ownership, ids, streamId, sizeof(int));
            PdfDictionary spec = AttachmentBuilder.BuildFilespecDictionary(fileName, add.Description, streamId, governor, ownership, cancellationToken);
            int specId = nextId;
            checked
            {
                nextId += 1;
            }

            ownership.Reserve(128L);
            ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size);
            pool[specId] = spec;
            ParserContainers.AddSetItem(ownership, ids, specId, sizeof(int));
            if (!existing.ContainsKey(key)) { ownership.Reserve(64L); }
            existing[key] = new PdfReference(specId, 0);
        }

        if (duplicateKeys)
        {
            throw new QpdfParseException("Attachments with conflicting keys already exist; use --replace to replace or --key to specify a different key.", 0L, null);
        }

        bool copiedAny = false;
        string? firstCopyConflict = null;
        bool moreCopyConflicts = false;
        foreach (AttachmentCopy copy in edits.Copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            await using QpdfDocument source = await QpdfDocument.OpenCore(host, SelectionPlan.CheckedPath(copy.File), limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
            List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(source.Engine, limits, governor, diagnostics, ownership, cancellationToken);
            GraphCopier copyCopier = GraphCopier.CreateOwned(source.Engine, governor, limits, nextId, ownership);
            foreach (EmbeddedAttachment attachment in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                string newKey = AttachmentCopyKey(copy.Prefix, attachment.Key, governor, ownership, cancellationToken);
                if (existing.ContainsKey(newKey))
                {
                    firstCopyConflict ??= copy.File;
                    moreCopyConflicts |= !string.Equals(firstCopyConflict, copy.File, StringComparison.Ordinal);

                    continue;
                }

                PdfReference? link;
                try
                {
                    link = copyCopier.AddIndirectObject(attachment.Spec);
                }
                catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException))
                {
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable attachment.");
                    continue;
                }

                if (link is null)
                {
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable attachment.");
                    continue;
                }

                ownership.Reserve(64L); // SortedDictionary node; key text is funded separately.
                existing[newKey] = link;
                copiedAny = true;
            }

            nextId = copyCopier.NextId;
            // Final writer models order object numbers; no sorted key snapshot is needed here.
            foreach ((int id, QpdfObject value) in copyCopier.CopiedObjects)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.NoteOperation();
                ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size);
                pool[id] = value;
            }

            ParserContainers.AddItem(ownership, compressibleSources,
                (copyCopier.SourceToDestination, CollectCompressedOriginals(source.Engine, preserveObjectStreams, ownership, governor, cancellationToken)), 2L * IntPtr.Size);
            source.Engine.TransferExclusiveValuesTo(ownership);
        }

        if (firstCopyConflict is not null)
        {
            throw AttachmentCopyConflict(firstCopyConflict, moreCopyConflicts, governor, ownership, cancellationToken);
        }

        if (existing.Count == 0)
        {
            return new AttachmentAssembly(null, inputPageMode, ids, 0);
        }

        PdfDictionary tree = AttachmentBuilder.BuildNameTree(existing, governor, ownership, cancellationToken);
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
        ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size);
        pool[rootId] = wrapper;
        ParserContainers.AddSetItem(ownership, ids, rootId, sizeof(int));
        string? pageMode = edits.Adds.Count > 0 || copiedAny ? inputPageMode ?? "UseAttachments" : inputPageMode;
        return new AttachmentAssembly(new PdfReference(rootId, 0), pageMode, ids, existing.Count);
    }

    private static string AttachmentCopyKey(string? prefix, string key, ResourceGovernor governor,
        ParserMemoryScope memory, CancellationToken token)
    {
        if (string.IsNullOrEmpty(prefix)) { return key; }
        if (key.Length == 0) { return prefix; }
        int length = checked(prefix.Length + key.Length);
        memory.Reserve(GovernedText.StringBytes(length));
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return string.Create(length, (prefix, key, governor, token), static (target, state) =>
        {
            int offset = 0;
            while (offset < target.Length)
            {
                state.token.ThrowIfCancellationRequested(); state.governor.CheckCancellation();
                ReadOnlySpan<char> source = offset < state.prefix.Length
                    ? state.prefix.AsSpan(offset) : state.key.AsSpan(offset - state.prefix.Length);
                int count = Math.Min(256, source.Length);
                source[..count].CopyTo(target.Slice(offset, count));
                offset += count;
            }
        });
    }

    private static QpdfParseException AttachmentCopyConflict(string firstFile, bool moreFiles,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        // One source, at most 256 UTF-16 units, plus an explicit overflow marker.
        // Fund both the message and QpdfParseException's bounded offset suffix copy.
        memory.Reserve(2L * GovernedText.StringBytes(512));
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        Span<char> message = stackalloc char[512];
        const string start = "Attachments copied from '";
        start.AsSpan().CopyTo(message);
        int length = start.Length;
        int take = Math.Min(firstFile.Length, 256);
        if (take < firstFile.Length && char.IsHighSurrogate(firstFile[take - 1])) { take--; }
        firstFile.AsSpan(0, take).CopyTo(message[length..]); length += take;
        if (take < firstFile.Length) { "...".AsSpan().CopyTo(message[length..]); length += 3; }
        string end = moreFiles
            ? "' (additional source paths omitted) conflict with existing keys; use --prefix to specify a key prefix."
            : "' conflict with existing keys; use --prefix to specify a key prefix.";
        end.AsSpan().CopyTo(message[length..]); length += end.Length;
        return new QpdfParseException(new string(message[..length]), 0L, null);
    }

    private static bool IsAscii(string text, ResourceGovernor governor, CancellationToken token)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 255) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
            if (text[index] > 127)
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
            byte[] bytes = await QpdfInputReader.ReadAdmittedAsync(input, 0L, (int)input.Length, governor, cancellationToken).ConfigureAwait(false);
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

        await using QpdfDocument document = await OpenPlanInputAsync(host, plan.Input, plan.EmptyDocument, limits, governor, diagnostics, plan.Recovery, cancellationToken, false).ConfigureAwait(false);
        int total = document.PageReferences.Count;
        if ((long)total > limits.MaxContainerSize)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), total, limits.MaxContainerSize);
        }

        List<PdfPageSource> solo = new(1)
        {
            new PdfPageSource(plan.Input, PdfPageSelectionOptions.FromRange(null)),
        };
        QpdfObject? verbatimTree = plan.Labels.Count == 0 && !plan.RemoveLabels && !plan.SplitPages.HasValue
            ? Content.PageLabelMerge.TryGetRawTree(document.Engine)
            : null;
        // The shared selection core consumes this same stable source snapshot,
        // transfers required values and closes its lease before publication.
        return await SelectPagesCoreAsync(
            host: host,
            sources: solo,
            outputPath: plan.Output,
            primaryInput: plan.Input,
            pruneUnreachableFields: false,
            limits: limits,
            recovery: plan.Recovery,
            settings: SelectionSettings.FromPlan(plan, pruneEnabled: plan.SplitPages.HasValue) with { PreserveAttachments = true },
            collation: null,
            verbatimLabels: verbatimTree,
            splitPages: plan.SplitPages,
            replaceInput: plan.ReplaceInput,
            governor: governor,
            diagnostics: diagnostics,
            emptyPrimary: false,
            preparedSource: document,
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
        ResourceGovernor governor = new(limits, cancellationToken, host);
        DiagnosticBag diagnostics = new(governor);
        try { return await ListAttachmentsCoreAsync(host, inputPath, limits, QpdfRecoveryMode.Strict, false, governor, diagnostics, true, cancellationToken).ConfigureAwait(false); }
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
        ResourceGovernor governor = new(limits, cancellationToken, host);
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
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfJsonResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            JsonInspectionOutcome result;
            using (ConfigurationText? textStorage = options.Objects.Count == 0 ? null : ConfigurationText.Create(governor))
            {
                HashSet<(bool Trailer, int Number, int Generation)>? wanted = ValidateJsonObjects(options.Objects, 0L, textStorage);
                Commands.JsonInspection inspection = new(options.Version, options.Keys, null, options.StreamData, options.StreamPrefix, wanted, options.MinimalOutput);
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
        ResourceGovernor governor = new(limits, cancellationToken, host);
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
        ResourceGovernor governor = new(limits, cancellationToken, host);
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
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0) { throw new ArgumentException("At least one attachment is required.", nameof(attachments)); }
        return EditAttachmentsAsync(host, inputPath, outputPath, attachments, null, null, limits, cancellationToken);
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

        return EditAttachmentsAsync(host, inputPath, outputPath, null, keys, null, limits, cancellationToken);
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

        return EditAttachmentsAsync(host, inputPath, outputPath, null, null, sources, limits, cancellationToken);
    }

    private static IReadOnlyList<AttachmentAdd> ToAttachmentAdds(IReadOnlyList<QpdfAttachmentSpec> attachments,
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

    private static async ValueTask<QpdfAttachmentEditResult> EditAttachmentsAsync(
        IQpdfHost host, QpdfPath inputPath, QpdfPath outputPath,
        IReadOnlyList<QpdfAttachmentSpec>? attachments, IReadOnlyList<string>? keys, IReadOnlyList<QpdfAttachmentCopy>? sources,
        QpdfLimits limits, CancellationToken cancellationToken)
    {
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
                        if (key.Length == 0) { throw new ArgumentException("Attachment keys must not be empty.", nameof(keys)); }
                        removes.Add(key);
                    }
                }
                List<AttachmentCopy>? copies = null;
                if (sources is not null)
                {
                    int count = sources.Count;
                    requests.Reserve(checked(64L + 40L * count));
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    copies = new(count);
                    for (int index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        QpdfAttachmentCopy source = sources[index];
                        if (source is null) { throw new ArgumentException("Attachment sources must not contain null entries.", nameof(sources)); }
                        copies.Add(new AttachmentCopy(source.Input.Value, source.Prefix));
                    }
                }
                WholeFilePlan plan = new(inputPath, outputPath, QpdfRecoveryMode.Strict)
                {
                    AttachmentsToAdd = adds, AttachmentsToRemove = removes is null ? [] : removes,
                    AttachmentsToCopy = copies is null ? [] : copies,
                };
                DiagnosticBag diagnostics = new(governor);
                (result, attachmentCount) = await WholeFileCoreAsync(host, plan, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            QpdfAttachmentEditResult edited = new(attachmentCount, result.Warnings, governor.TypedCounters, true);
            governor.AcceptTypedResult(); governor.FinishTypedCounters(); return edited;
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
        using ParserMemoryScope attachmentMemory = ParserMemoryScope.Create(governor);
        ReadOnlyMemory<byte> data = ReadAttachmentData(document, key, limits, governor, diagnostics, attachmentMemory, cancellationToken);
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

    internal static HashSet<(bool Trailer, int Number, int Generation)>? ValidateJsonObjects(
        IReadOnlyList<string> objects, long index, ConfigurationText? textStorage)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Count == 0) { return null; }
        ConfigurationText owner = textStorage ?? throw new InvalidOperationException("Missing JSON selection owner.");
        HashSet<(bool Trailer, int Number, int Generation)>? selected = null;
        foreach (string spec in objects) { selected = AddJsonObject(selected, spec, index, owner); }
        return selected;
    }

    internal static HashSet<(bool Trailer, int Number, int Generation)> AddJsonObject(
        HashSet<(bool Trailer, int Number, int Generation)>? selected, string spec, long index, ConfigurationText owner)
    {
        ResourceGovernor governor = owner.Governor;
        governor.CheckCancellation();
        if (spec is null) { throw new QpdfParseException("JSON object selectors must not be null.", index, null); }
        bool trailer = string.Equals(spec, "trailer", StringComparison.Ordinal);
        int number = 0, generation = 0;
        if (!trailer)
        {
            // Two ten-digit integers and a comma are the longest valid spelling.
            if (spec.Length > 21) { throw InvalidJsonObject(spec, index, owner); }
            int comma = spec.IndexOf(',');
            if (!TryParseObjectNumber(comma < 0 ? spec.AsSpan() : spec.AsSpan(0, comma), out number)
                || comma >= 0 && !TryParseObjectNumber(spec.AsSpan(comma + 1), out generation))
            { throw InvalidJsonObject(spec, index, owner); }
        }
        selected ??= ParserContainers.CreateSet<(bool Trailer, int Number, int Generation)>(owner.Memory);
        ParserContainers.AddSetItem(owner.Memory, selected, (trailer, number, generation), 12L);
        return selected;
    }

    private static QpdfParseException InvalidJsonObject(string spec, long index, ConfigurationText owner)
    {
        // Bound the excerpt before formatting and fund the offset suffix copy too.
        owner.Memory.Reserve(2L * GovernedText.StringBytes(256));
        owner.Governor.CheckCancellation();
        Span<char> message = stackalloc char[256];
        const string start = "JSON object '";
        start.AsSpan().CopyTo(message);
        int length = start.Length, take = Math.Min(spec.Length, 128);
        if (take < spec.Length && char.IsHighSurrogate(spec[take - 1])) { take--; }
        spec.AsSpan(0, take).CopyTo(message[length..]); length += take;
        if (take < spec.Length) { "...".AsSpan().CopyTo(message[length..]); length += 3; }
        const string end = "' must be trailer or obj[,gen].";
        end.AsSpan().CopyTo(message[length..]); length += end.Length;
        return new QpdfParseException(new string(message[..length]), index, null);
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
        // The configuration owner funds this immutable selection through execution.
        HashSet<(bool Trailer, int Number, int Generation)>? wanted = inspection.Objects;
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
                CheckJsonSchema(text.Value, text.Utf8Bytes, inspection.Version, governor, diagnostics, cancellationToken);
                await document.DisposeAsync().ConfigureAwait(false);
            }
            output.Prepare(text, outputPath.HasValue, escapeText, limits);
        }
        List<Json.JsonPayload> sidecars = output.Sidecars;
        long mainBytes = output.Main?.Data.Length ?? 0L;

        long anticipatedBytes = mainBytes;
        long publicationOperations = outputPath.HasValue ? 1L + (mainBytes + 32767L) / 32768L : 0L;
        foreach (Json.JsonPayload sidecar in sidecars)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (string.Equals(sidecar.Name, inputPath.Value, StringComparison.Ordinal))
            { throw new QpdfParseException("A JSON stream file must differ from the input file.", 0L, null); }
            if (string.Equals(sidecar.Name, outputPath?.Value, StringComparison.Ordinal))
            { throw new QpdfParseException("A JSON stream file must differ from the main JSON output.", 0L, null); }
            _ = QpdfPath.From(sidecar.Name);
            anticipatedBytes = checked(anticipatedBytes + sidecar.Data.Length);
            publicationOperations = checked(publicationOperations + 1L + (sidecar.Data.Length + 32767L) / 32768L);
        }
        governor.ChargeOutputBytes(anticipatedBytes);
        using PublicationReservation publicationAdmission = PublicationReservation.Create(governor, publicationOperations);
        governor.PrepareExecutionPresentation();
        publicationAdmission.Begin();
        if (outputPath.HasValue)
        { await PublishPreparedBytesAsync(host, outputPath.Value, output.Main ?? throw new InvalidOperationException("Missing JSON publication."), governor, cancellationToken).ConfigureAwait(false); }
        foreach (Json.JsonPayload sidecar in sidecars)
        { await PublishPreparedBytesAsync(host, QpdfPath.From(sidecar.Name), sidecar, governor, cancellationToken).ConfigureAwait(false); }
        return new JsonInspectionOutcome(output.TransferText(), ResultWarningSnapshot.Read(resultWarnings, diagnostics));



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

        using ParserMemoryScope importMemory = ParserMemoryScope.Create(governor);
        System.Text.Json.JsonDocument parsed = await Json.JsonImport.ReadJsonAsync(host, jsonPath, limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
        using (parsed)
        {
            Json.ImportedModel model = await Json.JsonImport.BuildCreateModelAsync(host, parsed, limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
            (model, PdfVersion effective) = PrepareImportedVersion(model, minVersion, forceVersion, governor, cancellationToken);
            Json.JsonImport.RenumberedModel renumbered = Json.JsonImport.Renumber(model, limits, governor, importMemory, cancellationToken);
            await PublishModelAsync(host, effective, renumbered.Model, null, false, null, renumbered.InfoNumber, null, staticId, newlineBeforeEndstream, Writing.QdfOptions.Disabled, null, false, null, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
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
        using ParserMemoryScope importMemory = ParserMemoryScope.Create(governor);
        System.Text.Json.JsonDocument parsed = await Json.JsonImport.ReadJsonAsync(host, jsonPath, limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
        using (parsed)
        {
            Json.ImportedModel model = await Json.JsonImport.BuildUpdateModelAsync(host, parsed, document, limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
            (model, PdfVersion effective) = PrepareImportedVersion(model, minVersion, forceVersion, governor, cancellationToken);
            Json.JsonImport.RenumberedModel renumbered = Json.JsonImport.Renumber(model, limits, governor, importMemory, cancellationToken);
            byte[]? originalId1 = TryGetOriginalId(document.Engine);
            await PublishModelAsync(host, effective, renumbered.Model, null, false, null, renumbered.InfoNumber, originalId1, staticId, newlineBeforeEndstream, Writing.QdfOptions.Disabled, null, false, null, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
            return new JsonImportOutcome(renumbered.PageCount, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
        }
    }

    private static (Json.ImportedModel Model, PdfVersion Version) PrepareImportedVersion(Json.ImportedModel model,
        string? minVersion, string? forceVersion, ResourceGovernor governor, CancellationToken token)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        IIndirectResolver resolver = Content.PoolResolver.Create(graph.Memory, model.Objects);
        model.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root);
        PdfDictionary? catalog = Content.CatalogCopy.ResolveDictionary(root, resolver, governor, token);
        int level = Content.CatalogCopy.ReadExtensionLevel(catalog, resolver, governor, token);
        (PdfVersion version, int extensionLevel) = NegotiateVersion(model.HeaderVersion, level, minVersion, forceVersion, governor);
        if (catalog is not null)
        {
            PdfDictionaryLookup.TryGet(catalog, "Extensions"u8, governor, token, out QpdfObject original);
            QpdfObject? extensions = PrepareExtensions(original, resolver, version, extensionLevel, graph);
            if (!ReferenceEquals(extensions, original))
            {
                PdfDictionary replacement = extensions is null ? PdfDictionaryRebuild.RemoveEntry(graph, catalog, "Extensions"u8)
                    : PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "Extensions"u8, extensions);
                if (root is PdfReference reference) { model.Objects[reference.ObjectNumber] = replacement; }
                else
                {
                    PdfDictionary trailer = PdfDictionaryRebuild.ReplaceEntry(graph, model.Trailer, "Root"u8, replacement);
                    graph.Memory.Reserve(48L); graph.Add(48L);
                    model = new(model.HeaderVersion, model.Objects, trailer);
                }
            }
        }
        graph.Retain(); return (model, version);
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
        await using (QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false))
        {
            checks = DocumentCheck.Run(document, options, limits, governor, diagnostics, false, cancellationToken);
            pageCount = document.PageCount;
            version = document.OwnVersion();
            if (showNpages && !checks.Exists(static outcome => outcome.Status == QpdfCheckStatus.Failed))
            {
                AddPageCountDiagnostic(document, inputPath, diagnostics);
            }
        }
        using OwnedAllocationBatch resultStorage = OwnedAllocationBatch.Create(governor, cancellationToken);
        IReadOnlyList<QpdfCheckOutcome> resultChecks = ResultCollectionStorage.Copy(resultStorage, checks, IntPtr.Size);
        IReadOnlyList<QpdfDiagnostic> resultDiagnostics = ResultCollectionStorage.Copy(resultStorage, diagnostics.Entries, IntPtr.Size);
        resultStorage.RetainResult();
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

    private static readonly PdfName WriterMediaBox = PdfName.FromAscii("MediaBox");
    private static readonly PdfName[] WriterInherited =
        [PdfName.FromAscii("Resources"), WriterMediaBox, PdfName.FromAscii("CropBox"), PdfName.FromAscii("Rotate")];

    private static IReadOnlyDictionary<PdfName, QpdfObject> MaterializeAncestors(
        DocumentEngine engine, PageLocation location, GraphCopier copier,
        ResourceGovernor governor, QpdfLimits limits, CancellationToken cancellationToken,
        ParserMemoryScope ownership)
    {
        ownership.CheckOwner(governor);
        Dictionary<PdfName, QpdfObject>? extra = null;
        PageInheritance.InheritedValues inherited = PageInheritance.FindAncestors(engine, location, WriterInherited,
            limits, governor, cancellationToken);
        for (int index = 0; index < WriterInherited.Length; index++)
        {
            governor.CheckCancellation();
            PdfName key = WriterInherited[index];
            QpdfObject? value = inherited[index];
            if (value is null) { continue; }
            QpdfObject copied = copier.CopyValue(value, 0);
            if (copied is PdfNull) { continue; }
            extra ??= ParserContainers.CreateDictionary<PdfName, QpdfObject>(ownership);
            ParserContainers.EnsureEntryCapacity(ownership, extra, IntPtr.Size);
            extra[key] = copied;
        }
        return extra is null ? System.Collections.ObjectModel.ReadOnlyDictionary<PdfName, QpdfObject>.Empty : extra;
    }

    private static void RequireMediaBox(PdfDictionary page)
    {
        if (!page.TryGet(WriterMediaBox, out QpdfObject box) || box is PdfNull)
        {
            throw new QpdfParseException("Selected page has no /MediaBox in its ancestry.", null, null);
        }
    }

    private sealed class TransactionSink : ISynchronousByteSink, IOutputCapacityHint
    {
        private readonly IQpdfOutputTransaction _transaction;
        private readonly ResourceGovernor _governor;
        private readonly ParserMemoryScope? _staging;
        private readonly long _maximumOutputBytes = long.MaxValue;
        private long _capacityCharge;
        private long _length;

        internal TransactionSink(IQpdfOutputTransaction transaction, ResourceGovernor governor)
        {
            _transaction = transaction;
            _governor = governor;
        }

        internal TransactionSink(IQpdfOutputTransaction transaction, ResourceGovernor governor,
            ParserMemoryScope staging, QpdfLimits limits) : this(transaction, governor)
        {
            _staging = staging;
            _maximumOutputBytes = limits.MaxOutputBytes;
        }

        void IOutputCapacityHint.EnsureCapacity(long length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            if (length > _maximumOutputBytes)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, _maximumOutputBytes); }
            if (_staging is not null && length > _capacityCharge)
            {
                _staging.ReserveHostMemory(length - _capacityCharge);
                _capacityCharge = length;
            }
            GovernedHost.PrepareOutputCapacity(_transaction, length, cancellationToken);
        }

        public long Length => _length;

        public bool CanAppendSynchronously => GovernedHost.SupportsSynchronousWrite(_transaction);

        public void Append(ReadOnlySpan<byte> chunk, CancellationToken cancellationToken)
        {
            _governor.ChargeOutputBytes(chunk.Length);
            GovernedHost.WriteSynchronously(_transaction, chunk, cancellationToken);
            _length = checked(_length + chunk.Length);
        }

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
        IQpdfHost host, QpdfPath inputPath, QpdfLimits limits, QpdfRecoveryMode recovery, bool emptyDocument,
        ResourceGovernor governor, DiagnosticBag diagnostics, bool returnMetadata, CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using ParserMemoryScope? results = returnMetadata ? ParserMemoryScope.Create(governor) : null;
        await using QpdfDocument document = await OpenPlanInputAsync(host, inputPath, emptyDocument, limits, governor,
            diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
        IReadOnlyList<QpdfAttachmentInfo> infos = ListAttachments(document, inputPath, limits, governor, diagnostics,
            memory, results, cancellationToken);
        await document.DisposeAsync().ConfigureAwait(false);
        if (results is not null) { results.RetainCallerStorage(results.ReservedBytes); }
        return infos;
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
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        await ShowObjectAsync(host, document, showObject, limits, governor, diagnostics, memory, cancellationToken).ConfigureAwait(false);
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
        ExecutionPresentation? presentation = null;
        try
        {
            limits = ResolveParserLimits(plan, limits, hostExplicit);
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
                ShowXref(document, governor, diagnostics, cancellationToken);

                return CompleteWithPresentation(governor);
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
                return CompleteWithPresentation(governor);
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

                return CompleteWithPresentation(governor);
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
                return CompleteWithPresentation(governor);
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

                return CompleteWithPresentation(governor);
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
                    await ListAttachmentsCoreAsync(host, attachments.Input, limits, attachments.Recovery, attachments.EmptyDocument, governor, diagnostics, false, cancellationToken).ConfigureAwait(false);
                }

                if (attachments.Key is not null)
                {
                    QpdfPath output = attachments.Output ?? throw new QpdfParseException("An output file is required after --.", 0L, null);
                    await ExtractAttachmentCoreAsync(host, attachments.Input, attachments.Key, output, limits, attachments.Recovery, attachments.EmptyDocument, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                }

                return CompleteWithPresentation(governor);
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
                return CompleteWithPresentation(governor).WithOutputText(json.Output.HasValue ? null : inspected.Json);
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
                await ImportCreateCoreAsync(
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
                return CompleteWithPresentation(governor);
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
                await ImportUpdateCoreAsync(
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
                return CompleteWithPresentation(governor);
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
                await WholeFileCoreAsync(host, whole, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                return CompleteWithPresentation(governor);
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

                    await SelectPagesCoreAsync(
                        host: host,
                        sources: resolved,
                        outputPath: merge.Output,
                        primaryInput: merge.Input,
                        pruneUnreachableFields: true,
                        limits: limits,
                        recovery: merge.Recovery,
                        settings: SelectionSettings.FromPlan(merge, pruneEnabled: true),
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
                    await SelectPagesCoreAsync(
                        host: host,
                        sources: solo,
                        outputPath: target,
                        primaryInput: selection.Input,
                        pruneUnreachableFields: true,
                        limits: limits,
                        recovery: selection.Recovery,
                        settings: SelectionSettings.FromPlan(selection, pruneEnabled: true),
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

            await SelectPageCoreAsync(
                host: host,
                inputPath: selection.Input,
                outputPath: selection.Output,
                options: new PdfPageSelectionOptions(selection.Page),
                limits: limits,
                recovery: selection.Recovery,
                settings: SelectionSettings.FromPlan(selection, pruneEnabled: true),
                governor: governor,
                diagnostics: diagnostics,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return CompleteWithPresentation(governor);
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

    private static ExecutionOutcome CompleteWithPresentation(ResourceGovernor governor)
    {
        PresentedDiagnostics result = governor.CompleteExecutionPresentation();
        return new ExecutionOutcome(result.ExitCode == QpdfExitCode.Error ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed,
            result.ExitCode, result.Diagnostics);
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

        if (ex is QpdfLimitException or QpdfMemoryAdmissionException)
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
        ResourceGovernor governor = new(limits, cancellationToken, host);
        if (!governor.TryExpectExecutionResult()) { return ExecutionResultAdmission.SharedMemoryFailure; }
        try
        {
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
