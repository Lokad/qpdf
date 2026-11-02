using System.IO.Compression;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ImageFilterLimitTests
{
    private static byte[] Fixture(int count, bool indirect)
    {
        byte[] data = [127];
        for (int i = 0; i < count; i++)
        {
            using MemoryStream buffer = new();
            using (ZLibStream compressor = new(buffer, CompressionLevel.Fastest, true)) { compressor.Write(data); }
            data = buffer.ToArray();
        }
        string filters = count == 1 ? "/FlateDecode" : "[" + string.Join(" ", Enumerable.Repeat("/FlateDecode", count)) + "]";
        string entry = count == 0 ? "" : "/Filter " + (indirect ? "6 0 R" : filters);
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /Im 5 0 R >> >> >>", null),
            (4, "<< /Length 7 >>", "/Im Do\n"u8.ToArray()),
            (5, "<< /Type /XObject /Subtype /Image /Width " + (indirect ? "7 0 R" : "1")
                + " /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray " + entry + " /Length " + data.Length + " >>", data),
            (6, filters, null), (7, "1", null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach ((int count, int cap, string level, bool indirect, bool expected) in new[]
        {
            (0, 0, "none", false, false), (0, 0, "generalized", false, true),
            (1, 0, "none", false, false), (1, 0, "generalized", false, false),
            (1, 0, "all", true, false), (1, 1, "generalized", true, true),
            (2, 1, "generalized", false, false), (2, 2, "generalized", true, true),
            (2, 1, "none", true, false), (2, 2, "all", false, true)
        })
        {
            if (route == "typed" && level != "generalized") { continue; }
            yield return [route, count, cap, level, indirect, expected];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ImageFilterabilityUsesAcceptedFilterCountAndDecodeLevel(
        string route, int count, int cap, string level, bool indirect, bool expected)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(count, indirect), QpdfInputOwnership.Copy);
        if (route == "typed")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                new QpdfJsonOptions(2, ["pages"]), QpdfLimits.Default with { MaxStreamFilters = cap }, CancellationToken.None);
            Assert.Empty(result.Warnings);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=pages", "--decode-level=" + level,
                    "--max-stream-filters=" + cap, "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
                {
                    inputFile = "input", outputFile = "output", json = "2", jsonKey = new[] { "pages" },
                    decodeLevel = level, global = new { maxStreamFilters = cap.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement image = json.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        Assert.Equal(expected, image.GetProperty("filterable").GetBoolean());
        if (indirect) { Assert.Equal("7 0 R", image.GetProperty("width").GetString()); }
        else { Assert.Equal(1, image.GetProperty("width").GetInt32()); }
        Assert.Equal(count == 0 ? 1 : count, image.GetProperty("filter").GetArrayLength());
        if (count == 0) { Assert.Equal(JsonValueKind.Null, image.GetProperty("filter")[0].ValueKind); }
        else { Assert.All(image.GetProperty("filter").EnumerateArray(), item => Assert.Equal("/FlateDecode", item.GetString())); }
    }
}
