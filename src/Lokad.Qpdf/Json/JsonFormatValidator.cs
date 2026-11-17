using System.Text.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Json;

/// <summary>Checks the described container structure without retaining an output DOM.</summary>
internal sealed class JsonFormatValidator : IDisposable
{
    private sealed record Node(string Path, Field[]? Fields, Node?[]? Items);
    private readonly record struct Field(string Name, Node? Value);

    private readonly ResourceGovernor _governor;
    private readonly CancellationToken _token;
    private readonly int _version;
    private DisplayBuffer? _errors;
    private Action<long>? _observe;

    private JsonFormatValidator(ResourceGovernor governor, CancellationToken token, int version, Action<long>? observe)
    { _governor = governor; _token = token; _version = version; _observe = observe; }

    internal static void Check(ReadOnlySpan<byte> json, string description, int version, IReadOnlyList<string> keys,
        bool minimalOutput, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
        => CheckCore(json, description, version, keys, minimalOutput, governor, diagnostics, token, null);

    // Observes actual matching reads, after the independent syntax pass, so
    // cancellation tests do not depend on a timer or admission during encoding.
    internal static void CheckObserved(ReadOnlySpan<byte> json, string description, int version, IReadOnlyList<string> keys,
        bool minimalOutput, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token, Action<long> observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        CheckCore(json, description, version, keys, minimalOutput, governor, diagnostics, token, observe);
    }

    private static void CheckCore(ReadOnlySpan<byte> json, string description, int version, IReadOnlyList<string> keys,
        bool minimalOutput, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token, Action<long>? observe)
    {
        JsonReaderOptions options = new() { MaxDepth = DocumentJson.SyntaxMaxDepth };
        Utf8JsonReader syntax = new(json, options);
        // Complete syntax wins over a format diagnostic, including when drift
        // precedes a malformed suffix. No output values are materialized.
        if (!Read(ref syntax, governor, token) || syntax.TokenType != JsonTokenType.StartObject)
        { throw new JsonException("JSON schema checking requires an object."); }
        while (Read(ref syntax, governor, token)) { }

        using ParserMemoryScope modelMemory = ParserMemoryScope.Create(governor);
        // The fixed description bounds its small DOM, decoded names, node/field
        // arrays, paths and construction overlap. It never scales with output.
        modelMemory.Reserve(checked(256L + 32L * description.Length));
        Node root;
        using (JsonDocument schema = JsonDocument.Parse(description, new JsonDocumentOptions { MaxDepth = 32 }))
        { root = Build(schema.RootElement, "$", 0, keys, minimalOutput, governor, token) ?? throw new InvalidOperationException("JSON format root must be a dictionary."); }
        using JsonFormatValidator validator = new(governor, token, version, observe);
        Utf8JsonReader reader = new(json, options);
        _ = validator.Read(ref reader);
        validator.Validate(ref reader, root);
        if (validator._errors is DisplayBuffer errors)
        {
            using GovernedText message = errors.Finish();
            diagnostics.AddOwned(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, message);
        }
        else
        {
            diagnostics.AddParts(QpdfDiagnosticCode.JsonSchemaCheck, QpdfDiagnosticSeverity.Info,
                ["JSON schema check passed for version ", version, "."]);
        }
    }

    private static Node? Build(JsonElement value, string path, int depth, IReadOnlyList<string> keys, bool minimalOutput,
        ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (depth > 32) { throw new InvalidOperationException("JSON format description is too deep."); }
        if (value.ValueKind == JsonValueKind.String) { return null; }
        if (value.ValueKind == JsonValueKind.Object)
        {
            int count = value.GetPropertyCount();
            if (count > 64) { throw new InvalidOperationException("JSON format dictionary has too many fields."); }
            Field[] fields = new Field[count]; int used = 0;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                string name = property.Name;
                if (depth == 0 && !Wanted(name, keys, minimalOutput)) { continue; }
                fields[used++] = new(name, Build(property.Value, path + "." + name, depth + 1, keys, minimalOutput, governor, token));
            }
            if (used != fields.Length) { Array.Resize(ref fields, used); }
            return new(path, fields, null);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            int count = value.GetArrayLength();
            if (count > 64) { throw new InvalidOperationException("JSON format array has too many elements."); }
            Node?[] items = new Node?[count];
            for (int index = 0; index < count; index++)
            {
                string child = count == 1 ? path + "[]" : path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                items[index] = Build(value[index], child, depth + 1, keys, minimalOutput, governor, token);
            }
            return new(path, null, items);
        }
        throw new InvalidOperationException("JSON format values must be descriptions, dictionaries or arrays.");
    }

