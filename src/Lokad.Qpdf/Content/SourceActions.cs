using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Preserves retained action meaning across source contexts and baked page frames.</summary>
internal static partial class SourceActions
{
    [Flags]
    private enum Role
    {
        None = 0, Page = 1, Annotation = 2, Field = 4, Action = 8, Outline = 16,
        PageEvents = 32, AnnotationEvents = 64, FieldEvents = 128, DocumentEvents = 256,
        Annotations = 512, Fields = 1024, Actions = 2048,
        Form = 4096, Names = 8192, DestinationIndex = 16384, DestinationTree = 32768,
        DestinationTrees = 65536, DestinationPairs = 131072, Destination = 262144, OpenAction = 524288,
        Catalog = 1048576,
    }

    // Equal PDF contents are not graph identity. One identity can also carry
    // different semantics on distinct incoming role edges.
    private readonly struct NodeKey(QpdfObject value, int number, Role role) : IEquatable<NodeKey>
    {
        private readonly QpdfObject _value = value;
        private readonly int _number = number;
        private readonly Role _role = role;
        public bool Equals(NodeKey other) => ReferenceEquals(_value, other._value) && _number == other._number && _role == other._role;
        public override bool Equals(object? other) => other is NodeKey key && Equals(key);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(_value), _number, _role);
    }

    private sealed class Node(QpdfObject value, int number, Role role)
    {
        internal readonly QpdfObject Original = value;
        internal readonly int Number = number;
        internal readonly Role Roles = role;
        internal List<Node>? Parents;
        internal QpdfObject? Edited, Result;
        internal PdfArray? PromotedNext;
        internal PdfName? Subtype;
        internal PdfReference? Reference;
        internal bool Changed, SkippedAction;
    }

    internal static (CopiedForm? Form, CopiedForm? AppearanceForm, IReadOnlyDictionary<PdfName, QpdfObject>? Extras, PdfReference? Outlines) BindDocument(
        DocumentEngine engine, GraphCopier copier, CopiedForm? form, CopiedForm? appearanceForm,
        IReadOnlyDictionary<PdfName, QpdfObject>? extras, bool preserveContext, bool discardLocalArticles,
        PdfReference? outlines, QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
    {
        PdfDictionary? catalog = Context(engine, governor, token);
        if (catalog is not null && preserveContext && PreservesContext(catalog, engine, copier.CopiedObjects, extras, governor, token))
        { catalog = null; }
        if (catalog is null && !discardLocalArticles) { return (form, appearanceForm, extras, outlines); }
        using Binding binding = new(engine, copier, copier, catalog, discardLocalArticles, limits, governor, ownership, token);
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
            binding.MapExtras(extras), binding.Map(outlines, Role.Outline) as PdfReference);
    }

    internal static void BindPlacement(DocumentEngine engine, GraphCopier sourceCopier, GraphCopier destination,
        List<PdfReference> annotations, List<AcroFormCopy.FieldImport> imports, int firstImport, QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token, ref int allocator)
    {
        PdfDictionary? catalog = Context(engine, governor, token);
        destination.AdvanceNextId(allocator); sourceCopier.AdvanceNextId(allocator);
        using Binding binding = new(engine, sourceCopier, destination, catalog, true, limits, governor, ownership, token);
        foreach (PdfReference annotation in annotations) { binding.Add(annotation, Role.Annotation, null); }
        for (int index = firstImport; index < imports.Count; index++) { binding.AddForm(imports[index].Part); }
        binding.Apply();
        for (int index = 0; index < annotations.Count; index++)
        { annotations[index] = binding.Map(annotations[index], Role.Annotation) as PdfReference ?? annotations[index]; }
        for (int index = firstImport; index < imports.Count; index++)
        {
            AcroFormCopy.FieldImport import = imports[index];
            CopiedForm? mapped = binding.MapForm(import.Part);
            if (mapped is not null && !ReferenceEquals(mapped, import.Part))
            { ownership.Reserve(64L); imports[index] = import with { Part = mapped }; }
        }
        allocator = Math.Max(destination.NextId, sourceCopier.NextId);
    }

    internal static (CopiedForm? Registrations, PdfDictionary? Form, IReadOnlyDictionary<PdfName, QpdfObject>? Extras, PdfReference? Outlines) BindRotation(
        GraphCopier copier, Dictionary<int, QpdfObject> pool, IReadOnlyList<PdfReference> pages, IReadOnlyDictionary<int, double[]>? frames,
        CopiedForm? registrations, PdfDictionary? form, IReadOnlyDictionary<PdfName, QpdfObject>? extras, PdfReference? outlines,
        ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
    {
        if (frames is null || frames.Count == 0) { return (registrations, form, extras, outlines); }
        copier.UseOutputPool(pool, token);
        using Binding binding = new(copier, frames, governor, ownership, token);
        binding.AddRotationPages(copier, pages);
        binding.Add(form, Role.Form, null); binding.AddExtras(extras); binding.Add(outlines, Role.Outline, null);
        binding.Apply();
        return (binding.MapForm(registrations), binding.Map(form, Role.Form) as PdfDictionary, binding.MapExtras(extras),
            binding.Map(outlines, Role.Outline) as PdfReference);
    }

    internal static PdfDictionary BindCatalogRotation(GraphCopier copier, IReadOnlyList<PdfReference> pages,
        PdfDictionary catalog, IReadOnlyDictionary<int, double[]> frames, ResourceGovernor governor,
        ParserMemoryScope ownership, CancellationToken token)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        using Binding binding = new(copier, copier.CreateResolver(scratch), frames, governor, ownership, token);
        binding.AddRotationPages(copier, pages);
        binding.Add(catalog, Role.Catalog, null);
        binding.Apply();
        return binding.Map(catalog, Role.Catalog) as PdfDictionary ?? catalog;
    }

    internal static (PdfDictionary? Form, Dictionary<int, QpdfObject>? Objects) ProjectForm(
        PdfDictionary? form, IReadOnlyDictionary<int, QpdfObject> pool, PoolResolver resolver,
        ReadOnlySpan<PdfReference> pages, HashSet<int> survivors, ref int? maximum, int reservedNumber,
        QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
    {
        if (form is null || survivors.Count == 0) { return (form, null); }
        Dictionary<int, PdfArray>? children = AcroFormCopy.ProjectFieldKids(form, resolver, survivors, limits,
            ownership, governor, token, out PdfArray? calculationOrder);
        if (children is null) { return (form, null); }
        using Binding binding = new(resolver, children, calculationOrder, governor, ownership, token);
        foreach (PdfReference page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (pool.TryGetValue(page.ObjectNumber, out QpdfObject? value) && value is PdfDictionary dictionary)
            { binding.AddProjectionPage(page, dictionary); }
        }
        binding.Add(form, Role.Form, null);
        int last = ProjectionMaximum(pool, ref maximum, reservedNumber, governor, token);
        Dictionary<int, QpdfObject>? objects = binding.ApplyProjection(last, limits);
        return ((PdfDictionary?)binding.Map(form, Role.Form), objects);
    }

    internal static int ProjectionMaximum(IReadOnlyDictionary<int, QpdfObject> pool, ref int? maximum,
        int reservedNumber, ResourceGovernor governor, CancellationToken token)
    {
        if (maximum.HasValue) { return maximum.Value; }
        int last = reservedNumber;
        foreach (int id in pool.Keys)
        { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); last = Math.Max(last, id); }
        maximum = last;
        return last;
    }

    internal static PdfDictionary? ProjectOutputForm(PdfDictionary? form, Dictionary<int, QpdfObject> pool,
        GraphCopier copier, ReadOnlySpan<PdfReference> pages, HashSet<int> survivors, HashSet<int>? generatedPacking, QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
    {
        if (form is null) { return null; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        int? maximum = null;
        (PdfDictionary? projected, Dictionary<int, QpdfObject>? objects) = ProjectForm(form, pool,
            PoolResolver.Create(scratch, pool), pages, survivors, ref maximum, copier.NextId - 1,
            limits, governor, ownership, token);
        if (objects is null) { return projected; }
        foreach ((int number, QpdfObject value) in objects)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (!pool.ContainsKey(number))
            { ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size, (int)Math.Min(int.MaxValue, limits.MaxContainerSize)); }
            pool[number] = value;
            if (generatedPacking is not null && ObjectStreamPreparation.IsCompressibleValue(number, value, 1, 2, governor, token))
            { ParserContainers.AddSetItem(ownership, generatedPacking, number, sizeof(int)); }
        }
        copier.MarkChangedRoots();
        return projected;
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
    private sealed partial class Binding : IDisposable
    {
        private readonly DocumentEngine? _source;
        private readonly GraphCopier? _sourceCopier;
        private readonly GraphCopier? _copier;
        private readonly PdfDictionary? _catalog;
        private readonly IReadOnlyDictionary<int, double[]>? _frames;
        private readonly ResourceGovernor _governor;
        private readonly ParserMemoryScope _ownership, _scratch;
        private readonly CancellationToken _token;
        private readonly Dictionary<NodeKey, Node> _nodes;
        private readonly List<Node> _pending;
        private readonly Dictionary<int, PdfArray>? _fieldKids;
        private readonly PdfArray? _calculationOrder;
        private readonly Dictionary<int, PdfReference>? _widgetPages;
        private OwnedAllocationBatch? _graph;
        private NamedDestinations? _destinations;
        internal IIndirectResolver Resolver { get; }
        private OwnedAllocationBatch Graph => _graph ??= OwnedAllocationBatch.Create(_governor, _token);

        internal Binding(DocumentEngine source, GraphCopier sourceCopier, GraphCopier copier, PdfDictionary? catalog,
            bool discardLocalArticles, QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
            : this(new PoolResolver(copier.CopiedObjects), governor, ownership, token)
        {
            _source = catalog is null ? null : source;
            _sourceCopier = sourceCopier; _copier = copier; _catalog = catalog;
            _projectArticleActions = discardLocalArticles; _maxActionContainerSize = limits.MaxContainerSize;
        }

        internal Binding(GraphCopier copier, IReadOnlyDictionary<int, double[]> frames,
            ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
            : this(copier, new PoolResolver(copier.CopiedObjects), frames, governor, ownership, token) { }

        internal Binding(GraphCopier copier, IIndirectResolver resolver, IReadOnlyDictionary<int, double[]> frames,
            ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
            : this(resolver, governor, ownership, token)
        { _copier = copier; _frames = frames; }

        internal void AddRotationPages(GraphCopier copier, IReadOnlyList<PdfReference> pages)
        {
            foreach (PdfReference reference in pages)
            {
                Check();
                if (copier.CopiedObjects.TryGetValue(reference.ObjectNumber, out QpdfObject? value) && value is PdfDictionary page)
                { AddPage(reference.ObjectNumber, page); }
            }
        }

        internal Binding(PoolResolver resolver, Dictionary<int, PdfArray> fieldKids, PdfArray? calculationOrder,
            ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
            : this(resolver, governor, ownership, token)
        {
            _fieldKids = fieldKids; _calculationOrder = calculationOrder;
            try { _widgetPages = ParserContainers.CreateDictionary<int, PdfReference>(_scratch); }
            catch { _scratch.Dispose(); throw; }
        }

        private Binding(IIndirectResolver resolver, ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
        {
            Resolver = resolver;
            _governor = governor; _ownership = ownership; _token = token;
            _scratch = ParserMemoryScope.Create(governor);
            try
            {
                _scratch.Reserve(280L);
                _nodes = ParserContainers.CreateDictionary<NodeKey, Node>(_scratch);
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

        internal void AddProjectionPage(PdfReference page, PdfDictionary dictionary)
        {
            if (Resolve(Read(dictionary, "Annots"u8, _governor, _token)) is not PdfArray annotations) { return; }
            Dictionary<int, PdfReference> widgetPages = _widgetPages ?? throw new InvalidOperationException("Missing projection page map.");
            for (int index = 0; index < annotations.Count; index++)
            {
                Check();
                if (annotations[index] is not PdfReference annotation || widgetPages.ContainsKey(annotation.ObjectNumber)) { continue; }
                ParserContainers.EnsureEntryCapacity(_scratch, widgetPages, IntPtr.Size);
                widgetPages.Add(annotation.ObjectNumber, page);
            }
            AddNode(dictionary, page.ObjectNumber, Role.Page, null);
        }

        internal void Add(QpdfObject? raw, Role role, Node? parent)
        {
            Check();
            if (role == Role.None) { return; }
            QpdfObject? value = Resolve(raw);
            if (value is not (PdfDictionary or PdfArray)) { return; }
            if (value is PdfArray && (role & (Role.Annotations | Role.Fields | Role.Actions | Role.Destination
                | Role.DestinationTrees | Role.DestinationPairs | Role.OpenAction)) == 0) { return; }
            AddNode(value, (raw as PdfReference)?.ObjectNumber ?? 0, role, parent);
        }

        private void AddNode(QpdfObject value, int number, Role role, Node? parent)
        {
            role = NodeRole(value, role);
            NodeKey key = new(value, number, role);
            if (!_nodes.TryGetValue(key, out Node? node))
            {
                _scratch.Reserve(96L); node = new(value, number, role);
                ParserContainers.EnsureEntryCapacity(_scratch, _nodes, 2L * IntPtr.Size);
                _nodes.Add(key, node);
                ParserContainers.AddItem(_scratch, _pending, node, IntPtr.Size);
            }
            if (parent is not null)
            {
                node.Parents ??= ParserContainers.CreateList<Node>(_scratch);
                ParserContainers.AddItem(_scratch, node.Parents, parent, IntPtr.Size);
            }
        }

        // A genuine widget is both a field and an annotation; its registrations
        // must still point to the same replacement.
        private Role NodeRole(QpdfObject value, Role role)
            => role == Role.OpenAction ? value is PdfArray ? Role.Destination : Role.Action
                : (role & (Role.Annotation | Role.Field)) != 0 && value is PdfDictionary dictionary
                && PdfDictionaryLookup.HasName(dictionary, "Subtype"u8, "Widget"u8, Resolver, _governor, _token)
                ? role | Role.Annotation | Role.Field : role;

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
            => key.SequenceEqual("AA"u8) ? Role.DocumentEvents
                : key.SequenceEqual("OpenAction"u8) ? _frames is null && _splitPages is null ? Role.Action : Role.OpenAction
                : _frames is null && _splitPages is null ? Role.None : key.SequenceEqual("Names"u8) ? Role.Names
                : key.SequenceEqual("Dests"u8) ? Role.DestinationIndex : Role.None;

        private Role ChildRole(Node node, ReadOnlySpan<byte> key)
        {
            Role roles = node.Roles, result = Role.None;
            if (roles == Role.Catalog)
            {
                if (key.SequenceEqual("Outlines"u8)) { return Role.Outline; }
                if (key.SequenceEqual("AcroForm"u8)) { return Role.Form; }
                return CatalogRole(key);
            }
            if ((_frames is not null || _fieldKids is not null || _splitPages is not null || _projectArticleActions) && (roles & Role.Form) != 0
                && (key.SequenceEqual("Fields"u8) || key.SequenceEqual("CO"u8))) { result |= Role.Fields; }
            if (_frames is not null || _splitPages is not null)
            {
                if ((roles & Role.Names) != 0 && key.SequenceEqual("Dests"u8)) { result |= Role.DestinationTree; }
                if ((roles & Role.DestinationTree) != 0 && key.SequenceEqual("Kids"u8)) { result |= Role.DestinationTrees; }
            }
            if (_frames is not null)
            {
                if ((roles & Role.DestinationIndex) != 0) { result |= Role.Destination; }
                if ((roles & Role.DestinationTree) != 0 && key.SequenceEqual("Names"u8)) { result |= Role.DestinationPairs; }
                if ((roles & Role.Destination) != 0 && key.SequenceEqual("D"u8)) { result |= Role.Destination; }
                if (key.SequenceEqual("Dest"u8) && ((roles & Role.Outline) != 0
                    || (roles & Role.Annotation) != 0 && IsSubtype(node, "Link"u8))) { result |= Role.Destination; }
                if ((roles & Role.Action) != 0 && key.SequenceEqual("D"u8)
                    && PdfDictionaryLookup.HasName((PdfDictionary)node.Original, "S"u8, "GoTo"u8, Resolver, _governor, _token))
                { result |= Role.Destination; }
            }
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
                if (key.SequenceEqual("Parent"u8) && IsSubtype(node, "Popup"u8)) { result |= Role.Annotation; }
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
                | ((role & Role.Actions) != 0 ? Role.Action : Role.None)
                | ((role & Role.DestinationTrees) != 0 ? Role.DestinationTree : Role.None)
                | ((role & Role.DestinationPairs) != 0 ? Role.Destination : Role.None);

        private void PrepareChanges()
        {
            for (int index = 0; index < _pending.Count; index++)
            {
                Check(); Node node = _pending[index];
                if (node.Original is PdfDictionary dictionary)
                {
                    if ((node.Roles & (Role.Field | Role.Annotation)) != 0)
                    { node.Subtype = Resolve(Read(dictionary, "Subtype"u8, _governor, _token)) as PdfName; }
                    if ((node.Roles & Role.Field) != 0 && _fieldKids?.TryGetValue(node.Number, out PdfArray? kids) == true)
                    { node.Edited = dictionary = PdfDictionaryRebuild.ReplaceEntry(Graph, dictionary, "Kids"u8, kids); }
                    if ((node.Roles & Role.Form) != 0 && _calculationOrder is not null)
                    { node.Edited = dictionary = PdfDictionaryRebuild.ReplaceEntry(Graph, dictionary, "CO"u8, _calculationOrder); }
                    using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
                    while (entries.MoveNext(_governor, _token)) { Add(entries.Current.Value, ChildRole(node, entries.Current.Key.RawSpan), node); }
                    BindLeaves(node, dictionary);
                    if (_splitPages is not null) { ProjectNavigation(node, dictionary); }
                    if (_projectArticleActions) { ProjectThreadAction(node, dictionary); }
                }
                else if (node.Original is PdfArray array)
                {
                    if (node.Roles == Role.Destination)
                    {
                        if (_frames is not null && array.Count > 0 && array[0] is PdfReference page && _frames.TryGetValue(page.ObjectNumber, out double[]? matrix))
                        {
                            PdfArray result = DestinationCoordinates.Transform(array, matrix, Resolver, Graph);
                            if (!ReferenceEquals(result, array)) { node.Edited = result; }
                        }
                        if (_splitPages is not null && IsStaleDestination(array)) { node.Edited = PdfNull.Instance; }
                        continue;
                    }
                    Role role = ItemRole(node.Roles);
                    for (int item = 0; item < array.Count; item++)
                    { if (node.Roles != Role.DestinationPairs || (item & 1) != 0) { Add(array[item], role, node); } }
                }
            }
            if (_splitPages is not null) { PrepareDestinationIndexes(); }
            if (_hasStaleContinuations) { PrepareActionContinuations(); }
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
        }

        internal void Apply()
        {
            GraphCopier copier = _copier ?? throw new InvalidOperationException("Missing action copier.");
            PrepareChanges();
            foreach (Node node in _pending)
            {
                Check();
                if (node.Number != 0 && (node.Roles & Role.Page) == 0 && node.Edited is not PdfNull)
                { node.Reference = copier.AddIndirectObjectWithId(node.Original, copier.NextId); }
            }
            foreach (Node node in _pending)
            {
                Check();
                if (node.Number != 0 && node.Edited is not PdfNull)
                { copier.ReplaceCopiedObject(node.Reference?.ObjectNumber ?? node.Number, Build(node, 0)); }
            }
            if (_pending.Count > 0) { copier.MarkChangedRoots(); }
        }

        internal Dictionary<int, QpdfObject>? ApplyProjection(int maximum, QpdfLimits limits)
        {
            PrepareChanges();
            if (_pending.Count == 0) { return null; }
            Dictionary<int, QpdfObject> result = ParserContainers.CreateDictionary<int, QpdfObject>(_ownership);
            foreach (Node node in _pending)
            {
                Check();
                if (node.Number == 0 || (node.Roles & Role.Page) != 0 || node.Edited is PdfNull) { continue; }
                _ownership.Reserve(32L);
                node.Reference = new PdfReference(checked(++maximum), 0);
            }
            foreach (Node node in _pending)
            {
                Check();
                if (node.Number == 0 || node.Edited is PdfNull) { continue; }
                ParserContainers.EnsureEntryCapacity(_ownership, result, IntPtr.Size, (int)Math.Min(int.MaxValue, limits.MaxContainerSize));
                result.Add(node.Reference?.ObjectNumber ?? node.Number, Build(node, 0));
            }
            return result;
        }

        private Node? Find(QpdfObject? raw, Role role)
        {
            if (role == Role.None) { return null; }
            QpdfObject? value = Resolve(raw);
            return value is not null && _nodes.TryGetValue(new(value, (raw as PdfReference)?.ObjectNumber ?? 0, NodeRole(value, role)), out Node? node) ? node : null;
        }

        internal QpdfObject? Map(QpdfObject? raw, Role role)
        {
            Check(); Node? node = Find(raw, role);
            return node is not { Changed: true } ? raw : node.Edited is PdfNull ? PdfNull.Instance
                : node.Reference ?? (node.Number != 0 ? raw : Build(node, 0));
        }

        private QpdfObject Build(Node node, int depth)
        {
            Check(); if (node.Result is not null) { return node.Result; }
            if (depth > _governor.MaxNestingDepth) { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, _governor.MaxNestingDepth); }
            if (node.Edited is PdfArray edited) { return node.Result = edited; }
            QpdfObject original = node.Edited ?? node.Original;
            QpdfObject Remap(QpdfObject value, Role role)
            {
                Node? child = Find(value, role);
                return child is not { Changed: true } ? value : child.Edited is PdfNull ? PdfNull.Instance
                    : child.Reference ?? (child.Number != 0 ? value : Build(child, depth + 1));
            }
            if (original is PdfArray array)
            {
                if ((_splitPages is not null || _projectArticleActions) && node.Roles is Role.Actions or Role.DestinationTrees)
                { return node.Result = BuildProjectedArray(array, ItemRole(node.Roles), depth); }
                long bytes = checked(64L + IntPtr.Size * (long)array.Count); Graph.Memory.Reserve(bytes);
                QpdfObject[] values = new QpdfObject[array.Count]; Graph.Add(bytes);
                Role role = ItemRole(node.Roles);
                for (int index = 0; index < values.Length; index++)
                { Check(); values[index] = node.Roles == Role.DestinationPairs && (index & 1) == 0 ? array[index] : Remap(array[index], role); }
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
                    ReadOnlySpan<byte> key = source.Current.Key.RawSpan;
                    if (node.SkippedAction && key.SequenceEqual("Next"u8)) { continue; }
                    Role role = ChildRole(node, key);
                    if (role != Role.None) { value = Remap(value, role); }
                    if ((_splitPages is not null || _projectArticleActions) && !ReferenceEquals(source.Current.Value, value) && value is PdfNull)
                    { continue; }
                    entries.Assign(Graph.Memory, _governor, source.Current.Key, value, _token);
                }
                if (node.PromotedNext is PdfArray next)
                { entries.Assign(Graph.Memory, _governor, PdfDictionaryRebuild.CreateName(Graph, "Next"u8), BuildProjectedArray(next, Role.Action, depth), _token); }
                if (IsSubtype(node, "Widget"u8) && _widgetPages?.TryGetValue(node.Number, out PdfReference? page) == true)
                { entries.Assign(Graph.Memory, _governor, PdfDictionaryRebuild.CreateName(Graph, "P"u8), page, _token); }
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
                PdfReference value = form.Fields[index]; PdfReference mapped = Map(value, Role.Field) as PdfReference ?? value;
                if (fields is null && !Equals(value, mapped))
                {
                    fields = ParserContainers.CreateList<PdfReference>(_ownership, form.Fields.Count, IntPtr.Size);
                    for (int prefix = 0; prefix < index; prefix++) { Check(); fields.Add(form.Fields[prefix]); }
                }
                fields?.Add(mapped);
            }
            for (int index = 0; index < form.CalculationOrder.Count; index++)
            {
                QpdfObject value = form.CalculationOrder[index], mapped = Map(value, Role.Field) ?? value;
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
            { Check(); Role role = CatalogRole(entry.Key.RawSpan); if (role != Role.None && !Equals(entry.Value, Map(entry.Value, role))) { changed = true; break; } }
            if (!changed) { return extras; }
            PdfNameTable<QpdfObject> result = PdfNameTable<QpdfObject>.Create(_ownership, _governor, IntPtr.Size, _token);
            foreach (KeyValuePair<PdfName, QpdfObject> entry in extras)
            {
                Check(); Role role = CatalogRole(entry.Key.RawSpan); QpdfObject value = role == Role.None ? entry.Value : Map(entry.Value, role) ?? entry.Value;
                if (_projectArticleActions && value is PdfNull && !ReferenceEquals(entry.Value, value)) { continue; }
                result.Assign(_ownership, _governor, entry.Key, value, _token);
            }
            result.Freeze(_ownership, _governor, _token); return result;
        }

        private void BindLeaves(Node node, PdfDictionary dictionary)
        {
            if (_source is null) { return; }
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
            DocumentEngine source = _source ?? throw new InvalidOperationException("Missing source context.");
            GraphCopier sourceCopier = _sourceCopier ?? throw new InvalidOperationException("Missing source copier.");
            GraphCopier copier = _copier ?? throw new InvalidOperationException("Missing action copier.");
            PdfDictionary catalog = _catalog ?? throw new InvalidOperationException("Missing source catalog.");
            QpdfObject? value = Resolve(raw);
            if (value is not (PdfName or PdfByteString)) { return raw; }
            _destinations ??= NamedDestinations.Create(source, catalog, _scratch, _governor, _token);
            QpdfObject? found = _destinations.Find(value);
            if (found is not PdfArray) { throw new QpdfParseException("The retained source-local named destination cannot be resolved.", null, null); }
            sourceCopier.AdvanceNextId(copier.NextId);
            QpdfObject result = sourceCopier.CopyValue(found, 0); copier.AdvanceNextId(sourceCopier.NextId);
            return result;
        }

        private QpdfObject UriValue(QpdfObject raw)
        {
            DocumentEngine source = _source ?? throw new InvalidOperationException("Missing source context.");
            PdfDictionary catalog = _catalog ?? throw new InvalidOperationException("Missing source catalog.");
            if (Resolve(raw) is not PdfByteString value || Absolute(value.ValueSpan)) { return raw; }
            PdfDictionary? uri = CatalogCopy.ResolveDictionary(Read(catalog, "URI"u8, _governor, _token), source, _governor, _token);
            QpdfObject? rawBase = uri is null ? null : CatalogCopy.ResolveValue(Read(uri, "Base"u8, _governor, _token), source, _governor, _token);
            if (rawBase is null or PdfNull) { return raw; }
            if (rawBase is not PdfByteString baseValue) { throw new QpdfParseException("Source URI bases must be strings.", null, null); }
            if (!Absolute(baseValue.ValueSpan)) { throw new QpdfUnsupportedFeatureException("Source URI bases must be absolute URI strings."); }
            using ParserMemoryScope text = ParserMemoryScope.Create(_governor);
            // URI parsing is a BCL boundary. Fund its transient strings/parts
            // coarsely, and keep only the authored PDF byte string afterward.
            text.Reserve(checked(1024L + 16L * (value.ValueMemory.Length + (long)baseValue.ValueMemory.Length)));
            string basis = Ascii(baseValue.ValueMemory), relative = Ascii(value.ValueMemory);
            Check();
            if (!Uri.TryCreate(basis, UriKind.Absolute, out Uri? absoluteBase) || !Uri.TryCreate(absoluteBase, relative, out Uri? absolute))
            { throw new QpdfUnsupportedFeatureException("The retained URI action's explicit source base cannot be resolved."); }
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
                    if (unit is < 32 or >= 127 or (byte)'\\') { throw new QpdfUnsupportedFeatureException("Source URI action binding requires ASCII URI strings."); }
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
