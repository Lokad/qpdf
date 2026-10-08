using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineImageResourceTests
{
    private static readonly byte[] Image = "BI /W 4 /H 1 /BPC 8 /CS /G ID abcd EI\n"u8.ToArray();
    private static readonly byte[] OldImage = "z"u8.ToArray();
    private const string OldHeader = "<< /Length 1 /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray >>";
    private const string FormHeader = " /Type /XObject /Subtype /Form /BBox [0 0 10 10]";

    private static byte[] Page(byte[] content, string resources, params (int Number, string Dict, byte[]? StreamData)[] extra)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R /Resources " + resources + " >>", null),
            (4, "<< /Length " + content.Length + " >>", content)
        ];
        objects.AddRange(extra);
        return CheckTests.Assemble(objects, 1);
    }

    private static async Task<byte[]> Rewrite(byte[] input, string route, bool compress, QpdfExitCode expected)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        Dictionary<string, string> settings = new()
        {
            ["externalizeInlineImages"] = "", ["iiMinBytes"] = "0",
            ["streamData"] = "preserve", ["compressStreams"] = compress ? "y" : "n",
            ["decodeLevel"] = "none", ["objectStreams"] = "disable", ["removeUnreferencedResources"] = "no"
        };
        QpdfExecutionResult result;
        if (route == "job")
        {
            settings["inputFile"] = "inputs/a.pdf"; settings["outputFile"] = "outputs/a.pdf";
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None);
        }
        else
        {
            List<string> argv = ["inputs/a.pdf"];
            if (route == "mixed")
            {
                host.AddInput(QpdfPath.From("inputs/settings.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings)), QpdfInputOwnership.Copy);
                argv.Add("--job-json-file=inputs/settings.json");
            }
            else
            {
                argv.AddRange(["--externalize-inline-images", "--ii-min-bytes=0", "--stream-data=preserve",
                    "--compress-streams=" + (compress ? "y" : "n"), "--decode-level=none",
                    "--object-streams=disable", "--remove-unreferenced-resources=no"]);
            }
            argv.AddRange(["--", "outputs/a.pdf"]);
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(expected, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        return output;
    }

    private static async Task<QpdfDocument> Open(byte[] output)
    {
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("result.pdf"), output, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, QpdfPath.From("result.pdf"), CancellationToken.None);
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject? value) && value is not null ? value : throw new InvalidOperationException(key);
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static PdfDictionary Resources(QpdfDocument document, PdfDictionary page)
        => Assert.IsType<PdfDictionary>(Resolve(document, Get(page, "Resources")));
    private static PdfDictionary Images(QpdfDocument document, PdfDictionary page)
        => Assert.IsType<PdfDictionary>(Resolve(document, Get(Resources(document, page), "XObject")));
    private static PdfStream Stream(QpdfDocument document, PdfDictionary dictionary, string key)
        => Assert.IsType<PdfStream>(Resolve(document, Get(dictionary, key)));
    private static byte[] Decoded(PdfStream stream)
    {
        if (!stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject? filter) || filter is PdfNull) { return stream.Data; }
        Assert.Equal(PdfName.FromAscii("FlateDecode"), Assert.IsType<PdfName>(filter));
        return FlateFilter.Decode(stream.Data, null, QpdfLimits.Default, new(QpdfLimits.Default, CancellationToken.None));
    }

    [Theory]
    [InlineData("indirect", "argv")]
    [InlineData("indirect", "job")]
    [InlineData("indirect", "mixed")]
    [InlineData("null", "argv")]
    [InlineData("null", "job")]
    [InlineData("null", "mixed")]
    [InlineData("font", "argv")]
    [InlineData("font", "job")]
    [InlineData("font", "mixed")]
    public async Task NamesRespectAllResourceCategoriesAndIgnoreNullEntries(string shape, string route)
    {
        string resources = shape switch
        {
            "indirect" => "<< /XObject 6 0 R >>",
            "null" => "<< /XObject << /IIm1 null /IIm3 null >> >>",
            _ => "<< /Font << /IIm1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>"
        };
        byte[] content = shape == "font" ? Image : [.. Image, .. Image, .. Image];
        byte[] input = shape == "indirect"
            ? Page(content, resources, (5, OldHeader, OldImage), (6, "<< /IIm1 5 0 R /IIm3 5 0 R >>", null))
            : Page(content, resources);
        await using QpdfDocument document = await Open(await Rewrite(input, route, false, QpdfExitCode.Success));
        PdfDictionary page = document.PageReferences[0].Page, images = Images(document, page);
        string expected = shape == "indirect" ? "/IIm2 Do\n\n/IIm4 Do\n\n/IIm5 Do\n\n"
            : shape == "font" ? "/IIm2 Do\n\n" : "/IIm1 Do\n\n/IIm2 Do\n\n/IIm3 Do\n\n";
        Assert.Equal(expected, Encoding.ASCII.GetString(Decoded(Stream(document, page, "Contents"))));
        Assert.Equal(shape == "indirect" ? 5 : shape == "font" ? 1 : 3, images.Count);
        if (shape == "indirect")
        {
            Assert.Equal(OldImage, Stream(document, images, "IIm1").Data);
            Assert.Equal(Get(images, "IIm1"), Get(images, "IIm3"));
        }
        else if (shape == "font") { Assert.IsType<PdfDictionary>(Get(Resources(document, page), "Font")); }
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task SparseColorspaceReferenceKeepsItsCopiedDestinationIdentity(string route)
    {
        List<(int Number, string Dict, byte[]? StreamData)> extra = [];
        for (int number = 5; number < 17; number++) { extra.Add((number, "null", null)); }
        extra.Add((17, "[/Indexed /DeviceGray 0 <00>]", null));
        byte[] content = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Image).Replace("/CS /G", "/CS /Local", StringComparison.Ordinal));
        byte[] input = Page(content, "<< /ColorSpace << /Local 17 0 R >> >>", [.. extra]);
        await using QpdfDocument document = await Open(await Rewrite(input, route, false, QpdfExitCode.Success));
        PdfDictionary resources = Resources(document, document.PageReferences[0].Page);
        PdfDictionary colors = Assert.IsType<PdfDictionary>(Get(resources, "ColorSpace"));
        PdfStream image = Stream(document, Images(document, document.PageReferences[0].Page), "IIm1");
        Assert.Equal(Get(colors, "Local"), Get(image.Dictionary, "ColorSpace"));
        PdfArray indexed = Assert.IsType<PdfArray>(Resolve(document, Get(image.Dictionary, "ColorSpace")));
        Assert.Equal(PdfName.FromAscii("Indexed"), indexed[0]); Assert.Equal(PdfName.FromAscii("DeviceGray"), indexed[1]);
        Assert.Equal("abcd "u8.ToArray(), image.Data);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task FormUsesItsIndirectResourceAndColorspaceDictionaries(string route)
    {
        byte[] content = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Image).Replace("/CS /G", "/CS /Local", StringComparison.Ordinal));
        byte[] input = Page("/Fm1 Do\n"u8.ToArray(), "<< /XObject << /Fm1 5 0 R >> >>",
            (5, "<< /Length " + content.Length + FormHeader + " /Resources 6 0 R >>", content),
            (6, "<< /ColorSpace 7 0 R >>", null), (7, "<< /Local 8 0 R >>", null), (8, "/DeviceGray", null));
        await using QpdfDocument document = await Open(await Rewrite(input, route, false, QpdfExitCode.Success));
        PdfStream form = Stream(document, Images(document, document.PageReferences[0].Page), "Fm1");
        PdfStream image = Stream(document, Images(document, form.Dictionary), "IIm1");
        Assert.Equal(PdfName.FromAscii("DeviceGray"), Resolve(document, Get(image.Dictionary, "ColorSpace")));
        Assert.Equal("/IIm1 Do\n\n", Encoding.ASCII.GetString(Decoded(form)));
        Assert.Equal("abcd "u8.ToArray(), image.Data);
    }

    [Theory]
    [InlineData("resources", "argv")]
    [InlineData("resources", "job")]
    [InlineData("resources", "mixed")]
    [InlineData("content", "argv")]
    [InlineData("content", "job")]
    [InlineData("content", "mixed")]
    public async Task RewritingOnePagePreservesSharedResourcesAndProcessesSharedContentIndependently(string shared, string route)
    {
        bool resources = shared == "resources";
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 5 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R /Resources " + (resources ? "7 0 R" : "<< /XObject << /IIm1 8 0 R >> >>") + " >>", null),
            (4, "<< /Length " + Image.Length + " >>", Image),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents " + (resources ? "6" : "4") + " 0 R /Resources " + (resources ? "7 0 R" : "<< >>") + " >>", null),
            (6, "<< /Length 4 >>", "q Q\n"u8.ToArray()), (7, "<< /XObject << /Old 8 0 R >> >>", null), (8, OldHeader, OldImage)
        ], 1);
        await using QpdfDocument document = await Open(await Rewrite(input, route, false, QpdfExitCode.Success));
        PdfDictionary first = document.PageReferences[0].Page, second = document.PageReferences[1].Page;
        Assert.Equal(resources ? "/IIm1 Do\n\n" : "/IIm2 Do\n\n", Encoding.ASCII.GetString(Decoded(Stream(document, first, "Contents"))));
        Assert.Equal(resources ? "q Q\n" : "/IIm1 Do\n\n", Encoding.ASCII.GetString(Decoded(Stream(document, second, "Contents"))));
        Assert.NotEqual(Get(first, "Contents"), Get(second, "Contents"));
        Assert.Equal(1, Images(document, second).Count);
        Assert.Equal(resources ? OldImage : "abcd "u8.ToArray(), Stream(document, Images(document, second), resources ? "Old" : "IIm1").Data);
    }

    [Theory]
    [InlineData(false, "argv")]
    [InlineData(false, "job")]
    [InlineData(false, "mixed")]
    [InlineData(true, "argv")]
    [InlineData(true, "job")]
    [InlineData(true, "mixed")]
    public async Task RewrittenFormDropsOldPredictorParameters(bool compress, string route)
    {
        using MemoryStream encoded = new();
        using (ZLibStream encoder = new(encoded, CompressionLevel.Optimal, true)) { encoder.WriteByte(0); encoder.Write(Image); }
        byte[] data = encoded.ToArray();
        byte[] input = Page("/Fm1 Do\n"u8.ToArray(), "<< /XObject << /Fm1 5 0 R >> >>",
            (5, "<< /Length " + data.Length + FormHeader + " /Resources << >> /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns " + Image.Length + " >> >>", data));
        await using QpdfDocument document = await Open(await Rewrite(input, route, compress, QpdfExitCode.Success));
        PdfStream form = Stream(document, Images(document, document.PageReferences[0].Page), "Fm1");
        Assert.False(form.Dictionary.TryGet(PdfName.FromAscii("DecodeParms"), out _));
        Assert.Equal(compress && route == "argv", form.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
        Assert.Equal("/IIm1 Do\n\n", Encoding.ASCII.GetString(Decoded(form)));
        Assert.Equal("abcd "u8.ToArray(), Stream(document, Images(document, form.Dictionary), "IIm1").Data);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task ReferenceWrittenInsideInlineDictionaryWarnsAndKeepsOriginalContent(string route)
    {
        byte[] content = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Image).Replace("/W 4", "/W 5 0 R", StringComparison.Ordinal));
        byte[] input = Page(content, "<< >>", (5, "4", null));
        await using QpdfDocument document = await Open(await Rewrite(input, route, false, QpdfExitCode.Warning));
        PdfDictionary page = document.PageReferences[0].Page;
        Assert.Equal(content, Stream(document, page, "Contents").Data);
        Assert.Empty(Assert.IsType<PdfDictionary>(Get(Resources(document, page), "XObject")).Keys);
    }
}
