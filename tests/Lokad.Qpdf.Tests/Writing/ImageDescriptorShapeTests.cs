using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ImageDescriptorShapeTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return ["", "", "[null]", "[null]"];
        yield return ["null", "null", "[null]", "[null]"];
        yield return ["[]", "", "[]", "[]"];
        yield return ["/Fl", "", """["/Fl"]""", "[null]"];
        yield return ["/Fl", "<< /Predictor 1 >>", """["/Fl"]""", """[{"/Predictor":1}]"""];
        yield return ["[/AHx /Fl]", "", """["/AHx","/Fl"]""", "[null,null]"];
        yield return ["[/AHx /Fl]", "[null << /Predictor 1 >>]", """["/AHx","/Fl"]""", """[null,{"/Predictor":1}]"""];
        yield return ["8 7 R", "", """["8 7 R"]""", "[null]"];
        yield return ["5 7 R", "", "\"5 7 R\"", "[null,null]"];
        yield return ["[8 7 R]", "6 7 R", """["8 7 R"]""", """["6 7 R"]"""];
        yield return ["/Fl", "6 7 R", """["/Fl"]""", """["6 7 R"]"""];
        yield return ["[/AHx /Fl]", "10 7 R", """["/AHx","/Fl"]""", "\"10 7 R\""];
        yield return ["/Fl", "[]", """["/Fl"]""", "[]"];
        yield return ["/Fl", "7 7 R", """["/Fl"]""", "\"7 7 R\""];
        yield return ["/Fl", "9 7 R", """["/Fl"]""", """["9 7 R"]"""];
        yield return ["9 7 R", "", """["9 7 R"]""", "[null]"];
        yield return ["8 8 R", "", "[null]", "[null]"];
        yield return ["/Fl", "6 8 R", """["/Fl"]""", "[null]"];
        yield return ["[/Fl]", "[6 8 R]", """["/Fl"]""", "[null]"];
        yield return ["/Fl", "<< /Predictor 9 7 R >>", """["/Fl"]""", "[{}]"];
        yield return ["/Fl", "<< /Predictor 6 8 R >>", """["/Fl"]""", "[{}]"];
        yield return ["/Fl", "<< /Unknown [6 8 R 9 7 R] >>", """["/Fl"]""", """[{"/Unknown":[null,"9 7 R"]}]"""];
        yield return ["[/Fl]", "[null]", """["/Fl"]""", "[null]"];
        yield return ["[]", "<< /Predictor 1 >>", "[]", "[]"];
        yield return ["[/Fl]", "[6 7 R]", """["/Fl"]""", """["6 7 R"]"""];
    }

    private static byte[] Fixture(string filter, string parameters)
    {
        bool chain = filter.Contains("AHx", StringComparison.Ordinal) || filter == "5 7 R";
        bool raw = filter is "" or "null" or "[]" or "9 7 R" or "8 8 R";
        byte[] data = raw ? "A"u8.ToArray() : chain ? "789C73040000420042>"u8.ToArray() : Convert.FromHexString("789C73040000420042");
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << /Im 4 0 R >> >> >>", null),
            (4, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray "
                + (filter.Length == 0 ? "" : "/Filter " + filter) + (parameters.Length == 0 ? "" : " /DecodeParms " + parameters)
                + " /Length " + data.Length + " >>", data),
            (5, "[/AHx /Fl]", null), (6, "<< /Predictor 1 >>", null), (7, "[]", null),
            (8, "/Fl", null), (9, "null", null), (10, "[null 6 7 R]", null)], 1,
            Enumerable.Range(5, 6).ToDictionary(number => number, _ => 7));
    }

    private static void EqualJson(string expected, JsonElement actual)
    {
        using JsonDocument parsed = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(parsed.RootElement, actual), actual.GetRawText());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DescriptorsRetainRawNamesReferencesAndOuterShapes(string filter, string parameters, string expectedFilter, string expectedParameters)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(filter, parameters), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["pages", "qpdf"]), CancellationToken.None);
        using JsonDocument report = JsonDocument.Parse(result.Json);
        JsonElement image = report.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        EqualJson(expectedFilter, image.GetProperty("filter")); EqualJson(expectedParameters, image.GetProperty("decodeparms"));
        Assert.True(image.GetProperty("filterable").GetBoolean()); Assert.Equal("4 0 R", image.GetProperty("object").GetString());
        JsonElement dictionary = report.RootElement.GetProperty("qpdf")[1].GetProperty("obj:4 0 R").GetProperty("stream").GetProperty("dict");
        if (filter is "8 8 R" or "9 7 R") { Assert.False(dictionary.TryGetProperty("/Filter", out _)); }
        else if (filter.EndsWith(" R", StringComparison.Ordinal)) { Assert.Equal(filter, dictionary.GetProperty("/Filter").GetString()); }
        if (parameters is "6 8 R" or "9 7 R") { Assert.False(dictionary.TryGetProperty("/DecodeParms", out _)); }
        else if (parameters.EndsWith(" R", StringComparison.Ordinal)) { Assert.Equal(parameters, dictionary.GetProperty("/DecodeParms").GetString()); }
        Assert.Equal(0L, result.Counters.TotalDecodedBytes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task JobVersionOneKeepsBothOuterReferencesAndTheirFullPairs()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("5 7 R", "10 7 R"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","json":"1","jsonKey":["pages","objects"]}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        JsonElement image = report.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        Assert.Equal("5 7 R", image.GetProperty("filter").GetString()); Assert.Equal("10 7 R", image.GetProperty("decodeparms").GetString());
        Assert.True(image.GetProperty("filterable").GetBoolean());
        JsonElement objects = report.RootElement.GetProperty("objects");
        Assert.True(objects.TryGetProperty("5 7 R", out _)); Assert.True(objects.TryGetProperty("10 7 R", out _));
        Assert.False(objects.TryGetProperty("5 0 R", out _));
        Assert.Equal(0L, result.Counters.TotalDecodedBytes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("6 7 R", "")]
    [InlineData("/Fl", "8 7 R")]
    [InlineData("[8 8 R]", "")]
    [InlineData("", "42")]
    public async Task MalformedSpecificationsStillRefuseBeforeReplacingTheReport(string filter, string parameters)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(filter, parameters), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("report"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--json=2", "--json-key=pages", "--", "report"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssembledAndCompressedSourcesKeepTheirLiveDescriptorReferences(bool compressed)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("5 7 R", "10 7 R"), QpdfInputOwnership.Transfer);
        string text;
        if (compressed)
        {
            QpdfExecutionResult packed = await Qpdf.ExecuteAsync(host,
                new(["input", "--object-streams=generate", "--stream-data=preserve", "--compress-streams=n", "--decode-level=none", "packed"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, packed.ExitCode); Assert.True(host.TryGetOutput(QpdfPath.From("packed"), out byte[] pdf));
            host.AddInput(QpdfPath.From("packed-input"), pdf, QpdfInputOwnership.Transfer);
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("packed-input"), new QpdfJsonOptions(2, ["pages", "qpdf"]), CancellationToken.None);
            text = result.Json;
        }
        else
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
                new(["--empty", "--pages", "input", "1", "--", "--json=2", "--json-key=pages", "--json-key=qpdf"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); text = result.OutputText ?? throw new InvalidOperationException();
        }
        using JsonDocument parsed = JsonDocument.Parse(text);
        JsonElement image = parsed.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        JsonElement objects = parsed.RootElement.GetProperty("qpdf")[1];
        JsonElement dictionary = objects.GetProperty("obj:" + image.GetProperty("object").GetString()).GetProperty("stream").GetProperty("dict");
        foreach ((string field, string key) in new[] { ("filter", "/Filter"), ("decodeparms", "/DecodeParms") })
        {
            string reference = image.GetProperty(field).GetString() ?? throw new InvalidOperationException();
            Assert.Equal(dictionary.GetProperty(key).GetString(), reference);
            Assert.Equal(JsonValueKind.Array, objects.GetProperty("obj:" + reference).GetProperty("value").ValueKind);
        }
        Assert.True(image.GetProperty("filterable").GetBoolean()); Assert.Equal(0L, host.OpenLeaseCount);
        if (compressed)
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("packed-input"), CancellationToken.None);
            int number = int.Parse((image.GetProperty("filter").GetString() ?? throw new InvalidOperationException()).Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[number].Kind);
        }
    }
}
