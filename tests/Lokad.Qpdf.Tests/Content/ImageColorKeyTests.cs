using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Filters;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ImageColorKeyTests
{
    private sealed class Codec : IQpdfJpegCodec
    {
        internal int Encodes, Decodes;
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
        { Decodes++; LokadImageJpegCodec.Instance.Decode(encoded, frame, samples, context); }
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        { Encodes++; LokadImageJpegCodec.Instance.Encode(samples, frame, quality, encoded, context); }
    }

    private static byte[] Pixels(int components)
    {
        byte[] bytes = new byte[64 * 64 * components];
        for (int i = 0; i < bytes.Length; i += components) { bytes[i] = components == 1 ? (byte)129 : (byte)255; }
        return bytes;
    }

    private static byte[] Fixture(int components, string shape, byte[] payload, bool jpeg)
    {
        string mask = components == 1 ? "[129 129]" : "[255 255 0 0 0 0]";
        string entry = shape switch { "absent" or "soft-mask" => "", "null" => "/Mask null", "null-ref" => "/Mask 7 0 R",
            "stream-mask" => "/Mask 8 0 R", "empty-key" => "/Mask []", _ => "/Mask " + (shape.StartsWith("key-ref", StringComparison.Ordinal) ? "6 0 R" : mask) };
        entry += shape is "key-soft" or "key-ref-soft" or "soft-mask" ? " /SMask 9 0 R"
            : shape == "key-null-soft" ? " /SMask null" : shape == "key-ref-null-soft" ? " /SMask 7 0 R" : shape == "key-bad-soft" ? " /SMask 3" : "";
        byte[] content = "q 64 0 0 64 10 20 cm /Image Do Q"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 10 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources 11 0 R /Contents 4 0 R >>", null),
            (4, "<< /Length " + content.Length + " >>", content),
            (5, "<< /Subtype /Image /Width 64 /Height 64 /ColorSpace /Device" + (components == 1 ? "Gray" : "RGB") +
                " /BitsPerComponent 8 /Length " + payload.Length + " " + entry + (jpeg ? " /Filter /DCTDecode" : "") + " >>", payload),
            (6, mask, null), (7, "null", null),
            (8, "<< /Subtype /Image /Width 64 /Height 64 /ImageMask true /BitsPerComponent 1 /Length 512 >>", new byte[512]),
            (9, "<< /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 4096 >>", new byte[4096]),
            (10, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources 11 0 R /Contents 4 0 R >>", null),
            (11, "<< /XObject << /Image 5 0 R /Alias 5 0 R >> >>", null)], 1);
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, bool job, QpdfLimits limits)
        => job ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","optimizeImages":true,"keepInlineImages":true,"oiMinWidth":"0","oiMinHeight":"0","oiMinArea":"0","jpegQuality":"70","compressStreams":"n","decodeLevel":"none","removeUnreferencedResources":"no"}""", new(limits), CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new(["input", "--optimize-images", "--keep-inline-images", "--oi-min-width=0", "--oi-min-height=0", "--oi-min-area=0", "--jpeg-quality=70", "--compress-streams=n", "--decode-level=none", "--remove-unreferenced-resources=no", "output"]), new(limits), CancellationToken.None);

    public static IEnumerable<object[]> Cases()
    {
        foreach (int components in new[] { 1, 3 })
        foreach (bool job in new[] { false, true })
        foreach (string shape in new[] { "key", "key-ref", "key-null-soft", "key-ref-null-soft", "key-bad-soft", "empty-key", "key-soft", "key-ref-soft", "absent", "null", "null-ref", "stream-mask", "soft-mask" })
        { yield return [components, shape, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OptimizationPreservesColorKeysAndStillSupportsOtherMaskKinds(int components, string shape, bool job)
    {
        bool keep = shape is "key" or "key-ref" or "key-null-soft" or "key-ref-null-soft" or "key-bad-soft" or "empty-key";
        Codec codec = new(); byte[] raw = Pixels(components);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(QpdfPath.From("input"), Fixture(components, shape, raw, false), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(keep ? 0 : 1, codec.Encodes); Assert.Equal(0, codec.Decodes);
        Assert.Equal(keep ? 0L : raw.Length, result.Counters.TotalDecodedBytes);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary node, string key) { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
        PdfStream? first = null;
        foreach (var page in document.PageReferences)
        {
            PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(page.Page, "Resources")), objects = Assert.IsType<PdfDictionary>(Get(resources, "XObject"));
            PdfStream image = Assert.IsType<PdfStream>(Get(objects, "Image")); Assert.Same(image, Get(objects, "Alias"));
            if (first is not null) { Assert.Same(first, image); } first = image;
            if (keep) { Assert.Equal(raw, image.RawMemory.ToArray()); Assert.False(image.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _)); }
            else { Assert.Equal(PdfName.FromAscii("DCTDecode"), Get(image.Dictionary, "Filter")); }
            if (shape == "stream-mask") { Assert.Equal(new byte[512], Assert.IsType<PdfStream>(Get(image.Dictionary, "Mask")).RawMemory.ToArray()); }
            if (shape is "key-soft" or "key-ref-soft" or "soft-mask") { Assert.Equal(new byte[4096], Assert.IsType<PdfStream>(Get(image.Dictionary, "SMask")).RawMemory.ToArray()); }
            if (keep)
            {
                PdfArray mask = Assert.IsType<PdfArray>(Get(image.Dictionary, "Mask")); Assert.Equal(shape == "empty-key" ? 0 : components * 2, mask.Count);
                for (int index = 0; index < mask.Count; index++) { Assert.Equal((long)raw[index / 2], Assert.IsType<PdfInteger>(Resolve(mask[index])).Value); }
            }
        }
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(3, false, false)]
    [InlineData(3, true, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, true)]
    [InlineData(3, false, true)]
    [InlineData(3, true, true)]
    public async Task ColorKeysSkipSampleAdmissionAndDoNotRequireACodec(int components, bool job, bool jpeg)
    {
        byte[] raw = jpeg ? JpegFixtures.Encode(64, 64, components, 75, (_, _, channel) => components == 1 ? (byte)129 : channel == 0 ? (byte)255 : (byte)0) : Pixels(components);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 1 };
        using InMemoryQpdfHost host = new(limits); host.AddInput(QpdfPath.From("input"), Fixture(components, "key-ref", raw, jpeg), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, limits);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, result.Counters.TotalDecodedBytes); Assert.Equal(0L, host.OpenLeaseCount);
    }
}
