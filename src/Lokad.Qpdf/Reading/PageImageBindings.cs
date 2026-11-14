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
    private int _next;

    private PageImageBindings(DocumentEngine engine, PdfDictionary? xobjects, List<PdfName>? names,
        ResourceGovernor governor, CancellationToken token)
    { _engine = engine; _xobjects = xobjects; _names = names; _governor = governor; _token = token; _next = 0; }

    internal static PageImageBindings Create(DocumentEngine engine, PageLocation location, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        QpdfObject? raw = PageInheritance.Find(engine, location, PdfName.FromAscii("Resources"), limits, governor, token);
        PdfDictionary? resources = Content.CatalogCopy.ResolveDictionary(raw, engine, governor, token);
        if (resources is null || !resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjectsRaw) || xobjectsRaw is PdfNull)
        { return new(engine, null, null, governor, token); }
        PdfDictionary? xobjects = xobjectsRaw as PdfDictionary;
        if (xobjects is null && xobjectsRaw is PdfReference reference
            && ((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved))
        { xobjects = resolved as PdfDictionary; }
        if (xobjects is null) { return new(engine, null, null, governor, token); }

        List<PdfName> names = ParserContainers.CreateList<PdfName>(memory, xobjects.Count, IntPtr.Size);
        using (PdfDictionary.EntryEnumerator entries = xobjects.GetEntryEnumerator())
        {
            while (entries.MoveNext())
            { token.ThrowIfCancellationRequested(); governor.NoteOperation(); names.Add(entries.Current.Key); }
        }
        GovernedCollections.SortNames(memory, governor, names);
        return new(engine, xobjects, names, governor, token);
    }

    internal bool TryReadNext(out Binding binding)
    {
        _token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
        binding = default;
        if (_names is null || _xobjects is null) { return false; }
        while (_next < _names.Count)
        {
            _token.ThrowIfCancellationRequested(); _governor.NoteOperation();
            PdfName name = _names[_next++];
            if (!_xobjects.TryGet(name, out QpdfObject entry) || entry is not PdfReference reference
                || !((IIndirectResolver)_engine).TryResolve(reference, out QpdfObject? target) || target is not PdfStream stream
                || !PdfDictionaryLookup.HasName(stream.Dictionary, "Subtype"u8, "Image"u8, _engine, _governor, _token))
            { continue; }
            binding = new(name, reference, stream); return true;
        }
        return false;
    }
}
