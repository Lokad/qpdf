using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

internal readonly record struct PageTreeRoleSet(HashSet<int> Numbers, ParserMemoryScope Memory)
{
    internal void Include(QpdfObject edge)
    {
        if (edge is PdfReference reference)
        { ParserContainers.AddSetItem(Memory, Numbers, reference.ObjectNumber, sizeof(int)); }
    }
}

/// <summary>Bounded page-tree reading shared by documents and imported models.</summary>
internal static class PageTree
{
    internal static List<PageLocation> Read(
        IIndirectResolver resolver,
        QpdfObject root,
        DiagnosticBag diagnostics,
        ResourceGovernor governor,
        QpdfLimits limits,
        ParserMemoryScope pageStorage,
        CancellationToken cancellationToken)
        => Read(resolver, root, diagnostics, governor, limits, pageStorage, null, cancellationToken);

    internal static List<PageLocation> Read(IIndirectResolver resolver, QpdfObject root,
        DiagnosticBag diagnostics, ResourceGovernor governor, QpdfLimits limits, ParserMemoryScope pageStorage,
        PageTreeRoleSet? roles, CancellationToken cancellationToken)
        => ReadCore(resolver, root, diagnostics, governor, limits, pageStorage, null, roles, cancellationToken);

    internal static List<PageLocation> Rewrite(
        Dictionary<int, QpdfObject> pool, int catalogNumber, IIndirectResolver resolver,
        Func<PdfDictionary, PdfDictionary> rewritePage, Func<QpdfObject, PdfReference> addObject,
        DiagnosticBag diagnostics, ResourceGovernor governor,
        QpdfLimits limits, ParserMemoryScope pageStorage, ParserMemoryScope valueStorage, CancellationToken token)
    {
        valueStorage.CheckOwner(governor);
        using OwnedAllocationBatch changes = OwnedAllocationBatch.Create(governor, token);
        using ParserMemoryScope editMemory = ParserMemoryScope.Create(governor);
        editMemory.Reserve(48L);
        LeafEdit edit = new(pool, rewritePage, addObject, changes);
        PdfDictionary root = (PdfDictionary)pool[catalogNumber];
        List<PageLocation> pages = ReadCore(resolver, root, diagnostics, governor, limits, pageStorage, edit, null, token);
        if (edit.Catalog is PdfDictionary catalog) { pool[catalogNumber] = catalog; }
        changes.Memory.TransferTo(valueStorage, changes.Bytes);
        return pages;
    }

    private sealed record LeafEdit(
        Dictionary<int, QpdfObject> Pool, Func<PdfDictionary, PdfDictionary> Rewrite,
        Func<QpdfObject, PdfReference> AddObject, OwnedAllocationBatch Changes)
    {
        internal PdfDictionary? Catalog { get; set; }
    }

    private static List<PageLocation> ReadCore(IIndirectResolver resolver, QpdfObject root, DiagnosticBag diagnostics,
        ResourceGovernor governor, QpdfLimits limits, ParserMemoryScope pageStorage, LeafEdit? edit,
        PageTreeRoleSet? roles, CancellationToken cancellationToken)
    {
        pageStorage.CheckOwner(governor);
        PdfDictionary pages = ReadRoot(resolver, root, governor, cancellationToken, out PdfDictionary catalog, out QpdfObject pagesEdge);
        if (roles is { } retained)
        {
            retained.Memory.CheckOwner(governor);
            retained.Include(root);
            retained.Include(pagesEdge);
        }
        List<PageLocation> collector = ParserContainers.CreateList<PageLocation>(pageStorage);
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        HashSet<int> path = ParserContainers.CreateSet<int>(traversal);
        Dictionary<PdfDictionary, int>? emptySubtrees = null;
        PdfDictionary original = pages;
        WalkPages(resolver, diagnostics, governor, limits, ref pages, null, path, collector, pageStorage, traversal,
            ref emptySubtrees, 0, edit, roles, cancellationToken);
        if (edit is not null && !ReferenceEquals(original, pages))
        {
            if (pagesEdge is PdfReference reference) { edit.Pool[reference.ObjectNumber] = pages; }
            else if (ReferenceEquals(original, catalog)) { edit.Catalog = pages; }
            else { edit.Catalog = PdfDictionaryRebuild.ReplaceEntry(edit.Changes, catalog, "Pages"u8, pages); }
        }
        return collector;
    }

    internal static PdfDictionary ReadRoot(IIndirectResolver resolver, QpdfObject rootRaw, ResourceGovernor governor, CancellationToken token)
        => ReadRoot(resolver, rootRaw, governor, token, out _, out _);

