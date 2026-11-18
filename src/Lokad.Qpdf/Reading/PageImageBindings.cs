using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>Sorted image bindings borrowed from one page's effective resources.</summary>
internal struct PageImageBindings
{
    internal readonly record struct Binding(PdfName Name, PdfReference Reference, PdfStream Stream);

    private readonly DocumentEngine _engine;
    private readonly PdfDictionary? _xobjects;
    private readonly List<PdfName>? _names;
    private readonly ResourceGovernor _governor;
    private readonly CancellationToken _token;
    private readonly Cache? _cache;
    private readonly Binding[]? _cached;
    private readonly ParserMemoryScope? _memory;
    private List<Binding>? _found;
    private bool _canRemember;
    private int _next;

    private PageImageBindings(DocumentEngine engine, PdfDictionary? xobjects, List<PdfName>? names,
        ResourceGovernor governor, CancellationToken token, Cache? cache, Binding[]? cached, ParserMemoryScope? memory)
    {
        _engine = engine; _xobjects = xobjects; _names = names; _governor = governor; _token = token;
        _cache = cache; _cached = cached; _memory = memory; _canRemember = cache is not null && names is not null;
    }

    internal static PageImageBindings Create(DocumentEngine engine, PageLocation location, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token, Cache? cache)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        QpdfObject? raw = PageInheritance.Find(engine, location, PdfName.FromAscii("Resources"), limits, governor, token);
        PdfDictionary? resources = Content.CatalogCopy.ResolveDictionary(raw, engine, governor, token);
        if (resources is null || !resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjectsRaw) || xobjectsRaw is PdfNull)
        { return new(engine, null, null, governor, token, null, null, null); }
        PdfDictionary? xobjects = xobjectsRaw as PdfDictionary;
        if (xobjects is null && xobjectsRaw is PdfReference reference
            && ((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved))
        { xobjects = resolved as PdfDictionary; }
        if (xobjects is null) { return new(engine, null, null, governor, token, null, null, null); }
        if (cache?.Find(xobjects) is Binding[] cached)
        { return new(engine, null, null, governor, token, null, cached, null); }

        List<PdfName> names = ParserContainers.CreateList<PdfName>(memory, xobjects.Count, IntPtr.Size);
        using (PdfDictionary.EntryEnumerator entries = xobjects.GetEntryEnumerator())
        {
            while (entries.MoveNext())
            { token.ThrowIfCancellationRequested(); governor.NoteOperation(); names.Add(entries.Current.Key); }
        }
        GovernedCollections.SortNames(memory, governor, names);
        return new(engine, xobjects, names, governor, token, cache, null, memory);
    }

    internal bool TryReadNext(out Binding binding)
    {
        _token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
        binding = default;
        if (_cached is not null)
        {
            if (_next >= _cached.Length) { return false; }
            binding = _cached[_next++];
            return true;
        }
        if (_names is null || _xobjects is null) { return false; }
        long version = _cache?.DiagnosticVersion ?? 0L;
        while (_next < _names.Count)
        {
            _token.ThrowIfCancellationRequested(); _governor.NoteOperation();
            PdfName name = _names[_next++];
            if (!_xobjects.TryGet(name, out QpdfObject entry) || entry is not PdfReference reference
                || !((IIndirectResolver)_engine).TryResolve(reference, out QpdfObject? target) || target is not PdfStream stream
                || !PdfDictionaryLookup.HasName(stream.Dictionary, "Subtype"u8, "Image"u8, _engine, _governor, _token))
            { continue; }
            binding = new(name, reference, stream);
            if (_canRemember)
            {
                _canRemember = _cache?.DiagnosticVersion == version && (_found?.Count ?? 0) < Cache.MaximumBindings;
                if (_canRemember && _memory is ParserMemoryScope memory)
                {
                    _found ??= ParserContainers.CreateList<Binding>(memory);
                    ParserContainers.AddItem(memory, _found, binding, 24L);
                }
            }
            return true;
        }
        if (_canRemember && _cache is Cache cache && cache.DiagnosticVersion == version)
        { cache.Remember(_xobjects, _found); }
        _canRemember = false;
        return false;
    }

    // One immutable graph per inspection. Cache admission is separate from
    // page scratch and retains no copied stream payloads.
    internal sealed class Cache : IDisposable
    {
        internal const int MaximumBindings = 64;
        private const int Capacity = 8;
        private readonly ParserMemoryScope _memory;
        private readonly DiagnosticBag _diagnostics;
        private Entry[]? _entries;
        private int _nextSlot;

        private readonly record struct Entry(PdfDictionary? Source, Binding[]? Bindings, long Bytes);

        private Cache(ParserMemoryScope memory, DiagnosticBag diagnostics)
        { _memory = memory; _diagnostics = diagnostics; }

        internal static Cache Create(ResourceGovernor governor, DiagnosticBag diagnostics)
        {
            ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            try { memory.Reserve(48L); return new Cache(memory, diagnostics); }
            catch { memory.Dispose(); throw; }
        }

        internal long DiagnosticVersion => _diagnostics.EmissionVersion;

        internal Binding[]? Find(PdfDictionary source)
        {
            if (_entries is null) { return null; }
            foreach (Entry entry in _entries)
            {
                if (ReferenceEquals(entry.Source, source)) { return entry.Bindings; }
            }
            return null;
        }

        internal void Remember(PdfDictionary source, List<Binding>? found)
        {
            if (_entries is null)
            {
                _memory.Reserve(32L + Capacity * 24L);
                _entries = new Entry[Capacity];
            }
            int count = found?.Count ?? 0;
            long bytes = count == 0 ? 0L : 32L + count * 24L;
            _memory.Reserve(bytes);
            Binding[] items = found?.ToArray() ?? Array.Empty<Binding>();
            long retired = _entries[_nextSlot].Bytes;
            _entries[_nextSlot] = new Entry(source, items, bytes);
            _nextSlot = (_nextSlot + 1) % Capacity;
            _memory.Release(retired);
        }

        public void Dispose()
        {
            _entries = null;
            _memory.Dispose();
        }
    }
}
