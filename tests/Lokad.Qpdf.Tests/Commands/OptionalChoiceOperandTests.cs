using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class OptionalChoiceOperandTests
{
    private static InMemoryQpdfHost Host()
    {
        InMemoryQpdfHost host = new();
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf"));
        host.AddInput(QpdfPath.From("input.pdf"), input, QpdfInputOwnership.Transfer);
        return host;
    }

    [Theory]
    [InlineData("--json", true)]
    [InlineData("--json", false)]
    [InlineData("--json-output", false)]
    public async Task BareJsonLeavesTheFollowingFileOperand(string flag, bool beforeInput)
    {
        using InMemoryQpdfHost host = Host();
        string[] arguments = beforeInput ? [flag, "input.pdf", "report.json"] : ["input.pdf", flag, "report.json"];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Null(result.OutputText);
        Assert.True(host.TryGetOutput(QpdfPath.From("report.json"), out byte[] report));
        using JsonDocument json = JsonDocument.Parse(report);
        Assert.Equal("/Catalog", json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:1 0 R")
            .GetProperty("value").GetProperty("/Type").GetString());
        Assert.Equal(flag == "--json", json.RootElement.TryGetProperty("version", out _));
    }

    [Fact]
    public async Task BareCompressionLeavesTheTargetForRewrite()
    {
        using InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input.pdf", "--compress-streams", "out.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] bytes));
        QpdfPath readback = QpdfPath.From("readback.pdf");
        host.AddInput(readback, bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, readback, CancellationToken.None);
        Assert.Equal(2, document.PageCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonDashReturnsTheReportWithoutOutputAccess(bool job)
    {
        using RecordingJobHost observed = new(Host(), null, null, "success", true);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(observed, """{"inputFile":"input.pdf","outputFile":"-","jsonOutput":"2"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(observed, new(["input.pdf", "--json-output", "-"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        Assert.Equal(2, report.RootElement.GetProperty("qpdf")[0].GetProperty("jsonversion").GetInt32());
        Assert.Empty(observed.CreatedPaths);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparatorAndEqualsFormsAllowLiteralChoiceFileNames(bool separator)
    {
        using InMemoryQpdfHost host = Host();
        string[] arguments = separator ? ["input.pdf", "--json-output", "--", "2"] : ["input.pdf", "--json-output=2", "2"];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("2"), out byte[] bytes));
        using JsonDocument report = JsonDocument.Parse(bytes);
        Assert.Equal(2, report.RootElement.GetProperty("qpdf")[0].GetProperty("jsonversion").GetInt32());
    }

    [Theory]
    [InlineData("--json=")]
    [InlineData("--json-output=1")]
    [InlineData("--compress-streams=maybe")]
    public async Task InvalidEqualsChoiceFailsBeforeAnyAccess(string flag)
    {
        using InMemoryQpdfHost host = Host();
        QpdfPath output = QpdfPath.From("report");
        byte[] prior = [17, 23, 41];
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(output, CancellationToken.None))
        {
            await transaction.WriteAsync(prior, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input.pdf", flag, "report"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        Assert.Equal(prior, bytes);
    }
}
