using System.Text.Json;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Jobs;

/// <summary>Estimates large configuration phases without allocating a second encoding or token ledger.</summary>
internal static class JobJsonMemory
{
    internal const int DetailedThreshold = 16384;
    internal const int MaxDepth = 32;

    internal static long ArgumentScratch(IReadOnlyList<string> arguments, long utf8Bytes, ResourceGovernor governor)
    {
        long blanket = checked(32L * utf8Bytes);
        if (utf8Bytes < DetailedThreshold) { return blanket; }
        long escaped = 0L, characters = 0L;
        foreach (string argument in arguments)
        {
            governor.CheckCancellation();
            characters = checked(characters + argument.Length);
            for (int offset = 0; offset < argument.Length; offset++)
            {
                if ((offset & 255) == 0) { governor.CheckCancellation(); }
                char value = argument[offset];
                // Keep the whitelist deliberately narrow. All other UTF-16
                // units can expand to six JSON characters, including surrogates.
                escaped = checked(escaped + (value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' ? 1L : 6L));
            }
        }
        // Fund serializer buffer growth, its final UTF-16 string and copied
        // argument text. Escape-heavy input keeps the established broad bound.
        return Math.Min(blanket, checked(8L * escaped + 2L * characters));
    }

    internal static (long Document, long Copies) Estimate(ReadOnlySpan<byte> json, ResourceGovernor governor)
    {
        governor.CheckCancellation();
        Utf8JsonReader reader = new(json, new JsonReaderOptions { MaxDepth = MaxDepth });
        long document = checked(4096L + 2L * json.Length), copies = 0L;
        while (reader.Read())
        {
            governor.CheckCancellation();
            if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
            { AddString(ref document, ref copies, reader.TokenType == JsonTokenType.PropertyName, reader.ValueSpan.Length); }
            else { document = checked(document + (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray ? 128L : 32L)); }
        }
        return (document, copies);
    }

    // JsonDocument already encodes string input. This conservative lexical scan
    // avoids introducing another large UTF-8 array just to estimate its shape.
    // Syntax/encoding validation remains with the shared JSON parser.
    internal static (long Document, long Copies) Estimate(ReadOnlySpan<char> json, long utf8Bytes, ResourceGovernor governor)
    {
        governor.CheckCancellation();
        long document = checked(4096L + 2L * utf8Bytes), copies = 0L;
        int position = 0, nextCheck = 0;
        while (position < json.Length)
        {
            Check(position);
            char current = json[position++];
            if (current is ' ' or '\r' or '\n' or '\t' or ',' or ':') { continue; }
            if (current is '{' or '[') { document = checked(document + 128L); continue; }
            if (current is '}' or ']') { document = checked(document + 32L); continue; }
            if (current == '"')
            {
                int start = position;
                while (position < json.Length && json[position] != '"')
                {
                    Check(position);
                    position += json[position] == '\\' ? Math.Min(2, json.Length - position) : 1;
                }
                int length = position - start;
                if (position < json.Length) { position++; }
                while (position < json.Length && json[position] is ' ' or '\r' or '\n' or '\t')
                { Check(position); position++; }
                AddString(ref document, ref copies, position < json.Length && json[position] == ':', length);
            }
            else
            {
                document = checked(document + 32L);
                while (position < json.Length && json[position] is not (' ' or '\r' or '\n' or '\t' or ',' or ':' or '{' or '[' or '}' or ']' or '"'))
                { Check(position); position++; }
            }
        }
        return (document, copies);

        void Check(int at)
        {
            if (at >= nextCheck) { governor.CheckCancellation(); nextCheck = at + Math.Min(256, int.MaxValue - at); }
        }
    }

    private static void AddString(ref long document, ref long copies, bool property, int encodedLength)
    {
        // Property batches include dictionaries, names and old/new sorting/list
        // capacity. String credit covers a UTF-16 backing and header, including
        // escape-heavy input conservatively. No credit is attached to a value.
        document = checked(document + (property ? 256L + 4L * encodedLength : 32L));
        if (!property && encodedLength != 0) { copies = checked(copies + GovernedText.StringBytes(encodedLength)); }
    }
}
