using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JobJsonHelpTests
{
    [Theory]
    [InlineData("check", "{\"inputFile\":\"in.pdf\",\"check\":\"\"}", false)]
    [InlineData("parserMaxErrors", "{\"inputFile\":\"in.pdf\",\"check\":\"\",\"global\":{\"parserMaxErrors\":\"0\"}}", false)]
    [InlineData("noDefaultLimits", "{\"inputFile\":\"in.pdf\",\"showNpages\":\"\",\"global\":{\"noDefaultLimits\":\"\"}}", false)]
    [InlineData("rawStreamData", "{\"inputFile\":\"in.pdf\",\"showObject\":\"4\",\"rawStreamData\":\"\"}", true)]
    [InlineData("filteredStreamData", "{\"inputFile\":\"in.pdf\",\"showObject\":\"4\",\"filteredStreamData\":\"\"}", true)]
    public async Task HelpAdvertisesLiveInspectionAndGlobalForms(string key, string job, bool binary)
    {
        using InMemoryQpdfHost host = new();
        byte[] input = CheckTests.Assemble(CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray()), 1);
        host.AddInput(QpdfPath.From("in.pdf"), input, QpdfInputOwnership.Copy);
        QpdfExecutionResult help = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["--job-json-help"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, help.Status);
        QpdfDiagnostic text = Assert.Single(help.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.HelpText, text.Code);
        // "check" inside "checkLinearization" must not count as documentation.
        Assert.Matches(@"(?<![A-Za-z0-9_])" + key + @"(?![A-Za-z0-9_])", text.Message);
        Assert.Contains("without outputFile they return result bytes", text.Message, StringComparison.Ordinal);
        Assert.Contains("\"0\" removes the threshold", text.Message, StringComparison.Ordinal);
        Assert.Contains("\"\" lifts unspecified parser defaults", text.Message, StringComparison.Ordinal);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        if (binary)
        {
            Assert.Equal("q Q\n"u8.ToArray(), result.OutputBytes?.ToArray());
        }
        else
        {
            Assert.Null(result.OutputBytes);
        }
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }
}
