using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class CatalogCopy
{
    internal static int ReadExtensionLevel(DocumentEngine engine, ResourceGovernor governor, CancellationToken token)
        => ReadExtensionLevel(TryGetCatalog(engine), engine, governor, token);

    internal static int ReadExtensionLevel(PdfDictionary? catalog, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        if (catalog is null || !PdfDictionaryLookup.TryGet(catalog, "Extensions"u8, governor, token, out QpdfObject raw)) { return 0; }
        PdfDictionary? extensions = ResolveDictionary(raw, resolver, governor, token);
        if (extensions is null || !PdfDictionaryLookup.TryGet(extensions, "ADBE"u8, governor, token, out raw)) { return 0; }
        PdfDictionary? adbe = ResolveDictionary(raw, resolver, governor, token);
        if (adbe is null || !PdfDictionaryLookup.TryGet(adbe, "ExtensionLevel"u8, governor, token, out raw)) { return 0; }
        raw = ResolveValue(raw, resolver, governor, token) ?? PdfNull.Instance;
        return raw is PdfInteger level && level.Value > 0 && level.Value <= int.MaxValue ? (int)level.Value : 0;
    }

    internal static PdfDictionary? ResolveDictionary(QpdfObject? raw, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken token)
        => ResolveValue(raw, resolver, governor, token) as PdfDictionary;

    internal static QpdfObject? ResolveValue(QpdfObject? raw, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (raw is PdfReference reference)
        { PdfObjectLookup.TryResolve(reference, resolver, governor, token, out raw); }
        return raw;
    }

    internal static PdfReference? TryCopyOutlines(
        DocumentEngine engine,
        GraphCopier copier,
        bool preserveCatalog,
        bool linearize,
        ResourceGovernor governor,
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

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (!preserveCatalog && !linearize)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        PdfDictionary? catalog = TryGetCatalog(engine);
        if (catalog is null)
        {
            return null;
        }

        if (!catalog.TryGet(PdfName.FromAscii("Outlines"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        if (linearize && raw is not PdfReference)
        {
            throw new QpdfParseException("Linearized output with a direct outlines dictionary is not implemented.", null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        int firstId = copier.BeginCopyAttempt();
        try
        {
            PdfReference? link = copier.AddIndirectObject(raw);
            return link;
        }
        catch (QpdfParseException)
        {
            copier.RollbackCopyAttempt(firstId, cancellationToken);
            return null;
        }
        finally { copier.EndCopyAttempt(); }
    }

    internal static (PdfReference? StructTree, QpdfObject? MarkInfo) TryCopyStructure(
        DocumentEngine engine,
        GraphCopier copier,
        bool preserveCatalog,
        bool removeStructure,
        ResourceGovernor governor,
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

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (!preserveCatalog || removeStructure)
        {
            return (null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        PdfDictionary? catalog = TryGetCatalog(engine);
        if (catalog is null)
        {
            return (null, null);
        }

        PdfReference? tree = null;
        QpdfObject? mark = null;
        int firstId = copier.BeginCopyAttempt();
        try
        {
            if (catalog.TryGet(PdfName.FromAscii("StructTreeRoot"), out QpdfObject? rawTree) && rawTree is not null && rawTree is not PdfNull)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                tree = copier.AddIndirectObject(rawTree);
            }

            if (catalog.TryGet(PdfName.FromAscii("MarkInfo"), out QpdfObject? rawMark) && rawMark is not null && rawMark is not PdfNull)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                mark = copier.CopyValue(rawMark, 0);
            }
        }
        catch (QpdfParseException)
        {
            copier.RollbackCopyAttempt(firstId, cancellationToken);
            return (null, null);
        }
        finally { copier.EndCopyAttempt(); }

        return (tree, mark);
    }

    /// <summary>
    /// Copies catalog keys the writer does not rebuild explicitly (for example
    /// `/NeedsRendering`, `/PageLayout`, `/ViewerPreferences`, or `/OpenAction`)
    /// through the owning copier so ordinary selection and rewrite paths preserve
    /// them while merge keeps the primary source copy and split groups drop them.
    /// Names retain the categories other than rebuilt embedded files. Fully
    /// rebuilt keys stay out; values that fail to resolve
    /// or reach structural nodes are skipped silently like the reference, while
    /// budget exhaustion and cancellation still fail loudly.
    /// </summary>
    internal static IReadOnlyDictionary<PdfName, QpdfObject> TryCopyExtras(
        DocumentEngine engine,
        GraphCopier copier,
        bool preserveCatalog,
        bool removeAcroform,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
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

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        ownership.CheckOwner(governor);
        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        PdfNameTable<QpdfObject> extras = PdfNameTable<QpdfObject>.Create(result, governor, IntPtr.Size, cancellationToken);
        PdfDictionary? catalog = preserveCatalog ? TryGetCatalog(engine) : null;
        using PdfDictionary.EntryEnumerator entries = catalog is null ? default : catalog.GetEntryEnumerator();
        while (catalog is not null && entries.MoveNext(governor, cancellationToken))
        {
            PdfName key = entries.Current.Key;
            if (IsHandledCatalogKey(key.RawSpan))
            {
                continue;
            }

            QpdfObject raw = entries.Current.Value;
            if (raw is null || raw is PdfNull)
            {
                continue;
            }

            // Dictionaries use the field-preservation path. Array-valued forms
            // stay opaque catalog graphs, including their shared/cyclic links.
            if (key.RawSpan.SequenceEqual("AcroForm"u8)
                && (removeAcroform || AcroFormCopy.TryGetSourceForm(engine) is not null))
            {
                continue;
            }

            // Name-valued PageMode is rebuilt by attachment handling. Preserve
            // other values, including indirect roots, through the ordinary copier.
            if (key.RawSpan.SequenceEqual("PageMode"u8) && raw is PdfName)
            {
                continue;
            }

            int firstId = copier.BeginCopyAttempt();
            try
            {
                QpdfObject? copied = key.RawSpan.SequenceEqual("Names"u8)
                    ? TryCopyNames(raw, engine, copier, governor, result, cancellationToken)
                    : copier.CopyValue(raw, 0);
                if (copied is not null) { extras.Assign(result, governor, key, copied, cancellationToken); }
            }
            catch (QpdfParseException)
            {
                copier.RollbackCopyAttempt(firstId, cancellationToken);
                continue;
            }
            finally { copier.EndCopyAttempt(); }
        }
        extras.Freeze(result, governor, cancellationToken);
        result.TransferTo(ownership); return extras;
    }

    private static PdfDictionary? TryCopyNames(QpdfObject raw, DocumentEngine engine, GraphCopier copier,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        PdfDictionary? names = ResolveDictionary(raw, engine, governor, token);
        if (names is null) { return null; }
        PdfNameTable<QpdfObject>? kept = null;
        using PdfDictionary.EntryEnumerator entries = names.GetEntryEnumerator();
        while (entries.MoveNext(governor, token))
        {
            PdfName key = entries.Current.Key;
            if (key.RawSpan.SequenceEqual("EmbeddedFiles"u8)) { continue; }
            int firstId = copier.BeginCopyAttempt();
            try
            {
                QpdfObject copied = copier.CopyValue(entries.Current.Value, 1);
                kept ??= PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, token);
                kept.Assign(memory, governor, key, copied, token);
            }
            catch (QpdfParseException) { copier.RollbackCopyAttempt(firstId, token); continue; }
            finally { copier.EndCopyAttempt(); }
        }
        if (kept is null) { return null; }
        kept.Freeze(memory, governor, token);
        memory.Reserve(24L);
        return PdfDictionary.FromOwnedEntries(kept);
    }

    private static bool IsHandledCatalogKey(ReadOnlySpan<byte> name)
    {
        return name.SequenceEqual("Type"u8)
            || name.SequenceEqual("Pages"u8)
            || name.SequenceEqual("PageLabels"u8)
            || name.SequenceEqual("Outlines"u8)
            || name.SequenceEqual("StructTreeRoot"u8)
            || name.SequenceEqual("MarkInfo"u8)
            || name.SequenceEqual("Metadata"u8)
            || name.SequenceEqual("Version"u8);
    }

    private static PdfDictionary? TryGetCatalog(DocumentEngine engine)
    {
        if (!engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) || root is null || root is PdfNull)
        {
            return null;
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        if (root is PdfReference rootReference)
        {
            if (resolver.TryResolve(rootReference, out QpdfObject? resolved) && resolved is PdfDictionary resolvedCatalog)
            {
                return resolvedCatalog;
            }

            return null;
        }

        return root as PdfDictionary;
    }
}
