using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonDepthRoundTripTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string envelope in new[] { "value", "stream", "trailer" })
        foreach (int depth in new[] { 123, 124, 160, 256, 499 })
        foreach (bool update in new[] { false, true })
        foreach (string route in depth == 124 ? new[] { "typed", "argv", "job" } : new[] { "typed" })
        { yield return [envelope, depth, update, route]; }
    }

    private static string Json(string envelope, int depth)
    {
        int arrays = envelope == "trailer" ? depth - 1 : depth;
        string nested = arrays == 0 ? "0" : new string('[', arrays) + new string(']', arrays);
        string review = ",\"/Review\":" + nested;
        return "{\"qpdf\":[{\"jsonversion\":2,\"pdfversion\":\"1.7\"},{"
            + "\"obj:1 0 R\":{\"value\":{\"/Type\":\"/Catalog\",\"/Pages\":\"2 0 R\",\"/Probe\":\"4 0 R\"" + (envelope == "value" ? review : "") + "}},"
            + "\"obj:2 0 R\":{\"value\":{\"/Type\":\"/Pages\",\"/Count\":1,\"/Kids\":[\"3 0 R\"]}},"
            + "\"obj:3 0 R\":{\"value\":{\"/Type\":\"/Page\",\"/Parent\":\"2 0 R\",\"/MediaBox\":[0,0,100,100],\"/Resources\":{}}},"
            + "\"obj:4 0 R\":{\"stream\":{\"dict\":{\"/Type\":\"/Review\"" + (envelope == "stream" ? review : "") + "},\"data\":\"\"}},"
            + "\"trailer\":{\"value\":{\"/Root\":\"1 0 R\"" + (envelope == "trailer" ? ",\"/Info\":{\"/Review\":" + nested + "}" : "") + "}}}]}";
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SupportedValuesRoundTripThroughEveryEnvelope(string envelope, int depth, bool update, string route)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 499 };
        using InMemoryQpdfHost host = new(limits);
        QpdfPath json = QpdfPath.From("json"), input = QpdfPath.From("input"), output = QpdfPath.From("output");
        host.AddInput(json, Encoding.UTF8.GetBytes(Json(envelope, depth)), QpdfInputOwnership.Transfer);
        if (update)
        {
            host.AddInput(QpdfPath.From("seed"), Encoding.UTF8.GetBytes(Json(envelope, 1)), QpdfInputOwnership.Transfer);
            await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("seed"), QpdfPath.From("seed-output"), limits, CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("seed-output"), out byte[] seed)); host.AddInput(input, seed, QpdfInputOwnership.Transfer);
        }
        if (route == "typed")
        {
            if (update) { await Qpdf.UpdatePdfFromJsonAsync(host, input, json, output, limits, CancellationToken.None); }
            else { await Qpdf.CreatePdfFromJsonAsync(host, json, output, limits, CancellationToken.None); }
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(update ? ["input", "--update-from-json=json", "--", "output"]
                    : ["json", "--json-input", "--", "output"]), new QpdfExecutionOptions(limits), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, update
                    ? """{"inputFile":"input","outputFile":"output","updateFromJson":"json"}"""
                    : """{"inputFile":"json","outputFile":"output","jsonInput":""}""", new QpdfExecutionOptions(limits), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(output, out byte[] bytes)); host.AddInput(input, bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument doc = await QpdfDocument.OpenAsync(host, input, limits, CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(doc.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("Probe"), out QpdfObject link));
            PdfDictionary dictionary;
            if (envelope == "value") { dictionary = catalog; }
            else if (envelope == "trailer")
            {
                Assert.True(doc.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject info));
                dictionary = Assert.IsType<PdfDictionary>(doc.GetObject(Assert.IsType<PdfReference>(info).ObjectNumber));
            }
            else { dictionary = Assert.IsType<PdfStream>(doc.GetObject(Assert.IsType<PdfReference>(link).ObjectNumber)).Dictionary; }
            Assert.True(dictionary.TryGet(PdfName.FromAscii("Review"), out QpdfObject value));
            int arrays = envelope == "trailer" ? depth - 1 : depth;
            for (int index = 0; index < arrays; index++)
            {
                PdfArray array = Assert.IsType<PdfArray>(value);
                if (index == arrays - 1) { Assert.Empty(array.Items); }
                else { Assert.Single(array.Items); value = array[0]; }
            }
        }
        QpdfExecutionResult exported = await Qpdf.ExecuteAsync(host, new(["input", "--json-output"]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, exported.ExitCode); Assert.NotNull(exported.OutputText);
        host.AddInput(json, Encoding.UTF8.GetBytes(exported.OutputText), QpdfInputOwnership.Transfer);
        await Qpdf.CreatePdfFromJsonAsync(host, json, QpdfPath.From("roundtrip"), limits, CancellationToken.None);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("stream")]
    [InlineData("trailer")]
    public async Task SyntaxAllowanceDoesNotRaiseTheConfiguredModelBudget(string envelope)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 64 };
        using InMemoryQpdfHost host = new(limits);
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(Json(envelope, 65)), QpdfInputOwnership.Transfer);
        QpdfLimitException error = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), limits, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), error.LimitName);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _));
    }

    [Fact]
    public async Task JsonSyntaxStillHasAFiniteCeiling()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 499 };
        using InMemoryQpdfHost host = new(limits);
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(Json("stream", 500)), QpdfInputOwnership.Transfer);
        QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() =>
            Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), limits, CancellationToken.None).AsTask());
        Assert.Contains("depth of 505", error.Message, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _));
    }
}
