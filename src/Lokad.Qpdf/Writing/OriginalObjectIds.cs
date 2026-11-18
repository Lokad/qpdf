using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

internal static class OriginalObjectIds
{
    internal static (int Number, int Generation) DisplayPair(int number, int generation,
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds)
        => generation == 0 && originalIds is not null && originalIds.TryGetValue(number, out var pair)
            ? pair : (number, generation);

    internal static IReadOnlyDictionary<int, (int Number, int Generation)>? PrepareOriginalIds(QdfOptions options,
        GraphCopier primary, IReadOnlyList<GraphCopier> overlays, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!options.Enabled || options.NoOriginalIds) { return null; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(128L);
        return InvertOriginals(Maps(primary, overlays), memory, governor, token);
    }

    internal static IReadOnlyDictionary<int, (int Number, int Generation)>? PrepareOriginalIds(QdfOptions options,
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

    internal static Dictionary<int, (int Number, int Generation)> InvertOriginals(IEnumerable<IReadOnlyDictionary<int, int>> maps,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        Dictionary<int, (int Number, int Generation)> inverted = ParserContainers.CreateDictionary<int, (int Number, int Generation)>(candidate);
        foreach (IReadOnlyDictionary<int, int> map in maps)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            foreach (KeyValuePair<int, int> entry in map)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                GovernedCollections.Assign(candidate, governor, inverted, entry.Value, (entry.Key, 0), 2L * sizeof(int));
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return inverted;
    }

    internal static IReadOnlyDictionary<int, (int Number, int Generation)>? PrepareImportedIds(
        IReadOnlyDictionary<(int Number, int Generation), int>? sources, IReadOnlyDictionary<int, int>? numbering,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (sources is null) { return null; }
        Dictionary<int, (int Number, int Generation)> result = ParserContainers.CreateDictionary<int, (int, int)>(memory);
        foreach ((var pair, int bound) in sources)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int destination = bound;
            if (numbering is not null && !numbering.TryGetValue(bound, out destination)) { continue; }
            ParserContainers.EnsureEntryCapacity(memory, result, 2L * sizeof(int));
            result.Add(destination, pair);
        }
        return result;
    }

}
