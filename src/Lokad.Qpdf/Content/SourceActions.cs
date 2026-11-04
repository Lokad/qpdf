using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Binds retained action roles while their source catalog is still available.</summary>
internal static class SourceActions
{
    [Flags]
    private enum Role
    {
        None = 0, Page = 1, Annotation = 2, Field = 4, Action = 8, Outline = 16,
        PageEvents = 32, AnnotationEvents = 64, FieldEvents = 128, DocumentEvents = 256,
        Annotations = 512, Fields = 1024, Actions = 2048,
    }

    private sealed class Node(QpdfObject value, int number)
    {
        internal readonly QpdfObject Original = value;
        internal readonly int Number = number;
        internal Role Roles, Expanded;
        internal List<Node>? Parents;
        internal QpdfObject? Edited, Result;
        internal PdfName? Subtype;
        internal PdfReference? Reference;
        internal bool Changed;
    }

    internal static (CopiedForm? Form, CopiedForm? AppearanceForm, IReadOnlyDictionary<PdfName, QpdfObject>? Extras, PdfReference? Outlines) BindDocument(
        DocumentEngine engine, GraphCopier copier, CopiedForm? form, CopiedForm? appearanceForm, IReadOnlyDictionary<PdfName, QpdfObject>? extras, bool preserveContext,
        PdfReference? outlines, ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
    {
        PdfDictionary? catalog = Context(engine, governor, token);
        if (catalog is null || preserveContext && PreservesContext(catalog, engine, copier.CopiedObjects, extras, governor, token))
        { return (form, appearanceForm, extras, outlines); }
        using Binding binding = new(engine, copier, copier, catalog, governor, ownership, token);
        foreach (KeyValuePair<int, QpdfObject> pair in copier.CopiedObjects)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (pair.Value is PdfDictionary page && PdfDictionaryLookup.HasName(page, "Type"u8, "Page"u8, binding.Resolver, governor, token))
            { binding.AddPage(pair.Key, page); }
        }
        binding.AddForm(form); binding.AddExtras(extras); binding.Add(outlines, Role.Outline, null);
        binding.Apply();
        CopiedForm? mapped = binding.MapForm(form);
        return (mapped, ReferenceEquals(form, appearanceForm) ? mapped : binding.MapForm(appearanceForm),
            binding.MapExtras(extras), binding.Map(outlines) as PdfReference);
    }

    internal static void BindPlacement(DocumentEngine engine, GraphCopier sourceCopier, GraphCopier destination,
        List<PdfReference> annotations, List<AcroFormCopy.FieldImport> imports, int firstImport,
        ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token, ref int allocator)
    {
        PdfDictionary? catalog = Context(engine, governor, token);
        if (catalog is null) { return; }
        destination.AdvanceNextId(allocator); sourceCopier.AdvanceNextId(allocator);
        using Binding binding = new(engine, sourceCopier, destination, catalog, governor, ownership, token);
        foreach (PdfReference annotation in annotations) { binding.Add(annotation, Role.Annotation, null); }
        for (int index = firstImport; index < imports.Count; index++) { binding.AddForm(imports[index].Part); }
        binding.Apply();
        for (int index = 0; index < annotations.Count; index++)
        { annotations[index] = binding.Map(annotations[index]) as PdfReference ?? annotations[index]; }
        for (int index = firstImport; index < imports.Count; index++)
        {
            AcroFormCopy.FieldImport import = imports[index];
            CopiedForm? mapped = binding.MapForm(import.Part);
            if (mapped is not null && !ReferenceEquals(mapped, import.Part))
            { ownership.Reserve(64L); imports[index] = import with { Part = mapped }; }
        }
        allocator = Math.Max(destination.NextId, sourceCopier.NextId);
    }

    private static PdfDictionary? Context(DocumentEngine engine, ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary? catalog = CatalogCopy.ResolveDictionary(Read(engine.Trailer, "Root"u8, governor, token), engine, governor, token);
        if (catalog is null) { return null; }
        PdfDictionary? uri = CatalogCopy.ResolveDictionary(Read(catalog, "URI"u8, governor, token), engine, governor, token);
        PdfDictionary? names = CatalogCopy.ResolveDictionary(Read(catalog, "Names"u8, governor, token), engine, governor, token);
        return uri is not null && Read(uri, "Base"u8, governor, token) is not (null or PdfNull)
            || Read(catalog, "Dests"u8, governor, token) is not (null or PdfNull)
            || names is not null && Read(names, "Dests"u8, governor, token) is not (null or PdfNull) ? catalog : null;
    }

