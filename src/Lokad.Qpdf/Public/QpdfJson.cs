namespace Lokad.Qpdf;

/// <summary>JSON inspection options: format version plus key filter.</summary>
public sealed record QpdfJsonOptions
{
    /// <summary>Creates JSON inspection options.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The version is not 1 or 2.</exception>
    /// <exception cref="ArgumentNullException">Keys is null.</exception>
    /// <exception cref="ArgumentException">Keys contains a null entry.</exception>
    public QpdfJsonOptions(int version, IReadOnlyList<string> keys)
    {
        if (version != 1 && version != 2)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "JSON version must be 1 or 2.");
        }

        if (keys is null)
        {
            throw new ArgumentNullException(nameof(keys));
        }

        List<string> frozen = new(keys.Count);
        foreach (string key in keys)
        {
            if (key is null)
            {
                throw new ArgumentException("JSON keys must not contain null entries.", nameof(keys));
            }

            frozen.Add(key);
        }

        Version = version;
        Keys = frozen;
    }

    /// <summary>Gets the JSON format version.</summary>
    public int Version { get; }

    /// <summary>Creates JSON inspection options with stream and output controls.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The version is not 1 or 2.</exception>
    /// <exception cref="ArgumentNullException">Keys, stream data, or objects are null.</exception>
    /// <exception cref="ArgumentException">A list contains a null entry or stream data is unknown.</exception>
    public QpdfJsonOptions(
        int version,
        IReadOnlyList<string> keys,
        string streamData,
        string? streamPrefix,
        IReadOnlyList<string> objects,
        bool minimalOutput)
        : this(version, keys)
    {
        if (streamData is null)
        {
            throw new ArgumentNullException(nameof(streamData));
        }

        if (streamData != "none" && streamData != "inline" && streamData != "file")
        {
            throw new ArgumentException("Stream data must be one of: none, inline, file.", nameof(streamData));
        }

        if (objects is null)
        {
            throw new ArgumentNullException(nameof(objects));
        }

        List<string> frozenObjects = new(objects.Count);
        foreach (string seen in objects)
        {
            if (seen is null)
            {
                throw new ArgumentException("JSON objects must not contain null entries.", nameof(objects));
            }

            frozenObjects.Add(seen);
        }

        StreamData = streamData;
        StreamPrefix = streamPrefix;
        Objects = frozenObjects;
        MinimalOutput = minimalOutput;
    }

    /// <summary>Gets the selected top-level keys; empty means all supported keys.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Gets how stream payloads are represented: none, inline, or file.</summary>
    public string StreamData { get; } = "none";

    /// <summary>Gets the stream sidecar prefix for file mode; null means the output path.</summary>
    public string? StreamPrefix { get; }

    /// <summary>Gets the selected objects in trailer or obj[,gen] form; empty means all.</summary>
    public IReadOnlyList<string> Objects { get; } = [];

    /// <summary>Gets whether minimal serialization output applies.</summary>
    public bool MinimalOutput { get; }
}

/// <summary>Outcome of JSON inspection.</summary>
public sealed record QpdfJsonResult
{
    /// <summary>Creates an inspection outcome.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public QpdfJsonResult(
        string json,
        IReadOnlyList<QpdfDiagnostic> warnings,
        QpdfExecutionCounters counters)
    {
        Json = json ?? throw new ArgumentNullException(nameof(json));
        Warnings = warnings is null ? throw new ArgumentNullException(nameof(warnings)) : [.. warnings];
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
    }

    /// <summary>Gets the JSON document text.</summary>
    public string Json { get; }

    /// <summary>Gets the warnings recorded by the job.</summary>
    public IReadOnlyList<QpdfDiagnostic> Warnings { get; }

    /// <summary>Gets the final job counters.</summary>
    public QpdfExecutionCounters Counters { get; }
}