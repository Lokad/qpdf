namespace Lokad.Qpdf;

/// <summary>Direction of a zlib-flate companion operation.</summary>
public enum QpdfZlibFlateMode
{
    /// <summary>Inflate zlib-wrapped bytes back to raw bytes.</summary>
    Uncompress = 0,

    /// <summary>Deflate raw bytes with a zlib wrapper.</summary>
    Compress = 1,
}

/// <summary>
/// Options for the zlib-flate companion facade. Compression levels follow
/// the governed buckets 1..9 (1 fastest, 9 smallest, default optimal);
/// levels are only meaningful when compressing.
/// </summary>
public sealed record QpdfZlibFlateOptions
{
    /// <summary>Creates zlib-flate options.</summary>
    public QpdfZlibFlateOptions(QpdfZlibFlateMode mode, int? compressionLevel)
    {
        Mode = mode;
        CompressionLevel = compressionLevel;
    }

    /// <summary>Gets the operation direction.</summary>
    public QpdfZlibFlateMode Mode { get; }

    /// <summary>Gets the governed level 1..9; meaningful only when compressing.</summary>
    public int? CompressionLevel { get; }
}