    private static QpdfObject? Read(PdfDictionary dictionary, ReadOnlySpan<byte> key, ResourceGovernor governor, CancellationToken token)
        => PdfDictionaryLookup.TryGet(dictionary, key, governor, token, out QpdfObject value) ? value : null;

    private static bool PreservesContext(PdfDictionary catalog, DocumentEngine engine, IReadOnlyDictionary<int, QpdfObject> pool,
        IReadOnlyDictionary<PdfName, QpdfObject>? extras, ResourceGovernor governor, CancellationToken token)
    {
        if (extras is null) { return false; }
        PdfDictionary? uri = CatalogCopy.ResolveDictionary(Read(catalog, "URI"u8, governor, token), engine, governor, token);
        if (uri is not null && Read(uri, "Base"u8, governor, token) is not (null or PdfNull))
        {
            PdfDictionary? copied = CatalogCopy.ResolveDictionary(CatalogCopy.ReadExtra(extras, "URI"u8, governor, token), pool, governor, token);
            if (copied is null || Read(copied, "Base"u8, governor, token) is null or PdfNull) { return false; }
        }
        if (Read(catalog, "Dests"u8, governor, token) is not (null or PdfNull)
            && CatalogCopy.ResolveDictionary(CatalogCopy.ReadExtra(extras, "Dests"u8, governor, token), pool, governor, token) is null) { return false; }
        PdfDictionary? names = CatalogCopy.ResolveDictionary(Read(catalog, "Names"u8, governor, token), engine, governor, token);
        if (names is not null && Read(names, "Dests"u8, governor, token) is not (null or PdfNull))
        {
            PdfDictionary? copied = CatalogCopy.ResolveDictionary(CatalogCopy.ReadExtra(extras, "Names"u8, governor, token), pool, governor, token);
            if (copied is null || CatalogCopy.ResolveDictionary(Read(copied, "Dests"u8, governor, token), pool, governor, token) is null) { return false; }
        }
        return true;
    }

    // This temporary walk follows only concrete PDF roles. It records incoming
    // role edges so changed cycles can receive identities before rebuilding.
    // Independent raw graph edges continue to refer to the original objects.
    private sealed class Binding : IDisposable
    {
        private readonly DocumentEngine _source;
        private readonly GraphCopier _sourceCopier, _copier;
        private readonly PdfDictionary _catalog;
        private readonly ResourceGovernor _governor;
        private readonly ParserMemoryScope _ownership, _scratch;
        private readonly CancellationToken _token;
        private readonly Dictionary<(QpdfObject, int), Node> _nodes;
        private readonly List<Node> _pending;
        private OwnedAllocationBatch? _graph;
        internal PoolResolver Resolver { get; }
        private OwnedAllocationBatch Graph => _graph ??= OwnedAllocationBatch.Create(_governor, _token);

