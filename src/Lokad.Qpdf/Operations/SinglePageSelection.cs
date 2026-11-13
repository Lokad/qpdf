using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Operations.AttachmentAssembler;
using static Lokad.Qpdf.Operations.PageAssembly;
using static Lokad.Qpdf.Operations.PageLabelAssembly;
using static Lokad.Qpdf.Writing.ObjectStreamPreparation;
using static Lokad.Qpdf.Writing.OriginalObjectIds;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputPublication;
using static Lokad.Qpdf.Writing.OutputSecurityPreparation;
using static Lokad.Qpdf.Writing.OutputVersionPolicy;
using static Lokad.Qpdf.Writing.StreamPreparation;

namespace Lokad.Qpdf.Operations;

internal static class SinglePageSelection
{
    internal static async ValueTask<PageSelectionOutcome> SelectPageCoreAsync(
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
                        ? settings.AuthenticatePrimary
                            ? await QpdfDocument.OpenInspectionCore(host, inputPath, settings.PrimaryPassword, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false)
                            : await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false)
                        : await QpdfDocument.OpenSelectionInputAsync(stableInput, inputPath, limits, governor, diagnostics,
                            recovery, attempt == 0, cancellationToken, settings.PrimaryPassword, settings.AuthenticatePrimary).ConfigureAwait(false);
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
                    bool duplicatePages = false;
                    foreach (int pageNumber in requested)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        PdfReference? reference = document.PageReferences[pageNumber - 1].Source;
                        duplicatePages |= reference is null || copier.SourceToDestination.ContainsKey(reference.ObjectNumber);
                        copier.RegisterPageRoot(reference?.ObjectNumber);
                    }
                    copier.HintPageCapacity(requested.Count);
                    List<PdfReference> kids = ParserContainers.CreateList<PdfReference>(copyMemory, requested.Count, IntPtr.Size);
                    copyMemory.Reserve(checked(24L * requested.Count));
                    List<(GraphCopier Copier, int Id)> occurrences = ParserContainers.CreateList<(GraphCopier, int)>(copyMemory, requested.Count, 16L);
                    PdfReference? outputParent = null;
                    foreach (int pageNumber in requested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        governor.NoteOperation();
                        PageLocation location = document.PageReferences[pageNumber - 1];
                        IReadOnlyDictionary<PdfName, QpdfObject> extra = MaterializeAncestors(document.Engine, location, copier, governor, limits, cancellationToken, copyMemory);
                        if (outputParent is null) { copyMemory.Reserve(32L); outputParent = new PdfReference(2, 0); }
                        (int pageId, PdfDictionary page) = copier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra, false, outputParent);
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

                    (PdfReference? singleInfo, PdfReference? singleMetadata) = CopyDocumentMetadata(copier, document.Engine, settings.RemoveInfo, settings.RemoveMetadata, diagnostics);
                    PdfReference? singleOutlines = Content.CatalogCopy.TryCopyOutlines(document.Engine, copier, settings.PreserveCatalog, settings.Linearize, governor, cancellationToken);
                    (PdfReference? singleStructTree, QpdfObject? singleMarkInfo) = Content.CatalogCopy.TryCopyStructure(document.Engine, copier, settings.PreserveCatalog, settings.RemoveStructure, governor, cancellationToken);
                    (QpdfObject? singlePageMode, IReadOnlyDictionary<PdfName, QpdfObject> singleExtras) = Content.CatalogCopy.TryCopyExtras(document.Engine, copier, settings.PreserveCatalog, settings.RemoveAcroform, governor, copyMemory, cancellationToken);
                    SortedDictionary<string, AttachmentReference> singleExisting = PreserveExistingAttachments(copier, document.Engine, settings.Edits, limits, governor, diagnostics, copyMemory, cancellationToken);

                    (bool singleCompress, string singleDecode, int? singleLevel) = ResolveCompression(settings.Compression, settings.Qdf.Enabled);
                    if (singleCompress || singleDecode != "none" || (singleMetadata is not null && settings.Compression.CompressStreams is not null))
                    {
                        TransformCopierStreams(copier, document.Engine, singleCompress, singleDecode, settings.Compression.RecompressFlate, singleLevel, limits, governor, diagnostics, cancellationToken, singleMetadata);
                    }

                    Content.ContentTransformRequest? singleTransforms = PrepareSourceContentTransforms(settings.CoalesceContents, settings.NormalizeContent, settings.ExternalizeInlineImages || (settings.Optimize.Optimize && !settings.Optimize.KeepInline), settings.IiMinBytes, singleCompress, singleLevel, StreamTransformer.Options(singleDecode, settings.Compression.RecompressFlate), settings.Prune, document.Engine, limits, governor, cancellationToken, out bool singlePrune);
                    if (singleTransforms is not null)
                    {
                        List<int> occurrenceIds = ParserContainers.CreateList<int>(copyMemory, occurrences.Count, sizeof(int));
                        foreach ((GraphCopier _, int occurrenceId) in occurrences)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                            occurrenceIds.Add(occurrenceId);
                        }
                        Content.ContentTransforms.ApplyPageTransforms(copier, occurrenceIds, singleTransforms, limits, governor, diagnostics, cancellationToken);
                    }

                    HashSet<GraphCopier>? singlePruneSources = null;
                    if (singlePrune)
                    {
                        singlePruneSources = ParserContainers.CreateSet<GraphCopier>(copyMemory);
                        ParserContainers.AddSetItem(copyMemory, singlePruneSources, copier, IntPtr.Size);
                    }
                    HashSet<int>? processedContentForms = singleTransforms is { Externalize: true }
                        && (settings.Overlays.Count != 0 || settings.Underlays.Count != 0)
                        ? ParserContainers.CreateSet<int>(copyMemory) : null;
                    bool needAppearances = settings.GenerateAppearances && Content.FlattenAnnotations.NeedsAppearances(document.Engine, governor, cancellationToken);
                    Content.OverlayApplication overlayApplied = await Content.Overlays.ApplyAsync(host, inputPath, occurrences, settings.Underlays, settings.Overlays, singleCompress, singleLevel, recovery, limits, governor, diagnostics, copyMemory, cancellationToken, copier.NextId, null, processedContentForms, settings.GenerateAppearances, needAppearances).ConfigureAwait(false);
                    copier.AdvanceNextId(overlayApplied.NextId);

                    int flattenAllocator = overlayApplied.NextId;
                    if (settings.FlattenAnnotations is not null && !settings.GenerateAppearances)
                    { needAppearances = Content.FlattenAnnotations.NeedsAppearances(document.Engine, governor, cancellationToken); }
                    if (settings.FlattenAnnotations is not null || settings.GenerateAppearances)
                    { needAppearances |= Content.FlattenAnnotations.NeedsAppearances(overlayApplied.FieldImports, governor, cancellationToken); }
                    bool singleGenerated = false;
                    Content.CopiedForm? singleForm = !settings.RemoveAcroform || (settings.GenerateAppearances && needAppearances)
                        ? Content.AcroFormCopy.TryCopySourceForm(document.Engine, copier, kept, true, limits, governor, copyMemory, cancellationToken, false)
                        : null;
                    (singleForm, _, IReadOnlyDictionary<PdfName, QpdfObject>? boundExtras, singleOutlines) = Content.SourceActions.BindDocument(
                        document.Engine, copier, singleForm, singleForm, singleExtras, true, singleOutlines, governor, copyMemory, cancellationToken);
                    singleExtras = boundExtras ?? singleExtras;
                    IReadOnlyList<(GraphCopier Copier, Content.CopiedForm Part, int Occurrence)> rotationFields = Array.Empty<(GraphCopier, Content.CopiedForm, int)>();
                    IReadOnlyDictionary<int, double[]>? rotationFrames = null;
                    flattenAllocator = Math.Max(flattenAllocator, copier.NextId);
                    if (settings.GenerateAppearances && needAppearances)
                    {
                        using ParserMemoryScope gateMemory = ParserMemoryScope.Create(governor);
                        HashSet<int> gateSurvivors = Content.AcroFormCopy.CollectAnnotRefs(occurrences, governor, gateMemory, cancellationToken);
                        List<(IReadOnlyDictionary<int, QpdfObject> Pool, Content.CopiedForm? Part)> gateParts = ParserContainers.CreateList<(IReadOnlyDictionary<int, QpdfObject>, Content.CopiedForm?)>(gateMemory);
                        ParserContainers.AddItem(gateMemory, gateParts, (copier.CopiedObjects, singleForm), 16L);
                        foreach (Content.AcroFormCopy.FieldImport import in overlayApplied.FieldImports)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                            ParserContainers.AddItem(gateMemory, gateParts, (import.Copier.CopiedObjects, import.Part), 16L);
                        }

                        HashSet<int>? gateReached = CollectGenerateReached(gateParts, gateSurvivors, true, limits, governor, gateMemory, cancellationToken);
                        Dictionary<GraphCopier, Content.CopiedForm?> singleForms = ParserContainers.CreateDictionary<GraphCopier, Content.CopiedForm?>(gateMemory);
                        ParserContainers.EnsureEntryCapacity(gateMemory, singleForms, IntPtr.Size); singleForms.Add(copier, singleForm);
                        flattenAllocator = Math.Max(flattenAllocator, copier.NextId);
                        (flattenAllocator, singleGenerated) = Content.GenerateAppearances.Apply(occurrences, singleForms, limits, governor, diagnostics, cancellationToken, gateReached, false, overlayApplied.GeneratedWidgets, flattenAllocator);
                        singleGenerated &= overlayApplied.GenerationComplete;
                        needAppearances = !singleGenerated;
                    }
                    if (settings.FlattenAnnotations is not null)
                    {
                        flattenAllocator = Content.FlattenAnnotations.Apply(occurrences, settings.FlattenAnnotations, needAppearances, singleCompress, singleLevel, limits, governor, diagnostics, cancellationToken, flattenAllocator);
                    }
                    IReadOnlyList<(GraphCopier Copier, Content.CopiedForm Part, int Occurrence)> duplicateFields = Array.Empty<(GraphCopier, Content.CopiedForm, int)>();
                    if (settings.FlattenRotation || duplicatePages && !settings.RemoveAcroform)
                    {
                        using ParserMemoryScope rotationMemory = ParserMemoryScope.Create(governor);
                        Dictionary<GraphCopier, Content.CopiedForm?> forms = ParserContainers.CreateDictionary<GraphCopier, Content.CopiedForm?>(rotationMemory);
                        ParserContainers.EnsureEntryCapacity(rotationMemory, forms, IntPtr.Size); forms.Add(copier, settings.RemoveAcroform ? null : singleForm);
                        if (duplicatePages && !settings.RemoveAcroform)
                        { duplicateFields = Content.AnnotationCopies.Isolate(occurrences, forms, true, governor, copyMemory, cancellationToken, ref flattenAllocator); }
                        if (settings.FlattenRotation)
                        {
                            (flattenAllocator, rotationFields, rotationFrames) = Content.FlattenRotation.Apply(occurrences, forms, singleCompress, singleLevel,
                                limits, governor, diagnostics, cancellationToken, flattenAllocator, copyMemory);
                        }
                    }
                    if (duplicateFields.Count > 0)
                    {
                        List<(GraphCopier Copier, Content.CopiedForm Part, int Occurrence)> fields = ParserContainers.CreateList<(GraphCopier, Content.CopiedForm, int)>(copyMemory,
                            checked(duplicateFields.Count + rotationFields.Count), 24L);
                        foreach (var part in duplicateFields) { cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); fields.Add(part); }
                        foreach (var part in rotationFields) { cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); fields.Add(part); }
                        rotationFields = fields;
                    }
                    copier.AdvanceNextId(flattenAllocator);

                    int inputExtension = Content.CatalogCopy.ReadExtensionLevel(document.Engine, governor, cancellationToken);
                    PdfVersion sourceVersion = governor.PrivateVersion(document.EffectiveVersionParts.Major, document.EffectiveVersionParts.Minor);
                    IncludeVersionRequirement(ref sourceVersion, ref inputExtension, overlayApplied.Version, overlayApplied.ExtensionLevel, governor);
                    (PdfVersion negotiated, int negotiatedExtension) = NegotiateVersion(sourceVersion, inputExtension, settings.MinVersion, settings.ForceVersion, governor);
                    string streamMode = ResolveObjectStreamMode(settings.ObjectStreams);
                    if (settings.ForceVersion is not null && PdfVersion.CompareParts(negotiated.Major, negotiated.Minor, 1, 5) < 0)
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

                        List<(int Index, PdfDictionary Label)> singleItems = Content.PageLabelMerge.Merge(singleOccurrences, copier.CopiedObjects, limits, governor, cancellationToken, copyMemory);
                        singleLabels = singleItems.Count == 0 ? null : NumbersDictionary(singleItems, governor);
                    }

                    Content.OptionalContent.Capture(document.Engine, copier, singleExtras, copyMemory, governor, cancellationToken);
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
                    var preparedSecurity = await PrepareOutputSecurityAsync(host, settings.OutputEncryption ?? QpdfOutputEncryption.Preserve,
                        document, TryGetOriginalId(document.Engine), copyMemory, governor, limits, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
                    using OutputSecurity? outputSecurity = preparedSecurity.Security;
                    byte[]? originalId = preparedSecurity.Identifier;
                    RequireEncryptionVersion(outputSecurity, ref negotiated, ref negotiatedExtension, settings.ForceVersion, governor);
                    PdfDictionary? singleAcroForm = settings.RemoveAcroform
                        ? null
                        : Content.AcroFormCopy.BuildSingle(
                            singleForm,
                            copier,
                            true,
                            occurrences,
                            singlePool,
                            overlayApplied.FieldImports,
                            rotationFields,
                            limits,
                            governor,
                            diagnostics,
                            copyMemory,
                            cancellationToken);
                    if (singleGenerated)
                    {
                        singleAcroForm = ClearNeedAppearances(singleAcroForm, governor, cancellationToken);
                    }

                    // Finish source copying before attachments allocate from the assembled pool.
                    HashSet<int> compressedOrig = CollectCompressedOriginals(document.Engine, streamMode == "preserve", copyMemory, governor, cancellationToken);
                    List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> singleSources = new(2)
                    {
                        (copier.SourceToDestination, compressedOrig),
                    };
                    AttachmentAssembly singleAttached = await AssembleAttachmentsAsync(host, singleExisting, singlePool, settings.Edits, singleCompress, singleLevel, streamMode == "preserve", singleSources, recovery, limits, governor, diagnostics, copyMemory, cancellationToken).ConfigureAwait(false);
                    if (settings.ForceVersion is null)
                    {
                        IncludeVersionRequirement(ref negotiated, ref negotiatedExtension, singleAttached.Version, singleAttached.ExtensionLevel, governor);
                        IncludeVersionRequirement(ref negotiated, ref negotiatedExtension,
                            AuthoredVersionFloor(settings.Edits.Adds.Count != 0, singleAttached.NamesRef is not null, singleLabels is not null), 0, governor);
                    }
                    FinishContentTransforms(singlePool, occurrences, singleTransforms, singlePruneSources, processedContentForms,
                        settings.Optimize, limits, governor, diagnostics, cancellationToken);
                    if (rotationFrames is not null)
                    {
                        (_, singleAcroForm, IReadOnlyDictionary<PdfName, QpdfObject>? rotatedExtras, singleOutlines) = Content.SourceActions.BindRotation(
                            copier, singlePool, kids, rotationFrames, null, singleAcroForm, singleExtras, singleOutlines, governor, copyMemory, cancellationToken);
                        singleExtras = rotatedExtras ?? singleExtras;
                    }
                    QpdfObject? singleOptionalContent = Content.OptionalContent.Combine(copier, Array.Empty<GraphCopier>(), overlayApplied.Copiers, singleExtras, singlePool, governor, cancellationToken);
                    if (singleOptionalContent is not null && settings.ForceVersion is null)
                    { IncludeVersionRequirement(ref negotiated, ref negotiatedExtension, new QpdfDocument.VersionParts(1, 5), 0, governor); }
                    HashSet<int> singleCompressed = ResolveCompressedSet(streamMode, singlePool, singleSources, copyMemory, governor, cancellationToken);
                    (PdfVersion effective, int extensionLevel) = RequireObjectStreamVersion(negotiated, negotiatedExtension, singleCompressed.Count != 0, governor);

                    PdfDictionary catalog = NewCatalog(new CatalogContents { PageLabels = singleLabels, Metadata = singleMetadata, Names = singleAttached.NamesRef, PageMode = singlePageMode ?? (singleAttached.Inserted ? UseAttachmentsPageMode : null), Outlines = singleOutlines, AcroForm = singleAcroForm, Structure = singleStructTree, MarkInfo = singleMarkInfo, Extras = singleExtras, OptionalContent = singleOptionalContent }, effective, extensionLevel, singlePool, governor, cancellationToken);
                    PdfDictionary pages = PagesNode(kids, copyMemory, governor, cancellationToken);
                    PreparedOutputModel preparedModel = OutputModelPreparation.Prepare(
                        new OutputGraph(catalog, pages, singlePool, singleCompressed, singleInfo,
                            (singleAttached.Replaced || copier.HasReplacedObjects
                                || overlayApplied.Copiers.Exists(static item => item.HasReplacedObjects)) && !settings.PreserveUnreferenced,
                            outputSecurity), copyMemory, governor, cancellationToken);
                    IReadOnlyDictionary<int, int>? singleOriginalIds = PrepareOriginalIds(settings.Qdf,
                        copier, overlayApplied.Copiers, copyMemory, governor, cancellationToken);
                    // Metadata admission stays with this operation through publication.
                    // Returned immutable arrays do not retain an execution owner.
                    IReadOnlyList<int> sourcePages = ResultCollectionStorage.Copy(resultStorage, requested, sizeof(int), governor, cancellationToken);
                    // All source resolution is finished. The writer receives only its
                    // immutable prepared graph, so fallback cannot follow publication.
                    document.Engine.AcceptSelectiveInput();
                    retryParse = false;
                    await PublishModelAsync(host, effective, preparedModel.Model, preparedModel.ObjectStreams, singleCompress, singleLevel, singleInfo?.ObjectNumber, originalId, settings.StaticId, settings.NewlineBeforeEndstream, settings.Qdf, singleOriginalIds, settings.Linearize, settings.LinearizePass1, outputPath, limits, governor, cancellationToken, outputSecurity, preparedModel.EncryptionObject, singleMetadata?.ObjectNumber ?? 0).ConfigureAwait(false);
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

}
