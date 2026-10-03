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
/// their copies; references to other pages prune to null under the contained-preview
/// policy so omitted pages can never resurrect. Page-tree (`/Pages`) and catalog
/// references stay structural errors and fail loudly.
/// </summary>
internal sealed class GraphCopier
{
    private readonly IIndirectResolver _resolver;
    private readonly ResourceGovernor _governor;
    private readonly QpdfLimits _limits;
    private readonly Dictionary<int, int> _mapping = new();
    private readonly Dictionary<int, QpdfObject> _copied = new();
    private int _nextId;
    private int _rootPageNumber = -1;

    internal GraphCopier(DocumentEngine engine, ResourceGovernor governor, QpdfLimits limits, int firstId)
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
    }

    internal IReadOnlyDictionary<int, QpdfObject> CopiedObjects => _copied;

    internal int NextId => _nextId;

    internal void ReplaceCopiedObject(int id, QpdfObject value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (!_copied.ContainsKey(id))
        {
            throw new QpdfException($"Destination object {id} does not exist.");
        }

        _copied[id] = value;
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
    {
        if (candidates is null)
        {
            throw new ArgumentNullException(nameof(candidates));
        }

        HashSet<int> wanted = new();
        foreach (int candidate in candidates)
        {
            _governor.ChargeWork(1L);
            if (candidate > 0 && _copied.ContainsKey(candidate))
            {
                wanted.Add(candidate);
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        HashSet<int> referenced = new();
        Stack<QpdfObject> stack = new();
        foreach (QpdfObject value in _copied.Values)
        {
            _governor.ChargeWork(1L);
            stack.Push(value);
            while (stack.TryPop(out QpdfObject? current))
            {
                _governor.ChargeWork(1L);
                if (current is PdfReference reference)
                {
                    if (wanted.Contains(reference.ObjectNumber))
                    {
                        referenced.Add(reference.ObjectNumber);
                    }
                }
                else if (current is PdfArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        stack.Push(array[i]);
                    }
                }
                else if (current is PdfDictionary dictionary)
                {
                    foreach (PdfName key in dictionary.Keys)
                    {
                        if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null)
                        {
                            stack.Push(entry);
                        }
                    }
                }
                else if (current is PdfStream stream)
                {
                    stack.Push(stream.Dictionary);
                }
            }
        }

        foreach (int id in wanted)
        {
            _governor.ChargeWork(1L);
            if (referenced.Contains(id))
            {
                continue;
            }

            if (!_copied.Remove(id))
            {
                continue;
            }

            List<int> stale = new();
            foreach (KeyValuePair<int, int> mapped in _mapping)
            {
                if (mapped.Value == id)
                {
                    stale.Add(mapped.Key);
                }
            }

            foreach (int source in stale)
            {
                _mapping.Remove(source);
            }
        }
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

        _governor.ReserveMemory(64L);
        _copied[id] = CopyValue(value, 0);
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

        _governor.ReserveMemory(64L);
        _copied[id] = value;
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
            if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
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
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, $"Skipped {skipped} unpreservable objects.");
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
            PdfName typeKey = PdfName.FromAscii("Type");
            if (stream.Dictionary.TryGet(typeKey, out QpdfObject raw) && raw is PdfName name
                && (name.DisplayText == "ObjStm" || name.DisplayText == "XRef"))
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
    {
        if (page is null)
        {
            throw new ArgumentNullException(nameof(page));
        }

        if (extra is null)
        {
            throw new ArgumentNullException(nameof(extra));
        }

        int pageId = _nextId;
        checked
        {
            _nextId += 1;
        }

        if (sourceNumber.HasValue)
        {
            _mapping[sourceNumber.Value] = pageId;
        }

        _rootPageNumber = sourceNumber ?? -2;
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in page.Keys)
        {
            if (key.DisplayText == "Parent")
            {
                continue;
            }

            if (!page.TryGet(key, out QpdfObject value))
            {
                continue;
            }

            _governor.ReserveMemory(32L);
            entries[key] = CopyValue(value, 0);
        }

        foreach (KeyValuePair<PdfName, QpdfObject> materialized in extra)
        {
            if (!entries.TryGetValue(materialized.Key, out QpdfObject? existing) || existing is PdfNull)
            {
                _governor.ReserveMemory(32L);
                entries[materialized.Key] = materialized.Value;
            }
        }

        PdfDictionary copied = new(entries);
        _copied[pageId] = copied;
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
            _governor.ReserveMemory(32L);
            List<QpdfObject> items = new(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                _governor.ReserveMemory(16L);
                items.Add(CopyValue(array[i], depth + 1));
            }

            return new PdfArray(items);
        }

        if (value is PdfDictionary dictionary)
        {
            return CopyDictionary(dictionary, depth);
        }

        if (value is PdfStream stream)
        {
            PdfDictionary copiedDictionary = CopyDictionary(stream.Dictionary, depth + 1);
            byte[] data = stream.Data;
            _governor.ReserveMemory(data.Length);
            byte[] preserved = new byte[data.Length];
            Buffer.BlockCopy(data, 0, preserved, 0, data.Length);
            return new PdfStream(copiedDictionary, preserved);
        }

        return value;
    }

    private QpdfObject CopyReference(PdfReference reference, int depth)
    {
        if (_mapping.TryGetValue(reference.ObjectNumber, out int mapped))
        {
            return new PdfReference(mapped, 0);
        }

        if (!_resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null)
        {
            throw new QpdfParseException("Unresolvable reference during page copy.", null, reference.ObjectNumber);
        }

        if (resolved is PdfNull)
        {
            return PdfNull.Instance;
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

        _mapping[reference.ObjectNumber] = id;
        QpdfObject copied = CopyValue(resolved, depth + 1);
        _copied[id] = copied;
        return new PdfReference(id, 0);
    }

    private PdfDictionary CopyDictionary(PdfDictionary dictionary, int depth)
    {
        _governor.ReserveMemory(64L);
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in dictionary.Keys)
        {
            if (!dictionary.TryGet(key, out QpdfObject value))
            {
                continue;
            }

            _governor.ReserveMemory(32L);
            if (key.DisplayText == "Parent")
            {
                QpdfObject? preserved = TryCopyParent(value, depth);
                if (preserved is null)
                {
                    continue;
                }

                entries[key] = preserved;
                continue;
            }

            entries[key] = CopyValue(value, depth + 1);
        }

        return new PdfDictionary(entries);
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
        PdfName typeKey = PdfName.FromAscii("Type");
        if (!dictionary.TryGet(typeKey, out QpdfObject raw) || raw is PdfNull)
        {
            return string.Empty;
        }

        return raw is PdfName name ? name.DisplayText : string.Empty;
    }
}
