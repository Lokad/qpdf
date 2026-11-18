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
        {
            using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
            HashSet<int> reachable = FindPublishedObjects(graph.Pool, graph.Catalog, graph.Pages, graph.Info, scratch, governor);
            RetainPublishedObjects(model, graph.Compressed, reachable, 2, governor, token);
        }
        return Finish(model, graph.Compressed, graph.Security, memory, governor, token);
    }

    // Split graphs already have occurrence-local roots and renumbered objects.
    // They share the same final numbering/ordering/packing stage.
    internal static PreparedOutputModel Finish(List<NumberedObject> model, HashSet<int> compressed,
        OutputSecurity? security, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        int encryption = AddEncryptionObject(model, security, memory, governor);
        GovernedSort.Sort(CollectionsMarshal.AsSpan(model), new WriterObjectOrder(), governor, token);
        List<List<int>> groups = PlanObjectStreamGroups(compressed, memory, governor, token);
        return new(model, groups.Count == 0 ? null : groups, encryption);
    }

    internal static int AddEncryptionObject(List<NumberedObject> model, OutputSecurity? security,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (security is null) { return 0; }
        int number = 0;
        bool crypt = false;
        foreach (NumberedObject item in model)
        {
            governor.CheckCancellation(); number = Math.Max(number, item.Number);
            if (!crypt && item.Value is PdfStream stream && StreamCryptFilter.MayContain(stream, governor)) { crypt = true; }
        }
        if (crypt) { NormalizeIdentityCrypt(model, memory, governor); }
        number = checked(number + 1);
        ParserContainers.AddItem(memory, model, new NumberedObject(number, security.Dictionary), 16L);
        return number;
    }

    internal static void NormalizeIdentityCrypt(List<NumberedObject> model, ParserMemoryScope memory, ResourceGovernor governor)
    {
        CancellationToken token = governor.CancellationToken;
        using ParserMemoryScope lookupMemory = ParserMemoryScope.Create(governor);
        Dictionary<int, QpdfObject> values = ParserContainers.CreateDictionary<int, QpdfObject>(lookupMemory);
        foreach (NumberedObject item in model)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserContainers.EnsureEntryCapacity(lookupMemory, values, IntPtr.Size); values.Add(item.Number, item.Value);
        }
        Content.PoolResolver resolver = Content.PoolResolver.Create(lookupMemory, values);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        for (int i = 0; i < model.Count; i++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            NumberedObject item = model[i];
            if (item.Value is not PdfStream stream || !StreamCryptFilter.MayContain(stream, governor)) { continue; }
            PdfName? name = StreamCryptFilter.ReadName(stream.Dictionary, resolver, governor, token, out int count);
            if (name is null) { continue; }
            if (!PdfDictionaryLookup.NameEquals(name, "Identity"u8, governor, token))
            { throw new QpdfUnsupportedFeatureException("An unauthenticated stream Crypt filter must use Identity for output encryption."); }
            PdfDictionary header = StreamCryptFilter.Remove(stream.Dictionary, count, resolver, graph);
            graph.Memory.Reserve(32L); graph.Add(32L);
            PdfStream normalized = stream.WithDictionary(header);
            values[item.Number] = normalized;
        }
        graph.Memory.TransferTo(memory, graph.Bytes);
        for (int i = 0; i < model.Count; i++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            NumberedObject item = model[i]; model[i] = new(item.Number, values[item.Number]);
        }
    }

    internal static HashSet<int> FindPublishedObjects(Dictionary<int, QpdfObject> pool, PdfDictionary catalog,
        PdfDictionary? separatePages, PdfReference? info, ParserMemoryScope scratch, ResourceGovernor governor)
    {
        scratch.Reserve(96L);
        // The catalog reaches the page tree of an imported graph. Ordinary
        // assembly supplies its new page tree before adding it to the pool.
        PdfArray roots = PdfArray.FromOwnedItems([catalog, (QpdfObject?)separatePages ?? PdfNull.Instance,
            (QpdfObject?)info ?? PdfNull.Instance]);
        return GraphReachability.Find(new Content.PoolResolver(pool), roots, scratch, governor);
    }

    internal static void RetainPublishedObjects(List<NumberedObject> model, HashSet<int>? compressed,
        HashSet<int> reachable, int explicitRootCount, ResourceGovernor governor, CancellationToken token)
    {
        int kept = explicitRootCount;
        for (int index = explicitRootCount; index < model.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            NumberedObject entry = model[index];
            if (reachable.Contains(entry.Number)) { model[kept++] = entry; }
            else { compressed?.Remove(entry.Number); }
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
