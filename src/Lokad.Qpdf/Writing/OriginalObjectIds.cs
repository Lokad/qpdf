using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

internal static class OriginalObjectIds
{
    internal static IReadOnlyDictionary<int, int>? PrepareOriginalIds(QdfOptions options,
        GraphCopier primary, IReadOnlyList<GraphCopier> overlays, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!options.Enabled || options.NoOriginalIds) { return null; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(128L);
        return InvertOriginals(Maps(primary, overlays), memory, governor, token);
    }

    internal static IReadOnlyDictionary<int, int>? PrepareOriginalIds(QdfOptions options,
        IReadOnlyList<GraphCopier> sources, IReadOnlyList<GraphCopier> overlays, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!options.Enabled || options.NoOriginalIds) { return null; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(128L);
        return InvertOriginals(Maps(sources, overlays), memory, governor, token);
    }

    private static IEnumerable<IReadOnlyDictionary<int, int>> Maps(GraphCopier primary, IReadOnlyList<GraphCopier> overlays)
    {
        yield return primary.SourceToDestination;
        for (int index = 0; index < overlays.Count; index++) { yield return overlays[index].SourceToDestination; }
    }

    private static IEnumerable<IReadOnlyDictionary<int, int>> Maps(IReadOnlyList<GraphCopier> sources, IReadOnlyList<GraphCopier> overlays)
    {
        for (int index = 0; index < sources.Count; index++) { yield return sources[index].SourceToDestination; }
        for (int index = 0; index < overlays.Count; index++) { yield return overlays[index].SourceToDestination; }
    }

    internal static Dictionary<int, int> InvertOriginals(IEnumerable<IReadOnlyDictionary<int, int>> maps,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        Dictionary<int, int> inverted = ParserContainers.CreateDictionary<int, int>(candidate);
        foreach (IReadOnlyDictionary<int, int> map in maps)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            foreach (KeyValuePair<int, int> entry in map)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                GovernedCollections.Assign(candidate, governor, inverted, entry.Value, entry.Key, sizeof(int));
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return inverted;
    }

}
