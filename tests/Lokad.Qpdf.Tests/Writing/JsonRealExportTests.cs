using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonRealExportTests
{
    internal static byte[] Fixture(string number) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /Probe " + number + " >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> >>", null)], 1);

    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach ((string pdf, string json) in new[] {
            ("+.5", "0.5"), ("+0.5", "0.5"), ("+01.50", "1.50"), ("-00.5", "-0.5"), ("-000.00", "-0.00"),
            ("-.5", "-0.5"), ("001.50", "1.50"), ("0000.250", "0.250"), ("+000", "0"),
            ("0.", "0.0"), ("+000.", "0.0"), ("-000.", "-0.0"),
            ("+.0000000000000000000001", "0.0000000000000000000001"),
            ("+12345678901234567890.01234567890123456789", "12345678901234567890.01234567890123456789"),
            ("-00012345678901234567890.01234567890123456789", "-12345678901234567890.01234567890123456789") })
        { yield return [route, pdf, json]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SignedAndZeroPaddedRealsExportValidExactJson(string route, string pdf, string expected)
    {
        byte[] output;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(pdf), QpdfInputOwnership.Copy);
            if (route == "typed")
            { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["qpdf"]), CancellationToken.None); }
            else
            {
                QpdfExecutionResult result = route == "argv"
                    ? await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=qpdf", "--test-json-schema", "--", "output"]), CancellationToken.None)
                    : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["qpdf"],"testJsonSchema":""}""", CancellationToken.None);
                Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            }
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out output)); Assert.Equal(0L, host.OpenLeaseCount);
        }
        using JsonDocument parsed = JsonDocument.Parse(output);
        JsonElement number = parsed.RootElement.GetProperty("qpdf")[1].GetProperty("obj:1 0 R").GetProperty("value").GetProperty("/Probe");
        Assert.Equal(expected, number.GetRawText());
    }
}
