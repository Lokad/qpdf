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
    private static readonly PdfName FieldTypeKey = PdfName.FromAscii("FT");
    private static readonly PdfName ValueKey = PdfName.FromAscii("V");
    private static readonly PdfName SignatureType = PdfName.FromAscii("Sig");
    private readonly IIndirectResolver _resolver;
    private readonly ResourceGovernor _governor;
    private readonly QpdfLimits _limits;
    private readonly Dictionary<int, int> _mapping;
    private readonly int _sourceObjectCount;
    private readonly Dictionary<int, QpdfObject> _copied;
    private readonly ParserMemoryScope _ownership;
    private int _nextId;
    private int _rootPageNumber = -1;
    private List<int>? _attemptSources;
    private int _copyAttempts;

    internal static GraphCopier CreateOwned(DocumentEngine engine, ResourceGovernor governor,
        QpdfLimits limits, int firstId, ParserMemoryScope ownership)
    {
        ownership.CheckOwner(governor);
        ownership.Reserve(96L);
        return new GraphCopier(engine, governor, limits, firstId, ownership);
    }

    private GraphCopier(DocumentEngine engine, ResourceGovernor governor, QpdfLimits limits, int firstId,
        ParserMemoryScope ownership)
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
        _sourceObjectCount = engine.XRefEntries.Count;
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _limits = limits;
        if (firstId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstId), "Destination numbering must start positive.");
        }

        _nextId = firstId;
        _ownership = ownership;
        _mapping = ParserContainers.CreateDictionary<int, int>(ownership);
        _copied = ParserContainers.CreateDictionary<int, QpdfObject>(ownership);
    }

    internal void HintPageCapacity(int count)
    {
        // Each requested occurrence produces a destination page, including
        // duplicates. Source identities need not be distinct, so only hint
        // the destination map. Tight jobs retain ordinary bounded growth.
        ParserContainers.HintEntryCapacity(_ownership, _copied, count, IntPtr.Size, _governor);
    }

    private void Store(int id, QpdfObject value)
    {
        if (!_copied.ContainsKey(id))
        {
            ParserContainers.EnsureEntryCapacity(_ownership, _copied, IntPtr.Size);
        }
        _copied[id] = value;
    }

    private void Map(int source, int destination)
    {
        if (!_mapping.ContainsKey(source))
        {
            // Keys identify source objects. Duplicate destination pages do not
            // increase this cardinality; their separate map keeps normal growth.
            ParserContainers.EnsureEntryCapacity(_ownership, _mapping, sizeof(int), _sourceObjectCount);
            if (_copyAttempts > 0)
            {
                _attemptSources ??= ParserContainers.CreateList<int>(_ownership);
                ParserContainers.AddItem(_ownership, _attemptSources, source, sizeof(int));
            }
        }
        _mapping[source] = destination;
    }

    // Only recovery boundaries track new source mappings. Nested name-category
    // attempts share this admitted scratch; successful ordinary copies do not.
    internal int BeginCopyAttempt()
    {
        _copyAttempts += 1;
        return _nextId;
    }

    internal void RollbackCopyAttempt(int firstId, CancellationToken token)
    {
        if (_attemptSources is not null)
        {
            for (int index = _attemptSources.Count - 1; index >= 0; index--)
            {
                token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
                int source = _attemptSources[index];
                if (_mapping[source] < firstId) { break; }
                _mapping.Remove(source);
                _attemptSources.RemoveAt(index);
            }
        }
        // Include direct roots, which consume IDs without a source mapping.
        for (int id = firstId; id < _nextId; id++)
        {
            token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            _copied.Remove(id);
        }
        _nextId = firstId;
    }

    internal void EndCopyAttempt()
    {
        _copyAttempts -= 1;
        if (_copyAttempts == 0) { _attemptSources?.Clear(); }
    }

    private PdfNameTable<QpdfObject> CreateEntries(int count)
    {
        PdfNameTable<QpdfObject> entries = PdfNameTable<QpdfObject>.Create(_ownership, _governor, IntPtr.Size, _governor.CancellationToken);
        entries.EnsureCapacity(_ownership, _governor, count, _governor.CancellationToken);
        return entries;
    }

    private PdfDictionary CompleteDictionary(PdfNameTable<QpdfObject> entries)
    {
        entries.Freeze(_ownership, _governor, _governor.CancellationToken);
        _ownership.Reserve(32L);
        return PdfDictionary.FromOwnedEntries(entries);
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
        _governor.NoteOperation();
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

        _ownership.Reserve(64L);
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

        _ownership.Reserve(64L);
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
    /// ascending original object-number order. Optionally collects new roots
    /// for split output in a set funded by the copier's owner. Shares the
    /// memoized copier so references into the selected
    /// graph remap and omitted pages still prune to null. Rebuilt roots
    /// (the input catalog plus the selected page-tree chain, supplied as
    /// excluded identities) are skipped, and object/xref-stream containers
    /// are skipped while their members travel through decoded values, so
    /// detached pages, detached trees, orphan dictionaries and orphan
    /// streams all survive as loose objects. Unresolvable roots and objects
    /// rejected during contained copying are skipped under one aggregated
    /// warning; strict load failures remain errors. Call only when the
    /// validated plan requests preservation.
    /// </summary>
    internal void PreserveUnreferencedObjects(
        DocumentEngine engine,
        IReadOnlySet<int> excluded,
        DiagnosticBag diagnostics,
        HashSet<int>? preserved,
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

        using ParserMemoryScope scratch = ParserMemoryScope.Create(_governor);
        scratch.Reserve(32L); // One temporary source reference at a time.
        List<int> unmapped = ParserContainers.CreateList<int>(scratch);
        foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.NoteOperation();
            if (entry.Key <= 0 || _mapping.ContainsKey(entry.Key))
            {
                continue;
            }

            if (entry.Value.Kind == XrefEntryKind.Free)
            {
                continue;
            }

            ParserContainers.AddItem(scratch, unmapped, entry.Key, sizeof(int));
        }

        unmapped.Sort();
        int skipped = 0;
        IIndirectResolver resolver = (IIndirectResolver)engine;
        foreach (int number in unmapped)
        {
            cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            if (_mapping.TryGetValue(number, out int existing))
            {
                if (preserved is not null) { ParserContainers.AddSetItem(_ownership, preserved, existing, sizeof(int)); }
                continue;
            }
            XrefEntry entry = engine.XRefEntries[number];
            int generation = entry.Kind == XrefEntryKind.Uncompressed ? entry.Second : 0;
            PdfReference reference = new(number, generation);
            if (!PdfObjectLookup.TryResolve(reference, resolver, _governor, cancellationToken, out QpdfObject? resolved) || resolved is null)
            { skipped += 1; continue; }
            if (IsSkippedStructure(resolved, number, excluded)) { continue; }
            int firstId = BeginCopyAttempt();
            try
            {
                int id = _nextId;
                checked { _nextId += 1; }
                Map(number, id);
                QpdfObject copied = CopyValue(resolved, 0);
                cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
                Store(id, copied);
                if (preserved is not null) { ParserContainers.AddSetItem(_ownership, preserved, id, sizeof(int)); }
            }
            catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException))
            {
                // Complete descendants can refer back to this failed root.
                // Retire this whole attempt, keeping earlier shared copies.
                RollbackCopyAttempt(firstId, cancellationToken);
                skipped += 1;
            }
            finally { EndCopyAttempt(); }
        }

        if (skipped == 1)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped 1 unpreservable object.");
        }
        else if (skipped > 1)
        {
            diagnostics.AddParts(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, ["Skipped ", skipped, " unpreservable objects."]);
        }

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
        PdfNameTable<QpdfObject> entries = CreateEntries(checked(page.Count + extra.Count));
        using (PdfDictionary.EntryEnumerator source = page.GetEntryEnumerator())
        {
            while (source.MoveNext(_governor, _governor.CancellationToken))
            {
                (PdfName key, QpdfObject value) = source.Current;
                if (value is null) { continue; }
                if (!retainSourceObjects && key.Equals(ParentKey)) { continue; }

                _ownership.Reserve(32L);
                entries.Assign(_ownership, _governor, key, retainSourceObjects ? value : CopyValue(value, 0), _governor.CancellationToken);
            }
        }

        foreach (KeyValuePair<PdfName, QpdfObject> materialized in extra)
        {
            bool found = entries is PdfNameTable<QpdfObject> names
                ? names.TryGetValue(materialized.Key, _governor, _governor.CancellationToken, out QpdfObject? existing)
                : entries.TryGetValue(materialized.Key, out existing);
            if (!found || existing is PdfNull)
            {
                _ownership.Reserve(32L);
                entries.Assign(_ownership, _governor, materialized.Key, materialized.Value, _governor.CancellationToken);
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

        _governor.NoteOperation();
        if (value is PdfReference reference)
        {
            return CopyReference(reference, depth);
        }

        if (value is PdfArray array)
        {
            _governor.CheckCancellation();
            QpdfObject[]? items = null;
            for (int i = 0; i < array.Count; i++)
            {
                _governor.CheckCancellation();
                QpdfObject copied = CopyValue(array[i], depth + 1);
                if (items is null && !ReferenceEquals(copied, array[i]))
                {
                    _ownership.Reserve(checked(64L + (long)IntPtr.Size * array.Count));
                    items = new QpdfObject[array.Count];
                    _governor.NoteOperation();
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
                _ownership.Reserve(128L);
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
            _ownership.Reserve(32L);
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
            if (mapped == reference.ObjectNumber && reference.Generation == 0) { return reference; }
            _ownership.Reserve(24L);
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
        if (id == reference.ObjectNumber && reference.Generation == 0) { return reference; }
        _ownership.Reserve(24L);
        return new PdfReference(id, 0);
    }

    private PdfDictionary CopyDictionary(PdfDictionary dictionary, int depth)
        => CopyDictionary(dictionary, depth, null);

    private PdfDictionary CopyDictionary(PdfDictionary dictionary, int depth, PdfInteger? streamLength)
    {
        RejectSignatureCopy(dictionary);
        bool addLength = streamLength is not null && !dictionary.TryGet(LengthKey, out _);
        PdfNameTable<QpdfObject>? entries = null;
        int processed = 0;
        using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
        while (source.MoveNext(_governor, _governor.CancellationToken))
        {
            (PdfName key, QpdfObject value) = source.Current;
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
                _ownership.Reserve(32L);
                entries.Assign(_ownership, _governor, key, copied, _governor.CancellationToken);
            }
            processed++;
        }

        if (addLength && streamLength is not null)
        {
            entries ??= CopyDictionaryPrefix(dictionary, processed, true);
            _ownership.Reserve(32L);
            entries.Assign(_ownership, _governor, LengthKey, streamLength, _governor.CancellationToken);
        }

        return entries is null ? dictionary : CompleteDictionary(entries);
    }

    // Rewriting bytes cannot preserve a source signature. Reject its value before
    // following auxiliary references into the source catalog. Empty signature
    // fields remain ordinary form structure; read-only retention never calls here.
    private void RejectSignatureCopy(PdfDictionary dictionary)
    {
        _governor.CheckCancellation();
        if (dictionary.TryGet(TypeKey, out QpdfObject type)
            && ResolveFieldAttribute(type) is PdfName name && name.Equals(SignatureType))
        { throw new QpdfCryptoException("/Sig"); }
        if (!dictionary.TryGet(ValueKey, out QpdfObject value) || value is PdfNull) { return; }
        if (IsSignatureField(dictionary) && ResolveFieldAttribute(value) is not PdfNull)
        { throw new QpdfCryptoException("/Sig"); }
    }

    private bool IsSignatureField(PdfDictionary dictionary)
    {
        if (dictionary.TryGet(FieldTypeKey, out QpdfObject fieldType))
        {
            QpdfObject resolvedType = ResolveFieldAttribute(fieldType);
            if (resolvedType is not PdfNull)
            { return resolvedType is PdfName name && name.Equals(SignatureType); }
        }
        if (!dictionary.TryGet(ParentKey, out _)) { return false; }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(_governor);
        scratch.Reserve(96L);
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        PdfDictionary? current = dictionary;
        for (int depth = 0; current is not null; depth++)
        {
            _governor.CheckCancellation(); _governor.NoteOperation();
            if (!ParserContainers.AddSetItem(scratch, seen, current, IntPtr.Size))
            { throw new QpdfParseException("Field parent cycle during page copy.", null, null); }
            if (current.TryGet(FieldTypeKey, out fieldType))
            {
                QpdfObject inherited = ResolveFieldAttribute(fieldType);
                if (inherited is not PdfNull)
                { return inherited is PdfName name && name.Equals(SignatureType); }
            }
            if (!current.TryGet(ParentKey, out QpdfObject parent)) { return false; }
            if (depth >= _limits.MaxNestingDepth)
            { throw new QpdfParseException("Field parent chain exceeds the copy nesting limit.", null, null); }
            current = ResolveFieldAttribute(parent) as PdfDictionary;
            if (current is not null && IsPageTreeNode(current)) { return false; }
        }
        return false;
    }

    private QpdfObject ResolveFieldAttribute(QpdfObject value)
    {
        for (int depth = 0; value is PdfReference reference; depth++)
        {
            _governor.CheckCancellation(); _governor.NoteOperation();
            if (depth >= _limits.MaxNestingDepth)
            { throw new QpdfParseException("Field attribute reference chain exceeds the copy nesting limit.", null, reference.ObjectNumber); }
            if (!_resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null) { return PdfNull.Instance; }
            value = resolved;
        }
        return value;
    }

    private PdfNameTable<QpdfObject> CopyDictionaryPrefix(PdfDictionary dictionary, int count, bool addLength)
    {
        // Earlier children were already visited and stayed identical. Copy only
        // their entries; immutable source storage remains owned through publication.
        _ownership.Reserve(64L);
        PdfNameTable<QpdfObject> entries = CreateEntries(checked(dictionary.Count + (addLength ? 1 : 0)));
        using PdfDictionary.EntryEnumerator prefix = dictionary.GetEntryEnumerator();
        for (int i = 0; i < count && prefix.MoveNext(_governor, _governor.CancellationToken); i++)
        {
            _ownership.Reserve(32L);
            (PdfName key, QpdfObject value) = prefix.Current;
            entries.Assign(_ownership, _governor, key, value, _governor.CancellationToken);
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
