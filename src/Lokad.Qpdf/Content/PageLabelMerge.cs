using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>
/// One source number-tree entry with resolved values. Indirect style and
/// prefix references keep their source identity for the redundancy comparison
/// but embed pool copies, so merged trees never dangle into source numbering.
/// </summary>
internal sealed record SourceLabelEntry(
    PdfReference? StyleReference,
    PdfReference? PrefixReference,
    QpdfObject? Style,
    QpdfObject? Prefix,
    long Start);

/// <summary>
/// One source document's resolved page-label tree.
/// </summary>
internal sealed record SourceLabels(bool HasTree, SortedDictionary<int, SourceLabelEntry> Entries);

/// <summary>
/// Source page-label preservation through the shared copy engine, mirroring
/// the reference merge behavior: per-output-page label resolution over each
/// source number tree in output order, with fabricated continuation entries
/// for pages without a covering entry, run compression against the previous
/// entry, and explicit starts on every computed entry. Whole-file rewrites
/// instead carry the source tree verbatim.
/// </summary>
internal static class PageLabelMerge
{
    internal static bool HasLabels(DocumentEngine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        return TryGetCatalog(engine) is PdfDictionary catalog
            && catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject? labels)
            && labels is not null
            && labels is not PdfNull
            && ResolveTree((IIndirectResolver)engine, labels) is PdfDictionary;
    }

    internal static QpdfObject? TryGetRawTree(DocumentEngine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (TryGetCatalog(engine) is not PdfDictionary catalog)
        {
            return null;
        }

        if (!catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject? labels) || labels is null || labels is PdfNull)
        {
            return null;
        }

        return labels;
    }

    internal static SourceLabels ReadSource(
        DocumentEngine engine,
        GraphCopier copier,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
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
        bool hasTree = HasLabels(engine);
        SortedDictionary<int, QpdfObject> raw = hasTree
            ? DocumentJson.ReadLabelTree(engine, limits, governor, diagnostics, cancellationToken)
            : new SortedDictionary<int, QpdfObject>();
        IIndirectResolver resolver = (IIndirectResolver)engine;
        using ParserMemoryScope metadata = ParserMemoryScope.Create(governor);
        metadata.Reserve(96L);
        SortedDictionary<int, SourceLabelEntry> entries = new();
        foreach (KeyValuePair<int, QpdfObject> pair in raw)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            metadata.Reserve(128L); // Number-tree node and source-label metadata.
            entries[pair.Key] = ResolveEntry(resolver, copier, pair.Value, governor, cancellationToken);
        }

        SourceLabels result = new(hasTree, entries); metadata.RetainGraph(); return result;
    }

    internal static List<(int Index, PdfDictionary Label)> Merge(
        IReadOnlyList<(SourceLabels Source, int Page)> occurrences,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        ParserMemoryScope memory)
    {
        memory.CheckOwner(governor);
        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
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
        List<(int Index, PdfDictionary Label)> merged = ParserContainers.CreateList<(int Index, PdfDictionary Label)>(memory);
        for (int k = 0; k < occurrences.Count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            (SourceLabels source, int page) = occurrences[k];
            int index = page - 1;
            (long start, QpdfObject? styleIdentity, QpdfObject? prefixIdentity, QpdfObject? style, QpdfObject? prefix) = Peek(source.Entries, index, k, governor, cancellationToken);
            if (merged.Count > 0)
            {
                (int lastIndex, PdfDictionary lastLabel) = merged[merged.Count - 1];
                if (SameValue(LabelValue(lastLabel, "S"), styleIdentity, governor, cancellationToken) && SameValue(LabelValue(lastLabel, "P"), prefixIdentity, governor, cancellationToken)
                    && lastLabel.TryGet(PdfName.FromAscii("St"), out QpdfObject? lastRaw) && lastRaw is PdfInteger lastStart)
                {
                    bool redundant;
                    try
                    {
                        checked
                        {
                            redundant = start - lastStart.Value == (long)k - (long)lastIndex;
                        }
                    }
                    catch (OverflowException ex)
                    {
                        throw new QpdfParseException("Page-label start overflows its output position.", null, null, ex);
                    }

                    if (redundant)
                    {
                        continue;
                    }
                }
            }

            PdfDictionary label = PageLabelStorage.Merged(style, prefix, start, governor, cancellationToken);
            GovernedCollections.AddItem(memory, governor, merged, (k, label), 16L);
        }

        return merged;
    }

    private static (long Start, QpdfObject? StyleIdentity, QpdfObject? PrefixIdentity, QpdfObject? Style, QpdfObject? Prefix) Peek(
        SortedDictionary<int, SourceLabelEntry> entries,
        int pageIndex,
        int outputIndex,
        ResourceGovernor governor,
        CancellationToken token)
    {
        int key = -1;
        SourceLabelEntry? covering = null;
        foreach (KeyValuePair<int, SourceLabelEntry> pair in entries)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (pair.Key > pageIndex)
            {
                break;
            }

            key = pair.Key;
            covering = pair.Value;
        }

        if (covering is null)
        {
            long fabricated;
            try
            {
                checked
                {
                    fabricated = 1L + (long)outputIndex;
                }
            }
            catch (OverflowException ex)
            {
                throw new QpdfParseException("Page-label start overflows its output position.", null, null, ex);
            }

            return (fabricated, null, null, null, null);
        }

        long start;
        try
        {
            checked
            {
                start = covering.Start + (long)(pageIndex - key);
            }
        }
        catch (OverflowException ex)
        {
            throw new QpdfParseException("Page-label start overflows its output position.", null, null, ex);
        }

        return (start, (QpdfObject?)covering.StyleReference ?? covering.Style, (QpdfObject?)covering.PrefixReference ?? covering.Prefix, covering.Style, covering.Prefix);
    }

    private static SourceLabelEntry ResolveEntry(
        IIndirectResolver resolver,
        GraphCopier copier,
        QpdfObject value,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfDictionary? dictionary = value as PdfDictionary;
        if (dictionary is null && value is PdfReference reference
            && resolver.TryResolve(reference, out QpdfObject? resolved)
            && resolved is PdfDictionary resolvedDictionary)
        {
            dictionary = resolvedDictionary;
        }

        if (dictionary is null)
        {
            return new SourceLabelEntry(null, null, null, null, 1L);
        }

        (PdfReference? styleReference, QpdfObject? style) = SplitLink(resolver, copier, dictionary, "S", governor, cancellationToken);
        (PdfReference? prefixReference, QpdfObject? prefix) = SplitLink(resolver, copier, dictionary, "P", governor, cancellationToken);
        long start = 1L;
        if (dictionary.TryGet(PdfName.FromAscii("St"), out QpdfObject? rawStart) && rawStart is PdfInteger startInteger)
        {
            start = startInteger.Value;
        }

        return new SourceLabelEntry(styleReference, prefixReference, style, prefix, start);
    }

    private static (PdfReference? Reference, QpdfObject? Value) SplitLink(
        IIndirectResolver resolver,
        GraphCopier copier,
        PdfDictionary dictionary,
        string key,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return (null, null);
        }

        if (raw is not PdfReference reference)
        {
            if (raw is PdfDictionary direct && IsPageTreeNode(direct))
            {
                return (null, null);
            }

            return (null, raw);
        }

        if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
        {
            return (reference, null);
        }

        if (resolved is PdfDictionary target && IsPageTreeNode(target))
        {
            return (reference, null);
        }

        governor.NoteOperation();
        return (reference, copier.CopyValue(raw, 0));
    }

    private static bool IsPageTreeNode(PdfDictionary dictionary)
    {
        if (!dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return false;
        }

        return raw is PdfName name && (name.DisplayText == "Page" || name.DisplayText == "Pages" || name.DisplayText == "Catalog");
    }

    internal static bool SameValue(QpdfObject? left, QpdfObject? right, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (left is PdfNull) { left = null; }
        if (right is PdfNull) { right = null; }
        if (ReferenceEquals(left, right)) { return true; }
        if (left is null || right is null || left.GetType() != right.GetType()) { return false; }
        return (left, right) switch
        {
            (PdfReference a, PdfReference b) => a.ObjectNumber == b.ObjectNumber && a.Generation == b.Generation,
            (PdfName a, PdfName b) => SameBytes(a.RawSpan, b.RawSpan, governor, token),
            (PdfByteString a, PdfByteString b) => SameBytes(a.ValueSpan, b.ValueSpan, governor, token),
            (PdfInteger a, PdfInteger b) => a.Value == b.Value,
            (PdfBoolean a, PdfBoolean b) => a.Value == b.Value,
            (PdfReal a, PdfReal b) => string.Equals(a.Text, b.Text, StringComparison.Ordinal),
            // Preserve the prior conservative grouping of malformed container values.
            _ => true,
        };
    }

    private static bool SameBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, ResourceGovernor governor, CancellationToken token)
    {
        if (left.Length != right.Length) { return false; }
        for (int at = 0; at < left.Length; at += Math.Min(8192, left.Length - at))
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int count = Math.Min(8192, left.Length - at);
            if (!left.Slice(at, count).SequenceEqual(right.Slice(at, count))) { return false; }
        }
        return true;
    }

    private static QpdfObject? LabelValue(PdfDictionary label, string key)
        => label.TryGet(PdfName.FromAscii(key), out QpdfObject? value) ? value : null;

    private static PdfDictionary? TryGetCatalog(DocumentEngine engine)
    {
        if (!engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) || root is null || root is PdfNull)
        {
            return null;
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        PdfDictionary? catalog = root as PdfDictionary;
        if (catalog is null && root is PdfReference rootReference
            && resolver.TryResolve(rootReference, out QpdfObject? resolvedRoot)
            && resolvedRoot is PdfDictionary resolvedCatalog)
        {
            catalog = resolvedCatalog;
        }

        return catalog;
    }

    private static QpdfObject? ResolveTree(IIndirectResolver resolver, QpdfObject labels)
    {
        if (labels is PdfReference labelsReference
            && resolver.TryResolve(labelsReference, out QpdfObject? resolvedLabels))
        {
            return resolvedLabels;
        }

        return labels;
    }
}
