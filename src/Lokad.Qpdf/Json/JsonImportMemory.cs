using System.Runtime.InteropServices;
using System.Text.Json;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Json;

/// <summary>Admits JSON phases from backing bytes and token density, without value receipts.</summary>
internal static class JsonImportMemory
{
    internal static void AdmitDocument(ParserMemoryScope memory, ReadOnlySpan<byte> json,
        ResourceGovernor governor, CancellationToken token)
        => memory.Reserve(Estimate(json, governor, token).Document);

    internal static void AdmitModel(ParserMemoryScope memory, JsonElement json,
        ResourceGovernor governor, CancellationToken token)
        => memory.Reserve(Estimate(JsonMarshal.GetRawUtf8Value(json), governor, token).Model);

    private static (long Document, long Model) Estimate(ReadOnlySpan<byte> json,
        ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        Utf8JsonReader reader = new(json, new JsonReaderOptions { MaxDepth = 128 });
        long document = 1024L, model = 512L;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            document = checked(document + 32L);
            // Capacity estimates cover wrappers and backing arrays together.
            // A large scalar pays for its text/encoding overlap, while many
            // short values pay for container slots and headers instead.
            model = checked(model + (reader.TokenType switch
            {
                JsonTokenType.PropertyName => 128L + 4L * reader.ValueSpan.Length,
                JsonTokenType.String => 80L + 4L * reader.ValueSpan.Length,
                JsonTokenType.Number => 80L + 2L * reader.ValueSpan.Length,
                JsonTokenType.StartObject => 128L,
                JsonTokenType.StartArray => 64L,
                JsonTokenType.EndObject or JsonTokenType.EndArray => 0L,
                _ => 16L,
            }));
        }
        return (document, model);
    }
}
