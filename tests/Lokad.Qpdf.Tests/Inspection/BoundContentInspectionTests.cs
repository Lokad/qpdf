using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class BoundContentInspectionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input");
    private static readonly QpdfPath Output = QpdfPath.From("report");

    private static byte[] CreateGraph() => """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "obj:1 3 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Archive":"6 8 R","/Metadata":"71 2 R"}},
          "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 5 R","5 4 R"],"/Count":2}},
          "obj:3 5 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{},"/Contents":"6 8 R"}},
          "obj:5 4 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{},"/Contents":"9 1 R"}},
          "obj:6 8 R":{"value":["4 7 R","8 2 R"]},
          "obj:4 7 R":{"stream":{"dict":{},"data":"cQo="}},
          "obj:8 2 R":{"stream":{"dict":{},"data":"UQo="}},
          "obj:9 1 R":{"stream":{"dict":{},"data":"cSBRCg=="}},
          "obj:71 2 R":{"stream":{"dict":{},"data":"QQ=="}},
          "trailer":{"value":{"/Root":"1 3 R","/Size":72}}
        }]}
        """u8.ToArray();

    [Fact]
    public async Task CheckedListingAndRawExtractionResolveNewAndDeclaredPairs()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, CreateGraph(), QpdfInputOwnership.Transfer);
        string[] transform = ["input", "--json-input", "--coalesce-contents", "--remove-metadata"];
        QpdfExecutionResult check = await Qpdf.ExecuteAsync(host,
            new([.. transform, "--check", "--show-object=3,5", "--show-pages"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, check.ExitCode);
        Assert.Contains(check.Diagnostics, item => item.Code == QpdfDiagnosticCode.CheckSkipped);
        Assert.Contains(check.Diagnostics, item => item.Code == QpdfDiagnosticCode.ObjectListing
            && item.Message.Contains("/Contents 72 0 R", StringComparison.Ordinal));
        Assert.Contains(check.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageListing && item.Message == "page 1: 3 5 R");
        Assert.Contains(check.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageListing && item.Message == "    72 0 R");
        Assert.Contains(check.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageListing && item.Message == "page 2: 5 4 R");
        Assert.Contains(check.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageListing && item.Message == "    9 1 R");

        QpdfExecutionResult generated = await Qpdf.ExecuteAsync(host,
            new([.. transform, "--show-object=72", "--raw-stream-data"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, generated.ExitCode);
        Assert.Equal("q\nQ\n"u8.ToArray(), generated.OutputBytes?.ToArray());
        QpdfExecutionResult archived = await Qpdf.ExecuteAsync(host,
            new([.. transform, "--show-object=6,8"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, archived.ExitCode);
        Assert.Equal("[ 4 7 R 8 2 R ]", Assert.Single(archived.Diagnostics,
            item => item.Code == QpdfDiagnosticCode.ObjectListing).Message);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NamedJobReportCoalescesPatchedProgramAndKeepsItsIndependentSourceEdges()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 6 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 30 30] /Resources << >> /Contents 6 0 R >>", null),
            (4, "<< /Length 2 >>", "q\n"u8.ToArray()),
            (5, "<< /Length 2 >>", "Q\n"u8.ToArray()),
            (6, "[4 7 R 5 0 R]", null)], 1, new Dictionary<int, int> { [4] = 7 }, "1.4", ""),
            QpdfInputOwnership.Transfer);
        byte[] changed = "q 1 0 0 1 7 8 cm\n"u8.ToArray();
        string data = Convert.ToBase64String(changed);
        host.AddInput(QpdfPath.From("patch"), System.Text.Encoding.UTF8.GetBytes($$$$"""
            {"qpdf":[{"jsonversion":2},{"obj:4 7 R":{"stream":{"dict":{},"data":"{{{{data}}}}"}}}]}
            """), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"input","updateFromJson":"patch","coalesceContents":"","jsonOutput":"2","outputFile":"report"}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Output, out byte[] stored));
        using JsonDocument report = JsonDocument.Parse(stored);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        Assert.Equal("7 0 R", table.GetProperty("obj:3 0 R").GetProperty("value").GetProperty("/Contents").GetString());
        Assert.Equal([.. changed, (byte)'Q', (byte)'\n'], ReadStream(table, "7 0 R"));
        Assert.Equal("6 0 R", table.GetProperty("obj:1 0 R").GetProperty("value").GetProperty("/Audit").GetString());
        Assert.Collection(table.GetProperty("obj:6 0 R").GetProperty("value").EnumerateArray(),
            first => Assert.Equal("4 7 R", first.GetString()), last => Assert.Equal("5 0 R", last.GetString()));
        Assert.Equal(changed, ReadStream(table, "4 7 R"));
        Assert.Equal("Q\n"u8.ToArray(), ReadStream(table, "5 0 R"));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task GeneratedIdentityRefusalPrecedesNamedReportStaging()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        byte[] input = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(CreateGraph()).Replace("71 2 R", "2147483647 2 R", StringComparison.Ordinal));
        host.Inner.AddInput(Input, input, QpdfInputOwnership.Transfer);
        byte[] prior = [17, 23, 41];
        await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None))
        {
            await seed.WriteAsync(prior, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--json-input", "--json-output=2", "--coalesce-contents", "report"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments
            && item.Message.Contains("identity is available", StringComparison.Ordinal));
        Assert.Empty(host.CreatedPaths);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] stored));
        Assert.Equal(prior, stored);
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    private static byte[] ReadStream(JsonElement table, string reference)
        => table.GetProperty("obj:" + reference).GetProperty("stream").GetProperty("data").GetBytesFromBase64();
}
