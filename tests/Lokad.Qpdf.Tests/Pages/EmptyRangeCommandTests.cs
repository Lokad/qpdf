using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Pages;

public sealed class EmptyRangeCommandTests
{
    private static byte[] Fixture() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 0 /Kids [] >>", null)], 1);

    private static ValueTask<QpdfExecutionResult> ExecuteAsync(InMemoryQpdfHost host, string operation, string range, bool job)
    {
        List<string> argv = ["input"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input" };
        if (operation == "rotate")
        { argv.Add("--rotate=90:" + range); settings["rotate"] = new[] { "90:" + range }; }
        else
        {
            argv.AddRange(["--pages", ".", range, "--"]);
            settings["pages"] = new[] { new { file = ".", range } };
        }
        if (operation == "count") { argv.Add("--show-npages"); settings["showNpages"] = ""; }
        else { argv.Add("output"); settings["outputFile"] = "output"; }
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
    }

    private static async Task AssertEmptyAsync(string operation, string range, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await ExecuteAsync(host, operation, range, job);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Null(result.OutputBytes); Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
        if (operation == "count")
        {
            Assert.EndsWith(": 0", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
            Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _));
        }
        else
        {
            Assert.Empty(result.Diagnostics); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
            { Assert.Equal(0, document.PageCount); }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("select", ":odd")]
    [InlineData("select", ":even")]
    [InlineData("rotate", ":odd")]
    [InlineData("count", ":even")]
    public Task EmptyFiltersWorkThroughTheCommandOperationBranches(string operation, string range)
        => AssertEmptyAsync(operation, range, false);

    [Theory]
    [InlineData("select", ":even")]
    [InlineData("rotate", ":even")]
    [InlineData("count", ":odd")]
    public Task JobRangesUseTheSharedEmptyExpansion(string operation, string range)
        => AssertEmptyAsync(operation, range, true);
}
