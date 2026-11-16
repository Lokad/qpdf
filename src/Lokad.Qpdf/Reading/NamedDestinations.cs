using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>Operation-local lookup of legacy and string-named destinations.</summary>
internal sealed class NamedDestinations
{
    private const int CacheCapacity = 32;
    private readonly record struct Entry(bool IsName, ReadOnlyMemory<byte> Key, QpdfObject? Value);
    private readonly IIndirectResolver _resolver;
    private readonly PdfDictionary _catalog;
    private readonly ParserMemoryScope _memory;
    private readonly ResourceGovernor _governor;
    private readonly CancellationToken _token;
    private List<Entry>? _cache;
    private int _next;

    private NamedDestinations(IIndirectResolver resolver, PdfDictionary catalog, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    { _resolver = resolver; _catalog = catalog; _memory = memory; _governor = governor; _token = token; }

    internal static NamedDestinations Create(IIndirectResolver resolver, PdfDictionary catalog, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); memory.CheckOwner(governor); memory.Reserve(80L);
        return new(resolver, catalog, memory, governor, token);
    }

    internal QpdfObject? Find(QpdfObject name)
    {
        Check();
        bool legacy = name is PdfName;
        ReadOnlyMemory<byte> key = name switch
        {
            PdfName value => value.RawMemory,
            PdfByteString value => value.ValueMemory,
            _ => throw new ArgumentException("A named destination must be a name or string.", nameof(name))
        };
        if (_cache is not null)
        {
            foreach (Entry entry in _cache)
            {
                Check();
                if (entry.IsName == legacy && Compare(entry.Key.Span, key.Span) == 0) { return entry.Value; }
            }
        }
        QpdfObject? found = null;
        if (legacy)
        {
            if (Resolve(Read(_catalog, "Dests"u8)) is PdfDictionary destinations) { found = Read(destinations, key.Span); }
        }
        else if (Resolve(Read(_catalog, "Names"u8)) is PdfDictionary names
            && Resolve(Read(names, "Dests"u8)) is PdfDictionary destinations)
        {
            using ParserMemoryScope walk = ParserMemoryScope.Create(_governor);
            walk.Reserve(96L); HashSet<PdfDictionary> path = new(ReferenceEqualityComparer.Instance);
            Dictionary<PdfDictionary, int> misses = ParserContainers.CreateDictionary<PdfDictionary, int>(walk, ReferenceEqualityComparer.Instance);
            found = FindName(destinations, key, path, misses, walk, 0).Value;
        }
        found = Resolve(found);
        if (found is PdfDictionary wrapper) { found = Resolve(Read(wrapper, "D"u8)); }
        // Keys and values borrow the source graph, whose owner spans this lookup.
        // The bounded index has its own admitted storage under the operation owner.
        _cache ??= ParserContainers.CreateList<Entry>(_memory);
        Entry kept = new(legacy, key, found);
        if (_cache.Count < CacheCapacity) { ParserContainers.AddItem(_memory, _cache, kept, 40L); }
        else { _cache[_next] = kept; _next = (_next + 1) % CacheCapacity; }
        return found;
    }

    private (QpdfObject? Value, int Height) FindName(PdfDictionary node, ReadOnlyMemory<byte> key,
        HashSet<PdfDictionary> path, Dictionary<PdfDictionary, int> misses, ParserMemoryScope walk, int depth)
    {
        Check(); CheckDepth(depth);
        if (misses.TryGetValue(node, out int remaining)) { CheckDepth((long)depth + remaining); return (null, remaining); }
        if (!ParserContainers.AddSetItem(walk, path, node, IntPtr.Size))
        { throw new QpdfParseException("Source destination name tree contains a cycle.", null, null); }
        int height = 0;
        try
        {
            if (Resolve(Read(node, "Names"u8)) is PdfArray names)
            {
                if (names.Count % 2 != 0) { throw new QpdfParseException("Source destination names must contain pairs.", null, null); }
                int low = 0, high = names.Count / 2;
                while (low < high)
                {
                    Check(); int middle = low + (high - low) / 2;
                    if (Resolve(names[middle * 2]) is not PdfByteString candidate)
                    { throw new QpdfParseException("Source destination keys must be strings.", null, null); }
                    int comparison = Compare(candidate.ValueSpan, key.Span);
                    if (comparison == 0) { return (names[middle * 2 + 1], 0); }
                    if (comparison < 0) { low = middle + 1; } else { high = middle; }
                }
            }
            if (Resolve(Read(node, "Kids"u8)) is PdfArray kids)
            {
                for (int index = 0; index < kids.Count; index++)
                {
                    Check();
                    if (Resolve(kids[index]) is not PdfDictionary child)
                    { throw new QpdfParseException("Source destination child must be a dictionary.", null, null); }
                    if (Resolve(Read(child, "Limits"u8)) is PdfArray { Count: 2 } limits
                        && Resolve(limits[0]) is PdfByteString first && Resolve(limits[1]) is PdfByteString last
                        && (Compare(key.Span, first.ValueSpan) < 0 || Compare(key.Span, last.ValueSpan) > 0)) { continue; }
                    (QpdfObject? found, int childHeight) = FindName(child, key, path, misses, walk, depth + 1);
                    if (found is not null) { return (found, 0); }
                    height = Math.Max(height, childHeight + 1);
                }
            }
        }
        finally { path.Remove(node); }
        ParserContainers.EnsureEntryCapacity(walk, misses, sizeof(int)); misses.Add(node, height);
        return (null, height);
    }

    private QpdfObject? Read(PdfDictionary dictionary, ReadOnlySpan<byte> key)
        => PdfDictionaryLookup.TryGet(dictionary, key, _governor, _token, out QpdfObject value) ? value : null;

    private QpdfObject? Resolve(QpdfObject? value)
    {
        Check();
        if (value is PdfReference reference) { PdfObjectLookup.TryResolve(reference, _resolver, _governor, _token, out value); }
        return value;
    }

    private int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int length = Math.Min(left.Length, right.Length);
        for (int offset = 0; offset < length; offset += Math.Min(8192, length - offset))
        {
            Check(); int count = Math.Min(8192, length - offset);
            int order = left.Slice(offset, count).SequenceCompareTo(right.Slice(offset, count));
            if (order != 0) { return order; }
        }
        return left.Length.CompareTo(right.Length);
    }

    private void CheckDepth(long depth)
    {
        if (depth > _governor.MaxNestingDepth)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, _governor.MaxNestingDepth); }
    }

    private void Check() { _token.ThrowIfCancellationRequested(); _governor.CheckCancellation(); }
}
