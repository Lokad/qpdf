using System.Runtime.InteropServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ImageSampleBorrowTests
{
    private sealed class ObservingCodec(byte[] backing, int offset, int count) : IQpdfJpegCodec
    {
        internal int Calls;
        internal bool CorrectBacking, CorrectWindow, CorrectBytes;
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
            => throw new NotSupportedException();
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        {
            Calls++;
            bool array = MemoryMarshal.TryGetArray(samples, out ArraySegment<byte> segment);
            CorrectBacking = array && ReferenceEquals(backing, segment.Array);
            CorrectWindow = array && segment.Offset == offset && segment.Count == count;
            CorrectBytes = samples.Span.SequenceEqual(backing.AsSpan(offset, count));
            LokadImageJpegCodec.Instance.Encode(samples, frame, quality, encoded, context);
        }
    }

    [Theory]
    [InlineData(1, "array")]
    [InlineData(3, "array")]
    [InlineData(1, "slice")]
    [InlineData(3, "slice")]
    [InlineData(1, "trimmed")]
    [InlineData(3, "trimmed")]
    public void UnfilteredAndTrimmedSamplesBorrowTheirFundedStorage(int components, string storage)
    {
        int count = 64 * 64 * components;
        int offset = storage == "array" ? 0 : 17;
        int length = count + (storage == "trimmed" ? 1 : 0);
        byte[] backing = new byte[storage == "array" ? count : length + 41];
        backing.AsSpan(offset, count).Fill(7);
        if (storage == "trimmed") { backing[offset + count] = 10; }
        ObservingCodec codec = new(backing, offset, count);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { JpegCodec = codec };
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        source.Reserve(1024L + backing.Length);
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject"),
            [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Image"),
            [PdfName.FromAscii("Width")] = new PdfInteger("64", 64),
            [PdfName.FromAscii("Height")] = new PdfInteger("64", 64),
            [PdfName.FromAscii("BitsPerComponent")] = new PdfInteger("8", 8),
            [PdfName.FromAscii("ColorSpace")] = PdfName.FromAscii(components == 1 ? "DeviceGray" : "DeviceRGB"),
        });
        PdfStream image = PdfStream.FromImmutableSlice(dictionary, backing.AsMemory(offset, length));
        PdfStream? optimized = ImageOptimizer.TryOptimize(image, new(true, 0L, 0L, 0L, true, 75),
            new(new Dictionary<int, QpdfObject>()), QpdfLimits.Default, governor, new(governor), CancellationToken.None);
        Assert.NotNull(optimized);
        Assert.Equal(1, codec.Calls); Assert.True(codec.CorrectBacking); Assert.True(codec.CorrectWindow);
        Assert.True(codec.CorrectBytes); Assert.True(governor.Snapshot().LiveMemoryBytes >= source.ReservedBytes);
        Assert.Equal(length, governor.Snapshot().TotalDecodedBytes);
        Assert.True(image.RawMemory.Span[..count].IndexOfAnyExcept((byte)7) < 0);
        source.Dispose(); governor.CompletePrivateStorage();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(DctFilter.TryParseFrame(optimized.Data,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None), out int width, out int height, out int encodedComponents));
        Assert.Equal(64, width); Assert.Equal(64, height); Assert.Equal(components, encodedComponents);
    }
}
