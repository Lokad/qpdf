using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal static partial class SourceActions
{
    internal static (PdfDictionary Catalog, Dictionary<int, QpdfObject>? Edits) ProjectSplitNavigation(
        PdfDictionary catalog, IIndirectResolver resolver, ReadOnlySpan<PdfReference> pages, IReadOnlySet<int> group,
        int maximum, QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        using Binding binding = new(resolver, catalog, group, limits, governor, memory, token);
        foreach (PdfReference page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (resolver.TryResolve(page, out QpdfObject? value) && value is PdfDictionary dictionary)
            { binding.AddPage(page.ObjectNumber, dictionary); }
        }
        binding.Add(catalog, Role.Catalog, null);
        Dictionary<int, QpdfObject>? edits = binding.ApplyProjection(maximum, limits);
        return ((PdfDictionary?)binding.Map(catalog, Role.Catalog) ?? catalog, edits);
    }

    private sealed partial class Binding
    {
        private readonly IReadOnlySet<int>? _splitPages;
        private readonly long _maxSplitContainerSize;
        private bool _hasStaleContinuations;

        internal Binding(IIndirectResolver resolver, PdfDictionary catalog, IReadOnlySet<int> group, QpdfLimits limits,
            ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
            : this(resolver, governor, memory, token)
        {
            _catalog = catalog; _splitPages = group; _maxSplitContainerSize = limits.MaxContainerSize;
        }

        private bool IsStaleDestination(PdfArray destination)
            => destination.Count > 0 && (destination[0] is PdfNull
                || destination[0] is PdfReference target && _splitPages?.Contains(target.ObjectNumber) == false);

        private bool HasStaleDestination(PdfDictionary dictionary, ReadOnlySpan<byte> key)
        {
            QpdfObject? destination = Resolve(Read(dictionary, key, _governor, _token));
            if (destination is PdfName or PdfByteString && _catalog is PdfDictionary catalog)
            {
                _destinations ??= NamedDestinations.Create(Resolver, catalog, _scratch, _governor, _token);
                destination = _destinations.Find(destination);
            }
            return destination is PdfArray explicitDestination && IsStaleDestination(explicitDestination);
        }

        private void ProjectNavigation(Node node, PdfDictionary dictionary)
        {
            bool hasDestination = (node.Roles & Role.Outline) != 0
                || (node.Roles & Role.Annotation) != 0 && IsSubtype(node, "Link"u8);
            if (hasDestination && HasStaleDestination(dictionary, "Dest"u8))
            {
                node.Edited = PdfDictionaryRebuild.RemoveEntry(Graph, dictionary, "Dest"u8);
            }
            if ((node.Roles & Role.Action) != 0
                && PdfDictionaryLookup.HasName(dictionary, "S"u8, "GoTo"u8, Resolver, _governor, _token)
                && HasStaleDestination(dictionary, "D"u8))
            {
                node.Edited = PdfNull.Instance; node.SkippedGoTo = true;
                _hasStaleContinuations |= Resolve(Next(node)) is PdfDictionary or PdfArray { Count: > 0 };
            }
        }

        private void PrepareActionContinuations()
        {
            using ParserMemoryScope scratch = ParserMemoryScope.Create(_governor);
            HashSet<Node> continuing = ParserContainers.CreateSet<Node>(scratch);
            List<Node> pending = ParserContainers.CreateList<Node>(scratch);
            // A stale component without a live continuation disappears even
            // if its actions point back to each other.
            foreach (Node node in _nodes.Values)
            {
                Check();
                if (node.Roles == Role.Action && !node.SkippedGoTo) { FollowParents(node); }
            }
            for (int index = 0; index < pending.Count; index++)
            { Check(); FollowParents(pending[index]); }

            HashSet<Node> visited = ParserContainers.CreateSet<Node>(scratch);
            List<ActionCursor> traversal = ParserContainers.CreateList<ActionCursor>(scratch);
            List<QpdfObject> tail = ParserContainers.CreateList(scratch);
            List<QpdfObject> next = ParserContainers.CreateList(scratch);
            foreach (Node node in pending)
            {
                Check();
                if (!node.SkippedGoTo) { continue; }
                visited.Clear(); traversal.Clear(); tail.Clear(); next.Clear();
                ParserContainers.AddSetItem(scratch, visited, node, IntPtr.Size);
                Node? first = FirstAction(Next(node));
                if (first is null) { continue; }
                node.Edited = first.Original;
                // The promoted action's own successors run before the rest
                // of the skipped action's array.
                QpdfObject? existing = Next(first);
                if (Resolve(existing) is PdfArray children)
                {
                    for (int index = 0; index < children.Count; index++) { Append(next, children[index]); }
                }
                else if (existing is not null) { Append(next, existing); }
                foreach (QpdfObject item in tail) { Append(next, item); }
                if (next.Count > 0)
                {
                    _scratch.Reserve(checked(64L + IntPtr.Size * (long)next.Count));
                    node.PromotedNext = new PdfArray(next);
                }
            }

            void FollowParents(Node node)
            {
                if (node.Parents is null) { return; }
                foreach (Node parent in node.Parents)
                {
                    Check();
                    if ((parent.Roles == Role.Actions || parent.SkippedGoTo)
                        && ParserContainers.AddSetItem(scratch, continuing, parent, IntPtr.Size))
                    { ParserContainers.AddItem(scratch, pending, parent, IntPtr.Size); }
                }
            }

            Node? FirstAction(QpdfObject? raw)
            {
                Push(raw);
                while (traversal.Count > 0)
                {
                    Check();
                    ActionCursor cursor = traversal[^1];
                    QpdfObject? item = cursor.Take();
                    if (item is null) { traversal.RemoveAt(traversal.Count - 1); continue; }
                    traversal[^1] = cursor;
                    if (Find(item, Role.Action) is not Node node) { continue; }
                    if (!node.SkippedGoTo)
                    {
                        for (int index = traversal.Count - 1; index >= 0; index--)
                        {
                            Check(); cursor = traversal[index];
                            while (cursor.Take() is QpdfObject later) { Append(tail, later); }
                        }
                        return node;
                    }
                    if (continuing.Contains(node) && ParserContainers.AddSetItem(scratch, visited, node, IntPtr.Size))
                    { Push(Next(node)); }
                }
                return null;
            }

            void Push(QpdfObject? raw)
            {
                Check();
                QpdfObject? value = Resolve(raw) is PdfArray array ? array : raw;
                if (value is not null)
                { ParserContainers.AddItem(scratch, traversal, new ActionCursor(value), 2L * IntPtr.Size); }
            }

            void Append(List<QpdfObject> target, QpdfObject raw)
            {
                Check();
                if (Find(raw, Role.Action) is not Node action || action.SkippedGoTo && !continuing.Contains(action)) { return; }
                if (target.Count >= _maxSplitContainerSize)
                { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), target.Count + 1L, _maxSplitContainerSize); }
                ParserContainers.AddItem(scratch, target, raw);
            }
        }

        private struct ActionCursor(QpdfObject value)
        {
            private int _index;

            internal QpdfObject? Take()
            {
                if (value is PdfArray items) { return _index < items.Count ? items[_index++] : null; }
                if (_index != 0) { return null; }
                _index = 1;
                return value;
            }
        }

        private QpdfObject? Next(Node node) => Read((PdfDictionary)node.Original, "Next"u8, _governor, _token);

        private QpdfObject BuildActionArray(PdfArray array, int depth)
        {
            List<QpdfObject> items = ParserContainers.CreateList<QpdfObject>(Graph.Memory, array.Count, IntPtr.Size);
            for (int index = 0; index < array.Count; index++)
            {
                Check();
                QpdfObject raw = array[index];
                Node? child = Find(raw, Role.Action);
                if (child is { Edited: PdfNull }) { continue; }
                items.Add(child is { Changed: true } ? child.Reference ?? Build(child, depth + 1) : raw);
            }
            PdfArray projected = ParserContainers.CopyArray(Graph.Memory, items);
            Graph.Add(checked(64L + IntPtr.Size * (long)projected.Count));
            return projected;
        }
    }
}
