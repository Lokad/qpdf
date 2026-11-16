using System.Text.Json;
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
        IReadOnlyDictionary<int, XrefEntry>? source, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor);
        Dictionary<(int Number, int Generation), int> bindings = ParserContainers.CreateDictionary<(int, int), int>(memory);
        if (source is not null)
        {
            foreach (KeyValuePair<int, XrefEntry> entry in source)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (entry.Value.Kind == XrefEntryKind.Free) { continue; }
                Add((entry.Key, entry.Value.Kind == XrefEntryKind.Compressed ? 0 : entry.Value.Second));
            }
        }
        foreach (JsonProperty property in objects.EnumerateObject())
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            string name = ReadPropertyName(property);
            if (name != "trailer") { Add(ParseObjectKey(name)); }
        }
        return bindings;

        void Add((int Number, int Generation) pair)
        {
            governor.NoteOperation();
            if (bindings.ContainsKey(pair)) { return; }
            ParserContainers.EnsureEntryCapacity(memory, bindings, sizeof(int));
            bindings.Add(pair, checked(bindings.Count + 1));
        }
    }
}
