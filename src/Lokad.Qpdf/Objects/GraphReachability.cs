using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Objects;

/// <summary>Shared reference traversal with admitted temporary containers.</summary>
internal static class GraphReachability
{
    internal static HashSet<int> Find(IIndirectResolver resolver, QpdfObject root,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        memory.CheckOwner(governor);
        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        HashSet<int> numbers = ParserContainers.CreateSet<int>(result);
        HashSet<(int Number, int Generation)> visited = ParserContainers.CreateSet<(int, int)>(scratch);
        List<(object Values, int Next)> frames = ParserContainers.CreateList<(object, int)>(scratch);
        QpdfObject? current = root;
        while (current is not null || frames.Count > 0)
        {
            governor.CheckCancellation();
            if (current is null)
            {
                (object values, int next) = frames[^1];
                if (next < 0)
                {
                    frames.RemoveAt(frames.Count - 1);
                    if (values is List<QpdfObject> legacy) { ParserContainers.ReleaseList(scratch, legacy); }
                    continue;
                }
                frames[^1] = (values, next - 1);
                current = values is PdfNameTable<QpdfObject> names ? names.GetFrozenValueAt(next)
                    : ((IReadOnlyList<QpdfObject>)values)[next];
            }
            QpdfObject valueToVisit = current;
            current = null;
            if (valueToVisit is PdfReference reference)
            {
                if (ParserContainers.AddSetItem(scratch, visited, (reference.ObjectNumber, reference.Generation), 8L)
                    && resolver.TryResolve(reference, out QpdfObject? value) && value is not null)
                {
                    ParserContainers.AddSetItem(result, numbers, reference.ObjectNumber, sizeof(int));
                    current = value;
                }
            }
            else if (valueToVisit is PdfArray array)
            {
                if (array.Count > 0) { ParserContainers.AddItem(scratch, frames, (array.BorrowedItems, array.Count - 1), 16L); }
            }
            else if (valueToVisit is PdfDictionary dictionary)
            {
                if (dictionary.Count == 0) { continue; }
                object values = dictionary.AllocationCarrier;
                if (values is not PdfNameTable<QpdfObject>)
                {
                    // Public/legacy dictionaries have no reverse cursor. Keep
                    // their admitted snapshot local to this frame; frozen name
                    // tables and arrays borrow storage directly.
                    List<QpdfObject> legacy = ParserContainers.CreateList(scratch);
                    using var entries = dictionary.GetEntryEnumerator();
                    while (entries.MoveNext(governor, governor.CancellationToken))
                    { ParserContainers.AddItem(scratch, legacy, entries.Current.Value); }
                    values = legacy;
                }
                ParserContainers.AddItem(scratch, frames, (values, dictionary.Count - 1), 16L);
            }
            else if (valueToVisit is PdfStream stream)
            {
                current = stream.Dictionary;
            }
        }
        governor.CheckCancellation();
        result.TransferTo(memory);
        return numbers;
    }
}