        internal Binding(DocumentEngine source, GraphCopier sourceCopier, GraphCopier copier, PdfDictionary catalog,
            ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
        {
            _source = source; _sourceCopier = sourceCopier; _copier = copier; _catalog = catalog;
            _governor = governor; _ownership = ownership; _token = token;
            _scratch = ParserMemoryScope.Create(governor);
            try
            {
                _scratch.Reserve(256L); Resolver = new(copier.CopiedObjects);
                _nodes = ParserContainers.CreateDictionary<(QpdfObject, int), Node>(_scratch);
                _pending = ParserContainers.CreateList<Node>(_scratch);
            }
            catch { _scratch.Dispose(); throw; }
        }

        private void Check() { _token.ThrowIfCancellationRequested(); _governor.CheckCancellation(); }
        private QpdfObject? Resolve(QpdfObject? raw) => CatalogCopy.ResolveValue(raw, Resolver, _governor, _token);

        internal void AddPage(int number, PdfDictionary page)
        {
            Check();
            if (Read(page, "Annots"u8, _governor, _token) is null or PdfNull
                && Read(page, "AA"u8, _governor, _token) is null or PdfNull) { return; }
            AddNode(page, number, Role.Page, null);
        }

        internal void Add(QpdfObject? raw, Role role, Node? parent)
        {
            Check();
            if (role == Role.None) { return; }
            QpdfObject? value = Resolve(raw);
            if (value is not (PdfDictionary or PdfArray)) { return; }
            if (value is PdfArray && (role & (Role.Annotations | Role.Fields | Role.Actions)) == 0) { return; }
            AddNode(value, (raw as PdfReference)?.ObjectNumber ?? 0, role, parent);
        }

        private void AddNode(QpdfObject value, int number, Role role, Node? parent)
        {
            if (!_nodes.TryGetValue((value, number), out Node? node))
            {
                _scratch.Reserve(96L); node = new(value, number);
                ParserContainers.EnsureEntryCapacity(_scratch, _nodes, 2L * IntPtr.Size);
                _nodes.Add((value, number), node);
            }
            if ((node.Roles & role) != role)
            { node.Roles |= role; ParserContainers.AddItem(_scratch, _pending, node, IntPtr.Size); }
            if (parent is not null)
            {
                node.Parents ??= ParserContainers.CreateList<Node>(_scratch);
                ParserContainers.AddItem(_scratch, node.Parents, parent, IntPtr.Size);
            }
        }

        internal void AddForm(CopiedForm? form)
        {
            if (form is null) { return; }
            foreach (PdfReference field in form.Fields) { Add(field, Role.Field, null); }
        }

        internal void AddExtras(IReadOnlyDictionary<PdfName, QpdfObject>? extras)
        {
            if (extras is null) { return; }
            foreach (KeyValuePair<PdfName, QpdfObject> entry in extras)
            { Check(); Add(entry.Value, CatalogRole(entry.Key.RawSpan), null); }
        }

        private Role CatalogRole(ReadOnlySpan<byte> key)
            => key.SequenceEqual("AA"u8) ? Role.DocumentEvents : key.SequenceEqual("OpenAction"u8) ? Role.Action : Role.None;

        private Role ChildRole(Node node, ReadOnlySpan<byte> key)
        {
            Role roles = node.Roles, result = Role.None;
            if ((roles & Role.Page) != 0)
            {
                if (key.SequenceEqual("Annots"u8)) { result |= Role.Annotations; }
                if (key.SequenceEqual("AA"u8)) { result |= Role.PageEvents; }
            }
            if ((roles & Role.Annotation) != 0)
            {
                if (key.SequenceEqual("A"u8) && !IsSubtype(node, "Movie"u8) && !IsSubtype(node, "Popup"u8)) { result |= Role.Action; }
                if (key.SequenceEqual("AA"u8) && !IsSubtype(node, "Popup"u8)) { result |= Role.AnnotationEvents; }
                if (key.SequenceEqual("Popup"u8) || key.SequenceEqual("IRT"u8)) { result |= Role.Annotation; }
                if (IsSubtype(node, "Widget"u8)) { roles |= Role.Field; }
                else if (key.SequenceEqual("Parent"u8) && IsSubtype(node, "Popup"u8)) { result |= Role.Annotation; }
            }
            if ((roles & Role.Field) != 0)
            {
                if (key.SequenceEqual("Parent"u8)) { result |= Role.Field; }
                if (key.SequenceEqual("Kids"u8)) { result |= Role.Fields; }
                if (key.SequenceEqual("AA"u8)) { result |= Role.FieldEvents; }
            }
            if ((roles & Role.Action) != 0 && key.SequenceEqual("Next"u8))
            { result |= Resolve(Read((PdfDictionary)node.Original, key, _governor, _token)) is PdfArray ? Role.Actions : Role.Action; }
            if ((roles & Role.Outline) != 0)
            {
                if (key.SequenceEqual("A"u8)) { result |= Role.Action; }
                if (key.SequenceEqual("First"u8) || key.SequenceEqual("Last"u8) || key.SequenceEqual("Next"u8)
                    || key.SequenceEqual("Prev"u8) || key.SequenceEqual("Parent"u8)) { result |= Role.Outline; }
            }
            if (((roles & Role.PageEvents) != 0 && (key.SequenceEqual("O"u8) || key.SequenceEqual("C"u8)))
                || ((roles & Role.AnnotationEvents) != 0 && AnnotationEvent(key))
                || ((roles & Role.FieldEvents) != 0 && (key.SequenceEqual("K"u8) || key.SequenceEqual("F"u8) || key.SequenceEqual("V"u8) || key.SequenceEqual("C"u8)))
                || ((roles & Role.DocumentEvents) != 0 && (key.SequenceEqual("WC"u8) || key.SequenceEqual("WS"u8)
                    || key.SequenceEqual("DS"u8) || key.SequenceEqual("WP"u8) || key.SequenceEqual("DP"u8)))) { result |= Role.Action; }
            return result;
        }

        private static bool AnnotationEvent(ReadOnlySpan<byte> key)
            => key.SequenceEqual("E"u8) || key.SequenceEqual("X"u8) || key.SequenceEqual("D"u8) || key.SequenceEqual("U"u8)
            || key.SequenceEqual("Fo"u8) || key.SequenceEqual("Bl"u8) || key.SequenceEqual("PO"u8)
            || key.SequenceEqual("PC"u8) || key.SequenceEqual("PV"u8) || key.SequenceEqual("PI"u8);

        private static bool IsSubtype(Node node, ReadOnlySpan<byte> name)
            => node.Subtype is PdfName subtype && subtype.RawSpan.SequenceEqual(name);

        private static Role ItemRole(Role role)
            => ((role & Role.Annotations) != 0 ? Role.Annotation : Role.None)
                | ((role & Role.Fields) != 0 ? Role.Field : Role.None)
                | ((role & Role.Actions) != 0 ? Role.Action : Role.None);

        internal void Apply()
        {
            for (int index = 0; index < _pending.Count; index++)
            {
                Check(); Node node = _pending[index];
                if (node.Expanded == node.Roles) { continue; }
                node.Expanded = node.Roles;
                if (node.Original is PdfDictionary dictionary)
                {
                    if ((node.Roles & (Role.Field | Role.Annotation)) != 0)
                    { node.Subtype = Resolve(Read(dictionary, "Subtype"u8, _governor, _token)) as PdfName; }
                    if ((node.Roles & Role.Field) != 0 && IsSubtype(node, "Widget"u8))
                    { node.Roles |= Role.Annotation; node.Expanded = node.Roles; }
                    using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
                    while (entries.MoveNext(_governor, _token)) { Add(entries.Current.Value, ChildRole(node, entries.Current.Key.RawSpan), node); }
                    BindLeaves(node, dictionary);
                }
                else if (node.Original is PdfArray array)
                {
                    Role role = ItemRole(node.Roles);
                    for (int item = 0; item < array.Count; item++) { Add(array[item], role, node); }
                }
            }
            _pending.Clear();
            foreach (Node node in _nodes.Values)
            { Check(); if (node.Edited is not null) { node.Changed = true; ParserContainers.AddItem(_scratch, _pending, node, IntPtr.Size); } }
            for (int index = 0; index < _pending.Count; index++)
            {
                Check(); Node node = _pending[index];
                if (node.Parents is null) { continue; }
                foreach (Node parent in node.Parents)
                {
                    Check(); if (parent.Changed) { continue; }
                    parent.Changed = true; ParserContainers.AddItem(_scratch, _pending, parent, IntPtr.Size);
                }
            }
            foreach (Node node in _pending)
            {
                Check();
                if (node.Number != 0 && (node.Roles & Role.Page) == 0)
                { node.Reference = _copier.AddIndirectObjectWithId(node.Original, _copier.NextId); }
            }
            foreach (Node node in _pending)
            {
                Check();
                if (node.Number != 0) { _copier.ReplaceCopiedObject(node.Reference?.ObjectNumber ?? node.Number, Build(node, 0)); }
            }
            if (_pending.Count > 0) { _copier.MarkChangedRoots(); }
        }

        private Node? Find(QpdfObject? raw)
        {
            QpdfObject? value = Resolve(raw);
            return value is not null && _nodes.TryGetValue((value, (raw as PdfReference)?.ObjectNumber ?? 0), out Node? node) ? node : null;
        }

        internal QpdfObject? Map(QpdfObject? raw)
        {
            Check(); Node? node = Find(raw);
            return node is not { Changed: true } ? raw : node.Reference ?? (node.Number != 0 ? raw : Build(node, 0));
        }

        private QpdfObject Build(Node node, int depth)
        {
            Check(); if (node.Result is not null) { return node.Result; }
            if (depth > _governor.MaxNestingDepth) { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, _governor.MaxNestingDepth); }
            QpdfObject original = node.Edited ?? node.Original;
            QpdfObject Remap(QpdfObject value)
            {
                Node? child = Find(value);
                return child is not { Changed: true } ? value : child.Reference ?? (child.Number != 0 ? value : Build(child, depth + 1));
            }
            if (original is PdfArray array)
            {
                long bytes = checked(64L + IntPtr.Size * (long)array.Count); Graph.Memory.Reserve(bytes);
                QpdfObject[] values = new QpdfObject[array.Count]; Graph.Add(bytes);
                for (int index = 0; index < values.Length; index++) { Check(); values[index] = Remap(array[index]); }
                node.Result = PdfArray.FromOwnedItems(values);
            }
            else
            {
                PdfDictionary dictionary = (PdfDictionary)original;
                PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(Graph, out long bytes);
                using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
                while (source.MoveNext(_governor, _token))
                {
                    QpdfObject value = source.Current.Value;
                    if (ChildRole(node, source.Current.Key.RawSpan) != Role.None) { value = Remap(value); }
                    entries.Assign(Graph.Memory, _governor, source.Current.Key, value, _token);
                }
                node.Result = PdfDictionaryRebuild.Complete(Graph, entries, bytes);
            }
            return node.Result;
        }

