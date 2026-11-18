using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>Immutable source-pair overlays for page traversal and inherited attributes.</summary>
internal static class PageTreePreparation
{
    // Native preparation allocates nonscalar attributes in bytewise key order.
    private static readonly PdfName[] Inherited =
        [PdfName.FromAscii("CropBox"), PdfName.FromAscii("MediaBox"), PdfName.FromAscii("Resources"), PdfName.FromAscii("Rotate")];

    internal sealed record Prepared(PdfDictionary Trailer, Dictionary<int, QpdfObject> Objects,
        HashSet<PdfReference> AddedReferences, List<PageLocation>? Pages);

    internal static Prepared Prepare(DocumentEngine source, bool pushInherited, QpdfLimits limits,
        ParserMemoryScope values, ParserMemoryScope temporary, ResourceGovernor governor, CancellationToken token)
        => PrepareCore(source, pushInherited, false, limits, values, temporary, governor, token);

    internal static Prepared PrepareForInspection(DocumentEngine source, bool pushInherited, QpdfLimits limits,
        ParserMemoryScope values, ParserMemoryScope temporary, ResourceGovernor governor, CancellationToken token)
        => PrepareCore(source, pushInherited, true, limits, values, temporary, governor, token);

    private static Prepared PrepareCore(DocumentEngine source, bool pushInherited, bool collectPages, QpdfLimits limits,
        ParserMemoryScope values, ParserMemoryScope temporary, ResourceGovernor governor, CancellationToken token)
    {
        values.CheckOwner(governor); temporary.CheckOwner(governor);
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        temporary.Reserve(128L);
        Builder builder = new(source, pushInherited, collectPages, limits, temporary, traversal, graph);
        Prepared prepared = builder.Build();
        // The overlay indexes are temporary; immutable headers may be borrowed
        // by the normalized model after those indexes and this builder retire.
        graph.Memory.TransferTo(values);
        return prepared;
    }

    private sealed class Builder
    {
        private readonly DocumentEngine _source;
        private readonly bool _pushInherited;
        private readonly QpdfLimits _limits;
        private readonly ParserMemoryScope _temporary;
        private readonly ParserMemoryScope _traversal;
        private readonly OwnedAllocationBatch _graph;
        private readonly Dictionary<int, QpdfObject> _objects;
        private readonly HashSet<PdfReference> _added;
        private readonly HashSet<int> _containers, _pages;
        private readonly List<PageLocation>? _locations;
        private int _greatest;
        private ResourceGovernor Governor => _graph.Governor;
        private CancellationToken Token => _graph.Token;

        internal Builder(DocumentEngine source, bool pushInherited, bool collectPages, QpdfLimits limits,
            ParserMemoryScope temporary, ParserMemoryScope traversal, OwnedAllocationBatch graph)
        {
            _source = source; _pushInherited = pushInherited; _limits = limits;
            _temporary = temporary; _traversal = traversal; _graph = graph;
            _objects = ParserContainers.CreateDictionary<int, QpdfObject>(temporary);
            temporary.Reserve(96L);
            _added = new(ReferenceEqualityComparer.Instance);
            _containers = ParserContainers.CreateSet<int>(traversal);
            _pages = ParserContainers.CreateSet<int>(traversal);
            _locations = collectPages ? ParserContainers.CreateList<PageLocation>(graph.Memory) : null;
        }

