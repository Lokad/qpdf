using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

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
    {
        pageStorage.CheckOwner(governor);
        PdfDictionary pages = ReadRoot(resolver, root, governor, cancellationToken);
        List<PageLocation> collector = ParserContainers.CreateList<PageLocation>(pageStorage);
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        HashSet<int> path = ParserContainers.CreateSet<int>(traversal);
        WalkPages(resolver, diagnostics, governor, limits, pages, null, path, collector, pageStorage, traversal, 0, cancellationToken);
        return collector;
    }

    internal static PdfDictionary ReadRoot(IIndirectResolver resolver, QpdfObject rootRaw, ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary root = ResolveDictionary(resolver, rootRaw, "catalog", governor, token);
        PdfName? rootType = ReadType(resolver, root, governor, token);
        if (rootType is not null && rootType.RawSpan.SequenceEqual("Pages"u8))
        {
            return root;
        }
        if (rootType is not null && !rootType.RawSpan.IsEmpty && !rootType.RawSpan.SequenceEqual("Catalog"u8))
        {
            if (rootType.RawSpan.Length > 64)
            { throw new QpdfParseException("Catalog has unexpected /Type (name exceeds 64 bytes).", null, null); }
            throw new QpdfParseException($"Catalog has unexpected /Type /{rootType.DisplayText}.", null, null);
        }
        if (!PdfDictionaryLookup.TryGet(root, "Pages"u8, governor, token, out QpdfObject pagesRaw))
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

    private static void WalkPages(
        IIndirectResolver resolver,
        DiagnosticBag diagnostics,
        ResourceGovernor governor,
        QpdfLimits limits,
        PdfDictionary node,
        PdfReference? incoming,
        HashSet<int> path,
        List<PageLocation> collector,
        ParserMemoryScope pageStorage,
        ParserMemoryScope traversal,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException($"Page tree depth exceeds {limits.MaxNestingDepth}.", null, null);
        }

        governor.NoteOperation();
        PdfName? type = ReadType(resolver, node, governor, cancellationToken);
        if (type is not null && type.RawSpan.SequenceEqual("Page"u8))
        {
            ParserContainers.AddItem(pageStorage, collector, new PageLocation(incoming, node), 2L * IntPtr.Size);
            return;
        }

        if (type is null || !type.RawSpan.SequenceEqual("Pages"u8))
        {
            throw new QpdfParseException("Page tree nodes must have /Type /Page or /Pages.", null, null);
        }

        PdfArray? kids = null;
        if (PdfDictionaryLookup.TryGet(node, "Kids"u8, governor, cancellationToken, out QpdfObject kidsRaw))
        {
            if (kidsRaw is PdfReference reference)
            { PdfObjectLookup.TryResolve(reference, resolver, governor, cancellationToken, out QpdfObject? resolved); kids = resolved as PdfArray; }
            else { kids = kidsRaw as PdfArray; }
        }
        if (kids is null)
        {
            throw new QpdfParseException("Pages node must declare a /Kids array.", null, null);
        }

        long before = collector.Count;
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

                WalkPages(resolver, diagnostics, governor, limits, child, reference, path, collector, pageStorage, traversal, depth + 1, cancellationToken);
                path.Remove(reference.ObjectNumber);
                continue;
            }

            if (kid is PdfDictionary direct)
            {
                WalkPages(resolver, diagnostics, governor, limits, direct, null, path, collector, pageStorage, traversal, depth + 1, cancellationToken);
                continue;
            }

            throw new QpdfParseException("Page /Kids elements must be dictionaries or references.", null, null);
        }

        if (ReadCount(resolver, node, governor, cancellationToken) is PdfInteger declared)
        {
            long total = collector.Count - before;
            if (declared.Value != total && diagnostics.ReportPageCountMismatch)
            {
                diagnostics.AddParts(
                    QpdfDiagnosticCode.PageCountMismatch,
                    QpdfDiagnosticSeverity.Warning,
                    ["A /Pages /Count of ", declared.Value, " disagrees with traversal (", total, "); traversal wins."]);
            }
        }
    }
}