        internal CopiedForm? MapForm(CopiedForm? form)
        {
            if (form is null) { return null; }
            List<PdfReference>? fields = null; List<QpdfObject>? order = null;
            for (int index = 0; index < form.Fields.Count; index++)
            {
                PdfReference value = form.Fields[index]; PdfReference mapped = Map(value) as PdfReference ?? value;
                if (fields is null && !Equals(value, mapped))
                {
                    fields = ParserContainers.CreateList<PdfReference>(_ownership, form.Fields.Count, IntPtr.Size);
                    for (int prefix = 0; prefix < index; prefix++) { Check(); fields.Add(form.Fields[prefix]); }
                }
                fields?.Add(mapped);
            }
            for (int index = 0; index < form.CalculationOrder.Count; index++)
            {
                QpdfObject value = form.CalculationOrder[index], mapped = Map(value) ?? value;
                if (order is null && !Equals(value, mapped))
                {
                    order = ParserContainers.CreateList<QpdfObject>(_ownership, form.CalculationOrder.Count, IntPtr.Size);
                    for (int prefix = 0; prefix < index; prefix++) { Check(); order.Add(form.CalculationOrder[prefix]); }
                }
                order?.Add(mapped);
            }
            if (fields is null && order is null) { return form; }
            _ownership.Reserve(80L);
            return form with { Fields = fields ?? form.Fields, CalculationOrder = order ?? form.CalculationOrder };
        }