    private static bool Wanted(string name, IReadOnlyList<string> keys, bool minimalOutput)
        => name is "version" or "parameters" ? !minimalOutput : keys.Count == 0 || keys.Contains(name);

    private static bool Read(ref Utf8JsonReader reader, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool found = reader.Read();
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return found;
    }

    private bool Read(ref Utf8JsonReader reader)
    {
        bool found = Read(ref reader, _governor, _token);
        if (found) { _observe?.Invoke(reader.BytesConsumed); }
        _token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
        return found;
    }

    private void Validate(ref Utf8JsonReader reader, Node? schema)
    {
        if (schema is null) { Skip(ref reader); return; }
        if (schema.Fields is Field[] fields) { ValidateObject(ref reader, schema.Path, fields); return; }
        Node?[] items = schema.Items ?? throw new InvalidOperationException("Missing JSON format array.");
        if (items.Length == 1 && reader.TokenType != JsonTokenType.StartArray)
        { Validate(ref reader, items[0]); return; }
        if (reader.TokenType != JsonTokenType.StartArray)
        { Fail(schema.Path, "expected an array"); Skip(ref reader); return; }
        int index = 0;
        while (Read(ref reader) && reader.TokenType != JsonTokenType.EndArray)
        {
            if (items.Length == 1) { Validate(ref reader, items[0]); }
            else if (index < items.Length) { Validate(ref reader, items[index]); }
            else { Skip(ref reader); }
            index++;
        }
        if (items.Length != 1 && index != items.Length) { Fail(schema.Path, "array length differs from the format"); }
    }

    private void ValidateObject(ref Utf8JsonReader reader, string path, Field[] fields)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        { Fail(path, "expected a dictionary"); Skip(ref reader); return; }
        bool wildcard = fields.Length == 1 && fields[0].Name.StartsWith('<') && fields[0].Name.EndsWith('>');
        ulong seen = 0UL;
        while (Read(ref reader) && reader.TokenType != JsonTokenType.EndObject)
        {
            int field = wildcard ? 0 : Find(ref reader, fields);
            if (field < 0) { Unknown(path, ref reader); }
            if (!Read(ref reader)) { throw new JsonException("Missing JSON value."); }
            if (field < 0) { Skip(ref reader); }
            else { seen |= 1UL << field; Validate(ref reader, fields[field].Value); }
        }
        if (wildcard) { return; }
        for (int index = 0; index < fields.Length; index++)
        {
            _token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            if ((seen & (1UL << index)) == 0) { Fail(path, "missing key '" + fields[index].Name + "'"); }
        }
    }

    private static int Find(ref Utf8JsonReader reader, Field[] fields)
    {
        for (int index = 0; index < fields.Length; index++)
        {
            // Escaped ASCII cannot occupy more than six bytes per character.
            if (reader.ValueSpan.Length <= 6L * fields[index].Name.Length && reader.ValueTextEquals(fields[index].Name)) { return index; }
        }
        return -1;
    }

    private void Skip(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) { return; }
        int depth = reader.CurrentDepth;
        while (Read(ref reader))
        {
            if (reader.CurrentDepth == depth && reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject) { return; }
        }
    }

    private DisplayBuffer Errors()
    {
        if (_errors is not null) { return _errors; }
        _errors = DisplayBuffer.Create(_governor, true);
        _errors.Append("JSON output does not match --json-help version "); _errors.AppendInteger(_version); _errors.Append(": ");
        return _errors;
    }

    private void Fail(string path, string reason)
    {
        DisplayBuffer errors = Errors(); errors.Append(path); errors.Append(": "); errors.Append(reason); errors.Append("; ");
    }

    private void Unknown(string path, ref Utf8JsonReader reader)
    {
        DisplayBuffer errors = Errors();
        // Bound unescape scratch before materializing a diagnostic key. Large
        // ordinary PDF keys at unconstrained leaves never need this conversion.
        _governor.CheckDiagnostic(reader.ValueIsEscaped ? (reader.ValueSpan.Length + 5L) / 6L : reader.ValueSpan.Length);
        using ParserMemoryScope keyMemory = ParserMemoryScope.Create(_governor);
        keyMemory.Reserve(checked(GovernedText.StringBytes(reader.ValueSpan.Length) + 32L + 2L * reader.ValueSpan.Length));
        string name = reader.GetString() ?? string.Empty;
        errors.Append(path); errors.Append(": unknown key '"); errors.Append(name); errors.Append("'; ");
    }

    public void Dispose() { DisplayBuffer? errors = _errors; _errors = null; _observe = null; errors?.Dispose(); }
}
