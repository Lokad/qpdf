using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class ReportAppearanceGenerationTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly byte[] Stale = "q BT /Helv 10 Tf (STALE) Tj ET Q\n"u8.ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonReportUsesTheGeneratedProgramAndKeepsTheOriginalStream(bool job)
    {
        using InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input.pdf","json":"2","jsonKey":["qpdf"],"jsonStreamData":"inline","generateAppearances":""}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, "--json=2", "--json-key=qpdf", "--json-stream-data=inline", "--generate-appearances"]), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
        using JsonDocument json = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement form = Value(table, catalog.GetProperty("/AcroForm"));
        Assert.False(form.TryGetProperty("/NeedAppearances", out _));
        JsonElement widget = Value(table, form.GetProperty("/Fields")[0]);
        JsonElement normal = widget.GetProperty("/AP").GetProperty("/N");
        Assert.Contains("(FRESH) Tj", Encoding.ASCII.GetString(Stream(table, normal)), StringComparison.Ordinal);
        Assert.Equal(Stale, Stream(table, catalog.GetProperty("/Audit")));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CheckedWidgetListingAndNamedStreamUseTheSameGeneratedAppearance()
    {
        using InMemoryQpdfHost host = Host();
        QpdfExecutionResult listing = await Qpdf.ExecuteAsync(host,
            new([Input.Value, "--generate-appearances", "--check", "--show-object=5"]), CancellationToken.None);
        Assert.True(listing.ExitCode == QpdfExitCode.Success, string.Join("\n", listing.Diagnostics.Select(item => item.Message)));
        string widget = Assert.Single(listing.Diagnostics, item => item.Code == QpdfDiagnosticCode.ObjectListing).Message;
        Match link = Regex.Match(widget, @"/N\s+(\d+)\s+0\s+R", RegexOptions.CultureInvariant);
        Assert.True(link.Success, widget);

        string select = "--show-object=" + link.Groups[1].Value;
        QpdfExecutionResult binary = await Qpdf.ExecuteAsync(host,
            new([Input.Value, "--generate-appearances", select, "--filtered-stream-data"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, binary.ExitCode);
        ReadOnlyMemory<byte> bytes = Assert.IsType<ReadOnlyMemory<byte>>(binary.OutputBytes);
        Assert.Contains("(FRESH) Tj", Encoding.ASCII.GetString(bytes.Span), StringComparison.Ordinal);
        QpdfExecutionResult named = await Qpdf.ExecuteAsync(host,
            new([Input.Value, "--generate-appearances", select, "--filtered-stream-data", "program"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, named.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("program"), out byte[] saved));
        Assert.Equal(bytes.ToArray(), saved);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task GenerationRunsBeforeStoredAppearancesAreFlattened()
    {
        using InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([Input.Value, "--generate-appearances", "--flatten-annotations=all", "--json=2", "--json-key=qpdf", "--json-key=pages", "--json-stream-data=inline"]), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
        using JsonDocument json = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        JsonElement page = Value(table, json.RootElement.GetProperty("pages")[0].GetProperty("object"));
        Assert.False(page.TryGetProperty("/Annots", out _));
        JsonElement resources = Value(table, page.GetProperty("/Resources"));
        JsonElement xobjects = Value(table, resources.GetProperty("/XObject"));
        JsonProperty appearance = Assert.Single(xobjects.EnumerateObject());
        Assert.Contains("(FRESH) Tj", Encoding.ASCII.GetString(Stream(table, appearance.Value)), StringComparison.Ordinal);
        Assert.Contains(page.GetProperty("/Contents").EnumerateArray(), stream =>
            Encoding.ASCII.GetString(Stream(table, stream)).Contains(appearance.Name + " Do", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task IncompleteGenerationKeepsNeedAppearancesAndReportsTheWarning()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generate-radio-1page.pdf")), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([Input.Value, "--generate-appearances", "--json=2", "--json-key=acroform"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.UnsupportedFeature
            && item.Message.StartsWith("Radio button", StringComparison.Ordinal));
        using JsonDocument report = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        Assert.True(report.RootElement.GetProperty("acroform").GetProperty("needappearances").GetBoolean());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public Task GenerationRefusalKeepsThePriorReportAndClosesItsInput()
    {
        QpdfPath target = QpdfPath.From("report");
        return PreparationAdmissionChecks.VerifyAsync(() => PreparationAdmissionChecks.StorageAsync(Fixture(), target),
            (host, token) => Qpdf.ExecuteAsync(host,
                new(["input", "--generate-appearances", "--json=2", "--json-key=qpdf", "--json-stream-data=inline", target.Value]), token),
            target, 1024, ["write", "commit", "cancel"]);
    }

    private static InMemoryQpdfHost Host()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static byte[] Fixture() => PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R /Audit 7 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Annots [5 0 R] /Contents 8 0 R >>", null),
            (4, "<< /Fields [5 0 R] /NeedAppearances true /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >>", null),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /V (FRESH) /Rect [10 10 110 30] /F 4 /P 3 0 R /AP << /N 7 0 R >> >>", null),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (7, $"<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /Helv 6 0 R >> >> /Length {Stale.Length} >>", Stale),
            (8, "<< /Length 4 >>", "q Q\n"u8.ToArray())], 1);

    private static JsonElement Value(JsonElement table, JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? table.GetProperty("obj:" + value.GetString()).GetProperty("value") : value;

    private static byte[] Stream(JsonElement table, JsonElement reference)
        => table.GetProperty("obj:" + reference.GetString()).GetProperty("stream").GetProperty("data").GetBytesFromBase64();
}