        internal IReadOnlyDictionary<PdfName, QpdfObject>? MapExtras(IReadOnlyDictionary<PdfName, QpdfObject>? extras)
        {
            if (extras is null) { return null; }
            bool changed = false;
            foreach (KeyValuePair<PdfName, QpdfObject> entry in extras)
            { Check(); if (CatalogRole(entry.Key.RawSpan) != Role.None && !Equals(entry.Value, Map(entry.Value))) { changed = true; break; } }
            if (!changed) { return extras; }
            PdfNameTable<QpdfObject> result = PdfNameTable<QpdfObject>.Create(_ownership, _governor, IntPtr.Size, _token);
            foreach (KeyValuePair<PdfName, QpdfObject> entry in extras)
            {
                Check(); QpdfObject value = CatalogRole(entry.Key.RawSpan) == Role.None ? entry.Value : Map(entry.Value) ?? entry.Value;
                result.Assign(_ownership, _governor, entry.Key, value, _token);
            }
            result.Freeze(_ownership, _governor, _token); return result;
        }

        private void BindLeaves(Node node, PdfDictionary dictionary)
        {
            PdfDictionary result = node.Edited as PdfDictionary ?? dictionary;
            bool hasDestination = (node.Roles & Role.Outline) != 0 || (node.Roles & Role.Annotation) != 0 && IsSubtype(node, "Link"u8);
            if (hasDestination && Read(result, "Dest"u8, _governor, _token) is QpdfObject rawDestination)
            {
                QpdfObject bound = Destination(rawDestination);
                if (!ReferenceEquals(bound, rawDestination)) { result = PdfDictionaryRebuild.ReplaceEntry(Graph, result, "Dest"u8, bound); }
            }
            if ((node.Roles & Role.Action) != 0)
            {
                if (PdfDictionaryLookup.HasName(result, "S"u8, "GoTo"u8, Resolver, _governor, _token)
                    && Read(result, "D"u8, _governor, _token) is QpdfObject raw)
                {
                    QpdfObject bound = Destination(raw);
                    if (!ReferenceEquals(bound, raw)) { result = PdfDictionaryRebuild.ReplaceEntry(Graph, result, "D"u8, bound); }
                }
                else if (PdfDictionaryLookup.HasName(result, "S"u8, "URI"u8, Resolver, _governor, _token)
                    && Read(result, "URI"u8, _governor, _token) is QpdfObject uri)
                {
                    QpdfObject bound = UriValue(uri);
                    if (!ReferenceEquals(bound, uri)) { result = PdfDictionaryRebuild.ReplaceEntry(Graph, result, "URI"u8, bound); }
                }
            }
            if (!ReferenceEquals(result, dictionary)) { node.Edited = result; }
        }

