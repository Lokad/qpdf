using System.Text.Json;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class TextEncodingRoundTripTests
{
    public static IEnumerable<object[]> JsonTextCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach ((string text, string hex) in new[]
        {
            ("þÿA", "FEFF00FE00FF0041"), ("ÿþA", "FEFF00FF00FE0041"), ("ï»¿A", "FEFF00EF00BB00BF0041"),
            ("þ", "FE"), ("ï»", "EFBB"), ("Aþÿ", "41FEFF"), ("Aï»¿", "41EFBBBF"),
            ("A-", "412D"), ("AÉ", "41C9"), ("日本", "FEFF65E5672C"),
            ("A\u0018B", "FEFF004100180042"), ("A\u007fB", "FEFF0041007F0042"),
        }) { yield return [route, text, hex]; }
    }

    private static byte[] JsonInput(string text)
    {
        Dictionary<string, object> catalog = new() { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R", ["/ReviewText"] = "u:" + text };
        Dictionary<string, object> objects = new()
        {
            ["obj:1 0 R"] = new { value = catalog },
            ["obj:2 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Count"] = 0, ["/Kids"] = Array.Empty<object>() } },
            ["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = "1 0 R", ["/Size"] = 3 } },
        };
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.7" }, objects } });
    }

    [Theory]
    [MemberData(nameof(JsonTextCases))]
    public async Task JsonTextPreservesUnicodeAndUnambiguousBytes(string route, string text, string hex)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), JsonInput(text), QpdfInputOwnership.Transfer);
        if (route == "typed")
        { await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json-input", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","jsonInput":""}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] pdf));
        host.AddInput(QpdfPath.From("readback"), pdf, QpdfInputOwnership.Transfer);
        PdfByteString escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("ReviewText"), out QpdfObject value));
            escaped = Assert.IsType<PdfByteString>(value);
        }
        Assert.Equal(0L, host.OpenLeaseCount); host.Dispose();
        ResourceGovernor decoding = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope decoded = ParserMemoryScope.Create(decoding);
        Assert.Equal(text, PdfText.Decode(escaped.ValueMemory, decoded, decoding, CancellationToken.None)); Assert.Equal(hex, Convert.ToHexString(escaped.Value));
    }
}
