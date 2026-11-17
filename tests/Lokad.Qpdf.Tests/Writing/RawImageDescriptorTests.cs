using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class RawImageDescriptorTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Report = QpdfPath.From("report");

    private static byte[] Fixture(string filter, string parameters, int stages)
    {
        string names = "[" + string.Join(' ', Enumerable.Repeat("/FlateDecode", stages)) + "]";
        string options = "[" + string.Join(' ', Enumerable.Repeat("null", stages)) + "]";
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << /Im 4 0 R >> >> >>", null),
            (4, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 /Filter " + filter
                + (parameters.Length == 0 ? "" : " /DecodeParms " + parameters) + " >>", "A"u8.ToArray()),
            (5, names, null), (6, options, null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        yield return ["17", "", "[17]", "[null]"];
        yield return ["/FlateDecode", "42", "[\"/FlateDecode\"]", "[42]"];
        yield return ["[/FlateDecode /FlateDecode]", "[null]", "[\"/FlateDecode\",\"/FlateDecode\"]", "[null]"];
        yield return ["[/FlateDecode 17]", "", "[\"/FlateDecode\",17]", "[null,null]"];
        yield return ["/FlateDecode", "[17]", "[\"/FlateDecode\"]", "[17]"];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DisabledDecodingProjectsStoredMalformedSpecifications(string filter, string parameters, string expectedFilter, string expectedParameters)
    {
        byte[] source = Fixture(filter, parameters, 0), original = source.ToArray();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, source, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--json=2", "--json-key=pages", "--decode-level=none", "report"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(Report, out byte[] bytes));
        using JsonDocument report = JsonDocument.Parse(bytes);
        JsonElement image = report.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        using JsonDocument filters = JsonDocument.Parse(expectedFilter), parms = JsonDocument.Parse(expectedParameters);
        Assert.True(JsonElement.DeepEquals(filters.RootElement, image.GetProperty("filter")));
        Assert.True(JsonElement.DeepEquals(parms.RootElement, image.GetProperty("decodeparms")));
        Assert.False(image.GetProperty("filterable").GetBoolean());
        Assert.Equal(0L, result.Counters.TotalDecodedBytes); Assert.Equal(original, source); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedMinimalAndJobVersionOneUseTheSameInactivePolicy(bool typed)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture("17", "", 0), QpdfInputOwnership.Transfer);
        string text;
        if (typed)
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, Input,
                new QpdfJsonOptions(2, ["pages"], "none", null, [], true), CancellationToken.None);
            text = result.Json; Assert.Equal(0L, result.Counters.TotalDecodedBytes);
        }
        else
        {
            QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
                """{"inputFile":"input","json":"1","jsonKey":["pages"],"decodeLevel":"none"}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); text = Assert.IsType<string>(result.OutputText);
        }
        using JsonDocument report = JsonDocument.Parse(text);
        JsonElement image = report.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        Assert.Equal(17, image.GetProperty("filter")[0].GetInt32()); Assert.False(image.GetProperty("filterable").GetBoolean());
        Assert.False(host.TryGetOutput(Report, out _)); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task<QpdfExecutionResult> Inspect(byte[] source, string level, QpdfLimits limits)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, source, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--json=2", "--json-key=pages", "--decode-level=" + level]), new(limits), CancellationToken.None);
        Assert.Equal(0L, host.OpenLeaseCount); return result;
    }

    [Fact]
    public async Task InactiveInspectionAvoidsStageStorageWhileRetainingParsedContainerLimits()
    {
        byte[] source = Fixture("5 0 R", "6 0 R", 4096);
        QpdfExecutionResult raw = await Inspect(source, "none", QpdfLimits.Default);
        QpdfExecutionResult active = await Inspect(source, "generalized", QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, raw.ExitCode); Assert.Equal(QpdfExitCode.Success, active.ExitCode);
        Assert.True(active.Counters.PeakMemoryBytes - raw.Counters.PeakMemoryBytes > 32768L);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = raw.Counters.PeakMemoryBytes };
        Assert.Equal(QpdfExitCode.Success, (await Inspect(source, "none", limits)).ExitCode);
        QpdfExecutionResult refused = await Inspect(source, "generalized", limits);
        Assert.Equal(QpdfExecutionStatus.Failed, refused.Status);
        Assert.Contains(refused.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        QpdfExecutionResult container = await Inspect(source, "none", QpdfLimits.Default with { MaxContainerSize = 4095 });
        Assert.Equal(QpdfExecutionStatus.Failed, container.Status);
        Assert.Contains(container.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0L, raw.Counters.TotalDecodedBytes);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(raw.OutputText));
        JsonElement image = report.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        Assert.Equal("5 0 R", image.GetProperty("filter").GetString()); Assert.Equal("6 0 R", image.GetProperty("decodeparms").GetString());
        Assert.False(image.GetProperty("filterable").GetBoolean());
    }
}