    private static PdfDictionary ReadRoot(IIndirectResolver resolver, QpdfObject rootRaw, ResourceGovernor governor,
        CancellationToken token, out PdfDictionary root, out QpdfObject pagesRaw)
    {
        root = ResolveDictionary(resolver, rootRaw, "catalog", governor, token);
        PdfName? rootType = ReadType(resolver, root, governor, token);
        if (rootType is not null && rootType.RawSpan.SequenceEqual("Pages"u8))
        {
            pagesRaw = rootRaw;
            return root;
        }
        if (rootType is not null && !rootType.RawSpan.IsEmpty && !rootType.RawSpan.SequenceEqual("Catalog"u8))
        {
            if (rootType.RawSpan.Length > 64)
            { throw new QpdfParseException("Catalog has unexpected /Type (name exceeds 64 bytes).", null, null); }
            throw new QpdfParseException($"Catalog has unexpected /Type /{rootType.DisplayText}.", null, null);
        }
        if (!PdfDictionaryLookup.TryGet(root, "Pages"u8, governor, token, out pagesRaw))
        { throw new QpdfParseException("Catalog must declare /Pages.", null, null); }
        return ResolveDictionary(resolver, pagesRaw, "page tree", governor, token);
    }

    private static PdfDictionary ResolveDictionary(IIndirectResolver resolver, QpdfObject raw, string role, ResourceGovernor governor, CancellationToken token)
    {
        if (raw is PdfDictionary direct)
        {
            return direct;
        }

        if (raw is PdfReference reference)
        {
            if (!PdfObjectLookup.TryResolve(reference, resolver, governor, token, out QpdfObject? resolved) || resolved is null)
            {
                throw new QpdfParseException($"Unresolvable {role} reference.", null, reference.ObjectNumber);
            }

            if (resolved is PdfDictionary dictionary)
            {
                return dictionary;
            }

            throw new QpdfParseException($"The {role} reference does not resolve to a dictionary.", null, reference.ObjectNumber);
        }

        throw new QpdfParseException($"The {role} must be a dictionary or a reference.", null, null);
    }