        internal Prepared Build()
        {
            foreach (KeyValuePair<int, XrefEntry> entry in _source.XRefEntries)
            {
                Visit(0);
                if (entry.Value.Kind != XrefEntryKind.Free) { _greatest = Math.Max(_greatest, entry.Key); }
            }
            // The trailer is parsed before native's live-object table is fixed.
            // Its dangling pairs reserve identities; free xref slots and dangling
            // references encountered later inside ordinary objects do not.
            IncludeTrailerReferences(_source.Trailer, 0);
            PdfDictionary trailer = _source.Trailer;
            if (!PdfDictionaryLookup.TryGet(trailer, "Root"u8, Governor, Token, out QpdfObject root))
            { throw new QpdfParseException("Trailer must declare /Root.", null, null); }
            PdfDictionary catalog = Dictionary(root);
            bool rootIsPages = PdfDictionaryLookup.HasName(catalog, "Type"u8, "Pages"u8, _source, Governor, Token);
            QpdfObject pages = root;
            if (!rootIsPages && !PdfDictionaryLookup.TryGet(catalog, "Pages"u8, Governor, Token, out pages))
            { throw new QpdfParseException("Catalog must declare /Pages.", null, null); }
            QpdfObject preparedPages = Canonicalize(pages, 0, false, false);
            if (_pushInherited) { preparedPages = Push(preparedPages, default, 0); }
            QpdfObject preparedRoot = rootIsPages ? preparedPages
                : ReferenceEquals(pages, preparedPages) ? root
                : Store(root, PdfDictionaryRebuild.ReplaceEntry(_graph, catalog, "Pages"u8, preparedPages));
            if (!ReferenceEquals(root, preparedRoot))
            { trailer = PdfDictionaryRebuild.ReplaceEntry(_graph, trailer, "Root"u8, preparedRoot); }
            if (_locations is not null)
            {
                for (int index = 0; index < _locations.Count; index++)
                {
                    Visit(0);
                    PdfReference reference = _locations[index].Source ?? throw new InvalidOperationException("Unprepared page identity.");
                    _locations[index] = new(reference, Dictionary(reference));
                }
            }
            _temporary.Reserve(48L);
            return new(trailer, _objects, _added, _locations);
        }

