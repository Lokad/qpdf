using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests;

// Empty physical input with an explicitly supplied inspection trailer. Preserve
// the same table/value/engine admissions used by isolated object-graph tests.
internal static class TestDocumentEngine
{
    internal static DocumentEngine Create(PdfDictionary trailer, ResourceGovernor governor)
    {
        using ParserMemoryScope storage = ParserMemoryScope.Create(governor);
        Dictionary<int, XrefEntry> entries = ParserContainers.CreateDictionary<int, XrefEntry>(storage);
        using ParserMemoryScope values = ParserMemoryScope.Create(governor);
        List<PdfDictionary> trailers = ParserContainers.CreateList<PdfDictionary>(storage, 1, IntPtr.Size);
        trailers.Add(trailer);
        XRefTable table = XRefTable.CreateOwned(0L, entries, trailer, trailers, governor, storage, values, false);
        try
        {
            storage.Reserve(32L);
            DocumentEngine engine = DocumentEngine.CreateOwned(ReadOnlyMemory<byte>.Empty, table, QpdfLimits.Default,
                governor, new(governor), QpdfRecoveryMode.Strict, 32L, valuesEscape: true, hostInput: false, snapshotBorrow: null);
            storage.Retain();
            return engine;
        }
        catch { table.Dispose(); throw; }
    }
}
