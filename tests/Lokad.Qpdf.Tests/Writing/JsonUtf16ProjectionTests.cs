using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonUtf16ProjectionTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IEnumerable<object[]> Cases()
    {
        foreach ((string hex, string expected) in new[]
        {
            ("FEFF0041D8000042", "AB"), ("FEFF0041D800", "A"), ("FFFE410000D84200", "AB"),
            ("FEFF0041DC010042", "A\u0001B"), ("FEFFDFFF", "\u03ff"), ("FEFFDC000041", "\0A"),
            ("FEFF004100", "A"), ("FEFF004100420043", "ABC"), ("FFFE41004200", "AB"),
            ("FEFFD83DDE00", "\ud83d\ude00"), ("FEFFD800D8010041", "A"), ("FEFF", ""),
            ("EFBBBF41C3A9", "A\u00e9"), ("414243", "ABC"), ("EFBBBFE282", "\ufffd"),
        })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (int version in new[] { 1, 2 }) { yield return [hex, expected, route, version]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task JsonTextUsesTheEstablishedPdfDecodingPolicy(string hex, string expected, string route, int version)
    {
        string? named = null;
        foreach (bool fileless in route == "typed" ? new[] { false } : new[] { false, true })
        {
            using InMemoryQpdfHost host = new();
            host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R /Probe <" + hex + "> >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
            string key = version == 1 ? "objects" : "qpdf";
            string text;
            if (route == "typed")
            {
                QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(version, [key]), CancellationToken.None);
                text = result.Json;
            }
            else
            {
                QpdfExecutionResult result;
                if (route == "job")
                {
                    Dictionary<string, object> job = new() { ["inputFile"] = "input", ["json"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture), ["jsonKey"] = new[] { key } };
                    if (!fileless) { job["outputFile"] = "output"; }
                    result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
                }
                else
                {
                    List<string> argv = ["input", "--json=" + version.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json-key=" + key];
                    if (!fileless) { argv.AddRange(["--", "output"]); }
                    result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
                }
                Assert.Equal(QpdfExitCode.Success, result.ExitCode);
                if (fileless) { text = Assert.IsType<string>(result.OutputText); }
                else { Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); text = StrictUtf8.GetString(output); }
            }
            if (!fileless)
            {
                Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
                Assert.Equal(text, StrictUtf8.GetString(output)); Assert.Equal(output, StrictUtf8.GetBytes(text)); named = text;
            }
            else { Assert.Equal(named, text); StrictUtf8.GetBytes(text); }
            using JsonDocument parsed = JsonDocument.Parse(text);
            JsonElement catalog = version == 1 ? parsed.RootElement.GetProperty("objects").GetProperty("1 0")
                : parsed.RootElement.GetProperty("qpdf")[1].GetProperty("obj:1 0 R").GetProperty("value");
            Assert.Equal((version == 1 ? "" : "u:") + expected, catalog.GetProperty("/Probe").GetString());
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }
}
