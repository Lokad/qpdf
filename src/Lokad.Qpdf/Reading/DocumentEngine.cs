using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>
/// Strict single-section document engine: on-demand object loading over a
/// classic xref table with a parse cache. Reference chains never recurse:
/// nested references stay references until explicitly resolved, and the page
/// walk carries its own visited set and depth budget.
/// </summary>
internal sealed class DocumentEngine : IIndirectResolver
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly ClassicXrefTable _table;
    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private readonly DiagnosticBag _diagnostics;
    private readonly PdfParser _parser;
    private readonly Dictionary<int, QpdfObject> _cache = new();

    internal DocumentEngine(
        ReadOnlyMemory<byte> data,
        ClassicXrefTable table,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics)
    {
        _data = data;
        _table = table;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _parser = new PdfParser(limits, governor, diagnostics, this);
        limits.Validate();
    }

    internal PdfDictionary Trailer => _table.Trailer;

    internal DiagnosticBag Diagnostics => _diagnostics;

    bool IIndirectResolver.TryResolve(PdfReference reference, out QpdfObject? value)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (_cache.TryGetValue(reference.ObjectNumber, out QpdfObject? cached))
        {
            value = cached;
            return true;
        }

        if (!_table.Entries.TryGetValue(reference.ObjectNumber, out XrefEntry entry))
        {
            value = null;
            return false;
        }

        if (!entry.InUse)
        {
            value = PdfNull.Instance;
            return true;
        }

        value = Load(reference.ObjectNumber, entry);
        return true;
    }

    internal QpdfObject Load(int number)
    {
        if (_cache.TryGetValue(number, out QpdfObject? cached) && cached is not null)
        {
            return cached;
        }

        if (!_table.Entries.TryGetValue(number, out XrefEntry entry))
        {
            throw new QpdfParseException($"Object {number} has no xref entry.", null, number);
        }

        if (!entry.InUse)
        {
            return PdfNull.Instance;
        }

        return Load(number, entry);
    }

    private QpdfObject Load(int number, XrefEntry entry)
    {
        _governor.ChargeWork(1L);
        PdfTokenizer tokenizer = new(_data);
        tokenizer.SetPosition(entry.Offset);
        (int foundNumber, int foundGeneration, QpdfObject value) = _parser.ParseIndirectObject(tokenizer, 0);
        if (foundNumber != number || foundGeneration != entry.Generation)
        {
            throw new QpdfParseException($"Object header contradicts its xref entry for object {number}.", entry.Offset, number);
        }

        _cache[number] = value;
        return value;
    }
}
