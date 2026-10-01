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
        return await SelectPageCoreAsync(host, inputPath, outputPath, options, limits, QpdfRecoveryMode.Strict, [], [], governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        string countText = kids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PdfDictionary catalog = NewCatalog(BuildPageLabels(labels, kids.Count, governor));
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
        List<int> ids = new(copier.CopiedObjects.Keys);
        ids.Sort();
        foreach (int id in ids)
        {
            model.Add(new NumberedObject(id, copier.CopiedObjects[id]));
        }

        PdfSerializer serializer = new(limits, governor);
        CountingSink counter = new();
        long counted = await serializer.SerializeAsync(document.Version, model, counter, cancellationToken).ConfigureAwait(false);
        if (counted > limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), counted, limits.MaxOutputBytes);
        }

        governor.ReserveMemory(counted);
        governor.NoteHostOperation();
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(outputPath, cancellationToken).ConfigureAwait(false);
        TransactionSink sink = new(transaction, governor);
        long written = await serializer.SerializeAsync(document.Version, model, sink, cancellationToken).ConfigureAwait(false);
        if (written != counted)
        {
            throw new QpdfException($"Writer replay mismatch: counted {counted} bytes but wrote {written}.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        return await SelectPagesCoreAsync(host, frozen, outputPath, limits, QpdfRecoveryMode.Strict, null, [], [], governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        return await SelectPagesCoreAsync(host, frozen, outputPath, limits, QpdfRecoveryMode.Strict, null, specifications, [], governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
        return await SelectPagesCoreAsync(host, frozen, outputPath, limits, QpdfRecoveryMode.Strict, mergeOptions.Collation, specifications, labels, governor, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PdfMultiPageSelectionResult> SelectPagesCoreAsync(
        IQpdfHost host,
        IReadOnlyList<PdfPageSource> sources,
        QpdfPath outputPath,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        PdfPageCollation? collation,
        IReadOnlyList<OutputRotation> rotations,
        IReadOnlyList<PageLabelSpec> labels,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        List<int> sourceCounts = new(sources.Count);
        List<List<(GraphCopier Copier, int Id, PdfDictionary Page, int SourcePage)>> perSource = new(sources.Count);
        List<NumberedObject> model = new();
        int nextId = 3;
        List<GraphCopier> copiers = new(sources.Count);
        PdfVersion? version = null;
        long selected = 0L;
        for (int s = 0; s < sources.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using QpdfDocument document = await QpdfDocument.OpenCore(host, sources[s].Input, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
            sourceCounts.Add(document.PageReferences.Count);
            if (version is null || IsNewerVersion(document.Version, version))
            {
                version = document.Version;
            }

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
        foreach (GraphCopier copier in copiers)
        {
            List<int> ids = new(copier.CopiedObjects.Keys);
            ids.Sort();
            foreach (int id in ids)
            {
                model.Add(new NumberedObject(id, copier.CopiedObjects[id]));
            }
        }

        string countText = kids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        model.Insert(0, new NumberedObject(1, NewCatalog(BuildPageLabels(labels, kids.Count, governor))));
        model.Insert(1, new NumberedObject(2, new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Pages"),
            [PdfName.FromAscii("Kids")] = new PdfArray([.. kids]),
            [PdfName.FromAscii("Count")] = new PdfInteger(countText, kids.Count),
        })));

        PdfVersion effective = version ?? new PdfVersion(1, 4);
        PdfSerializer serializer = new(limits, governor);
        CountingSink counter = new();
        long counted = await serializer.SerializeAsync(effective, model, counter, cancellationToken).ConfigureAwait(false);
        if (counted > limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), counted, limits.MaxOutputBytes);
        }

        governor.ReserveMemory(counted);
        governor.NoteHostOperation();
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(outputPath, cancellationToken).ConfigureAwait(false);
        TransactionSink sink = new(transaction, governor);
        long written = await serializer.SerializeAsync(effective, model, sink, cancellationToken).ConfigureAwait(false);
        if (written != counted)
        {
            throw new QpdfException($"Writer replay mismatch: counted {counted} bytes but wrote {written}.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PdfMultiPageSelectionResult(kids.Count, mapping, sourceCounts, [.. diagnostics.Entries], governor.Snapshot());
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

    private static PdfDictionary NewCatalog(PdfDictionary? pageLabels)
    {
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Catalog"),
            [PdfName.FromAscii("Pages")] = new PdfReference(2, 0),
        };
        if (pageLabels is not null)
        {
            entries[PdfName.FromAscii("PageLabels")] = pageLabels;
        }

        return new PdfDictionary(entries);
    }

    private static PdfDictionary? BuildPageLabels(
        IReadOnlyList<PageLabelSpec> labels,
        int total,
        ResourceGovernor governor)
    {
        if (labels.Count == 0)
        {
            return null;
        }

        if (total == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(labels), "Page labels need at least one output page.");
        }

        List<(int Index, PdfDictionary Label)> items = new(labels.Count);
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
                        limits,
                        merge.Recovery,
                        merge.Collation,
                        merge.Rotations,
                        merge.Labels,
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
            PdfPageSelectionResult selected = await SelectPageCoreAsync(
                host,
                selection.Input,
                selection.Output,
                new PdfPageSelectionOptions(selection.Page),
                limits,
                selection.Recovery,
                selection.Rotations,
                selection.Labels,
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
