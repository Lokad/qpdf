using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class PredictorDefaultsTests
{
    internal static byte[] Fixture(string codec, string parameters, byte[] predicted)
    {
        byte[] encoded = DecoderOwnershipFixtures.Encode(codec, predicted);
        List<(int, string, byte[]?)> objects = CheckTests.SinglePage(
            $"<< /Length {encoded.Length} /Filter /{codec} /DecodeParms {parameters} >>", encoded);
        objects.Add((6, "null", null));
        return CheckTests.Assemble(objects, 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string codec in new[] { "FlateDecode", "LZWDecode" })
        foreach (string key in new[] { "Predictor", "Columns", "Colors", "BitsPerComponent" })
        foreach (string mode in new[] { "omitted", "explicit", "null", "indirect-null" })
        foreach (string route in new[] { "typed", "argv", "job" })
        { yield return [codec, key, mode, route]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OptionalNullParametersUseTheSameDefaultsAsAbsentKeys(string codec, string key, string mode, string route)
    {
        Dictionary<string, string> fields = new()
        {
            ["Predictor"] = key == "Predictor" ? "1" : "2",
            ["Columns"] = "1", ["Colors"] = "1", ["BitsPerComponent"] = "8",
        };
        if (mode == "omitted") { fields.Remove(key); }
        else if (mode != "explicit") { fields[key] = mode == "null" ? "null" : "6 0 R"; }
        string parameters = "<< " + string.Join(' ', fields.Select(p => "/" + p.Key + " " + p.Value)) + " >>";
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(codec, parameters, "q Q\n"u8.ToArray()), QpdfInputOwnership.Transfer);
        ReadOnlyMemory<byte> escaped = ReadOnlyMemory<byte>.Empty;
        if (route == "typed")
        {
            QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
            Assert.True(result.Passed, string.Join(';', result.Diagnostics.Select(d => d.Message)));
            Assert.Empty(result.Diagnostics); Assert.Equal(8L, result.Counters.TotalDecodedBytes);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--show-object=4", "--filtered-stream-data"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showObject":"4","filteredStreamData":""}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(4L, result.Counters.TotalDecodedBytes);
            escaped = Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        host.Dispose();
        if (route != "typed") { Assert.Equal("q Q\n"u8.ToArray(), escaped.ToArray()); }
    }
}
