using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonOutputAccountingTests
{
    private static byte[] Fixture() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /Audit <FEFF00E9> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>", null),
        (4, "<< /Length 4096 >>", Enumerable.Repeat((byte)32, 4096).ToArray())], 1);

    private static async Task<(QpdfExecutionResult Result, byte[] Json, byte[] Main, byte[] Sidecar)> Run(
        bool job, bool fileless, string mode, long maximum)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(), QpdfInputOwnership.Copy);
        foreach (string path in new[] { "output", "part-4" })
        {
            await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From(path), CancellationToken.None);
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxOutputBytes = maximum });
        List<string> argv = ["input", "--json=2", "--json-key=qpdf", "--decode-level=none",
            "--json-stream-data=" + mode, "--json-stream-prefix=part"];
        Dictionary<string, object> settings = new()
        {
            ["inputFile"] = "input", ["json"] = "2", ["jsonKey"] = new[] { "qpdf" },
            ["decodeLevel"] = "none", ["jsonStreamData"] = mode, ["jsonStreamPrefix"] = "part"
        };
        if (!fileless) { argv.AddRange(["--", "output"]); settings["outputFile"] = "output"; }
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), options, CancellationToken.None);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] main));
        Assert.True(host.TryGetOutput(QpdfPath.From("part-4"), out byte[] sidecar));
        byte[] json = result.Status != QpdfExecutionStatus.Completed ? []
            : fileless ? Encoding.UTF8.GetBytes(Assert.IsType<string>(result.OutputText)) : main;
        return (result, json, main, sidecar);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (bool fileless in new[] { false, true })
        foreach (string mode in new[] { "none", "file" })
        foreach (string boundary in new[] { "unlimited", "exact", "below" })
        { yield return [job, fileless, mode, boundary]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MainJsonAndSidecarsShareOneCumulativeOutputLimit(bool job, bool fileless, string mode, string boundary)
    {
        var control = await Run(job, fileless, mode, QpdfLimits.Default.MaxOutputBytes);
        Assert.Equal(QpdfExitCode.Success, control.Result.ExitCode);
        long total = control.Json.LongLength + (mode == "file" ? 4096L : 0L);
        long maximum = boundary == "unlimited" ? QpdfLimits.Default.MaxOutputBytes : total - (boundary == "below" ? 1L : 0L);
        var actual = await Run(job, fileless, mode, maximum);
        if (boundary == "below")
        {
            Assert.Equal(QpdfExecutionStatus.Failed, actual.Result.Status);
            Assert.Equal(QpdfExitCode.Error, actual.Result.ExitCode);
            Assert.Contains(actual.Result.Diagnostics, d => d.Message.Contains(nameof(QpdfLimits.MaxOutputBytes), StringComparison.Ordinal));
            Assert.Equal("prior"u8.ToArray(), actual.Main); Assert.Equal("prior"u8.ToArray(), actual.Sidecar);
        }
        else
        {
            Assert.Equal(QpdfExecutionStatus.Completed, actual.Result.Status);
            Assert.Equal(QpdfExitCode.Success, actual.Result.ExitCode); Assert.Empty(actual.Result.Diagnostics);
            Assert.Equal(total, actual.Result.Counters.TotalOutputBytes);
            Assert.Equal(control.Json, actual.Json);
            using JsonDocument json = JsonDocument.Parse(actual.Json);
            if (fileless) { Assert.Equal("prior"u8.ToArray(), actual.Main); }
            if (mode == "file") { Assert.Equal(4096, actual.Sidecar.Length); Assert.All(actual.Sidecar, b => Assert.Equal((byte)32, b)); }
            else { Assert.Equal("prior"u8.ToArray(), actual.Sidecar); }
        }
    }
}
