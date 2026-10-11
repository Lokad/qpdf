using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class CatalogCopy
{
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
        governor.ChargeWork(1L);
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
        governor.ChargeWork(1L);
        try
        {
            PdfReference? link = copier.AddIndirectObject(raw);
            return link;
        }
        catch (QpdfParseException)
        {
            return null;
        }
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
        governor.ChargeWork(1L);
        PdfDictionary? catalog = TryGetCatalog(engine);
        if (catalog is null)
        {
            return (null, null);
        }

        PdfReference? tree = null;
        QpdfObject? mark = null;
        try
        {
            if (catalog.TryGet(PdfName.FromAscii("StructTreeRoot"), out QpdfObject? rawTree) && rawTree is not null && rawTree is not PdfNull)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                tree = copier.AddIndirectObject(rawTree);
            }

            if (catalog.TryGet(PdfName.FromAscii("MarkInfo"), out QpdfObject? rawMark) && rawMark is not null && rawMark is not PdfNull)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                mark = copier.CopyValue(rawMark, 0);
            }
        }
        catch (QpdfParseException)
        {
            return (null, null);
        }

        return (tree, mark);
    }

    /// <summary>
    /// Copies catalog keys the writer does not rebuild explicitly (for example
    /// `/NeedsRendering`, `/PageLayout`, `/ViewerPreferences`, or `/OpenAction`)
    /// through the owning copier so ordinary selection and rewrite paths preserve
    /// them while merge keeps the primary source copy and split groups drop them.
    /// Keys with explicit writer handling stay out; values that fail to resolve
    /// or reach structural nodes are skipped silently like the reference, while
    /// budget exhaustion and cancellation still fail loudly.
    /// </summary>
    internal static Dictionary<PdfName, QpdfObject> TryCopyExtras(
        DocumentEngine engine,
        GraphCopier copier,
        bool preserveCatalog,
        bool removeAcroform,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        Dictionary<PdfName, QpdfObject> extras = new();
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

        if (!preserveCatalog)
        {
            return extras;
        }

        cancellationToken.ThrowIfCancellationRequested();
        governor.ChargeWork(1L);
        PdfDictionary? catalog = TryGetCatalog(engine);
        if (catalog is null)
        {
            return extras;
        }

        foreach (PdfName key in catalog.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (IsHandledCatalogKey(key.DisplayText))
            {
                continue;
            }

            if (!catalog.TryGet(key, out QpdfObject? raw) || raw is null || raw is PdfNull)
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

            try
            {
                extras[key] = copier.CopyValue(raw, 0);
            }
            catch (QpdfParseException)
            {
                continue;
            }
        }
        return extras;
    }

    private static bool IsHandledCatalogKey(string name)
    {
        return name == "Type"
            || name == "Pages"
            || name == "PageLabels"
            || name == "Outlines"
            || name == "StructTreeRoot"
            || name == "MarkInfo"
            || name == "Metadata"
            || name == "Names"
            || name == "Version"
            || name == "Extensions";
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
