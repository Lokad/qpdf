namespace Lokad.Qpdf;

/// <summary>JPEG frame dimensions and raw sample layout: eight-bit samples in tightly packed, top-to-bottom rows.</summary>
public sealed record QpdfJpegFrame
{
    /// <summary>Creates a positive-size grayscale, interleaved RGB or interleaved CMYK frame.</summary>
    public QpdfJpegFrame(int width, int height, int components)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (components is not (1 or 3 or 4)) { throw new ArgumentOutOfRangeException(nameof(components)); }
        Width = width; Height = height; Components = components;
    }

    /// <summary>Gets the pixel width.</summary>
    public int Width { get; }
    /// <summary>Gets the pixel height.</summary>
    public int Height { get; }
    /// <summary>Gets one for grayscale, three for RGB or four for CMYK samples, in that channel order.</summary>
    public int Components { get; }
}

/// <summary>Caller-supplied synchronous JPEG operations over borrowed buffers and streams.</summary>
/// <remarks>
/// Do not retain or dispose the supplied streams, buffers or context. Complete all
/// codec work before returning, observe cancellation and admit significant private
/// allocations through the context's borrowed MemoryBudget. Do not charge supplied
/// samples or input/output twice. Join all workers before returning. A shared implementation must support independent
/// concurrent calls without sharing job state. No PDF policy or storage access is required.
/// The read-only decode stream may expose its already admitted backing through
/// MemoryStream.TryGetBuffer. Borrow only the segment's offset/count, keep its
/// bytes unchanged and release all aliases before returning.
/// </remarks>
public interface IQpdfJpegCodec
{
    /// <summary>Gets whether decoding supports four-component JPEGs as raw PDF CMYK samples.</summary>
    /// <remarks>
    /// Decode YCCK to CMYK using the JPEG color transform, without applying Adobe
    /// sample inversion, PDF Decode arrays, ICC conversion or image orientation.
    /// This capability does not enable CMYK encoding. Existing codecs default to false.
    /// </remarks>
    bool SupportsCmykDecoding => false;

    /// <summary>Decodes one frame into the complete, exactly sized raw sample destination (width × height × components bytes).</summary>
    /// <remarks>Reject corrupt input or a frame/layout mismatch; do not silently convert color models.</remarks>
    void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context);

    /// <summary>Encodes raw samples to the bounded destination, with qpdf quality in the inclusive range 0..100.</summary>
    /// <remarks>Core requests encoding only for grayscale and RGB frames.</remarks>
    void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context);
}

/// <summary>Optional host capability supplying JPEG processing for typed, argv and job-JSON operations.</summary>
public interface IQpdfJpegCodecHost
{
    /// <summary>Gets the JPEG implementation, or null to leave JPEG processing unavailable.</summary>
    IQpdfJpegCodec? JpegCodec { get; }
}
