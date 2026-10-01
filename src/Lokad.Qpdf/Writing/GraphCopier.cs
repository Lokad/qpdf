using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// The single memoized graph copier: destination identities are registered
/// before descending, so cyclic and shared subgraphs are copied once. Page
/// ancestry (`/Parent`) is never followed here; the selector materializes
/// inherited attributes explicitly. References to already-copied objects remap to
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

    internal IReadOnlyDictionary<int, int> SourceToDestination => _mapping;

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
            if (key.DisplayText == "Parent")
            {
                continue;
            }

            if (!dictionary.TryGet(key, out QpdfObject value))
            {
                continue;
            }

            _governor.ReserveMemory(32L);
            entries[key] = CopyValue(value, depth + 1);
        }

        return new PdfDictionary(entries);
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
