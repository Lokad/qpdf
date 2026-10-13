using System.Globalization;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// The single memoized graph copier: destination identities are registered
/// before descending, so cyclic and shared subgraphs are copied once. Page
/// ancestry (`/Parent` on page dictionaries) is never followed here; the selector
/// materializes inherited attributes explicitly, while field and annotation
/// `/Parent` links remap through the shared memo table. References to already-copied objects remap to
/// their copies; references to other pages prune to null under the ordinary selection
/// policy so omitted pages can never resurrect. Explicit form pruning lives in AcroFormCopy
/// behind `--remove-acroform` (plus subset pruning); ordinary selection and rewrite paths
/// preserve the form. Page-tree (`/Pages`) and catalog
/// references stay structural errors and fail loudly.
/// </summary>
internal sealed class GraphCopier
{
    private static readonly PdfName ParentKey = PdfName.FromAscii("Parent");
    private static readonly PdfName LengthKey = PdfName.FromAscii("Length");
    private static readonly PdfName TypeKey = PdfName.FromAscii("Type");
    private static readonly PdfName PageType = PdfName.FromAscii("Page");
    private static readonly PdfName PagesType = PdfName.FromAscii("Pages");
    private static readonly PdfName CatalogType = PdfName.FromAscii("Catalog");
    private readonly IIndirectResolver _resolver;
    private readonly ResourceGovernor _governor;
    private readonly QpdfLimits _limits;
    private readonly Dictionary<int, int> _mapping;
    private readonly Dictionary<int, QpdfObject> _copied;
    private readonly ParserMemoryScope? _ownership;
    private int _nextId;
    private int _rootPageNumber = -1;

    internal GraphCopier(DocumentEngine engine, ResourceGovernor governor, QpdfLimits limits, int firstId)
        : this(engine, governor, limits, firstId, null)
    {
    }

    internal static GraphCopier CreateOwned(DocumentEngine engine, ResourceGovernor governor,
        QpdfLimits limits, int firstId, ParserMemoryScope ownership)
    {
        ownership.CheckOwner(governor);
        ownership.Reserve(80L);
        return new GraphCopier(engine, governor, limits, firstId, ownership);
    }