    internal static PdfName? ReadType(IIndirectResolver resolver, PdfDictionary dictionary, ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "Type"u8, governor, token, out QpdfObject raw) || raw is PdfNull)
        {
            return null;
        }

        QpdfObject? resolved = raw;
        if (raw is PdfReference reference && !PdfObjectLookup.TryResolve(reference, resolver, governor, token, out resolved))
        { throw new QpdfParseException("Unresolvable /Type reference.", null, reference.ObjectNumber); }
        if (resolved is PdfNull) { return null; }
        if (resolved is not PdfName name)
        {
            throw new QpdfParseException("The /Type entry must be a name.", null, null);
        }

        return name;
    }

    internal static PdfInteger? ReadCount(IIndirectResolver resolver, PdfDictionary dictionary, ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "Count"u8, governor, token, out QpdfObject raw)) { return null; }
        if (raw is not PdfReference reference) { return raw as PdfInteger; }
        return PdfObjectLookup.TryResolve(reference, resolver, governor, token, out QpdfObject? resolved) ? resolved as PdfInteger : null;
    }

    private static int WalkPages(
        IIndirectResolver resolver,
        DiagnosticBag diagnostics,
        ResourceGovernor governor,
        QpdfLimits limits,
        ref PdfDictionary node,
        PdfReference? incoming,
        HashSet<int> path,
        List<PageLocation> collector,
        ParserMemoryScope pageStorage,
        ParserMemoryScope traversal,
        ref Dictionary<PdfDictionary, int>? emptySubtrees,
        int depth,
        LeafEdit? edit,
        PageTreeRoleSet? roles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, limits.MaxNestingDepth);
        }

        governor.NoteOperation();
        governor.CheckCancellation();
        if (incoming is not null) { roles?.Include(incoming); }
        if (emptySubtrees is not null && emptySubtrees.TryGetValue(node, out int remainingDepth))
        {
            long deepest = (long)depth + remainingDepth;
            if (deepest > limits.MaxNestingDepth)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), deepest, limits.MaxNestingDepth); }
            return remainingDepth;
        }
        long diagnosticsBefore = governor.CounterValues.DiagnosticCount;
        PdfName? type = ReadType(resolver, node, governor, cancellationToken);
        if (type is not null && type.RawSpan.SequenceEqual("Page"u8))
        {
            if (edit is not null) { node = edit.Rewrite(node); }
            ParserContainers.AddItem(pageStorage, collector, new PageLocation(incoming, node), 2L * IntPtr.Size);
            return 0;
        }

        if (type is null || !type.RawSpan.SequenceEqual("Pages"u8))
        {
            throw new QpdfParseException("Page tree nodes must have /Type /Page or /Pages.", null, null);
        }

        PdfArray? kids = null;
        if (PdfDictionaryLookup.TryGet(node, "Kids"u8, governor, cancellationToken, out QpdfObject kidsRaw))
        {
            if (kidsRaw is PdfReference reference)
            {
                PdfObjectLookup.TryResolve(reference, resolver, governor, cancellationToken, out QpdfObject? resolved);
                kids = resolved as PdfArray;
                if (kids is not null) { roles?.Include(reference); }
            }
            else { kids = kidsRaw as PdfArray; }
        }
        if (kids is null)
        {
            throw new QpdfParseException("Pages node must declare a /Kids array.", null, null);
        }

        long before = collector.Count;
        int height = 0;
        QpdfObject[]? rewritten = null;
        for (int i = 0; i < kids.Count; i++)
        {
            QpdfObject kid = kids[i];
            if (kid is PdfReference reference)
            {
                if (!ParserContainers.AddSetItem(traversal, path, reference.ObjectNumber, sizeof(int)))
                {
                    throw new QpdfParseException($"Page tree cycle at object {reference.ObjectNumber}.", null, reference.ObjectNumber);
                }

                if (!PdfObjectLookup.TryResolve(reference, resolver, governor, cancellationToken, out QpdfObject? kidResolved) || kidResolved is not PdfDictionary child)
                {
                    throw new QpdfParseException($"Page kid does not resolve to a dictionary.", null, reference.ObjectNumber);
                }

                PdfDictionary originalChild = child;
                int childHeight = WalkPages(resolver, diagnostics, governor, limits, ref child, reference, path, collector,
                    pageStorage, traversal, ref emptySubtrees, depth + 1, edit, roles, cancellationToken);
                if (edit is not null && !ReferenceEquals(child, originalChild)) { edit.Pool[reference.ObjectNumber] = child; }
                height = Math.Max(height, childHeight + 1);
                path.Remove(reference.ObjectNumber);
                continue;
            }

            if (kid is PdfDictionary direct)
            {
                int pageIndex = collector.Count;
                PdfDictionary edited = direct;
                int childHeight = WalkPages(resolver, diagnostics, governor, limits, ref edited, null, path, collector,
                    pageStorage, traversal, ref emptySubtrees, depth + 1, edit, roles, cancellationToken);
                if (edit is not null && !ReferenceEquals(direct, edited))
                {
                    rewritten ??= CopyKids(kids, edit.Changes, governor, cancellationToken);
                    rewritten[i] = edited;
                    if (ReadType(resolver, edited, governor, cancellationToken) is PdfName editedType
                        && editedType.RawSpan.SequenceEqual("Page"u8))
                    {
                        // The edited page owns its new program through a live
                        // indirect identity. The original direct value can
                        // still be reached through an independent Kids alias.
                        PdfReference promoted = edit.AddObject(edited);
                        collector[pageIndex] = new PageLocation(promoted, edited);
                        rewritten[i] = promoted;
                    }
                }
                height = Math.Max(height, childHeight + 1);
                continue;
            }

            throw new QpdfParseException("Page /Kids elements must be dictionaries or references.", null, null);
        }

        if (ReadCount(resolver, node, governor, cancellationToken) is PdfInteger declared)
        {
            long total = collector.Count - before;
            // A partial walk must validate every declaration before acceptance,
            // even when the complete native-style inspection suppresses warnings.
            if (declared.Value != total && (diagnostics.ReportPageCountMismatch || diagnostics.ReadingPartialInput))
            {
                diagnostics.AddParts(
                    QpdfDiagnosticCode.PageCountMismatch,
                    QpdfDiagnosticSeverity.Warning,
                    ["A /Pages /Count of ", declared.Value, " disagrees with traversal (", total, "); traversal wins."]);
            }
        }
        // Real page occurrences fund their expansion. Only quiet empty subtrees
        // can multiply work without growing either pages or stored diagnostics.
        if (collector.Count == before && governor.CounterValues.DiagnosticCount == diagnosticsBefore)
        {
            emptySubtrees ??= ParserContainers.CreateDictionary<PdfDictionary, int>(traversal, ReferenceEqualityComparer.Instance);
            ParserContainers.EnsureEntryCapacity(traversal, emptySubtrees, sizeof(int));
            emptySubtrees.Add(node, height);
        }
        if (rewritten is not null && edit is not null)
        {
            // Replace the active Kids edge. Private references can still see
            // the original array, including any unchanged direct children.
            node = PdfDictionaryRebuild.ReplaceEntry(edit.Changes, node, "Kids"u8, PdfArray.FromOwnedItems(rewritten));
        }
        return height;
    }

    private static QpdfObject[] CopyKids(PdfArray kids, OwnedAllocationBatch changes,
        ResourceGovernor governor, CancellationToken token)
    {
        long size = 56L + IntPtr.Size * (long)kids.Count;
        changes.Memory.Reserve(size); changes.Add(size);
        QpdfObject[] copy = new QpdfObject[kids.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            copy[index] = kids[index];
        }
        return copy;
    }
}
