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
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        return await SelectPageCoreAsync(host, inputPath, outputPath, options, limits, QpdfRecoveryMode.Strict, [], [], null, null, false, false, false, null, false, CompressionRequest.Default, false, false, AttachmentEdits.Empty, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        string? minVersion,
        string? forceVersion,
        bool deterministicId,
        bool staticId,
        bool newlineBeforeEndstream,
        string? objectStreams,
        bool preserveUnreferenced,
        CompressionRequest compression,
        bool removeInfo,
        bool removeMetadata,
        AttachmentEdits edits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        await using QpdfDocument document = await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        int total = document.PageReferences.Count;
        IReadOnlyList<int> requested = options.Resolve(total);
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

        ApplyRotations(occurrences, ResolveRotationFolds(rotations, kids.Count, governor), governor);
        if (preserveUnreferenced)
        {
            copier.PreserveUnreferencedObjects(document.Engine, diagnostics, cancellationToken);
        }

        (PdfReference? singleInfo, PdfReference? singleMetadata) = CopyCatalogExtras(copier, document.Engine, removeInfo, removeMetadata, diagnostics);
        SortedDictionary<string, PdfReference> singleExisting = PreserveExistingAttachments(copier, document.Engine, edits, limits, governor, diagnostics, cancellationToken);
        string? singlePageMode = ReadInputPageMode(document.Engine);

        (bool singleCompress, string singleDecode, int? singleLevel) = ResolveCompression(compression);
        if (singleCompress || singleDecode != "none")
        {
            TransformCopierStreams(copier, document.Engine, singleCompress, singleDecode, compression.RecompressFlate, singleLevel, limits, governor, diagnostics, cancellationToken);
        }

        (PdfVersion negotiated, int negotiatedExtension) = NegotiateVersion(document.Version, minVersion, forceVersion);
        string streamMode = ResolveObjectStreamMode(objectStreams);
        if (forceVersion is not null && CompareVersions(negotiated.Major, negotiated.Minor, 1, 5) < 0)
        {
            streamMode = "disable";
        }

        HashSet<int> compressedOrig = CollectCompressedOriginals(document.Engine);
        Dictionary<int, QpdfObject> singlePool = new(copier.CopiedObjects);
        AttachmentAssembly singleAttached = await AssembleAttachmentsAsync(host, singleExisting, singlePool, edits, singlePageMode, singleCompress, singleLevel, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        HashSet<int> singleCompressed = ResolveCompressedSet(streamMode, singlePool, new List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> { (copier.SourceToDestination, compressedOrig) }, governor);
        PdfVersion effective = negotiated;
        int extensionLevel = negotiatedExtension;
        if (singleCompressed.Count > 0 && CompareVersions(effective.Major, effective.Minor, 1, 5) < 0)
        {
            effective = new PdfVersion(1, 5);
            extensionLevel = 0;
        }

        byte[]? originalId = TryGetOriginalId(document.Engine);
        string countText = kids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PdfDictionary catalog = NewCatalog(BuildPageLabels(labels, kids.Count, governor), effective, extensionLevel, singleMetadata, singleAttached.NamesRef, singleAttached.PageMode);
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
        await PublishModelAsync(host, effective, extensionLevel, model, singleStreams, singleCompress, singleLevel, singleInfo?.ObjectNumber, originalId, staticId, newlineBeforeEndstream, outputPath, limits, governor, cancellationToken).ConfigureAwait(false);
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

        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        return await SelectPagesCoreAsync(host, frozen, outputPath, frozen[0].Input, limits, QpdfRecoveryMode.Strict, null, [], [], null, false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, AttachmentEdits.Empty, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        return await SelectPagesCoreAsync(host, frozen, outputPath, frozen[0].Input, limits, QpdfRecoveryMode.Strict, null, specifications, [], null, false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, AttachmentEdits.Empty, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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

        List<OutputRotation> specifications = ConvertRotations(mergeOptions.Rotations);
        List<PageLabelSpec> labels = ConvertLabels(mergeOptions.Labels);
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        return await SelectPagesCoreAsync(host, frozen, outputPath, frozen[0].Input, limits, QpdfRecoveryMode.Strict, mergeOptions.Collation, specifications, labels, null, false, null, null, false, false, false, null, false, CompressionRequest.Default, false, false, AttachmentEdits.Empty, governor, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PdfMultiPageSelectionResult> SelectPagesCoreAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        QpdfPath primaryInput,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        PdfPageCollation? collation,
        IReadOnlyList<OutputRotation> rotations,
        IReadOnlyList<PageLabelSpec> labels,
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
        bool removeMetadata,
        AttachmentEdits edits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        List<int> sourceCounts = new(sources.Count);
        List<List<(GraphCopier Copier, int Id, PdfDictionary Page, int SourcePage)>> perSource = new(sources.Count);
        int nextId = 3;
        List<GraphCopier> copiers = new(sources.Count);
        PdfVersion? version = null;
        byte[]? originalId1 = null;
        List<HashSet<int>> sourceCompressed = new(sources.Count);
        HashSet<int> allPreserved = new();
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
        HashSet<int> catalogExtraIds = new();
        long selected = 0L;
        (bool multiCompress, string multiDecode, int? multiLevel) = ResolveCompression(compression);
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

            IReadOnlyList<int> requested = sources[s].Selection.Resolve(document.PageReferences.Count);
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

            if (preserveUnreferenced)
            {
                foreach (int preserved in copier.PreserveUnreferencedObjects(document.Engine, diagnostics, cancellationToken))
                {
                    allPreserved.Add(preserved);
                }
            }

            if (s == primaryIdx)
            {
                (primaryInfo, primaryMetadata) = CopyCatalogExtras(copier, document.Engine, removeInfo, removeMetadata, diagnostics);
                if (primaryInfo is not null)
                {
                    catalogExtraIds.Add(primaryInfo.ObjectNumber);
                }

                if (primaryMetadata is not null)
                {
                    catalogExtraIds.Add(primaryMetadata.ObjectNumber);
                }

                primaryExisting = PreserveExistingAttachments(copier, document.Engine, edits, limits, governor, diagnostics, cancellationToken);
                primaryPageMode = ReadInputPageMode(document.Engine);
            }

            if (multiCompress || multiDecode != "none")
            {
                TransformCopierStreams(copier, document.Engine, multiCompress, multiDecode, compression.RecompressFlate, multiLevel, limits, governor, diagnostics, cancellationToken);
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

        ApplyRotations(occurrences, ResolveRotationFolds(rotations, kids.Count, governor), governor);

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

        AttachmentAssembly multiAttached = await AssembleAttachmentsAsync(host, primaryExisting, pool, edits, primaryPageMode, multiCompress, multiLevel, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);

        List<(int Index, PdfDictionary Label)> labelItems = ResolveLabelItems(labels, kids.Count, governor);
        PdfVersion inputMax = version ?? new PdfVersion(1, 4);
        (PdfVersion negotiatedMulti, int negotiatedMultiExtension) = NegotiateVersion(inputMax, minVersion, forceVersion);
        string multiStreamMode = ResolveObjectStreamMode(objectStreams);
        if (forceVersion is not null && CompareVersions(negotiatedMulti.Major, negotiatedMulti.Minor, 1, 5) < 0)
        {
            multiStreamMode = "disable";
        }

        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> multiSources = new(copiers.Count);
        for (int s = 0; s < copiers.Count; s++)
        {
            multiSources.Add((copiers[s].SourceToDestination, sourceCompressed[s]));
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
        List<QpdfPath> outputPaths = new();
        if (!splitPages.HasValue)
        {
            List<NumberedObject> model = new()
            {
                new NumberedObject(1, NewCatalog(labelItems.Count == 0 ? null : NumbersDictionary(labelItems), effective, extensionLevel, primaryMetadata, multiAttached.NamesRef, multiAttached.PageMode)),
                new NumberedObject(2, PagesNode(kids)),
            };
            foreach (KeyValuePair<int, QpdfObject> entry in pool)
            {
                model.Add(new NumberedObject(entry.Key, entry.Value));
            }

            await PublishModelAsync(host, effective, extensionLevel, model, multiStreams, multiCompress, multiLevel, primaryInfo?.ObjectNumber, originalId1, staticId, newlineBeforeEndstream, target, limits, governor, cancellationToken).ConfigureAwait(false);
            outputPaths.Add(target);
            return new PdfMultiPageSelectionResult(kids.Count, mapping, sourceCounts, outputPaths, [.. diagnostics.Entries], governor.Snapshot());
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

            List<int> roots = new(groupIds);
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

            int? groupInfo = null;
            if (primaryInfo is not null && renumber.TryGetValue(primaryInfo.ObjectNumber, out int mappedInfo))
            {
                groupInfo = mappedInfo;
            }

            List<NumberedObject> sub = new()
            {
                new NumberedObject(1, NewCatalog(SlicePageLabels(labelItems, first, last), effective, extensionLevel, groupMetadata, groupNames, multiAttached.PageMode)),
                new NumberedObject(2, PagesNode(renumberedKids)),
            };
            foreach (int id in kept)
            {
                sub.Add(new NumberedObject(renumber[id], RemapIds(pool[id], renumber, governor)));
            }

            QpdfPath path = QpdfPath.From(names[g]);
            HashSet<int> groupCompressed = RemapCompressedSet(multiCompressed, renumber);
            List<List<int>> groupGroups = PlanObjectStreamGroups(groupCompressed, governor);
            IReadOnlyList<IReadOnlyList<int>>? groupStreams = groupGroups.Count == 0 ? null : groupGroups;
            await PublishModelAsync(host, effective, extensionLevel, sub, groupStreams, multiCompress, multiLevel, groupInfo, originalId1, staticId, newlineBeforeEndstream, path, limits, governor, cancellationToken).ConfigureAwait(false);
            outputPaths.Add(path);
        }

        return new PdfMultiPageSelectionResult(kids.Count, mapping, sourceCounts, outputPaths, [.. diagnostics.Entries], governor.Snapshot());
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
        ResourceGovernor governor)
    {
        List<List<(PdfRotationMode Mode, int Angle)>> folds = new(total);
        for (int i = 0; i < total; i++)
        {
            folds.Add(new List<(PdfRotationMode Mode, int Angle)>());
        }

        foreach (OutputRotation specification in rotations)
        {
            foreach (int position in specification.Expand(total))
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

    private static PdfDictionary NewCatalog(PdfDictionary? pageLabels)
    {
        return NewCatalog(pageLabels, new PdfVersion(1, 4), 0, null, null, null);
    }

    private static PdfDictionary NewCatalog(PdfDictionary? pageLabels, PdfVersion finalVersion, int extensionLevel, PdfReference? metadataRef, PdfReference? namesRef, string? pageMode)
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

    private static QpdfObject RemapIds(QpdfObject value, Dictionary<int, int> renumber, ResourceGovernor governor)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        governor.ChargeWork(1L);
        if (value is PdfReference reference)
        {
            if (!renumber.TryGetValue(reference.ObjectNumber, out int mapped))
            {
                throw new QpdfException($"Split publication reached unmapped object {reference.ObjectNumber}.");
            }

            return new PdfReference(mapped, 0);
        }

        if (value is PdfArray array)
        {
            List<QpdfObject> items = new(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                items.Add(RemapIds(array[i], renumber, governor));
            }

            return new PdfArray(items);
        }

        if (value is PdfDictionary dictionary)
        {
            Dictionary<PdfName, QpdfObject> entries = new();
            foreach (PdfName key in dictionary.Keys)
            {
                if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null)
                {
                    entries[key] = RemapIds(entry, renumber, governor);
                }
            }

            return new PdfDictionary(entries);
        }

        if (value is PdfStream stream)
        {
            if (RemapIds(stream.Dictionary, renumber, governor) is not PdfDictionary remapped)
            {
                throw new QpdfException("Split publication reached a non-dictionary stream header.");
            }

            byte[] data = stream.Data;
            byte[] preserved = new byte[data.Length];
            Buffer.BlockCopy(data, 0, preserved, 0, data.Length);
            return new PdfStream(remapped, preserved);
        }

        return value;
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

        byte[] id1;
        byte[] id2;
        if (staticId)
        {
            id2 = (byte[])StaticIdBytes.Clone();
            id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
            await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, path, limits, governor, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] placeholderId1 = originalId1 is null ? (byte[])PlaceholderIdBytes.Clone() : (byte[])originalId1.Clone();
        byte[] placeholderId2 = (byte[])PlaceholderIdBytes.Clone();
        PdfSerializer probe = new(limits, governor);
        MemorySink memory = new(limits, governor);
        if (objectStreamGroups is null || objectStreamGroups.Count == 0)
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
        await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, path, limits, governor, cancellationToken).ConfigureAwait(false);
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
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        PdfSerializer serializer = new(limits, governor);
        CountingSink counter = new();
        long counted;
        if (objectStreamGroups is null || objectStreamGroups.Count == 0)
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
        long written;
        if (objectStreamGroups is null || objectStreamGroups.Count == 0)
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

    private static (bool Compress, string Decode, int? Level) ResolveCompression(CompressionRequest request)
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
            ?? ((request.StreamData == "compress" || request.StreamData == "uncompress") ? "generalized" : "none");
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
        if (decode == "generalized")
        {
            return name == FlateFilter.FilterName || name == LzwFilter.FilterName
                || name == AsciiHexFilter.FilterName || name == Ascii85Filter.FilterName;
        }

        if (decode == "specialized" || decode == "all")
        {
            return name == FlateFilter.FilterName || name == LzwFilter.FilterName
                || name == AsciiHexFilter.FilterName || name == Ascii85Filter.FilterName
                || name == RunLengthFilter.FilterName;
        }

        return false;
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
        if (alreadyFlate && !recompressFlate)
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

    private sealed record AttachmentAssembly(PdfReference? NamesRef, string? PageMode, HashSet<int> ObjectIds);

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

        HashSet<int> ids = new();
        if (!edits.HasEdits)
        {
            return new AttachmentAssembly(null, null, ids);
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

        if (existing.Count == 0)
        {
            return new AttachmentAssembly(null, null, ids);
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
        string? pageMode = edits.Adds.Count > 0 ? inputPageMode ?? "UseAttachments" : inputPageMode;
        return new AttachmentAssembly(new PdfReference(rootId, 0), pageMode, ids);
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

    private static async ValueTask<PdfMultiPageSelectionResult> WholeFileCoreAsync(
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

        await using QpdfDocument document = await QpdfDocument.OpenCore(host, plan.Input, limits, governor, diagnostics, plan.Recovery, cancellationToken).ConfigureAwait(false);
        int total = document.PageReferences.Count;
        if (total <= 0)
        {
            throw new QpdfParseException("The input document has no pages.", 0L, null);
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
        return await SelectPagesCoreAsync(
            host,
            solo,
            plan.Output,
            plan.Input,
            limits,
            plan.Recovery,
            null,
            plan.Rotations,
            plan.Labels,
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
            plan.RemoveMetadata,
            plan.Edits(),
            governor,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
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
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        return await CheckCoreAsync(host, inputPath, options, limits, QpdfRecoveryMode.Strict, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        return await ListAttachmentsCoreAsync(host, inputPath, limits, QpdfRecoveryMode.Strict, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        return await ExtractAttachmentCoreAsync(host, inputPath, key, outputPath, limits, QpdfRecoveryMode.Strict, governor, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<QpdfAttachmentExtraction> ExtractAttachmentCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        string key,
        QpdfPath outputPath,
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

        await using QpdfDocument document = await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<QpdfCheckResult> CheckCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfCheckOptions options,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        await using QpdfDocument document = await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        List<QpdfCheckOutcome> checks = DocumentCheck.Run(document, options, limits, governor, diagnostics, cancellationToken);
        return new QpdfCheckResult(document.PageCount, document.Version, checks, [.. diagnostics.Entries], governor.Snapshot());
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
        ResourceGovernor governor = new(options.Limits, CancellationToken.None);
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
        ResourceGovernor governor = new(options.Limits, CancellationToken.None);
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
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        await using QpdfDocument document = await QpdfDocument.OpenCore(host, inputPath, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<QpdfExecutionResult> RunPlanAsync(
        IQpdfHost host,
        JobPlan plan,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
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
                    governor,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                return CompleteWithPresentation(inspected.Diagnostics, inspected.Counters, plan);
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
                    await ListAttachmentsCoreAsync(host, attachments.Input, limits, attachments.Recovery, governor, diagnostics, cancellationToken).ConfigureAwait(false);
                }

                if (attachments.Key is not null)
                {
                    QpdfPath output = attachments.Output ?? throw new QpdfParseException("An output file is required after --.", 0L, null);
                    await ExtractAttachmentCoreAsync(host, attachments.Input, attachments.Key, output, limits, attachments.Recovery, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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

        if (plan is WholeFilePlan whole)
        {
            try
            {
                PdfMultiPageSelectionResult result = await WholeFileCoreAsync(host, whole, limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
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

                    PdfMultiPageSelectionResult merged = await SelectPagesCoreAsync(
                        host,
                        resolved,
                        merge.Output,
                        merge.Input,
                        limits,
                        merge.Recovery,
                        merge.Collation,
                        merge.Rotations,
                        merge.Labels,
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
                        merge.RemoveMetadata,
                        merge.Edits(),
                        governor,
                        diagnostics,
                        cancellationToken).ConfigureAwait(false);
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
                    PdfMultiPageSelectionResult merged = await SelectPagesCoreAsync(
                        host,
                        solo,
                        target,
                        selection.Input,
                        limits,
                        selection.Recovery,
                        null,
                        selection.Rotations,
                        selection.Labels,
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
                        selection.RemoveMetadata,
                        selection.Edits(),
                        governor,
                        diagnostics,
                        cancellationToken).ConfigureAwait(false);
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
                selection.MinVersion,
                selection.ForceVersion,
                selection.DeterministicId,
                selection.StaticId,
                selection.NewlineBeforeEndstream,
                selection.ObjectStreams,
                selection.PreserveUnreferenced,
                selection.Compression(),
                selection.RemoveInfo,
                selection.RemoveMetadata,
                selection.Edits(),
                governor,
                diagnostics,
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

        return QpdfExecutionResult.Completed(effective, counters);
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

    private static QpdfExecutionCounters ZeroCounters()
    {
        return new QpdfExecutionCounters(0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L);
    }
}