    private GraphCopier(DocumentEngine engine, ResourceGovernor governor, QpdfLimits limits, int firstId,
        ParserMemoryScope? ownership)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        _resolver = (IIndirectResolver)engine;
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _limits = limits;
        if (firstId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstId), "Destination numbering must start positive.");
        }

        _nextId = firstId;
        _ownership = ownership;
        if (ownership is null)
        {
            _mapping = new();
            _copied = new();
        }
        else
        {
            ownership.CheckOwner(governor);
            _mapping = ParserContainers.CreateDictionary<int, int>(ownership);
            _copied = ParserContainers.CreateDictionary<int, QpdfObject>(ownership);
        }
    }

    private void Reserve(long bytes)
    {
        if (_ownership is null) { _governor.ReserveMemory(bytes); }
        else { _ownership.Reserve(bytes); }
    }

    private void Store(int id, QpdfObject value)
    {
        if (_ownership is not null && !_copied.ContainsKey(id))
        {
            ParserContainers.EnsureEntryCapacity(_ownership, _copied, IntPtr.Size);
        }
        _copied[id] = value;
    }

    private void Map(int source, int destination)
    {
        if (_ownership is not null && !_mapping.ContainsKey(source))
        {
            ParserContainers.EnsureEntryCapacity(_ownership, _mapping, sizeof(int));
        }
        _mapping[source] = destination;
    }

    private IReadOnlyDictionary<PdfName, QpdfObject> CreateEntries(int count)
    {
        if (_ownership is null) { return new Dictionary<PdfName, QpdfObject>(); }
        PdfNameTable<QpdfObject> entries = PdfNameTable<QpdfObject>.Create(_ownership, _governor, IntPtr.Size, _governor.CancellationToken);
        entries.EnsureCapacity(_ownership, _governor, count, _governor.CancellationToken);
        return entries;
    }

    private void SetEntry(IReadOnlyDictionary<PdfName, QpdfObject> entries, PdfName key, QpdfObject value)
    {
        if (_ownership is not null && entries is PdfNameTable<QpdfObject> names)
        {
            names.Assign(_ownership, _governor, key, value, _governor.CancellationToken);
            return;
        }
        ((Dictionary<PdfName, QpdfObject>)entries)[key] = value;
    }

    private PdfDictionary CompleteDictionary(IReadOnlyDictionary<PdfName, QpdfObject> entries)
    {
        if (_ownership is null) { return new PdfDictionary(entries); }
        if (entries is not PdfNameTable<QpdfObject> names) { throw new InvalidOperationException("Owned copy requires governed name storage."); }
        names.Freeze(_ownership, _governor, _governor.CancellationToken);
        _ownership.Reserve(32L);
        return PdfDictionary.FromOwnedEntries(names);
    }

    internal IReadOnlyDictionary<int, QpdfObject> CopiedObjects => _copied;

    // Inspection keeps original identities and immutable backing. Register only
    // newly reached values; already prepared pages stop the traversal at /P.
    internal QpdfObject RetainSourceValue(QpdfObject value)
    {
        using ParserMemoryScope traversal = ParserMemoryScope.Create(_governor);
        _ = GraphReachability.Find(new RetainedSourceResolver(this), value, traversal, _governor);
        return value;
    }

    private sealed class RetainedSourceResolver(GraphCopier copier) : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            if (!copier._resolver.TryResolve(reference, out value) || value is null || value is PdfNull) { return false; }
            if (copier._copied.ContainsKey(reference.ObjectNumber)) { value = null; return false; }
            copier.Store(reference.ObjectNumber, value);
            return true;
        }
    }

    internal int NextId => _nextId;

    // Register selected page identities before descending into any page graph.
    // Otherwise a link to a later selected page is prematurely pruned to null.
    internal void RegisterPageRoot(int? sourceNumber)
    {
        _governor.ChargeWork(1L);
        if (!sourceNumber.HasValue || _mapping.ContainsKey(sourceNumber.Value)) { return; }
        int id = _nextId;
        checked { _nextId += 1; }
        Map(sourceNumber.Value, id);
    }

    internal void AdvanceNextId(int nextId)
    {
        if (nextId <= 0) { throw new ArgumentOutOfRangeException(nameof(nextId)); }
        _nextId = Math.Max(_nextId, nextId);
    }

    internal void ReplaceCopiedObject(int id, QpdfObject value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        ValidateReplacement(id);
        _copied[id] = value;
    }

    internal void ValidateReplacement(int id)
    {
        if (!_copied.ContainsKey(id))
        {
            throw new QpdfException($"Destination object {id} does not exist.");
        }
    }

    /// <summary>
    /// Drops candidate pool objects that nothing in the pool references anymore.
    /// Content rewrites (coalescing, inline-image externalization) replace page
    /// entries with fresh objects; the replaced identities would otherwise ship
    /// as dead weight. Removal keeps any candidate that still has an inbound
    /// reference (shared streams and dictionaries stay), and it clears stale
    /// source mappings so a later copy re-materializes instead of dangling.
    /// </summary>
    internal void DropPoolObjectsIfUnreferenced(IReadOnlyList<int> candidates)
        => DropPoolObjectsIfUnreferenced(candidates, _governor.CancellationToken);

    internal void DropPoolObjectsIfUnreferenced(IReadOnlyList<int> candidates, CancellationToken cancellationToken)
    {
        using OrphanRemovalPlan plan = OrphanRemovalPlan.Create(candidates, _copied, _mapping, _governor, cancellationToken);
        plan.Apply();
    }

    internal IReadOnlyDictionary<int, int> SourceToDestination => _mapping;

    /// <summary>
    /// Links an already-resolved value into the destination graph as an
    /// indirect object, returning its reference (or null when a reference
    /// prunes to null under page policy). References remap through the shared
    /// memo table; direct values are deep-copied under a fresh number.
    /// </summary>
    internal PdfReference? AddIndirectObject(QpdfObject value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (value is PdfReference reference)
        {
            QpdfObject copied = CopyValue(reference, 0);
            return copied as PdfReference;
        }

        int id = _nextId;
        checked
        {
            _nextId += 1;
        }

        Reserve(64L);
        Store(id, CopyValue(value, 0));
        return new PdfReference(id, 0);
    }
    /// <summary>
    /// Inserts an already pool-local value under an explicit identity,
    /// advancing the allocator past it. Used when several pools share one
    /// central numbering budget.
    /// </summary>
    internal PdfReference AddIndirectObjectWithId(QpdfObject value, int id)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, null);
        }

        if (_copied.ContainsKey(id))
        {
            throw new InvalidOperationException("Destination identity is already allocated.");
        }

        Reserve(64L);
        Store(id, value);
        if (id >= _nextId)
        {
            checked
            {
                _nextId = id + 1;
            }
        }

        return new PdfReference(id, 0);
    }


    /// <summary>
    /// Copies every input object that page selection did not reach, in
    /// ascending original object-number order, returning the new destination
    /// numbers. Shares the memoized copier so references into the selected
    /// graph remap and omitted pages still prune to null. Rebuilt roots
    /// (the input catalog plus the selected page-tree chain, supplied as
    /// excluded identities) are skipped, and object/xref-stream containers
    /// are skipped while their members travel through decoded values, so
    /// detached pages, detached trees, orphan dictionaries and orphan
    /// streams all survive as loose objects. Objects that fail to load or
    /// copy are skipped under one aggregated warning. Call only when the
    /// validated plan requests preservation.
    /// </summary>
    internal HashSet<int> PreserveUnreferencedObjects(
        DocumentEngine engine,
        IReadOnlySet<int> excluded,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (excluded is null)
        {
            throw new ArgumentNullException(nameof(excluded));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        List<int> unmapped = new();
        foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            if (entry.Key <= 0 || _mapping.ContainsKey(entry.Key))
            {
                continue;
            }

            if (entry.Value.Kind == XrefEntryKind.Free)
            {
                continue;
            }

            unmapped.Add(entry.Key);
        }

        unmapped.Sort();
        HashSet<int> preserved = new();
        int skipped = 0;
        IIndirectResolver resolver = (IIndirectResolver)engine;
        foreach (int number in unmapped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            XrefEntry entry = engine.XRefEntries[number];
            int generation = entry.Kind == XrefEntryKind.Uncompressed ? entry.Second : 0;
            PdfReference reference = new(number, generation);
            if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null)
            {
                skipped += 1;
                continue;
            }

            if (IsSkippedStructure(resolved, number, excluded))
            {
                continue;
            }

            try
            {
                int id = _nextId;
                checked
                {
                    _nextId += 1;
                }

                _mapping[number] = id;
                QpdfObject copied = CopyValue(resolved, 0);
                _copied[id] = copied;
                preserved.Add(id);
            }
            catch (QpdfException)
            {
                _mapping.Remove(number);
                skipped += 1;
            }
        }

        if (skipped == 1)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped 1 unpreservable object.");
        }
        else if (skipped > 1)
        {
            diagnostics.AddParts(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, ["Skipped ", skipped, " unpreservable objects."]);
        }

        return preserved;
    }

    private static bool IsSkippedStructure(QpdfObject value, int number, IReadOnlySet<int> excluded)
    {
        if (excluded.Contains(number))
        {
            return true;
        }

        if (value is PdfStream stream)
        {
            if (stream.Dictionary.TryGet(TypeKey, out QpdfObject raw) && raw is PdfName name
                && (name.RawSpan.SequenceEqual("ObjStm"u8) || name.RawSpan.SequenceEqual("XRef"u8)))
            {
                return true;
            }
        }

        return false;
    }

    internal (int Id, PdfDictionary Page) CopyPageRoot(
        PdfDictionary page,
        int? sourceNumber,
        IReadOnlyDictionary<PdfName, QpdfObject> extra)
        => CopyPageRoot(page, sourceNumber, extra, false);

    // Retained inspection pages share their existing immutable values and keep
    // source identities. A null source number allocates a duplicate occurrence.
    internal (int Id, PdfDictionary Page) CopyPageRoot(
        PdfDictionary page, int? sourceNumber, IReadOnlyDictionary<PdfName, QpdfObject> extra,
        bool retainSourceObjects)
    {
        if (page is null)
        {
            throw new ArgumentNullException(nameof(page));
        }

        if (extra is null)
        {
            throw new ArgumentNullException(nameof(extra));
        }

        int pageId = retainSourceObjects && sourceNumber.HasValue ? sourceNumber.Value : _nextId;
        bool prepared = false;
        if (!retainSourceObjects && sourceNumber.HasValue
            && _mapping.TryGetValue(sourceNumber.Value, out int registered) && !_copied.ContainsKey(registered))
        {
            pageId = registered;
            prepared = true;
        }
        if (!(retainSourceObjects && sourceNumber.HasValue) && !prepared)
        {
            checked { _nextId += 1; }
        }

        if (sourceNumber.HasValue)
        {
            Map(sourceNumber.Value, pageId);
        }

        _rootPageNumber = sourceNumber ?? -2;
        IReadOnlyDictionary<PdfName, QpdfObject> entries = CreateEntries(checked(page.Count + extra.Count));
        using (PdfDictionary.EntryEnumerator source = page.GetEntryEnumerator())
        {
            while (source.MoveNext(_governor, _governor.CancellationToken))
            {
                PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
                if (value is null) { continue; }
                if (!retainSourceObjects && key.Equals(ParentKey)) { continue; }

                Reserve(32L);
                SetEntry(entries, key, retainSourceObjects ? value : CopyValue(value, 0));
            }
        }

        foreach (KeyValuePair<PdfName, QpdfObject> materialized in extra)
        {
            bool found = entries is PdfNameTable<QpdfObject> names
                ? names.TryGetValue(materialized.Key, _governor, _governor.CancellationToken, out QpdfObject? existing)
                : entries.TryGetValue(materialized.Key, out existing);
            if (!found || existing is PdfNull)
            {
                Reserve(32L);
                SetEntry(entries, materialized.Key, materialized.Value);
            }
        }

        PdfDictionary copied = CompleteDictionary(entries);
        Store(pageId, copied);
        return (pageId, copied);
    }

    internal QpdfObject CopyValue(QpdfObject value, int depth)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (depth > _limits.MaxNestingDepth)
        {
            throw new QpdfParseException($"Copy depth exceeds {_limits.MaxNestingDepth}.", null, null);
        }

        _governor.ChargeWork(1L);
        if (value is PdfReference reference)
        {
            return CopyReference(reference, depth);
        }

        if (value is PdfArray array)
        {
            _governor.ChargeWork(array.Count);
            _governor.CheckCancellation();
            QpdfObject[]? items = null;
            for (int i = 0; i < array.Count; i++)
            {
                _governor.CheckCancellation();
                QpdfObject copied = CopyValue(array[i], depth + 1);
                if (items is null && !ReferenceEquals(copied, array[i]))
                {
                    Reserve(checked(64L + (long)IntPtr.Size * array.Count));
                    items = new QpdfObject[array.Count];
                    _governor.ChargeWork(i);
                    for (int prefix = 0; prefix < i; prefix++)
                    {
                        _governor.CheckCancellation();
                        items[prefix] = array[prefix];
                    }
                }
                if (items is not null) { items[i] = copied; }
            }

            return items is null ? array : PdfArray.FromOwnedItems(items);
        }

        if (value is PdfDictionary dictionary)
        {
            return CopyDictionary(dictionary, depth);
        }

        if (value is PdfStream stream)
        {
            ReadOnlyMemory<byte> data = stream.RawMemory;
            PdfInteger? length = null;
            if (!StreamLengthMatches(stream.Dictionary, data.Length))
            {
                Reserve(128L);
                length = new PdfInteger(data.Length.ToString(CultureInfo.InvariantCulture), data.Length);
            }
            // Serialization uses the accepted payload length. An obsolete source
            // Length reference must not make a successfully repaired stream fail
            // while copying its otherwise meaningful dictionary references.
            PdfDictionary copiedDictionary = CopyDictionary(stream.Dictionary, depth + 1, length);
            // Payloads are immutable. Complete jobs retain the source admission
            // until publication (transferring exclusive values before closure);
            // public document payloads already have an independent owner.
            if (ReferenceEquals(copiedDictionary, stream.Dictionary)) { return stream; }
            Reserve(32L);
            _governor.CheckCancellation();
            return stream.WithDictionary(copiedDictionary);
        }

        return value;
    }

    private bool StreamLengthMatches(PdfDictionary dictionary, long length)
    {
        if (!dictionary.TryGet(LengthKey, out QpdfObject raw)) { return false; }
        if (raw is PdfInteger direct) { return direct.Value == length; }
        if (raw is not PdfReference reference) { return false; }
        try
        {
            return _resolver.TryResolve(reference, out QpdfObject? resolved)
                && resolved is PdfInteger indirect && indirect.Value == length;
        }
        catch (QpdfParseException) { return false; }
    }

    private QpdfObject CopyReference(PdfReference reference, int depth)
    {
        // Validate the source number/generation pair before using the
        // number-based memo. A stale generation must not alias a live copy.
        if (!_resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null)
        {
            throw new QpdfParseException("Unresolvable reference during page copy.", null, reference.ObjectNumber);
        }

        if (resolved is PdfNull)
        {
            return PdfNull.Instance;
        }

        if (_mapping.TryGetValue(reference.ObjectNumber, out int mapped))
        {
            _ownership?.Reserve(24L);
            return new PdfReference(mapped, 0);
        }

        if (resolved is PdfDictionary dictionary)
        {
            string type = ReadType(dictionary);
            if (type == "Pages" || type == "Catalog")
            {
                throw new QpdfParseException($"Page copy reached outside the selected page (/{type}).", null, reference.ObjectNumber);
            }

            if (type == "Page" && reference.ObjectNumber != _rootPageNumber)
            {
                return PdfNull.Instance;
            }
        }

        int id = _nextId;
        checked
        {
            _nextId += 1;
        }

        Map(reference.ObjectNumber, id);
        QpdfObject copied = CopyValue(resolved, depth + 1);
        Store(id, copied);
        _ownership?.Reserve(24L);
        return new PdfReference(id, 0);
    }

    private PdfDictionary CopyDictionary(PdfDictionary dictionary, int depth)
        => CopyDictionary(dictionary, depth, null);

    private PdfDictionary CopyDictionary(PdfDictionary dictionary, int depth, PdfInteger? streamLength)
    {
        bool addLength = streamLength is not null && !dictionary.TryGet(LengthKey, out _);
        IReadOnlyDictionary<PdfName, QpdfObject>? entries = null;
        int processed = 0;
        using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
        while (source.MoveNext(_governor, _governor.CancellationToken))
        {
            PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
            QpdfObject? copied;
            if (value is null) { copied = null; }
            else if (streamLength is not null && key.Equals(LengthKey))
            {
                copied = streamLength;
            }
            else if (key.Equals(ParentKey))
            {
                copied = TryCopyParent(value, depth);
            }
            else { copied = CopyValue(value, depth + 1); }

            if (entries is null && (value is null || !ReferenceEquals(copied, value)))
            { entries = CopyDictionaryPrefix(dictionary, processed, addLength); }
            if (entries is not null && copied is not null)
            {
                Reserve(32L);
                SetEntry(entries, key, copied);
            }
            processed++;
        }

        if (addLength && streamLength is not null)
        {
            entries ??= CopyDictionaryPrefix(dictionary, processed, true);
            Reserve(32L);
            SetEntry(entries, LengthKey, streamLength);
        }

        return entries is null ? dictionary : CompleteDictionary(entries);
    }

    private IReadOnlyDictionary<PdfName, QpdfObject> CopyDictionaryPrefix(PdfDictionary dictionary, int count, bool addLength)
    {
        // Earlier children were already visited and stayed identical. Copy only
        // their entries; immutable source storage remains owned through publication.
        Reserve(64L);
        IReadOnlyDictionary<PdfName, QpdfObject> entries = CreateEntries(checked(dictionary.Count + (addLength ? 1 : 0)));
        using PdfDictionary.EntryEnumerator prefix = dictionary.GetEntryEnumerator();
        for (int i = 0; i < count && prefix.MoveNext(_governor, _governor.CancellationToken); i++)
        {
            Reserve(32L);
            SetEntry(entries, prefix.Current.Key, prefix.Current.Value);
        }
        return entries;
    }

    /// <summary>
    /// Copies a `/Parent` link, preserving field and annotation hierarchies through
    /// the shared memo table. Page-tree ancestry (`/Page`, `/Pages`, `/Catalog`
    /// targets), unresolvable links and references pruned to null stay dropped so
    /// omitted pages can never resurrect through auxiliary links.
    /// </summary>
    private QpdfObject? TryCopyParent(QpdfObject value, int depth)
    {
        if (value is null || value is PdfNull)
        {
            return null;
        }

        if (value is PdfReference reference)
        {
            if (!_resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
            {
                return null;
            }

            if (resolved is PdfDictionary parent && IsPageTreeNode(parent))
            {
                return null;
            }

            QpdfObject copied = CopyValue(value, depth + 1);
            return copied is PdfNull ? null : copied;
        }

        if (value is PdfDictionary direct && IsPageTreeNode(direct))
        {
            return null;
        }

        return CopyValue(value, depth + 1);
    }

    private static bool IsPageTreeNode(PdfDictionary dictionary)
    {
        string type = ReadType(dictionary);
        return type == "Page" || type == "Pages" || type == "Catalog";
    }

    private static string ReadType(PdfDictionary dictionary)
    {
        if (!dictionary.TryGet(TypeKey, out QpdfObject raw) || raw is PdfNull)
        {
            return string.Empty;
        }

        if (raw is not PdfName name) { return string.Empty; }
        return name.Equals(PageType) ? "Page" : name.Equals(PagesType) ? "Pages"
            : name.Equals(CatalogType) ? "Catalog" : string.Empty;
    }
}
