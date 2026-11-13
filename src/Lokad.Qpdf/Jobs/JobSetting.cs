using System.Text.Json;
using Lokad.Qpdf.Commands;

namespace Lokad.Qpdf.Jobs;

internal readonly record struct JobSetting(string Name, JobValue Value);

// The semantic planner consumes either borrowed JSON tokens or direct argv
// values. Actual JSON retains its document owner; argv never acquires a DOM.
internal readonly struct JobValue
{
    private readonly JsonElement _json;
    private readonly object? _argument;

    internal JobValue(JsonElement json) { _json = json; _argument = null; }
    internal JobValue(object argument) { _json = default; _argument = argument; }

    internal JsonValueKind ValueKind => _argument switch
    {
        null => _json.ValueKind,
        string => JsonValueKind.String,
        bool flag => flag ? JsonValueKind.True : JsonValueKind.False,
        Dictionary<string, object> => JsonValueKind.Object,
        IEnumerable<object> => JsonValueKind.Array,
        _ => throw new InvalidOperationException("Invalid argv configuration value."),
    };

    internal string? ReadString(ConfigurationText storage)
    {
        storage.Governor.CheckCancellation();
        return _argument is string value ? value : storage.ReadJsonString(_json);
    }

    internal int GetArrayLength()
        => _argument is System.Collections.ICollection values ? values.Count : _json.GetArrayLength();

    internal bool TryGetInt64(out long value)
    {
        if (_argument is null) { return _json.TryGetInt64(out value); }
        value = 0L;
        return false;
    }

    internal bool TryGetInt32(out int value)
    {
        if (_argument is null) { return _json.TryGetInt32(out value); }
        value = 0;
        return false;
    }

    internal bool TryGetProperty(string name, out JobValue value)
    {
        if (_argument is Dictionary<string, object> fields)
        {
            if (fields.TryGetValue(name, out object? argument))
            { value = new(argument); return true; }
        }
        else if (_json.TryGetProperty(name, out JsonElement json))
        { value = new(json); return true; }
        value = default;
        return false;
    }

    internal IEnumerable<JobSetting> EnumerateObject()
    {
        if (_argument is Dictionary<string, object> fields)
        {
            foreach ((string name, object value) in fields)
            { yield return new(name, new(value)); }
        }
        else
        {
            foreach (JsonProperty property in _json.EnumerateObject())
            { yield return new(property.Name, new(property.Value)); }
        }
    }

    internal IEnumerable<JobValue> EnumerateArray()
    {
        if (_argument is IEnumerable<object> values)
        {
            foreach (object value in values) { yield return new(value); }
        }
        else
        {
            foreach (JsonElement value in _json.EnumerateArray()) { yield return new(value); }
        }
    }
}
