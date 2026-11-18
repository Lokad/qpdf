using System.Text.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Json;

internal static partial class JsonImport
{
    // Bind every declaration before decoding edges, including forward references.
    // Source edges additionally validate against the original xref so new JSON
    // declarations cannot revive missing, free or stale source references.
    private static Dictionary<(int Number, int Generation), int> ReadObjectBindings(JsonElement objects,
        IReadOnlyDictionary<int, XrefEntry>? source, IEnumerable<PdfReference>? addedReferences,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor);
        Dictionary<(int Number, int Generation), int> bindings = ParserContainers.CreateDictionary<(int, int), int>(memory);
        if (source is not null)
        {
            foreach (KeyValuePair<int, XrefEntry> entry in source)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (entry.Value.Kind == XrefEntryKind.Free) { continue; }
                Bind(bindings, (entry.Key, entry.Value.Kind == XrefEntryKind.Compressed ? 0 : entry.Value.Second), memory, governor);
            }
        }
        if (addedReferences is not null)
        {
            foreach (PdfReference reference in addedReferences)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                Bind(bindings, (reference.ObjectNumber, reference.Generation), memory, governor);
            }
        }
        AddJsonBindings(bindings, objects, memory, governor, token);
        return bindings;
    }

    private static int AddJsonBindings(Dictionary<(int Number, int Generation), int> bindings, JsonElement objects,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        int greatest = 0;
        foreach (JsonProperty property in objects.EnumerateObject())
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            string name = ReadPropertyName(property);
            if (name == "trailer") { continue; }
            (int Number, int Generation) pair = ParseObjectKey(name);
            Bind(bindings, pair, memory, governor);
            greatest = Math.Max(greatest, pair.Number);
        }
        return greatest;
    }

    private static int Bind(Dictionary<(int Number, int Generation), int> bindings, (int Number, int Generation) pair,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        governor.NoteOperation();
        if (bindings.TryGetValue(pair, out int previous)) { return previous; }
        ParserContainers.EnsureEntryCapacity(memory, bindings, sizeof(int));
        int bound = checked(bindings.Count + 1);
        bindings.Add(pair, bound);
        return bound;
    }
}