        private void Visit(long depth)
        {
            Token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); Governor.NoteOperation();
            if (depth > _limits.MaxNestingDepth)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, _limits.MaxNestingDepth); }
        }

        private QpdfObject? Resolve(QpdfObject raw)
        {
            Visit(0);
            if (raw is not PdfReference reference) { return raw; }
            bool original = _source.XRefEntries.TryGetValue(reference.ObjectNumber, out XrefEntry entry)
                && entry.MatchesGeneration(reference.Generation);
            if (!original && !_added.Contains(reference)) { return null; }
            if (_objects.TryGetValue(reference.ObjectNumber, out QpdfObject? value)) { return value; }
            return ((IIndirectResolver)_source).TryResolve(reference, out QpdfObject? loaded) ? loaded : null;
        }

        private PdfDictionary Dictionary(QpdfObject raw)
            => Resolve(raw) as PdfDictionary ?? throw new QpdfParseException("Page preparation requires a dictionary.", null, (raw as PdfReference)?.ObjectNumber);

        private QpdfObject Store(QpdfObject raw, QpdfObject value)
        {
            if (raw is not PdfReference reference) { return value; }
            if (!_objects.ContainsKey(reference.ObjectNumber))
            { ParserContainers.EnsureEntryCapacity(_temporary, _objects, IntPtr.Size); }
            _objects[reference.ObjectNumber] = value;
            return reference;
        }

        private PdfReference Allocate(QpdfObject value)
        {
            Visit(0);
            if (_greatest == int.MaxValue)
            { throw new QpdfParseException("No object identity is available for page preparation.", null, null); }
            _graph.Memory.Reserve(32L); _graph.Add(32L);
            PdfReference reference = new(++_greatest, 0);
            ParserContainers.AddSetItem(_temporary, _added, reference, IntPtr.Size);
            Store(reference, value);
            return reference;
        }

        private void MarkContainer(QpdfObject raw)
        {
            if (raw is PdfReference reference
                && !ParserContainers.AddSetItem(_traversal, _containers, reference.ObjectNumber, sizeof(int)))
            { throw new QpdfParseException("Page preparation encountered a repeated page-tree container.", null, reference.ObjectNumber); }
        }

        private QpdfObject Canonicalize(QpdfObject raw, long depth, bool mediaBox, bool resources)
        {
            Visit(depth);
            PdfDictionary node = Dictionary(raw);
            MarkContainer(raw);
            if (!PdfDictionaryLookup.TryGet(node, "Kids"u8, Governor, Token, out QpdfObject kidsRaw)
                || Resolve(kidsRaw) is not PdfArray kids)
            { throw new QpdfParseException("Pages must declare an array of /Kids.", null, (raw as PdfReference)?.ObjectNumber); }
            MarkContainer(kidsRaw);
            mediaBox = mediaBox || HasRectangle(node);
            resources = resources || PdfDictionaryLookup.TryGet(node, "Resources"u8, Governor, Token, out QpdfObject parentResources)
                && Resolve(parentResources) is PdfDictionary;
            QpdfObject[]? changed = null;
            for (int index = 0; index < kids.Count; index++)
            {
                Visit(checked(depth + 1));
                QpdfObject childRaw = kids[index];
                PdfDictionary child = Dictionary(childRaw);
                if (childRaw is not PdfReference) { childRaw = Allocate(child); }
                if (PdfDictionaryLookup.TryGet(child, "Kids"u8, Governor, Token, out QpdfObject childKids)
                    && Resolve(childKids) is not (null or PdfNull))
                { childRaw = Canonicalize(childRaw, checked(depth + 1), mediaBox, resources); }
                else
                {
                    PdfDictionary leaf = PrepareLeaf(child, mediaBox, resources);
                    childRaw = Store(childRaw, leaf);
                    if (childRaw is PdfReference page
                        && !ParserContainers.AddSetItem(_traversal, _pages, page.ObjectNumber, sizeof(int)))
                    { childRaw = Allocate(Clone(leaf)); }
                    if (_locations is not null)
                    { ParserContainers.AddItem(_graph.Memory, _locations, new PageLocation((PdfReference)childRaw, leaf), 16L); }
                }
                if (ReferenceEquals(childRaw, kids[index])) { continue; }
                changed ??= CopyItems(kids);
                changed[index] = childRaw;
            }
            if (changed is null) { return raw; }
            QpdfObject preparedKids = Store(kidsRaw, PdfArray.FromOwnedItems(changed));
            // An indirect Kids array keeps its pair; its own overlay already
            // relinks the visible tree, without replacing the parent header.
            return ReferenceEquals(preparedKids, kidsRaw) ? raw
                : Store(raw, PdfDictionaryRebuild.ReplaceEntry(_graph, node, "Kids"u8, preparedKids));
        }

        private bool HasRectangle(PdfDictionary node)
        {
            if (!PdfDictionaryLookup.TryGet(node, "MediaBox"u8, Governor, Token, out QpdfObject raw)) { return false; }
            Span<double> numbers = stackalloc double[4];
            return PdfGeometry.TryReadFiniteNumbers(Resolve(raw), _source, numbers);
        }

        private PdfDictionary PrepareLeaf(PdfDictionary leaf, bool mediaBox, bool resources)
        {
            if (!mediaBox && !HasRectangle(leaf))
            {
                _graph.Memory.Reserve(288L); _graph.Add(288L);
                PdfArray letter = PdfArray.FromOwnedItems([new PdfInteger("0", 0), new PdfInteger("0", 0),
                    new PdfInteger("612", 612), new PdfInteger("792", 792)]);
                leaf = PdfDictionaryRebuild.ReplaceEntry(_graph, leaf, "MediaBox"u8, letter);
            }
            if (!resources && (!PdfDictionaryLookup.TryGet(leaf, "Resources"u8, Governor, Token, out QpdfObject local)
                || Resolve(local) is not PdfDictionary))
            {
                PdfNameTable<QpdfObject> empty = PdfDictionaryRebuild.CreateEntries(_graph, out long allocation);
                leaf = PdfDictionaryRebuild.ReplaceEntry(_graph, leaf, "Resources"u8,
                    PdfDictionaryRebuild.Complete(_graph, empty, allocation));
            }
            if (PdfDictionaryLookup.TryGet(leaf, "Annots"u8, Governor, Token, out QpdfObject annotations)
                && Resolve(annotations) is not (null or PdfNull or PdfArray))
            { leaf = PdfDictionaryRebuild.RemoveEntry(_graph, leaf, "Annots"u8); }
            return leaf;
        }

        private QpdfObject Push(QpdfObject raw, PageInheritance.InheritedValues inherited, long depth)
        {
            Visit(depth);
            PdfDictionary node = Dictionary(raw);
            if (!PdfDictionaryLookup.TryGet(node, "Kids"u8, Governor, Token, out QpdfObject kidsRaw)
                || Resolve(kidsRaw) is not PdfArray kids)
            { throw new QpdfParseException("Pages must declare an array of /Kids.", null, (raw as PdfReference)?.ObjectNumber); }
            bool removes = false;
            for (int index = 0; index < Inherited.Length; index++)
            {
                if (!node.TryGet(Inherited[index], out QpdfObject value) || Resolve(value) is null or PdfNull) { continue; }
                removes = true;
                if (value is PdfArray or PdfDictionary) { value = Allocate(value); }
                inherited[index] = value;
            }
            QpdfObject[]? changed = null;
            for (int index = 0; index < kids.Count; index++)
            {
                Visit(checked(depth + 1));
                QpdfObject childRaw = kids[index];
                PdfDictionary child = Dictionary(childRaw);
                QpdfObject prepared;
                if (PdfDictionaryLookup.TryGet(child, "Kids"u8, Governor, Token, out QpdfObject childKids)
                    && Resolve(childKids) is not (null or PdfNull))
                { prepared = Push(childRaw, inherited, checked(depth + 1)); }
                else
                {
                    PageInheritance.InheritedValues additions = default;
                    bool adds = false;
                    for (int attr = 0; attr < Inherited.Length; attr++)
                    {
                        if (inherited[attr] is null) { continue; }
                        if (child.TryGet(Inherited[attr], out QpdfObject local) && Resolve(local) is not (null or PdfNull)) { continue; }
                        additions[attr] = inherited[attr]; adds = true;
                    }
                    prepared = adds ? Store(childRaw, RewriteInherited(child, additions, false)) : childRaw;
                }
                if (ReferenceEquals(prepared, childRaw)) { continue; }
                changed ??= CopyItems(kids); changed[index] = prepared;
            }
            if (removes) { node = RewriteInherited(node, default, true); }
            if (changed is not null)
            {
                QpdfObject preparedKids = Store(kidsRaw, PdfArray.FromOwnedItems(changed));
                if (!ReferenceEquals(preparedKids, kidsRaw))
                { node = PdfDictionaryRebuild.ReplaceEntry(_graph, node, "Kids"u8, preparedKids); }
            }
            return Store(raw, node);
        }

        private QpdfObject[] CopyItems(PdfArray source)
        {
            long allocation = checked(64L + IntPtr.Size * (long)source.Count);
            _graph.Memory.Reserve(allocation); _graph.Add(allocation);
            QpdfObject[] items = new QpdfObject[source.Count];
            for (int index = 0; index < source.Count; index++) { Visit(0); items[index] = source[index]; }
            return items;
        }

        private PdfDictionary Clone(PdfDictionary source) => RewriteInherited(source, default, false);

        private PdfDictionary RewriteInherited(PdfDictionary source, PageInheritance.InheritedValues additions, bool removes)
        {
            PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(_graph, out long allocation);
            using PdfDictionary.EntryEnumerator current = source.GetEntryEnumerator();
            while (current.MoveNext(Governor, Token))
            {
                bool inherited = false;
                for (int index = 0; index < Inherited.Length; index++)
                {
                    if (!current.Current.Key.Equals(Inherited[index])) { continue; }
                    inherited = removes || additions[index] is not null; break;
                }
                if (!inherited) { entries.Assign(_graph.Memory, Governor, current.Current.Key, current.Current.Value, Token); }
            }
            if (!removes)
            {
                for (int index = 0; index < Inherited.Length; index++)
                { if (additions[index] is QpdfObject value) { entries.Assign(_graph.Memory, Governor, Inherited[index], value, Token); } }
            }
            return PdfDictionaryRebuild.Complete(_graph, entries, allocation);
        }

        private void IncludeTrailerReferences(QpdfObject value, long depth)
        {
            Visit(depth);
            if (value is PdfReference reference) { _greatest = Math.Max(_greatest, reference.ObjectNumber); }
            else if (value is PdfArray array)
            { for (int index = 0; index < array.Count; index++) { IncludeTrailerReferences(array[index], checked(depth + 1)); } }
            else if (value is PdfDictionary dictionary)
            {
                using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
                while (entries.MoveNext(Governor, Token)) { IncludeTrailerReferences(entries.Current.Value, checked(depth + 1)); }
            }
        }
    }
}
