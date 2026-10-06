using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Objects;

/// <summary>Shared reference traversal with admitted temporary containers.</summary>
internal static class GraphReachability
{
    internal static HashSet<int> Find(IIndirectResolver resolver, QpdfObject root,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        HashSet<int> numbers = ParserContainers.CreateSet<int>(memory);
        HashSet<(int Number, int Generation)> visited = ParserContainers.CreateSet<(int, int)>(memory);
        List<QpdfObject> pending = ParserContainers.CreateList(memory);
        ParserContainers.AddItem(memory, pending, root);
        while (pending.Count > 0)
        {
            governor.CheckCancellation();
            governor.ChargeWork(1L);
            QpdfObject current = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (current is PdfReference reference)
            {
                if (ParserContainers.AddSetItem(memory, visited, (reference.ObjectNumber, reference.Generation), 8L)
                    && resolver.TryResolve(reference, out QpdfObject? value) && value is not null)
                {
                    ParserContainers.AddSetItem(memory, numbers, reference.ObjectNumber, sizeof(int));
                    ParserContainers.AddItem(memory, pending, value);
                }
            }
            else if (current is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    governor.ChargeWork(1L);
                    ParserContainers.AddItem(memory, pending, array[i]);
                }
            }
            else if (current is PdfDictionary dictionary)
            {
                using var entries = dictionary.GetEntryEnumerator();
                while (entries.MoveNext())
                {
                    governor.ChargeWork(1L);
                    ParserContainers.AddItem(memory, pending, entries.Current.Value);
                }
            }
            else if (current is PdfStream stream)
            {
                ParserContainers.AddItem(memory, pending, stream.Dictionary);
            }
        }
        return numbers;
    }
}
