using System.IO.Compression;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class LateContentTransformTests
{
    private sealed class CountingCodec : IQpdfJpegCodec
    {
        internal int Encodes { get; private set; }
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
            => LokadImageJpegCodec.Instance.Decode(encoded, frame, samples, context);
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        { Encodes++; LokadImageJpegCodec.Instance.Encode(samples, frame, quality, encoded, context); }
    }

    private static byte[] InlineProgram()
        => [.. "q 32 0 0 32 10 10 cm BI /W 32 /H 32 /BPC 8 /CS /RGB ID "u8,
            .. Enumerable.Range(0, 32 * 32).SelectMany(_ => new byte[] { 255, 0, 0 }), .. " EI Q\n"u8];

    private static byte[] Compressed(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, true)) { zlib.Write(data); }
        return output.ToArray();
    }

    internal static byte[] Fixture(string role, bool encoded, bool alias)
    {
        byte[] inline = InlineProgram();
        byte[] content = role == "page" ? inline : role == "form" ? "/Nested Do\n"u8.ToArray() : "q Q\n"u8.ToArray();
        byte[] appearance = role is "appearance" or "form" ? inline : "q Q\n"u8.ToArray();
        byte[] unused = new byte[128 * 128 * 3]; new Random(119).NextBytes(unused);
        byte[] pageData = encoded ? Compressed(content) : content;
        byte[] formData = encoded ? Compressed(appearance) : appearance;
        string filter = encoded ? "/Filter /FlateDecode " : "";
        string resources = role == "form" ? "<< /XObject << /Nested 6 0 R >> >>"
            : role == "unused" ? "<< /XObject << /Unused 7 0 R >> >>" : "<< >>";
        bool annotation = role == "appearance" || alias && role == "unused";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AuditContents 4 0 R /AuditAppearance 6 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources " + resources + " /Contents 4 0 R " + (annotation ? "/Annots [5 0 R]" : "") + " >>", null),
            (4, "<< " + filter + "/Length " + pageData.Length + " >>", pageData),
            (5, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect [0 0 100 100] /AP << /N 6 0 R >> " + (alias ? "/Audit 7 0 R" : "") + " >>", null),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << >> " + filter + "/Length " + formData.Length + " >>", formData),
            (7, "<< /Type /XObject /Subtype /Image /Width 128 /Height 128 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length " + unused.Length + " >>", unused)], 1);
    }

    private static byte[] Blank() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>", null),
        (4, "<< /Length 0 >>", [])], 1);

    public static IEnumerable<object[]> ExternalizationCases()
    {
        foreach (string role in new[] { "page", "form" })
        foreach (string operation in new[] { "overlay", "underlay" })
        foreach (bool encoded in new[] { false, true })
        foreach (bool job in new[] { false, true })
        foreach (bool optimize in new[] { false, true })
        { yield return [role, operation, encoded, job, optimize, false]; }
        foreach (string operation in new[] { "direct", "overlay", "underlay" })
        foreach (bool encoded in new[] { false, true })
        foreach (bool job in new[] { false, true })
        foreach (bool combined in new[] { false, true })
        { yield return ["appearance", operation, encoded, job, false, combined]; }
    }

    [Theory]
    [MemberData(nameof(ExternalizationCases))]
    public async Task FinalLiveFormsReceiveRequestedInlineProcessing(string role, string operation, bool encoded, bool job, bool optimize, bool combined)
    {
        CountingCodec codec = new(); using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(QpdfPath.From("input"), operation == "direct" ? Fixture(role, encoded, false) : Blank(), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("source"), Fixture(role, encoded, false), QpdfInputOwnership.Copy);
        List<string> argv = ["input", "--stream-data=uncompress"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress" };
        if (optimize)
        {
            argv.AddRange(["--optimize-images", "--oi-min-width=0", "--oi-min-height=0", "--oi-min-area=0"]);
            settings["optimizeImages"] = true; settings["oiMinWidth"] = "0"; settings["oiMinHeight"] = "0"; settings["oiMinArea"] = "0";
        }
        else { argv.AddRange(["--externalize-inline-images", "--ii-min-bytes=0"]); settings["externalizeInlineImages"] = true; settings["iiMinBytes"] = "0"; }
        if (role == "appearance") { argv.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; }
        if (combined) { argv.AddRange(["--coalesce-contents", "--normalize-content=y"]); settings["coalesceContents"] = true; settings["normalizeContent"] = "y"; }
        if (operation != "direct") { argv.AddRange(["--" + operation, "source", "--"]); settings[operation] = new { file = "source" }; }
        argv.Add("output");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        List<PdfStream> images = LiveImages(document);
        PdfStream image = Assert.Single(images);
        Assert.Equal(optimize ? 1 : 0, codec.Encodes);
        if (optimize) { Assert.True(image.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter)); Assert.Equal(PdfName.FromAscii("DCTDecode"), filter); }
        if (operation == "direct")
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("AuditAppearance"), out QpdfObject raw));
            PdfStream original = Assert.IsType<PdfStream>(Resolve(document, raw));
            Assert.Equal(InlineProgram(), original.RawMemory.ToArray());
        }
    }

    [Theory]
    [InlineData("overlay", false, false, true)]
    [InlineData("underlay", true, false, true)]
    [InlineData("overlay", true, true, true)]
    [InlineData("underlay", false, true, true)]
    [InlineData("overlay", false, false, false)]
    [InlineData("underlay", true, false, false)]
    public async Task SelectedPruningSeesImportedResourcesAndKeepsIndependentAliases(string operation, bool job, bool alias, bool selected)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Blank(), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("source"), Fixture("unused", false, alias), QpdfInputOwnership.Copy);
        List<string> argv = ["input", "--stream-data=uncompress", "--remove-unreferenced-resources=yes"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress", ["removeUnreferencedResources"] = "yes" };
        if (selected) { argv.AddRange(["--pages", ".", "1", "--"]); settings["pages"] = new[] { new { file = ".", range = "1" } }; }
        argv.AddRange(["--" + operation, "source", "--", "output"]); settings[operation] = new { file = "source" };
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(selected ? 0 : 1, LiveImages(document).Count);
        if (alias)
        {
            PdfDictionary page = document.PageReferences[0].Page; Assert.True(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw));
            PdfDictionary annot = Assert.IsType<PdfDictionary>(Resolve(document, Assert.IsType<PdfArray>(Resolve(document, raw))[0]));
            Assert.True(annot.TryGet(PdfName.FromAscii("Audit"), out QpdfObject image));
            Assert.Equal(128 * 128 * 3, Assert.IsType<PdfStream>(Resolve(document, image)).RawMemory.Length);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MixedSourceAutoPruningKeepsEachOriginalDecision(bool reverse, bool job)
    {
        byte[] image = new byte[128 * 128 * 3]; new Random(119).NextBytes(image);
        byte[] shared = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 7 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 5 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 6 0 R /Contents 4 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 6 0 R /Contents 4 0 R >>", null),
            (6, "<< /XObject << /Unused 7 0 R >> >>", null),
            (7, "<< /Type /XObject /Subtype /Image /Width 128 /Height 128 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length " + image.Length + " >>", image)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), shared, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("second"), Fixture("unused", false, false), QpdfInputOwnership.Transfer);
        string first = reverse ? "second" : ".", last = reverse ? "." : "second";
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", removeUnreferencedResources = "auto", streamData = "uncompress", pages = new[] { new { file = first, range = "1" }, new { file = last, range = "1" } } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--remove-unreferenced-resources=auto", "--stream-data=uncompress", "--pages", first, "1", last, "1", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(2, document.PageCount);
        for (int index = 0; index < 2; index++)
        {
            PdfDictionary page = document.PageReferences[index].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject raw));
            PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(document, raw));
            int count = resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject objects) ? Assert.IsType<PdfDictionary>(Resolve(document, objects)).Count : 0;
            Assert.Equal((index == 0) == reverse ? 1 : 0, count);
        }
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Audit"), out QpdfObject original));
        Assert.Equal(image, Assert.IsType<PdfStream>(Resolve(document, original)).RawMemory.ToArray());
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    internal static List<PdfStream> LiveImages(QpdfDocument document)
    {
        List<PdfStream> images = []; HashSet<int> seen = [];
        void Visit(PdfDictionary node)
        {
            if (!node.TryGet(PdfName.FromAscii("Resources"), out QpdfObject raw) || Resolve(document, raw) is not PdfDictionary resources
                || !resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject objects) || Resolve(document, objects) is not PdfDictionary xobjects) { return; }
            foreach (PdfName name in xobjects.Keys)
            {
                Assert.True(xobjects.TryGet(name, out QpdfObject value));
                if (value is PdfReference link && !seen.Add(link.ObjectNumber)) { continue; }
                if (Resolve(document, value) is not PdfStream stream || !stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject type)) { continue; }
                if (Resolve(document, type).Equals(PdfName.FromAscii("Image"))) { images.Add(stream); }
                else if (Resolve(document, type).Equals(PdfName.FromAscii("Form"))) { Visit(stream.Dictionary); }
            }
        }
        foreach (var page in document.PageReferences) { Visit(page.Page); }
        return images;
    }
}
