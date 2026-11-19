using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using System.Runtime.InteropServices;
using static Lokad.Qpdf.Operations.AttachmentAssembler;
using static Lokad.Qpdf.Operations.PageAssembly;
using static Lokad.Qpdf.Operations.PageLabelAssembly;
using static Lokad.Qpdf.Operations.SplitAssembly;
using static Lokad.Qpdf.Writing.ObjectStreamPreparation;
using static Lokad.Qpdf.Writing.OriginalObjectIds;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputPublication;
using static Lokad.Qpdf.Writing.OutputSecurityPreparation;
using static Lokad.Qpdf.Writing.OutputVersionPolicy;
using static Lokad.Qpdf.Writing.StreamPreparation;

namespace Lokad.Qpdf.Operations;

internal static class MultiPageSelection
{
    internal static async ValueTask<(MultiPageSelectionOutcome Result, int AttachmentCount)> SelectPagesCoreAsync(
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
        if (collation is not null && collation.GroupSizes.Count != 1 && collation.GroupSizes.Count != sources.Count)
        { throw new ArgumentException("Collation group sizes must hold one value or one per source.", nameof(collation)); }
        if (preparedSource is not null
            && (sources.Count != 1 || preparedSource.Path.Value != sources[0].Input.Value))
        { throw new InvalidOperationException("A prepared document requires its single original source."); }
        using PublicationInputAliases? ownedAliases = PublicationInputAliases.BeginIfNeeded(governor);
        // A pages block uses the multisource copier even with one source. Keep
        // that catalog/selection behavior, retrying its complete preparation on
        // the same lease if a bounded read cannot establish the original result.
        if (preparedSource is null && sources.Count == 1 && !emptyPrimary && !replaceInput && !splitPages.HasValue
            && settings.CanReadSelectedRanges(host))
        {
            IQpdfInput input = await GovernedHost.OpenInputAsync(host, sources[0].Input,
                governor, cancellationToken).ConfigureAwait(false);
            bool callerOwnsLease = true;
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await using QpdfDocument document = await QpdfDocument.OpenSelectionInputAsync(input,
                            sources[0].Input, limits, governor, diagnostics, recovery, attempt == 0,
                            cancellationToken, sources[0].Password).ConfigureAwait(false);
                        try
                        {
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
                        finally { callerOwnsLease = document.LeavesInputOpen; }
                    }
                    catch (CompleteInputRequiredException) when (attempt == 0 && callerOwnsLease) { }
                }
            }
            finally
            {
                if (callerOwnsLease)
                {
                    try { await input.DisposeAsync().ConfigureAwait(false); }
                    finally { governor.FinishDocumentCounters(); }
                }
            }
        }
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        using ParserMemoryScope selectionMemory = ParserMemoryScope.Create(governor);
        using SelectionSecurity securityOwner = SelectionSecurity.Create(governor);
        PdfReference? outputParent = null;
        int primaryIdx = sources.Count;
        for (int p = 0; p < sources.Count; p++)
        {
            if (sources[p].Input.Value == primaryInput.Value)
            {
                primaryIdx = p;
                break;
            }
        }
        Dictionary<string, List<int>> sourceGroups = GroupPageSources(sources, selectionMemory, governor, cancellationToken);
        ValidateSourceCredentials(sources, sourceGroups, governor);
        bool catalogOnlyPrimary = primaryIdx == sources.Count && !emptyPrimary;
        int documentCount = catalogOnlyPrimary ? checked(sourceGroups.Count + 1) : sourceGroups.Count;
        int sourcePasses = catalogOnlyPrimary ? checked(sources.Count + 1) : sources.Count;
        selectionMemory.Reserve(checked(96L + sources.Count * (sizeof(int) + 2L * IntPtr.Size)));
        int[] sourceCounts = new int[sources.Count];
        List<(GraphCopier Copier, int Id, int SourcePage)>[] perSource = new List<(GraphCopier, int, int)>[sources.Count];
        Content.SourceLabels?[] multiLabels = new Content.SourceLabels?[sources.Count];
        int nextId = 3;
        bool duplicateSourcePages = false;
        bool needAppearances = false;
        List<GraphCopier> copiers = ParserContainers.CreateList<GraphCopier>(selectionMemory, documentCount, IntPtr.Size);
        PdfVersion? version = null;
        int inputExtension = 0;
        byte[]? originalId1 = null;
        List<HashSet<int>> sourceCompressed = ParserContainers.CreateList<HashSet<int>>(selectionMemory, documentCount, IntPtr.Size);
        HashSet<int>? preservationCandidates = settings.PreserveUnreferenced && splitPages.HasValue
            ? ParserContainers.CreateSet<int>(selectionMemory) : null;
        List<Content.CopiedForm?> multiForms = ParserContainers.CreateList<Content.CopiedForm?>(selectionMemory, documentCount, IntPtr.Size);
        List<Content.CopiedForm?> gateForms = ParserContainers.CreateList<Content.CopiedForm?>(selectionMemory, documentCount, IntPtr.Size);
        QpdfObject? verbatimCopied = null;
        bool needSourceLabels = !settings.RemoveLabels && settings.Labels.Count == 0 && verbatimLabels is null;
        selectionMemory.Reserve(96L);
        SortedDictionary<string, AttachmentReference> primaryExisting = new(StringComparer.Ordinal);
        QpdfObject? primaryPageMode = null;
        PdfReference? primaryInfo = null;
        PdfReference? primaryMetadata = null;
        PdfReference? primaryOutlines = null;
        PdfReference? primaryStructTree = null;
        QpdfObject? primaryMarkInfo = null;
        IReadOnlyDictionary<PdfName, QpdfObject>? primaryExtras = null;
        int primaryPageCount = 0;
        long selected = 0L;
        (bool multiCompress, string multiDecode, int? multiLevel) = ResolveCompression(settings.Compression, settings.Qdf.Enabled);
        Content.ContentTransformRequest? finalTransforms = null;
        HashSet<GraphCopier>? pruneSources = null;
        HashSet<int>? processedContentForms = (settings.ExternalizeInlineImages || settings.Optimize.Optimize && !settings.Optimize.KeepInline)
            && (settings.Overlays.Count != 0 || settings.Underlays.Count != 0)
            ? ParserContainers.CreateSet<int>(selectionMemory) : null;
        // An unselected primary still supplies the document catalog and ID. Its
        // copier joins the ordinary pools, but it contributes no selection group.
        for (int s = 0; s < sourcePasses; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool catalogOnly = s == sources.Count;
            IReadOnlyList<int> sourceGroupIndices = catalogOnly ? Array.Empty<int>() : sourceGroups[sources[s].Input.Value];
            if (!catalogOnly && sourceGroupIndices[0] != s) { continue; }
            QpdfPath sourcePath = catalogOnly ? primaryInput : sources[s].Input;
            // Multisource graphs survive each reader. Independent payloads let
            // catalog-only and mostly unused inputs retire after their copies.
            await using QpdfDocument document = !catalogOnly && preparedSource is not null ? preparedSource
                : await QpdfDocument.OpenInspectionCore(host, sourcePath, catalogOnly ? settings.PrimaryPassword : sources[s].Password,
                    limits, governor, diagnostics, recovery, cancellationToken, !catalogOnly && sources.Count == 1,
                    replaceInput && !emptyPrimary && sourcePath == primaryInput).ConfigureAwait(false);
            try
            {
                // The existing source transfer funds a single complete input
                // until publication. Multiple inputs and ranged buffers remain
                // independent; retaining output sinks receive isolated payloads.
                document.Engine.BorrowImmutableStreamData = !catalogOnly && sources.Count == 1 && !document.Engine.HasSelectiveInput;
                if (!catalogOnly && (settings.ExternalizeInlineImages || (settings.Optimize.Optimize && !settings.Optimize.KeepInline))) { document.PrepareInlinePageResources(cancellationToken); }
                PdfVersion sourceVersion = governor.PrivateVersion(document.EffectiveVersionParts.Major, document.EffectiveVersionParts.Minor);
                if (version is null || PdfVersion.CompareParts(sourceVersion.Major, sourceVersion.Minor, version.Major, version.Minor) > 0)
                {
                    version = sourceVersion;
                    inputExtension = Content.CatalogCopy.ReadExtensionLevel(document.Engine, governor, cancellationToken);
                }
                else if (sourceVersion == version)
                { inputExtension = Math.Max(inputExtension, Content.CatalogCopy.ReadExtensionLevel(document.Engine, governor, cancellationToken)); }

                if (s == primaryIdx && !emptyPrimary)
                {
                    originalId1 = TryGetOriginalId(document.Engine);
                    originalId1 = await securityOwner.PrepareAsync(host, settings.OutputEncryption ?? QpdfOutputEncryption.Preserve,
                        document, originalId1, selectionMemory, governor, limits, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
                }

                sourceCompressed.Add(CollectCompressedOriginals(document.Engine, settings.ObjectStreams is not ("disable" or "generate"), selectionMemory, governor, cancellationToken));

                using ParserMemoryScope sourcePlan = ParserMemoryScope.Create(governor);
                // Zero-sized collation groups validate ranges and counts but copy no roots.
                List<(IReadOnlyList<int> Pages, bool Included)> requests = ParserContainers.CreateList<(IReadOnlyList<int>, bool)>(sourcePlan, sourceGroupIndices.Count, 16L);
                long groupedPageCount = 0L;
                foreach (int group in sourceGroupIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    IReadOnlyList<int> requested = sources[group].Selection.Resolve(document.PageReferences.Count,
                        limits.MaxContainerSize, governor, sourcePlan);
                    selected = checked(selected + requested.Count);
                    if (selected > limits.MaxContainerSize)
                    {
                        throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), selected, limits.MaxContainerSize);
                    }
                    sourceCounts[group] = document.PageReferences.Count;
                    bool included = collation is null || collation.GroupSizes[collation.GroupSizes.Count == 1 ? 0 : group] > 0;
                    requests.Add((requested, included));
                    if (included) { groupedPageCount = checked(groupedPageCount + requested.Count); }
                }
                if (groupedPageCount > Array.MaxLength)
                { throw new QpdfLimitException("Selected pages", groupedPageCount, Array.MaxLength); }
                List<PageLocation> kept = ParserContainers.CreateList<PageLocation>(sourcePlan, (int)groupedPageCount, 16L);
                GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, nextId, selectionMemory);
                for (int group = 0; group < sourceGroupIndices.Count; group++)
                {
                    (IReadOnlyList<int> requested, bool included) = requests[group];
                    foreach (int pageNumber in requested)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        if (pageNumber > document.PageReferences.Count)
                        { throw new ArgumentOutOfRangeException(nameof(sources), $"Source {sourceGroupIndices[group]} page {pageNumber} exceeds the page count of {document.PageReferences.Count}."); }
                        if (!included) { continue; }
                        PageLocation location = document.PageReferences[pageNumber - 1];
                        kept.Add(location);
                        duplicateSourcePages |= location.Source is null || copier.SourceToDestination.ContainsKey(location.Source.ObjectNumber);
                        copier.RegisterPageRoot(location.Source?.ObjectNumber);
                    }
                }
                copier.HintPageCapacity(kept.Count);
                using (PageInheritance.Batch? ancestry = PageInheritance.Batch.TryCreate(kept.Count > 1,
                    document.Engine, WriterInherited, limits, governor, cancellationToken))
                {
                    for (int group = 0; group < sourceGroupIndices.Count; group++)
                    {
                        IReadOnlyList<int> requested = requests[group].Included ? requests[group].Pages : Array.Empty<int>();
                        List<(GraphCopier Copier, int Id, int SourcePage)> taken = ParserContainers.CreateList<(GraphCopier, int, int)>(selectionMemory, requested.Count, 16L);
                        perSource[sourceGroupIndices[group]] = taken;
                        foreach (int pageNumber in requested)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); governor.NoteOperation();
                            PageLocation location = document.PageReferences[pageNumber - 1];
                            IReadOnlyDictionary<PdfName, QpdfObject> extra = MaterializeAncestors(document.Engine, location,
                                copier, governor, limits, cancellationToken, selectionMemory, ancestry);
                            if (outputParent is null) { selectionMemory.Reserve(32L); outputParent = new PdfReference(2, 0); }
                            (int pageId, PdfDictionary page) = copier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra, false, outputParent);
                            RequireMediaBox(page);
                            taken.Add((copier, pageId, pageNumber));
                        }
                    }
                }

                if (settings.PreserveUnreferenced && s == primaryIdx && !emptyPrimary)
                {
                    using ParserMemoryScope preservation = ParserMemoryScope.Create(governor);
                    HashSet<int> excluded = PreservationExclusions(document.Engine, kept, limits, governor, preservation, cancellationToken);
                    copier.PreserveUnreferencedObjects(document.Engine, excluded, diagnostics, preservationCandidates, cancellationToken);
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
                        cancellationToken,
                        false);
                multiForms.Add(sourceForm);
                gateForms.Add(settings.RemoveAcroform
                    ? Content.AcroFormCopy.TryCopySourceForm(document.Engine, copier, kept, pruneUnreachableFields, limits, governor, selectionMemory, cancellationToken, false)
                    : sourceForm);
                if (needSourceLabels && !catalogOnly)
                {
                    Content.SourceLabels labels = Content.PageLabelMerge.ReadSource(document.Engine, copier, limits, governor, cancellationToken);
                    foreach (int group in sourceGroupIndices)
                    { cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); multiLabels[group] = labels; }
                }

                if (verbatimLabels is not null && s == 0)
                {
                    verbatimCopied = copier.CopyValue(verbatimLabels, 0);
                }

                if (s == primaryIdx && !emptyPrimary)
                {
                    (primaryInfo, primaryMetadata) = CopyDocumentMetadata(copier, document.Engine, settings.RemoveInfo, settings.RemoveMetadata, diagnostics, governor, cancellationToken);
                    primaryOutlines = Content.CatalogCopy.TryCopyOutlines(document.Engine, copier, settings.PreserveCatalog, settings.Linearize, governor, cancellationToken);
                    (primaryStructTree, primaryMarkInfo) = Content.CatalogCopy.TryCopyStructure(document.Engine, copier, settings.PreserveCatalog, settings.RemoveStructure, governor, cancellationToken);
                    (primaryPageMode, primaryExtras) = Content.CatalogCopy.TryCopyExtras(document.Engine, copier, settings.PreserveCatalog, settings.RemoveAcroform, governor, selectionMemory, cancellationToken);
                    if (!splitPages.HasValue && primaryExtras.ContainsKey(Content.PrintPageRanges.ViewerPreferences))
                    { primaryPageCount = document.PageReferences.Count; }

                    primaryExisting = PreserveExistingAttachments(copier, document.Engine, settings.Edits, limits, governor, diagnostics, selectionMemory, cancellationToken);
                }

                if (multiCompress || multiDecode != "none" || (s == primaryIdx && primaryMetadata is not null && settings.Compression.CompressStreams is not null))
                {
                    TransformCopierStreams(copier, settings.Optimize.Optimize ? StreamSelection.ExceptImages : StreamSelection.All,
                        multiCompress, multiDecode, settings.Compression.RecompressFlate, multiLevel, limits, governor, diagnostics, cancellationToken, s == primaryIdx ? primaryMetadata : null);
                }

                if (!catalogOnly)
                {
                    Content.ContentTransformRequest? sourceTransforms = PrepareSourceContentTransforms(settings, multiCompress, multiLevel, multiDecode, document.Engine, limits, governor, cancellationToken, out bool sourcePrune);
                    if (sourcePrune)
                    {
                        pruneSources ??= ParserContainers.CreateSet<GraphCopier>(selectionMemory);
                        ParserContainers.AddSetItem(selectionMemory, pruneSources, copier, IntPtr.Size);
                    }
                    if (sourceTransforms is not null)
                    {
                        finalTransforms = sourceTransforms;
                        List<int> takenIds = ParserContainers.CreateList<int>(selectionMemory, kept.Count, sizeof(int));
                        foreach (int group in sourceGroupIndices)
                        {
                            foreach ((GraphCopier _, int takenId, int _) in perSource[group])
                            { cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); takenIds.Add(takenId); }
                        }
                        Content.ContentTransforms.ApplyPageTransforms(copier, takenIds, sourceTransforms, limits, governor, diagnostics, cancellationToken);
                    }
                }

                if (!catalogOnly && (settings.FlattenAnnotations is not null || settings.GenerateAppearances) && Content.FlattenAnnotations.NeedsAppearances(document.Engine, governor, cancellationToken))
                {
                    needAppearances = true;
                }

                (Content.CopiedForm? boundForm, Content.CopiedForm? boundGateForm,
                    IReadOnlyDictionary<PdfName, QpdfObject>? boundExtras, PdfReference? boundOutlines) = Content.SourceActions.BindDocument(
                        document.Engine, copier, sourceForm, gateForms[^1], s == primaryIdx ? primaryExtras : null, !splitPages.HasValue,
                        s == primaryIdx ? primaryOutlines : null, governor, selectionMemory, cancellationToken);
                multiForms[^1] = boundForm; gateForms[^1] = boundGateForm;
                if (s == primaryIdx)
                {
                    primaryExtras = boundExtras; primaryOutlines = boundOutlines;
                }
                Content.OptionalContent.Capture(document.Engine, copier, s == primaryIdx ? primaryExtras : null, selectionMemory, governor, cancellationToken);
                nextId = copier.NextId;
                copiers.Add(copier);
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

        Content.OverlayApplication overlayApplied = await Content.Overlays.ApplyAsync(host, primaryInput, occurrences, settings.Underlays, settings.Overlays, multiCompress, multiLevel, recovery, limits, governor, diagnostics, selectionMemory, cancellationToken, nextId, null, processedContentForms, settings.GenerateAppearances, needAppearances).ConfigureAwait(false);

        needAppearances = IncludeImportedAppearances(settings, overlayApplied, needAppearances, governor, cancellationToken);
        (int multiFlattenAllocator, bool multiGenerated, needAppearances) = CompleteSelectionAppearances(occurrences,
            CollectionsMarshal.AsSpan(copiers), CollectionsMarshal.AsSpan(multiForms), CollectionsMarshal.AsSpan(gateForms),
            pruneUnreachableFields, settings, overlayApplied, needAppearances, multiCompress, multiLevel,
            overlayApplied.NextId, limits, governor, diagnostics, cancellationToken);

        IReadOnlyList<(GraphCopier Copier, Content.CopiedForm Part, int Occurrence)> rotationFields = Array.Empty<(GraphCopier, Content.CopiedForm, int)>();
        IReadOnlyDictionary<int, double[]>? rotationFrames = null;
        IReadOnlyList<(GraphCopier Copier, Content.CopiedForm Part, int Occurrence)> duplicateFields = Array.Empty<(GraphCopier, Content.CopiedForm, int)>();
        List<Content.CopiedForm?> renameForms = multiForms;
        List<GraphCopier> renameCopiers = copiers;
        if (settings.FlattenRotation || duplicateSourcePages && !settings.RemoveAcroform)
        {
            using ParserMemoryScope rotationMemory = ParserMemoryScope.Create(governor);
            Dictionary<GraphCopier, Content.CopiedForm?> forms = ParserContainers.CreateDictionary<GraphCopier, Content.CopiedForm?>(rotationMemory);
            for (int index = 0; index < copiers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.EnsureEntryCapacity(rotationMemory, forms, IntPtr.Size);
                forms.Add(copiers[index], multiForms[index]);
            }
            if (duplicateSourcePages && !settings.RemoveAcroform)
            { duplicateFields = Content.AnnotationCopies.Isolate(occurrences, forms, true, governor, selectionMemory, cancellationToken, ref multiFlattenAllocator); }
            if (settings.FlattenRotation)
            {
                (nextId, rotationFields, rotationFrames) = Content.FlattenRotation.Apply(occurrences, forms, multiCompress, multiLevel,
                    limits, governor, diagnostics, cancellationToken, multiFlattenAllocator, selectionMemory);
            }
            if (duplicateFields.Count > 0)
            {
                int count = checked(multiForms.Count + duplicateFields.Count);
                renameForms = ParserContainers.CreateList<Content.CopiedForm?>(selectionMemory, count, IntPtr.Size);
                renameCopiers = ParserContainers.CreateList<GraphCopier>(selectionMemory, count, IntPtr.Size);
                int duplicate = 0;
                for (int position = 0; position < occurrences.Count; position++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    GraphCopier sourceCopier = occurrences[position].Copier;
                    if (forms.Remove(sourceCopier, out Content.CopiedForm? original))
                    { renameForms.Add(original); renameCopiers.Add(sourceCopier); }
                    while (duplicate < duplicateFields.Count && duplicateFields[duplicate].Occurrence == position)
                    {
                        var part = duplicateFields[duplicate++];
                        renameForms.Add(part.Part); renameCopiers.Add(part.Copier);
                    }
                }
                foreach (GraphCopier sourceCopier in copiers)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (forms.TryGetValue(sourceCopier, out Content.CopiedForm? original))
                    { renameForms.Add(original); renameCopiers.Add(sourceCopier); }
                }
            }
        }

        if (replaceInput && splitPages.HasValue)
        {
            throw new QpdfParseException("--split-pages may not be used with --replace-input.", null, null);
        }

        if (splitPages.HasValue && splitPages.Value < 1)
        {
            throw new QpdfParseException("--split-pages needs a positive group size.", null, null);
        }

        QpdfPath target = replaceInput ? primaryInput : outputPath;
        if (!replaceInput)
        {
            if (!emptyPrimary && outputPath.Value == primaryInput.Value)
            { throw new QpdfParseException("Output aliases an input; use --replace-input to overwrite it.", null, null); }
            foreach (PdfPageSource source in sources)
            {
                if (source.Input.Value == outputPath.Value)
                {
                    throw new QpdfParseException("Output aliases an input; use --replace-input to overwrite it.", null, null);
                }
            }
        }

        if (!settings.RemoveAcroform && (overlayApplied.FieldImports.Count > 0 || rotationFields.Count > 0))
        {
            List<Content.CopiedForm?> orderedForms = renameForms;
            List<GraphCopier> orderedCopiers = renameCopiers;
            int count = checked(orderedForms.Count + overlayApplied.FieldImports.Count + rotationFields.Count);
            renameForms = ParserContainers.CreateList<Content.CopiedForm?>(selectionMemory, count, IntPtr.Size);
            renameCopiers = ParserContainers.CreateList<GraphCopier>(selectionMemory, count, IntPtr.Size);
            for (int index = 0; index < orderedForms.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                renameForms.Add(orderedForms[index]); renameCopiers.Add(orderedCopiers[index]);
            }
            foreach ((GraphCopier sourceCopier, Content.CopiedForm fields, int _) in rotationFields)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                renameForms.Add(fields); renameCopiers.Add(sourceCopier);
            }
            foreach (Content.AcroFormCopy.FieldImport import in overlayApplied.FieldImports)
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

        AttachmentAssembly multiAttached = await AssembleAttachmentsAsync(host, primaryExisting, pool, 3, settings.Edits,
            multiCompress, multiLevel, settings.ObjectStreams is not ("disable" or "generate"), multiSources,
            recovery, limits, governor, diagnostics, preservationCandidates, selectionMemory, cancellationToken).ConfigureAwait(false);

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
        else if (settings.Labels.Count == 0)
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

                labelItems = Content.PageLabelMerge.Merge(labelOccurrences, pool, limits, governor, cancellationToken, selectionMemory);
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
        PdfVersion inputMax = version ?? governor.PrivateVersion(1, 4);
        IncludeVersionRequirement(ref inputMax, ref inputExtension, overlayApplied.Version, overlayApplied.ExtensionLevel, governor);
        (PdfVersion negotiatedMulti, int negotiatedMultiExtension) = NegotiateVersion(inputMax, inputExtension, settings.MinVersion, settings.ForceVersion, governor);
        if (emptyPrimary)
        { originalId1 = await securityOwner.PrepareAsync(host, settings.OutputEncryption ?? QpdfOutputEncryption.Preserve,
            null, originalId1, selectionMemory, governor, limits, diagnostics, recovery, cancellationToken).ConfigureAwait(false); }
        OutputSecurity? outputSecurity = securityOwner.Value;
        RequireEncryptionVersion(outputSecurity, ref negotiatedMulti, ref negotiatedMultiExtension, settings.ForceVersion, governor);
        if (settings.ForceVersion is null)
        {
            IncludeVersionRequirement(ref negotiatedMulti, ref negotiatedMultiExtension, multiAttached.Version, multiAttached.ExtensionLevel, governor);
            IncludeVersionRequirement(ref negotiatedMulti, ref negotiatedMultiExtension,
                AuthoredVersionFloor(settings.Edits.Adds.Count != 0, multiAttached.NamesRef is not null, catalogLabels is not (null or PdfNull)), 0, governor);
        }
        string multiStreamMode = ResolveObjectStreamMode(settings.ObjectStreams);
        if (settings.ForceVersion is not null && PdfVersion.CompareParts(negotiatedMulti.Major, negotiatedMulti.Minor, 1, 5) < 0)
        {
            multiStreamMode = "disable";
        }

        HashSet<int> multiCompressed = ResolveCompressedSet(multiStreamMode, pool, multiSources, selectionMemory, governor, cancellationToken);
        (PdfVersion effective, int extensionLevel) = RequireObjectStreamVersion(negotiatedMulti, negotiatedMultiExtension, multiCompressed.Count != 0, governor);

        IReadOnlyDictionary<int, (int Number, int Generation)>? multiOriginalIds = PrepareOriginalIds(settings.Qdf,
            copiers, overlayApplied.Copiers, selectionMemory, governor, cancellationToken);
        Content.CopiedForm? multiCombined = settings.RemoveAcroform
            ? null
            : Content.AcroFormCopy.Combine(renameForms, renameCopiers, overlayApplied.FieldImports,
                pool, limits, governor, diagnostics, selectionMemory, cancellationToken);
        HashSet<int>? survivingAnnots = multiCombined is null ? null
            : Content.AcroFormCopy.CollectAnnotRefs(pool, CollectionsMarshal.AsSpan(kids), governor, selectionMemory, cancellationToken);
        PdfDictionary? multiAcroForm = multiCombined is null || survivingAnnots is null
            ? null
            : Content.AcroFormCopy.BuildCatalogForm(
                multiCombined,
                pool,
                survivingAnnots,
                pruneUnreachableFields,
                limits,
                governor,
                cancellationToken);
        if (multiGenerated)
        {
            multiAcroForm = ClearNeedAppearances(multiAcroForm, governor, cancellationToken);
        }

        GraphCopier? contentCopier = occurrences.Count != 0 ? occurrences[0].Copier : copiers.FirstOrDefault();
        FinishContentTransforms(contentCopier, pool, occurrences, finalTransforms, pruneSources, processedContentForms,
            settings, limits, governor, diagnostics, cancellationToken);

        if (rotationFrames is not null)
        {
            (multiCombined, multiAcroForm, IReadOnlyDictionary<PdfName, QpdfObject>? rotatedExtras, primaryOutlines) = Content.SourceActions.BindRotation(
                copiers[0], pool, kids, rotationFrames, multiCombined, multiAcroForm, splitPages.HasValue ? null : primaryExtras, primaryOutlines,
                governor, selectionMemory, cancellationToken);
            if (!splitPages.HasValue) { primaryExtras = rotatedExtras; }
        }
        GraphCopier? primaryOptionalCopier = primaryIdx >= 0 && primaryIdx < copiers.Count ? copiers[primaryIdx] : null;
        QpdfObject? optionalContent = Content.OptionalContent.Combine(primaryOptionalCopier, copiers, overlayApplied.Copiers, primaryExtras, pool, governor, cancellationToken);
        if (optionalContent is not null && settings.ForceVersion is null)
        { IncludeVersionRequirement(ref effective, ref extensionLevel, new QpdfDocument.VersionParts(1, 5), 0, governor); }
        if (governor.HasTypedResult) { selectionMemory.PrepareResultStorage(checked(24L * mapping.Count)); }
        PublicationInputAliases inputs = PublicationInputAliases.Active(governor);
        QpdfPath? capture = settings.Linearize ? settings.LinearizePass1 : null;
        if (!splitPages.HasValue)
        {
            if (survivingAnnots is not null && (pruneUnreachableFields || settings.FlattenAnnotations is not null))
            {
                // Rotation binding may have replaced annotation identities.
                if (rotationFrames is not null)
                { survivingAnnots = Content.AcroFormCopy.CollectAnnotRefs(pool, CollectionsMarshal.AsSpan(kids), governor, selectionMemory, cancellationToken); }
                multiAcroForm = Content.SourceActions.ProjectOutputForm(multiAcroForm, pool, copiers[0],
                    CollectionsMarshal.AsSpan(kids), survivingAnnots, multiStreamMode == "generate" ? multiCompressed : null,
                    limits, governor, selectionMemory, cancellationToken);
            }
            PdfDictionary catalog = NewCatalog(new CatalogContents { PageLabels = catalogLabels, Metadata = primaryMetadata, Names = multiAttached.NamesRef, PageMode = primaryPageMode ?? (multiAttached.Inserted ? UseAttachmentsPageMode : null), Outlines = primaryOutlines, AcroForm = multiAcroForm, Structure = primaryStructTree, MarkInfo = primaryMarkInfo, Extras = primaryExtras, OptionalContent = optionalContent }, effective, extensionLevel, pool, governor, cancellationToken);
            PdfDictionary projectedCatalog = ProjectPrintRanges(catalog, pool, primaryPageCount, mapping, (sources, primaryInput),
                static (entry, state) => state.sources[entry.SourceIndex].Input == state.primaryInput ? entry.SourcePage : 0,
                limits, selectionMemory, governor, cancellationToken);
            PreparedOutputModel preparedModel = OutputModelPreparation.Prepare(
                new OutputGraph(projectedCatalog, PagesNode(kids, selectionMemory, governor, cancellationToken), pool, multiCompressed, primaryInfo,
                    (!ReferenceEquals(catalog, projectedCatalog) || multiAttached.Replaced || copiers.Exists(static item => item.HasReplacedObjects)
                        || overlayApplied.Copiers.Exists(static item => item.HasReplacedObjects)) && !settings.PreserveUnreferenced,
                    outputSecurity), selectionMemory, governor, cancellationToken);

            IReadOnlyList<PdfPageSelectionMapping> resultMapping = ResultCollectionStorage.Copy(selectionMemory, mapping, IntPtr.Size, governor, cancellationToken);
            IReadOnlyList<int> resultCounts = ResultCollectionStorage.Copy(selectionMemory, sourceCounts, sizeof(int), governor, cancellationToken);
            IReadOnlyList<QpdfPath> resultPaths = ResultCollectionStorage.Single(selectionMemory, target, IntPtr.Size, governor, cancellationToken);
            await PublishModelAsync(host, effective, preparedModel.Model, preparedModel.ObjectStreams, multiCompress, multiLevel, primaryInfo?.ObjectNumber, originalId1, settings.StaticId, settings.NewlineBeforeEndstream, settings.Qdf, multiOriginalIds, settings.Linearize, settings.LinearizePass1, target, limits, governor, cancellationToken,
                outputSecurity, preparedModel.EncryptionObject, primaryMetadata?.ObjectNumber ?? 0).ConfigureAwait(false);
            return (new MultiPageSelectionOutcome(kids.Count, resultMapping, resultCounts, resultPaths, ResultWarningSnapshot.Read(resultWarnings, diagnostics)), multiAttached.FinalCount);
        }

        List<string> names = GenerateSplitNames(target.Value, kids.Count, splitPages.Value, selectionMemory, governor);
        await inputs.ValidateTargetsAsync(host, names, capture, "Split", cancellationToken).ConfigureAwait(false);

        List<(int First, int Last)> groups = PartitionGroups(kids.Count, splitPages.Value, selectionMemory, governor);
        HashSet<int> pageNumbers = ParserContainers.CreateSet<int>(selectionMemory);
        foreach (var source in perSource)
        {
            foreach (var page in source)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddSetItem(selectionMemory, pageNumbers, page.Id, sizeof(int));
            }
        }
        // Split roots must use the final role identities, after source binding
        // and rotation. Earlier root snapshots can retain obsolete outlines.
        selectionMemory.Reserve(64L + 7L * IntPtr.Size);
        PdfArray sharedRoots = PdfArray.FromOwnedItems([
            (QpdfObject?)primaryInfo ?? PdfNull.Instance, (QpdfObject?)primaryMetadata ?? PdfNull.Instance,
            (QpdfObject?)primaryOutlines ?? PdfNull.Instance, (QpdfObject?)primaryStructTree ?? PdfNull.Instance,
            primaryMarkInfo is PdfReference markInfo ? markInfo : PdfNull.Instance,
            (QpdfObject?)multiAttached.NamesRef ?? PdfNull.Instance, optionalContent ?? PdfNull.Instance,
        ]);
        PdfArray? detachedRoots = FindSplitDetachedRoots(pool, kids, sharedRoots, multiAcroForm, catalogLabels,
            preservationCandidates, selectionMemory, governor, cancellationToken);
        using PreparedSplitPublications preparation = PreparedSplitPublications.Create(governor);
        int? projectionMaximum = null;
        for (int g = 0; g < groups.Count; g++)
        {
            using ParserMemoryScope groupMemory = ParserMemoryScope.Create(governor);
            cancellationToken.ThrowIfCancellationRequested();
            (int first, int last) = groups[g];
            List<QpdfObject> roots = ParserContainers.CreateList(groupMemory);
            HashSet<int> groupPageNumbers = ParserContainers.CreateSet<int>(groupMemory);
            for (int slot = first - 1; slot < last; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(groupMemory, roots, kids[slot]);
                ParserContainers.AddSetItem(groupMemory, groupPageNumbers, kids[slot].ObjectNumber, sizeof(int));
            }

            ReadOnlySpan<PdfReference> partPages = CollectionsMarshal.AsSpan(kids).Slice(first - 1, last - first + 1);
            var (groupAcroForm, fieldEdits) = ProjectSplitForm(multiCombined, pool, partPages, pruneUnreachableFields,
                ref projectionMaximum, 2, limits, groupMemory, governor, cancellationToken);
            if (multiGenerated) { groupAcroForm = ClearNeedAppearances(groupAcroForm, governor, cancellationToken); }

            PdfDictionary catalog = NewCatalog(new CatalogContents
            {
                PageLabels = SlicePageLabels(labelItems, first, last, governor),
                Metadata = primaryMetadata, Names = multiAttached.NamesRef,
                PageMode = primaryPageMode as PdfName ?? (multiAttached.Inserted ? UseAttachmentsPageMode : null),
                Outlines = primaryOutlines, AcroForm = groupAcroForm, Structure = primaryStructTree,
                MarkInfo = primaryMarkInfo, OptionalContent = optionalContent,
            }, effective, extensionLevel, pool, governor, cancellationToken);
            (catalog, Dictionary<int, QpdfObject>? edits) = ProjectSplitNavigation(catalog, pool, partPages, groupPageNumbers,
                fieldEdits, ref projectionMaximum, 2, limits, groupMemory, governor, cancellationToken);
            ParserContainers.AddItem(groupMemory, roots, catalog);
            if (primaryInfo is not null) { ParserContainers.AddItem(groupMemory, roots, primaryInfo); }
            if (detachedRoots is not null) { ParserContainers.AddItem(groupMemory, roots, detachedRoots); }

            using ParserMemoryScope reachabilityMemory = ParserMemoryScope.Create(governor);
            groupMemory.Reserve(48L);
            SplitResolver resolver = new(pool, pageNumbers, groupPageNumbers, edits);
            HashSet<int> reachable = GraphReachability.Find(resolver, roots, reachabilityMemory, governor);
            GroupNumbers numbers = NumberGroup(reachable, 0, 2, groupMemory, governor, cancellationToken);
            Dictionary<int, int> renumber = numbers.Map;
            List<PdfReference> renumberedKids = MapKids(partPages, renumber, groupMemory, governor, cancellationToken);
            int? groupInfo = primaryInfo is not null && renumber.TryGetValue(primaryInfo.ObjectNumber, out int mappedInfo)
                ? mappedInfo : null;
            List<NumberedObject> sub = ParserContainers.CreateList<NumberedObject>(groupMemory);
            NumberedObject groupCatalog = new(1, Writing.ObjectRemap.RemapSplit(catalog, renumber, pageNumbers,
                groupMemory, governor, cancellationToken));
            NumberedObject groupPages = new(2, PagesNode(renumberedKids, groupMemory, governor, cancellationToken));
            ParserContainers.AddItem(groupMemory, sub, groupCatalog, 16L);
            ParserContainers.AddItem(groupMemory, sub, groupPages, 16L);
            AppendGroupObjects(sub, resolver, numbers, pageNumbers, groupMemory, governor, cancellationToken);
            QpdfPath path = QpdfPath.From(names[g]);
            HashSet<int> groupCompressed = multiStreamMode == "generate" && edits is not null
                ? ResolveGeneratedCompressedSet(sub, groupMemory, governor, cancellationToken)
                : RemapCompressedSet(multiCompressed, renumber, groupMemory, governor, cancellationToken);
            Dictionary<int, (int Number, int Generation)>? groupOriginalIds =
                MapOriginalIds(multiOriginalIds, numbers, groupMemory, governor, cancellationToken);

            using OutputSecurity? freshSecurity = g == 0 ? null : settings.OutputEncryption?.CreateOutput(governor);
            OutputSecurity? groupSecurity = freshSecurity ?? outputSecurity;
            PreparedOutputModel preparedModel = OutputModelPreparation.Finish(sub, groupCompressed, groupSecurity, groupMemory, governor, cancellationToken);
            PreparedSplitPublication publication = await PreparePresentationOutputAsync(effective, preparedModel.Model, preparedModel.ObjectStreams, multiCompress,
                multiLevel, groupInfo, originalId1, settings.StaticId, settings.NewlineBeforeEndstream, settings.Qdf, groupOriginalIds,
                settings.Linearize, settings.LinearizePass1, path, limits, governor, cancellationToken,
                groupSecurity, preparedModel.EncryptionObject, primaryMetadata is null ? 0 : renumber.GetValueOrDefault(primaryMetadata.ObjectNumber)).ConfigureAwait(false);
            preparation.Add(publication);
        }

        using PublicationReservation publicationAdmission = preparation.ReservePublication(governor);
        IReadOnlyList<PdfPageSelectionMapping> splitMapping = ResultCollectionStorage.Copy(selectionMemory, mapping, IntPtr.Size, governor, cancellationToken);
        IReadOnlyList<int> splitCounts = ResultCollectionStorage.Copy(selectionMemory, sourceCounts, sizeof(int), governor, cancellationToken);
        List<QpdfPath> outputPaths = ParserContainers.CreateList<QpdfPath>(selectionMemory);
        foreach (string name in names)
        {
            ParserContainers.AddItem(selectionMemory, outputPaths, QpdfPath.From(name), IntPtr.Size);
            if (governor.HasTypedResult) { selectionMemory.PrepareResultStorage(GovernedText.StringBytes(name.Length)); }
        }
        IReadOnlyList<QpdfPath> splitPaths = ResultCollectionStorage.Copy(selectionMemory, outputPaths, IntPtr.Size, governor, cancellationToken);
        await preparation.PublishAsync(host, publicationAdmission, governor, cancellationToken).ConfigureAwait(false);

        return (new MultiPageSelectionOutcome(kids.Count, splitMapping, splitCounts, splitPaths, ResultWarningSnapshot.Read(resultWarnings, diagnostics)), multiAttached.FinalCount);
    }

    internal static async ValueTask<(MultiPageSelectionOutcome Result, int AttachmentCount)> WholeFileCoreAsync(
        IQpdfHost host,
        WholeFilePlan plan,
        QpdfPassword? password,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        using PublicationInputAliases? ownedAliases = PublicationInputAliases.BeginIfNeeded(governor);
        await using QpdfDocument document = plan.EmptyDocument
            ? await QpdfDocument.OpenEmptyDocument(plan.Input, limits, governor, diagnostics, plan.Recovery, cancellationToken, false).ConfigureAwait(false)
            : await QpdfDocument.OpenInspectionCore(host, plan.Input, password, limits, governor, diagnostics, plan.Recovery,
                cancellationToken, true, plan.ReplaceInput).ConfigureAwait(false);
        int total = document.PageReferences.Count;
        if ((long)total > limits.MaxContainerSize)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), total, limits.MaxContainerSize);
        }

        List<PdfPageSource> solo = new(1)
        {
            new PdfPageSource(plan.Input, PdfPageSelectionOptions.FromRange(null), password),
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
            settings: SelectionSettings.FromPlan(plan, pruneEnabled: plan.SplitPages.HasValue, password: password),
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

}
