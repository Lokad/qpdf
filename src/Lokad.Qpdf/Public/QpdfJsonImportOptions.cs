namespace Lokad.Qpdf;

/// <summary>Physical PDF layout for JSON creation and update.</summary>
public sealed record QpdfJsonImportOptions
{
    internal static readonly QpdfJsonImportOptions Default = new("disable", false);

    /// <summary>Creates layout options without changing the imported PDF graph.</summary>
    /// <param name="objectStreams">The object-stream mode: disable, preserve or generate.</param>
    /// <param name="linearize">Whether to use the shared two-pass linearized writer.</param>
    /// <exception cref="ArgumentNullException">The object-stream mode is null.</exception>
    /// <exception cref="ArgumentException">The object-stream mode is unknown.</exception>
    public QpdfJsonImportOptions(string objectStreams, bool linearize)
    {
        ArgumentNullException.ThrowIfNull(objectStreams);
        if (objectStreams is not ("disable" or "preserve" or "generate"))
        { throw new ArgumentException("Object streams must be disable, preserve or generate.", nameof(objectStreams)); }
        ObjectStreams = objectStreams; Linearize = linearize;
    }

    /// <summary>Gets the object-stream mode. Preservation applies to matching source objects during updates.</summary>
    public string ObjectStreams { get; }

    /// <summary>Gets whether to linearize the output.</summary>
    public bool Linearize { get; }
}
