using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal sealed partial class FormResourceWalk
{
    // Redirect resource uses, leaving original stream identities available to
    // attachments and other roots. Parent links propagate only actual changes;
    // registering new form identities before rebuilding headers closes cycles.
    internal void ApplyReplacements(GraphCopier copier, IReadOnlyList<int> pageIds,
        GovernedIntDictionary<PdfStream> replacements, ref int allocator)
    {
        Visit();
        if (replacements.Count == 0) { return; }
        ResourceGovernor governor = Governor; CancellationToken token = _token;
        PoolResolver resolver = CreateResolver(copier.CopiedObjects);
        GovernedIntSet formIds = GovernedIntSet.Create(Memory, governor, token);
        GovernedIntDictionary<List<int>> parents = GovernedIntDictionary<List<int>>.Create(Memory, governor, IntPtr.Size, token);
        foreach (Form form in Forms)
        {
            Visit();
            if (form.Id is not int id || !formIds.Add(Memory, id)) { continue; }
            PdfDictionary? resources = ResolveDictionary(form.Stream.Dictionary, "Resources"u8, resolver);
            PdfDictionary? objects = resources is null ? null : ResolveDictionary(resources, "XObject"u8, resolver);
            if (objects is null) { continue; }
            using PdfDictionary.EntryEnumerator entries = objects.GetEntryEnumerator();
            while (entries.MoveNext(governor, token))
            {
                if (entries.Current.Value is not PdfReference child) { continue; }
                if (!parents.TryGetValue(child.ObjectNumber, out List<int>? ancestors))
                {
                    ancestors = ParserContainers.CreateList<int>(Memory);
                    parents.TryAdd(Memory, child.ObjectNumber, ancestors);
                }
                // Aliases in one dictionary have the same parent. Keep one
                // dependency per child/parent pair instead of per resource name.
                if (ancestors.Count == 0 || ancestors[^1] != id) { ParserContainers.AddItem(Memory, ancestors, id, sizeof(int)); }
            }
        }

        GovernedIntSet changed = GovernedIntSet.Create(Memory, governor, token);
        List<int> pending = ParserContainers.CreateList<int>(Memory);
        foreach (KeyValuePair<int, PdfStream> candidate in replacements)
        {
            Visit();
            if (changed.Add(Memory, candidate.Key)) { ParserContainers.AddItem(Memory, pending, candidate.Key, sizeof(int)); }
        }
        for (int index = 0; index < pending.Count; index++)
        {
            Visit();
            if (!parents.TryGetValue(pending[index], out List<int>? ancestors)) { continue; }
            foreach (int parent in ancestors)
            {
                Visit();
                if (changed.Add(Memory, parent)) { ParserContainers.AddItem(Memory, pending, parent, sizeof(int)); }
            }
        }

        GovernedIntDictionary<PdfReference> links = GovernedIntDictionary<PdfReference>.Create(Memory, governor, IntPtr.Size, token);
        allocator = Math.Max(allocator, copier.NextId);
        foreach (int id in pending)
        {
            Visit();
            PdfStream source = replacements.TryGetValue(id, out PdfStream? candidate) ? candidate : (PdfStream)copier.CopiedObjects[id];
            PdfReference link = copier.AddIndirectObjectWithId(source, allocator);
            allocator = checked(allocator + 1); links.TryAdd(Memory, id, link);
        }

        Memory.Reserve(320L); // Two dictionary wrappers and captured rewrite state.
        Dictionary<PdfDictionary, PdfDictionary> resourceCopies = new(ReferenceEqualityComparer.Instance);
        Dictionary<PdfDictionary, PdfDictionary> objectCopies = new(ReferenceEqualityComparer.Instance);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary RewriteResources(PdfDictionary owner)
        {
            PdfDictionary? resources = ResolveDictionary(owner, "Resources"u8, resolver);
            if (resources is null) { return owner; }
            if (!resourceCopies.TryGetValue(resources, out PdfDictionary? rebuilt))
            {
                rebuilt = resources;
                PdfDictionary? objects = ResolveDictionary(resources, "XObject"u8, resolver);
                if (objects is not null)
                {
                    if (!objectCopies.TryGetValue(objects, out PdfDictionary? copiedObjects))
                    {
                        copiedObjects = objects;
                        bool replace = false;
                        using (PdfDictionary.EntryEnumerator scan = objects.GetEntryEnumerator())
                        {
                            while (scan.MoveNext(governor, token))
                            {
                                if (scan.Current.Value is PdfReference link && links.ContainsKey(link.ObjectNumber)) { replace = true; break; }
                            }
                        }
                        if (replace)
                        {
                            PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
                            using PdfDictionary.EntryEnumerator scan = objects.GetEntryEnumerator();
                            while (scan.MoveNext(governor, token))
                            {
                                QpdfObject value = scan.Current.Value;
                                if (value is PdfReference link && links.TryGetValue(link.ObjectNumber, out PdfReference? replacement)) { value = replacement; }
                                entries.Assign(graph.Memory, governor, scan.Current.Key, value, token);
                            }
                            copiedObjects = PdfDictionaryRebuild.Complete(graph, entries, allocation);
                        }
                        ParserContainers.EnsureEntryCapacity(Memory, objectCopies, IntPtr.Size);
                        objectCopies.Add(objects, copiedObjects);
                    }
                    if (!ReferenceEquals(objects, copiedObjects)) { rebuilt = PdfDictionaryRebuild.ReplaceEntry(graph, resources, "XObject"u8, copiedObjects); }
                }
                ParserContainers.EnsureEntryCapacity(Memory, resourceCopies, IntPtr.Size);
                resourceCopies.Add(resources, rebuilt);
            }
            return ReferenceEquals(resources, rebuilt) ? owner : PdfDictionaryRebuild.ReplaceEntry(graph, owner, "Resources"u8, rebuilt);
        }

        foreach (int id in pending)
        {
            Visit();
            if (!formIds.Contains(id)) { continue; }
            PdfStream stream = replacements.TryGetValue(id, out PdfStream? candidate) ? candidate : (PdfStream)copier.CopiedObjects[id];
            PdfDictionary header = RewriteResources(stream.Dictionary);
            if (ReferenceEquals(header, stream.Dictionary)) { continue; }
            graph.Memory.Reserve(32L); PdfStream replacement = stream.WithDictionary(header); graph.Add(32L);
            copier.ReplaceCopiedObject(links[id].ObjectNumber, replacement);
        }
        foreach (int id in pageIds)
        {
            Visit();
            if (!copier.CopiedObjects.TryGetValue(id, out QpdfObject? raw) || raw is not PdfDictionary page) { continue; }
            PdfDictionary replacement = RewriteResources(page);
            if (!ReferenceEquals(replacement, page)) { copier.ReplaceCopiedObject(id, replacement); }
        }
        graph.Retain();
    }

    private PdfDictionary? ResolveDictionary(PdfDictionary owner, ReadOnlySpan<byte> key, PoolResolver resolver)
    {
        if (!PdfDictionaryLookup.TryGet(owner, key, Governor, _token, out QpdfObject value)) { return null; }
        if (value is PdfReference reference)
        {
            if (!PdfObjectLookup.TryResolve(reference, (IIndirectResolver)resolver, Governor, _token, out QpdfObject? resolved)) { return null; }
            value = resolved ?? PdfNull.Instance;
        }
        return value as PdfDictionary;
    }
}
