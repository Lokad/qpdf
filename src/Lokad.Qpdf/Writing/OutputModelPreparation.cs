using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

internal readonly record struct OutputGraph(PdfDictionary Catalog, PdfDictionary Pages,
    Dictionary<int, QpdfObject> Pool, HashSet<int> Compressed, PdfReference? Info,
    bool PruneSuperseded, OutputSecurity? Security);

internal readonly record struct PreparedOutputModel(List<NumberedObject> Model,
    IReadOnlyList<IReadOnlyList<int>>? ObjectStreams, int EncryptionObject);

/// <summary>Final numbering, reachability and packing policy over an assembled graph.</summary>
internal static class OutputModelPreparation
{
    internal static PreparedOutputModel Prepare(OutputGraph graph, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        int count = checked(graph.Pool.Count + 2 + (graph.Security is null ? 0 : 1));
        // Include encryption before allocating backing storage. Pruning can
        // remove objects without forcing one last array-growth overlap.
        memory.Reserve(checked(64L + 16L * count));
        List<NumberedObject> model = new(count) { new(1, graph.Catalog), new(2, graph.Pages) };
        foreach ((int number, QpdfObject value) in graph.Pool)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            model.Add(new(number, value));
        }
        if (graph.PruneSuperseded)
        { PruneOutputModel(model, graph.Pool, graph.Compressed, graph.Info, governor, token); }
        return Finish(model, graph.Compressed, graph.Security, memory, governor, token);
    }

    // Split graphs already have occurrence-local roots and renumbered objects.
    // They share the same final numbering/ordering/packing stage.
    internal static PreparedOutputModel Finish(List<NumberedObject> model, HashSet<int> compressed,
        OutputSecurity? security, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        int encryption = AddEncryptionObject(model, security, memory, governor);
        GovernedSort.Sort(CollectionsMarshal.AsSpan(model)[2..], new WriterObjectOrder(), governor, token);
        List<List<int>> groups = PlanObjectStreamGroups(compressed, memory, governor, token);
        return new(model, groups.Count == 0 ? null : groups, encryption);
    }

    internal static int AddEncryptionObject(List<NumberedObject> model, OutputSecurity? security,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (security is null) { return 0; }
        int number = 0;
        foreach (NumberedObject item in model)
        { governor.CheckCancellation(); number = Math.Max(number, item.Number); }
        number = checked(number + 1);
        ParserContainers.AddItem(memory, model, new NumberedObject(number, security.Dictionary), 16L);
        return number;
    }

    private static void PruneOutputModel(List<NumberedObject> model, Dictionary<int, QpdfObject> pool,
        HashSet<int> compressed, PdfReference? info, ResourceGovernor governor, CancellationToken token)
    {
        // Catalog, page tree and trailer Info are the actual output roots. Keep
        // shared page/catalog references; replaced-only objects have no root.
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(96L);
        PdfArray roots = PdfArray.FromOwnedItems([model[0].Value, model[1].Value, info is null ? PdfNull.Instance : info]);
        HashSet<int> reachable = GraphReachability.Find(new Content.PoolResolver(pool), roots, scratch, governor);
        int kept = 2;
        for (int index = 2; index < model.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            NumberedObject entry = model[index];
            if (reachable.Contains(entry.Number)) { model[kept++] = entry; }
            else { compressed.Remove(entry.Number); }
        }
        model.RemoveRange(kept, model.Count - kept);
    }

    internal static List<List<int>> PlanObjectStreamGroups(HashSet<int> compressed,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        if (compressed is null)
        {
            throw new ArgumentNullException(nameof(compressed));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        List<List<int>> groups = ParserContainers.CreateList<List<int>>(candidate);
        if (compressed.Count == 0)
        {
            candidate.TransferTo(ownership);
            return groups;
        }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(64L + sizeof(int) * (long)compressed.Count);
        List<int> ordered = new(compressed.Count);
        foreach (int number in compressed)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); ordered.Add(number);
        }
        GovernedSort.Sort(CollectionsMarshal.AsSpan(ordered), new WriterNumberOrder(), governor, token);
        int streamCount = 1 + (ordered.Count - 1) / 100;
        int perStream = 1 + (ordered.Count - 1) / streamCount;
        for (int i = 0; i < ordered.Count; i += perStream)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int take = Math.Min(perStream, ordered.Count - i);
            candidate.Reserve(64L + sizeof(int) * (long)take);
            List<int> group = new(take);
            for (int index = i; index < i + take; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation(); group.Add(ordered[index]);
            }
            ParserContainers.AddItem(candidate, groups, group, IntPtr.Size);
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return groups;
    }
}
