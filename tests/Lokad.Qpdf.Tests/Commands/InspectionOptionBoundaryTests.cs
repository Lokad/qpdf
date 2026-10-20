using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class InspectionOptionBoundaryTests
{
    [Theory]
    [InlineData("\"removeInfo\":false")]
    [InlineData("\"showXref\":false")]
    public async Task BinaryExtractionRejectsConflictingSettersEvenWhenFalse(string setter)
    {
        using InMemoryQpdfHost host = new();
        string job = "{\"inputFile\":\"missing.pdf\",\"showObject\":\"1\",\"rawStreamData\":true,\"outputFile\":\"out.bin\"," + setter + "}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("out.bin"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("\"checkLinearization\":true")]
    [InlineData("\"showLinearization\":true")]
    [InlineData("\"showNpages\":true")]
    [InlineData("\"showPages\":true")]
    [InlineData("\"showObject\":\"1\"")]
    [InlineData("\"showXref\":true")]
    public async Task OutputFreeInspectionsKeepSharedSettingsAndParserLimits(string inspection)
    {
        using InMemoryQpdfHost host = new();
        byte[] input = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf"));
        host.AddInput(QpdfPath.From("input.pdf"), input, QpdfInputOwnership.Copy);
        string job = "{\"inputFile\":\"input.pdf\"," + inspection + ",\"removeInfo\":false,\"global\":{\"parserMaxNesting\":32}}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("checkLinearization", "linearization inspection")]
    [InlineData("showNpages", "showNpages")]
    [InlineData("showPages", "showPages")]
    [InlineData("showXref", "showXref")]
    public async Task TextInspectionRejectsAnOutputTargetBeforeOpeningInput(string key, string diagnostic)
    {
        using InMemoryQpdfHost host = new();
        string job = "{\"inputFile\":\"missing.pdf\",\"outputFile\":\"out.pdf\",\"" + key + "\":true}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, error.Code);
        Assert.StartsWith("Job " + diagnostic + " cannot be combined", error.Message, StringComparison.Ordinal);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.False(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] _));
    }
}
