using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class CompressionSpecialStreamTests
{
    private static readonly byte[] Xml = "<x:xmpmeta xmlns:x='adobe:ns:meta/'>test</x:xmpmeta>"u8.ToArray();

    private static byte[] MetadataFixture(string type, string subtype)
    {
        byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToHexString(Xml) + ">");
        return CheckTests.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R /Metadata 6 0 R /Resources << >> >>", null),
            (4, "<< /Length 4 >>", "q Q\n"u8.ToArray()),
            (5, "<< /Length " + encoded.Length + " /Type " + type + " /Subtype " + subtype + " /Filter /ASCIIHexDecode >>", encoded),
            (6, "<< /Length " + encoded.Length + " /Type " + type + " /Subtype " + subtype + " /Filter /ASCIIHexDecode >>", encoded),
            (7, "/Metadata", null),
            (8, "/XML", null),
        ], 1);
    }

    private static byte[] EmptyFixture() => CheckTests.Assemble(
    [
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R /Resources << >> >>", null),
        (4, "<< /Length 0 >>", Array.Empty<byte>()),
    ], 1);

    private static async Task<QpdfDocument> Rewrite(byte[] fixture, string route, string mode)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), fixture, QpdfInputOwnership.Copy);
        Dictionary<string, string> settings = new() { ["objectStreams"] = "disable" };
        List<string> flags = ["--object-streams=disable"];
        if (mode == "preserve") { settings["streamData"] = "preserve"; flags.Add("--stream-data=preserve"); }
        if (mode == "none") { settings["decodeLevel"] = "none"; flags.Add("--decode-level=none"); }
        QpdfExecutionResult result;
        if (route == "job" || route == "chain")
        {
            Dictionary<string, string> job = route == "chain" ? new() { ["jobJsonFile"] = "settings.json" } : settings;
            if (route == "chain") { host.AddInput(QpdfPath.From("settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings), QpdfInputOwnership.Copy); }
            job["inputFile"] = "input.pdf"; job["outputFile"] = "output.pdf";
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        }
        else
        {
            List<string> argv = ["input.pdf"];
            if (route == "mixed")
            {
                host.AddInput(QpdfPath.From("settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings), QpdfInputOwnership.Copy);
                argv.Add("--job-json-file=settings.json");
            }
            else { argv.AddRange(flags); }
            argv.Add("output.pdf");
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("rewritten.pdf"), output, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, QpdfPath.From("rewritten.pdf"), CancellationToken.None);
    }

    private static PdfStream Metadata(QpdfDocument document, PdfDictionary dictionary)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii("Metadata"), out QpdfObject metadata));
        return Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(metadata).ObjectNumber));
    }

    public static IEnumerable<object[]> MetadataCases()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "chain" })
        foreach (string mode in new[] { "default", "preserve" })
        foreach (string type in new[] { "/Metadata", "7 0 R" })
        foreach (string subtype in new[] { "/XML", "8 0 R" })
        { yield return [route, mode, type, subtype]; }
    }

    [Theory]
    [MemberData(nameof(MetadataCases))]
    public async Task CatalogXmlIsUncompressedWhilePageXmlFollowsSettings(string route, string mode, string type, string subtype)
    {
        await using QpdfDocument document = await Rewrite(MetadataFixture(type, subtype), route, mode);
        PdfStream catalog = Metadata(document, Assert.IsType<PdfDictionary>(document.GetObject(1)));
        Assert.False(catalog.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
        Assert.Equal(Xml, catalog.Data);
        PdfStream page = Metadata(document, document.PageReferences[0].Page);
        Assert.True(page.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
        Assert.Equal(mode == "preserve" ? "ASCIIHexDecode" : "FlateDecode", Assert.IsType<PdfName>(filter).DisplayText);
        Assert.Equal(Xml, StreamObservation.Decode(page));
    }

    public static IEnumerable<object[]> MalformedMetadataCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string type in new[] { "null", "(Metadata)", "8 0 R" })
        { yield return [route, type, "/XML"]; }
        foreach (string route in new[] { "argv", "job" })
        foreach (string subtype in new[] { "null", "(XML)", "7 0 R" })
        { yield return [route, "/Metadata", subtype]; }
    }

    [Theory]
    [MemberData(nameof(MalformedMetadataCases))]
    public async Task MalformedMetadataNamesFollowOrdinaryCompression(string route, string type, string subtype)
    {
        await using QpdfDocument document = await Rewrite(MetadataFixture(type, subtype), route, "default");
        PdfStream catalog = Metadata(document, Assert.IsType<PdfDictionary>(document.GetObject(1)));
        Assert.True(catalog.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(filter).DisplayText);
        Assert.Equal(Xml, StreamObservation.Decode(catalog));
    }

    [Theory]
    [InlineData("argv", true)]
    [InlineData("argv", false)]
    [InlineData("job", true)]
    [InlineData("job", false)]
    public async Task DanglingMetadataNamesPreservePriorOutput(string route, bool type)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), MetadataFixture(type ? "99 0 R" : "/Metadata", type ? "/XML" : "99 0 R"), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\"}", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.NotEmpty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", "default")]
    [InlineData("job", "default")]
    [InlineData("mixed", "default")]
    [InlineData("chain", "default")]
    [InlineData("argv", "preserve")]
    [InlineData("job", "preserve")]
    [InlineData("mixed", "preserve")]
    [InlineData("chain", "preserve")]
    [InlineData("argv", "none")]
    [InlineData("job", "none")]
    [InlineData("mixed", "none")]
    [InlineData("chain", "none")]
    public async Task EmptyStreamsKeepZeroLengthWithoutEncoding(string route, string mode)
    {
        await using QpdfDocument document = await Rewrite(EmptyFixture(), route, mode);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(contents).ObjectNumber));
        Assert.False(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
        Assert.Empty(stream.Data);
    }
}
