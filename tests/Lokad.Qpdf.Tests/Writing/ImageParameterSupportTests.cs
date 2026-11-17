using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ImageParameterSupportTests
{
    private static byte[] Fixture(string filter, string parameters, bool indirect, byte[] data)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << /Im 4 0 R >> >> >>", null),
            (4, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /" + filter
                + " /DecodeParms " + (indirect ? "5 0 R" : parameters) + " /Length " + data.Length + " >>", data)];
        if (indirect) { objects.Add((5, parameters, null)); }
        return PdfTestDocument.Assemble(objects, 1);
    }

    [Theory]
    [InlineData("typed", 2, "FlateDecode", "<< /Predictor 99 >>", false, "789C73040000420042")]
    [InlineData("job", 1, "RunLengthDecode", "<< >>", false, "004180")]
    [InlineData("argv", 2, "LZWDecode", "<< /EarlyChange 2 >>", true, "80106020")]
    public async Task UnsupportedParametersReportFalseAndRetainRawStreamFallback(string route, int version, string filter, string parameters, bool indirect, string hex)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 1 };
        using InMemoryQpdfHost host = new(limits);
        byte[] encoded = Convert.FromHexString(hex);
        host.AddInput(QpdfPath.From("input"), Fixture(filter, parameters, indirect, encoded), QpdfInputOwnership.Transfer);
        string[] keys = ["pages", version == 2 ? "qpdf" : "objects"];
        string text;
        long decoded;
        if (route == "typed")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("report"),
                new QpdfJsonOptions(version, keys, "inline", null, [], false), limits, CancellationToken.None);
            text = result.Json; decoded = result.Counters.TotalDecodedBytes;
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
                { inputFile = "input", outputFile = "report", json = "1", jsonKey = keys, jsonStreamData = "inline", decodeLevel = "all" }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=pages", "--json-key=qpdf", "--json-stream-data=inline", "--", "report"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); decoded = result.Counters.TotalDecodedBytes;
            Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] bytes)); text = System.Text.Encoding.UTF8.GetString(bytes);
        }
        using JsonDocument parsed = JsonDocument.Parse(text);
        JsonElement image = Assert.Single(parsed.RootElement.GetProperty("pages")[0].GetProperty("images").EnumerateArray());
        Assert.False(image.GetProperty("filterable").GetBoolean());
        JsonElement dictionary;
        if (version == 2)
        {
            JsonElement stream = parsed.RootElement.GetProperty("qpdf")[1].GetProperty("obj:4 0 R").GetProperty("stream");
            Assert.Equal(encoded, Convert.FromBase64String(stream.GetProperty("data").GetString() ?? throw new InvalidOperationException()));
            dictionary = stream.GetProperty("dict");
        }
        else { dictionary = parsed.RootElement.GetProperty("objects").GetProperty("4 0 R"); }
        Assert.Equal("/" + filter, dictionary.GetProperty("/Filter").GetString());
        if (indirect) { Assert.Equal("5 0 R", dictionary.GetProperty("/DecodeParms").GetString()); }
        else { Assert.Equal(JsonValueKind.Object, dictionary.GetProperty("/DecodeParms").ValueKind); }
        Assert.Equal(0L, decoded); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EligibleMetadataDoesNotClaimFrameIntegrity()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("FlateDecode", "<< >>", false, [0]), QpdfInputOwnership.Transfer);
        QpdfJsonResult report = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None);
        using JsonDocument parsed = JsonDocument.Parse(report.Json);
        Assert.True(parsed.RootElement.GetProperty("pages")[0].GetProperty("images")[0].GetProperty("filterable").GetBoolean());
        Assert.Equal(0L, report.Counters.TotalDecodedBytes);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("payload"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult decode = await Qpdf.ExecuteAsync(host, new(["input", "--show-object=4", "--filtered-stream-data", "--", "payload"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, decode.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
