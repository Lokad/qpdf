using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Content;

/// <summary>Builds fresh resource containers while borrowing immutable keys, values and payloads.</summary>
internal static class ResourceRebuild
{
    internal static PdfDictionary ReplaceResources(OwnedAllocationBatch graph, PdfDictionary source, PdfDictionary resources)
        => PdfDictionaryRebuild.ReplaceEntry(graph, source, "Resources"u8, resources);

    internal static PdfStream ReplaceResources(OwnedAllocationBatch graph, PdfStream source, PdfDictionary resources)
    {
        PdfDictionary dictionary = ReplaceResources(graph, source.Dictionary, resources);
        graph.Memory.Reserve(32L); PdfStream stream = source.WithDictionary(dictionary);
        graph.Add(32L); return stream;
    }
}
