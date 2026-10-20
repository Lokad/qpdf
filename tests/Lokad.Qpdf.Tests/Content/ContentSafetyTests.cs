using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ContentSafetyTests
{
    internal static byte[] Fixture(byte[] payload, string encoding, bool indirect, int pages)
    {
        int first = 3 + pages, second = first + 1, array = first + 2;
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count " + pages + " /Kids [" +
                string.Join(" ", Enumerable.Range(3, pages).Select(id => id + " 0 R")) + "] >>", null),
        ];
        string contents = "[" + first + " 0 R " + second + " 0 R]";
        for (int page = 0; page < pages; page++)
        { objects.Add((3 + page, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents " + (indirect ? array + " 0 R" : contents) + " >>", null)); }
        objects.Add((first, "<< /Length 3 >>", "q Q"u8.ToArray()));
        objects.Add((second, "<< /Length " + payload.Length + " " + encoding + " >>", payload));
        objects.Add((array, contents, null));
        return Inspection.CheckTests.Assemble(objects, 1);
    }

    private static ValueTask<QpdfExecutionResult> Run(InMemoryQpdfHost host, string mode, bool job, QpdfLimits limits)
    {
        QpdfExecutionOptions options = new(limits);
        if (job)
        {
            Dictionary<string, object> settings = new()
            {
                ["inputFile"] = "input", ["outputFile"] = "output", ["decodeLevel"] = "none",
                ["compressStreams"] = "n", ["removeUnreferencedResources"] = "no", ["iiMinBytes"] = 0,
                [mode switch { "coalesce" => "coalesceContents", "normalize" => "normalizeContent", _ => "externalizeInlineImages" }] = mode == "normalize" ? "y" : true,
            };
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), options, CancellationToken.None);
        }
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input",
            mode switch { "coalesce" => "--coalesce-contents", "normalize" => "--normalize-content=y", _ => "--externalize-inline-images" },
            "--ii-min-bytes=0", "--decode-level=none", "--compress-streams=n", "--remove-unreferenced-resources=no", "output"]), options, CancellationToken.None);
    }

    private static List<PdfStream> Streams(QpdfDocument document, int page)
    {
        QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
        Assert.True(document.PageReferences[page].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
        QpdfObject contents = Resolve(raw);
        return contents is PdfArray array ? array.Items.Select(item => Assert.IsType<PdfStream>(Resolve(item))).ToList()
            : [Assert.IsType<PdfStream>(contents)];
    }

    [Theory]
    [InlineData("coalesce", false)]
    [InlineData("coalesce", true)]
    [InlineData("normalize", false)]
    [InlineData("normalize", true)]
    [InlineData("externalize", false)]
    [InlineData("externalize", true)]
    public async Task OpaqueAndDamagedEncodingsKeepTheirBytesAndHeaders(string mode, bool job)
    {
        byte[] payload = "q\rQ BI /W 1 /H 1 /BPC 8 /CS /G ID X EI"u8.ToArray();
        foreach (string encoding in new[] { "/Filter /UnknownFilter", "/Filter /DCTDecode", "/Filter 42", "/Filter /FlateDecode" })
        {
            using InMemoryQpdfHost host = new();
            host.AddInput(QpdfPath.From("input"), Fixture(payload, encoding, false, 1), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = await Run(host, mode, job, QpdfLimits.Default);
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode); Assert.NotEmpty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
            PdfStream retained = Streams(document, 0)[1]; Assert.Equal(payload, retained.Data);
            Assert.True(retained.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
            if (encoding == "/Filter 42") { Assert.Equal(42L, Assert.IsType<PdfInteger>(filter).Value); }
            else { Assert.Contains(Assert.IsType<PdfName>(filter).DisplayText, encoding); }
        }
    }

    [Theory]
    [InlineData("coalesce")]
    [InlineData("normalize")]
    [InlineData("externalize")]
    public async Task FilterCeilingPreservesEncodedContents(string mode)
    {
        using InMemoryQpdfHost host = new(); byte[] encoded = "710D51>"u8.ToArray();
        host.AddInput(QpdfPath.From("input"), Fixture(encoded, "/Filter /ASCIIHexDecode", false, 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, mode, false, QpdfLimits.Default with { MaxStreamFilters = 0 });
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Contains("/ASCIIHexDecode", Encoding.ASCII.GetString(output)); Assert.Contains("710D51>", Encoding.ASCII.GetString(output));
    }
}
