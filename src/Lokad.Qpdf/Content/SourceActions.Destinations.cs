using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal static partial class SourceActions
{
    private sealed partial class Binding
    {
        private readonly record struct DestinationRange(QpdfObject? First, QpdfObject? Last,
            List<QpdfObject>? Names, int Height, bool Changed);

        private void PrepareDestinationIndexes()
        {
            if (_catalog is not PdfDictionary catalog) { return; }
            if (Find(Read(catalog, "Dests"u8, _governor, _token), Role.DestinationIndex) is Node legacy)
            { ProjectLegacyIndex(legacy); }
            if (Resolve(Read(catalog, "Names"u8, _governor, _token)) is PdfDictionary names
                && Find(Read(names, "Dests"u8, _governor, _token), Role.DestinationTree) is Node root)
            { ProjectNameTree(root); }
        }

        private bool StaleIndexValue(QpdfObject raw)
        {
            QpdfObject? value = Resolve(raw);
            if (value is PdfDictionary wrapper) { value = Resolve(Read(wrapper, "D"u8, _governor, _token)); }
            return value is PdfArray array && IsStaleDestination(array);
        }

        private void ProjectLegacyIndex(Node node)
        {
            PdfDictionary original = (PdfDictionary)node.Original;
            using PdfDictionary.EntryEnumerator entries = original.GetEntryEnumerator();
            while (entries.MoveNext(_governor, _token))
            {
                if (!StaleIndexValue(entries.Current.Value)) { continue; }
                PdfNameTable<QpdfObject> kept = PdfDictionaryRebuild.CreateEntries(Graph, out long allocation);
                using PdfDictionary.EntryEnumerator prefix = original.GetEntryEnumerator();
                while (prefix.MoveNext(_governor, _token) && !ReferenceEquals(prefix.Current.Key, entries.Current.Key))
                { kept.Assign(Graph.Memory, _governor, prefix.Current.Key, prefix.Current.Value, _token); }
                while (entries.MoveNext(_governor, _token))
                {
                    if (!StaleIndexValue(entries.Current.Value))
                    { kept.Assign(Graph.Memory, _governor, entries.Current.Key, entries.Current.Value, _token); }
                }
                node.Edited = kept.Count == 0 ? PdfNull.Instance : PdfDictionaryRebuild.Complete(Graph, kept, allocation);
                return;
            }
        }

        private void ProjectNameTree(Node root)
        {
            using ParserMemoryScope scratch = ParserMemoryScope.Create(_governor);
            Dictionary<Node, DestinationRange> plans = ParserContainers.CreateDictionary<Node, DestinationRange>(scratch);
            HashSet<Node> path = ParserContainers.CreateSet<Node>(scratch);
            // Leave an unsupported tree in its stored shape. Apply plans
            // only after the whole reachable tree supplies usable bounds.
            if (Visit(root, 0) is not { Changed: true }) { return; }
            foreach ((Node node, DestinationRange plan) in plans)
            {
                Check();
                if (!plan.Changed) { continue; }
                if (plan.First is not QpdfObject first || plan.Last is not QpdfObject last)
                { node.Edited = PdfNull.Instance; continue; }
                PdfDictionary dictionary = (PdfDictionary)node.Original;
                if (plan.Names is not null)
                {
                    long bytes = checked(64L + IntPtr.Size * (long)plan.Names.Count);
                    Graph.Memory.Reserve(bytes);
                    PdfArray pairs = new(plan.Names);
                    Graph.Add(bytes);
                    dictionary = PdfDictionaryRebuild.ReplaceEntry(Graph, dictionary, "Names"u8, pairs);
                }
                if (!ReferenceEquals(node, root) || Read(dictionary, "Limits"u8, _governor, _token) is not null)
                {
                    long bytes = 64L + 2L * IntPtr.Size;
                    Graph.Memory.Reserve(bytes);
                    PdfArray bounds = PdfArray.FromOwnedItems([first, last]);
                    Graph.Add(bytes);
                    dictionary = PdfDictionaryRebuild.ReplaceEntry(Graph, dictionary, "Limits"u8, bounds);
                }
                node.Edited = dictionary;
            }

            DestinationRange? Visit(Node node, int depth)
            {
                Check();
                if (depth > _governor.MaxNestingDepth) { return null; }
                if (plans.TryGetValue(node, out DestinationRange ready))
                { return (long)depth + ready.Height <= _governor.MaxNestingDepth ? ready : null; }
                if (!ParserContainers.AddSetItem(scratch, path, node, IntPtr.Size)) { return null; }
                try
                {
                    PdfDictionary dictionary = (PdfDictionary)node.Original;
                    QpdfObject? leaf = Resolve(Read(dictionary, "Names"u8, _governor, _token));
                    QpdfObject? branch = Resolve(Read(dictionary, "Kids"u8, _governor, _token));
                    DestinationRange? result = (leaf, branch) switch
                    {
                        (PdfArray pairs, null or PdfNull) when (pairs.Count & 1) == 0 => ReadLeaf(pairs),
                        (null or PdfNull, PdfArray kids) => ReadBranch(kids, depth),
                        _ => null,
                    };
                    if (result is DestinationRange bounds)
                    {
                        ParserContainers.EnsureEntryCapacity(scratch, plans, 48L);
                        plans.Add(node, bounds);
                    }
                    return result;
                }
                finally { path.Remove(node); }
            }

            DestinationRange? ReadLeaf(PdfArray pairs)
            {
                List<QpdfObject>? kept = null;
                QpdfObject? first = null, last = null;
                for (int index = 0; index < pairs.Count; index += 2)
                {
                    Check();
                    QpdfObject key = pairs[index], value = pairs[index + 1];
                    if (Resolve(key) is not PdfByteString) { return null; }
                    if (StaleIndexValue(value))
                    {
                        if (kept is not null) { continue; }
                        kept = ParserContainers.CreateList(scratch);
                        for (int prefix = 0; prefix < index; prefix++)
                        { Check(); ParserContainers.AddItem(scratch, kept, pairs[prefix]); }
                        continue;
                    }
                    first ??= key; last = key;
                    if (kept is not null)
                    {
                        ParserContainers.AddItem(scratch, kept, key);
                        ParserContainers.AddItem(scratch, kept, value);
                    }
                }
                return new(first, last, kept, 0, kept is not null);
            }

            DestinationRange? ReadBranch(PdfArray kids, int depth)
            {
                QpdfObject? first = null, last = null;
                int height = 0;
                bool changed = false;
                for (int index = 0; index < kids.Count; index++)
                {
                    Check();
                    if (Find(kids[index], Role.DestinationTree) is not Node child
                        || Visit(child, depth + 1) is not DestinationRange bounds) { return null; }
                    first ??= bounds.First;
                    last = bounds.Last ?? last;
                    height = Math.Max(height, bounds.Height + 1);
                    changed |= bounds.Changed;
                }
                return new(first, last, null, height, changed);
            }
        }
    }
}