        // Destination and URI leaf binding share the owning copier's ordinary
        // page mapping; placement copiers still prune source-page references.
        private QpdfObject Destination(QpdfObject raw)
        {
            QpdfObject? value = Resolve(raw), found = null;
            if (value is PdfName name)
            {
                PdfDictionary? destinations = CatalogCopy.ResolveDictionary(Read(_catalog, "Dests"u8, _governor, _token), _source, _governor, _token);
                if (destinations is not null) { found = Read(destinations, name.RawSpan, _governor, _token); }
            }
            else if (value is PdfByteString text)
            {
                PdfDictionary? names = CatalogCopy.ResolveDictionary(Read(_catalog, "Names"u8, _governor, _token), _source, _governor, _token);
                PdfDictionary? destinations = names is null ? null : CatalogCopy.ResolveDictionary(Read(names, "Dests"u8, _governor, _token), _source, _governor, _token);
                if (destinations is not null)
                {
                    using ParserMemoryScope walk = ParserMemoryScope.Create(_governor);
                    found = FindName(destinations, text.ValueMemory, ParserContainers.CreateSet<PdfDictionary>(walk), walk, 0);
                }
            }
            else { return raw; }
            found = CatalogCopy.ResolveValue(found, _source, _governor, _token);
            if (found is PdfDictionary wrapper) { found = CatalogCopy.ResolveValue(Read(wrapper, "D"u8, _governor, _token), _source, _governor, _token); }
            if (found is not PdfArray) { throw new QpdfException("The retained source-local named destination cannot be resolved."); }
            _sourceCopier.AdvanceNextId(_copier.NextId);
            QpdfObject result = _sourceCopier.CopyValue(found, 0); _copier.AdvanceNextId(_sourceCopier.NextId);
            return result;
        }

        private QpdfObject? FindName(PdfDictionary node, ReadOnlyMemory<byte> key, HashSet<PdfDictionary> path, ParserMemoryScope walk, int depth)
        {
            Check();
            if (depth > _governor.MaxNestingDepth) { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, _governor.MaxNestingDepth); }
            if (!ParserContainers.AddSetItem(walk, path, node, IntPtr.Size)) { throw new QpdfException("Source destination name tree contains a cycle."); }
            try
            {
                if (CatalogCopy.ResolveValue(Read(node, "Names"u8, _governor, _token), _source, _governor, _token) is PdfArray names)
                {
                    if (names.Count % 2 != 0) { throw new QpdfException("Source destination names must contain pairs."); }
                    int low = 0, high = names.Count / 2;
                    while (low < high)
                    {
                        Check(); int middle = low + (high - low) / 2;
                        if (CatalogCopy.ResolveValue(names[middle * 2], _source, _governor, _token) is not PdfByteString candidate)
                        { throw new QpdfException("Source destination keys must be strings."); }
                        int comparison = Compare(candidate.ValueMemory.Span, key.Span);
                        if (comparison == 0) { return names[middle * 2 + 1]; }
                        if (comparison < 0) { low = middle + 1; } else { high = middle; }
                    }
                }
                if (CatalogCopy.ResolveValue(Read(node, "Kids"u8, _governor, _token), _source, _governor, _token) is PdfArray kids)
                {
                    for (int index = 0; index < kids.Count; index++)
                    {
                        Check(); PdfDictionary? child = CatalogCopy.ResolveDictionary(kids[index], _source, _governor, _token);
                        if (child is null) { throw new QpdfException("Source destination child must be a dictionary."); }
                        if (CatalogCopy.ResolveValue(Read(child, "Limits"u8, _governor, _token), _source, _governor, _token) is PdfArray { Count: 2 } limits
                            && CatalogCopy.ResolveValue(limits[0], _source, _governor, _token) is PdfByteString first
                            && CatalogCopy.ResolveValue(limits[1], _source, _governor, _token) is PdfByteString last
                            && (Compare(key.Span, first.ValueSpan) < 0 || Compare(key.Span, last.ValueSpan) > 0)) { continue; }
                        QpdfObject? found = FindName(child, key, path, walk, depth + 1); if (found is not null) { return found; }
                    }
                }
                return null;
            }
            finally { path.Remove(node); }
        }

