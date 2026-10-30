using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class WriterVersionBoundsTests
{
    private static InMemoryQpdfHost Host()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble(CheckTests.SinglePage("<< /Length 0 >>", []), 1), QpdfInputOwnership.Transfer);
        return host;
    }

    [Theory]
    [InlineData("forceVersion")]
    [InlineData("minVersion")]
    public async Task OversizedJobValuesRejectWithoutMaterializingTokens(string key)
    {
        using InMemoryQpdfHost host = Host();
        byte[] prior = [11, 23, 47];
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        {
            await transaction.WriteAsync(prior, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        string value = string.Concat(Enumerable.Repeat("1.", 65536));
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
            { ["inputFile"] = "input", ["outputFile"] = "output", [key] = value });
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = 2L * 1024L * 1024L });
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        // The memory host completes synchronously; measure only the public job.
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, json, options, CancellationToken.None);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.InRange(allocated, 0L, 2L * 1024L * 1024L);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] current)); Assert.Equal(prior, current);
    }

    [Theory]
    [InlineData("forceVersion", "argv")]
    [InlineData("minVersion", "argv")]
    [InlineData("forceVersion", "job")]
    [InlineData("minVersion", "job")]
    [InlineData("forceVersion", "mixed")]
    [InlineData("minVersion", "mixed")]
    public async Task MaximumLengthValuesPreserveLeadingZerosAndExtensions(string key, string route)
    {
        const string Value = "0001.0007.999999";
        using InMemoryQpdfHost host = Host();
        QpdfExecutionResult result;
        if (route == "job")
        {
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object>
                { ["inputFile"] = "input", ["outputFile"] = "output", [key] = Value }), CancellationToken.None);
        }
        else
        {
            List<string> argv = ["input"];
            if (route == "mixed")
            {
                host.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
                argv.Add("--job-json-file=settings");
            }
            argv.AddRange([key == "forceVersion" ? "--force-version" : "--min-version", Value, "output"]);
            result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        string text = Encoding.ASCII.GetString(output);
        Assert.StartsWith("%PDF-1.7", text); Assert.Contains("/ExtensionLevel 999999", text);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
    }
}
