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
public sealed record QpdfZlibFlateOptions(QpdfZlibFlateMode Mode, int? CompressionLevel);
