using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

/// <summary>Recognizes appearance streams whose resource lookup still uses their page.</summary>
internal static class AppearanceResources
{
    internal static bool PageNeedsResources(PdfDictionary page, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Annots"u8, governor, token, out QpdfObject raw)
            || CatalogCopy.ResolveValue(raw, resolver, governor, token) is not PdfArray annotations) { return false; }
        for (int index = 0; index < annotations.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (CatalogCopy.ResolveDictionary(annotations[index], resolver, governor, token) is PdfDictionary annotation
                && NeedsPageResources(annotation, resolver, governor, token)) { return true; }
        }
        return false;
    }

    internal static bool NeedsPageResources(PdfDictionary annotation, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(annotation, "AP"u8, governor, token, out QpdfObject raw)
            || CatalogCopy.ResolveDictionary(raw, resolver, governor, token) is not PdfDictionary appearances) { return false; }
        using PdfDictionary.EntryEnumerator entries = appearances.GetEntryEnumerator();
        while (entries.MoveNext(governor, token))
        {
            ReadOnlySpan<byte> key = entries.Current.Key.RawSpan;
            if (!key.SequenceEqual("N"u8) && !key.SequenceEqual("R"u8) && !key.SequenceEqual("D"u8)) { continue; }
            QpdfObject? target = CatalogCopy.ResolveValue(entries.Current.Value, resolver, governor, token);
            if (target is PdfStream stream)
            {
                if (UsesPageResources(stream, resolver, governor, token)) { return true; }
            }
            else if (target is PdfDictionary states)
            {
                // Appearance states add exactly one dictionary level. Other
                // nested values, including cycles, do not denote stream states.
                using PdfDictionary.EntryEnumerator values = states.GetEntryEnumerator();
                while (values.MoveNext(governor, token))
                {
                    if (CatalogCopy.ResolveValue(values.Current.Value, resolver, governor, token) is PdfStream state
                        && UsesPageResources(state, resolver, governor, token)) { return true; }
                }
            }
        }
        return false;
    }

    internal static bool UsesPageResources(PdfStream stream, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token)
        => !PdfDictionaryLookup.TryGet(stream.Dictionary, "Resources"u8, governor, token, out QpdfObject raw)
            || CatalogCopy.ResolveValue(raw, resolver, governor, token) is null or PdfNull;
}
