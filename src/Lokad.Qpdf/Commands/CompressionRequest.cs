namespace Lokad.Qpdf.Commands;

/// <summary>Raw stream-compression request shared by every entry path.</summary>
internal sealed record CompressionRequest(
    string? StreamData,
    string? CompressStreams,
    string? DecodeLevel,
    bool RecompressFlate,
    string? CompressionLevel)
{
    internal static CompressionRequest Default { get; } = new(null, null, null, false, null);
}