        private int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            int length = Math.Min(left.Length, right.Length);
            for (int offset = 0; offset < length; offset += Math.Min(8192, length - offset))
            { Check(); int count = Math.Min(8192, length - offset); int order = left.Slice(offset, count).SequenceCompareTo(right.Slice(offset, count)); if (order != 0) { return order; } }
            return left.Length.CompareTo(right.Length);
        }

        private QpdfObject UriValue(QpdfObject raw)
        {
            if (Resolve(raw) is not PdfByteString value || Absolute(value.ValueSpan)) { return raw; }
            PdfDictionary? uri = CatalogCopy.ResolveDictionary(Read(_catalog, "URI"u8, _governor, _token), _source, _governor, _token);
            QpdfObject? rawBase = uri is null ? null : CatalogCopy.ResolveValue(Read(uri, "Base"u8, _governor, _token), _source, _governor, _token);
            if (rawBase is null or PdfNull) { return raw; }
            if (rawBase is not PdfByteString baseValue) { throw new QpdfException("Source URI bases must be strings."); }
            if (!Absolute(baseValue.ValueSpan)) { throw new QpdfException("Source URI bases must be absolute URI strings."); }
            using ParserMemoryScope text = ParserMemoryScope.Create(_governor);
            // URI parsing is a BCL boundary. Fund its transient strings/parts
            // coarsely, and keep only the authored PDF byte string afterward.
            text.Reserve(checked(1024L + 16L * (value.ValueMemory.Length + (long)baseValue.ValueMemory.Length)));
            string basis = Ascii(baseValue.ValueMemory), relative = Ascii(value.ValueMemory);
            Check();
            if (!Uri.TryCreate(basis, UriKind.Absolute, out Uri? absoluteBase) || !Uri.TryCreate(absoluteBase, relative, out Uri? absolute))
            { throw new QpdfException("The retained URI action's explicit source base cannot be resolved."); }
            string result = absolute.AbsoluteUri; Check();
            long bytes = checked(64L + result.Length); Graph.Memory.Reserve(bytes);
            byte[] encoded = new byte[result.Length];
            for (int index = 0; index < encoded.Length; index++) { if ((index & 255) == 0) { Check(); } encoded[index] = checked((byte)result[index]); }
            Graph.Add(bytes); return PdfByteString.FromOwnedBytes(encoded, false);
        }

        private string Ascii(ReadOnlyMemory<byte> value)
            => string.Create(value.Length, (value, this), static (target, state) =>
            {
                for (int index = 0; index < target.Length; index++)
                {
                    if ((index & 255) == 0) { state.Item2.Check(); }
                    byte unit = state.value.Span[index];
                    if (unit is < 32 or >= 127 or (byte)'\\') { throw new QpdfException("Source URI action binding requires ASCII URI strings."); }
                    target[index] = (char)unit;
                }
            });

        private bool Absolute(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty || value[0] is not (>= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z')) { return false; }
            for (int index = 1; index < value.Length; index++)
            {
                if ((index & 255) == 0) { Check(); }
                byte unit = value[index]; if (unit == ':') { return true; }
                if (unit is not (>= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'-' or (byte)'.')) { return false; }
            }
            return false;
        }

        public void Dispose()
        {
            try { if (_graph is not null) { _graph.Memory.TransferTo(_ownership, _graph.Bytes); } }
            finally { _graph?.Dispose(); _scratch.Dispose(); }
        }
    }
}
