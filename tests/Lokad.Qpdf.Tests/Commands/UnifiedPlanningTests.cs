using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class UnifiedPlanningTests
{
    [Theory]
    [InlineData("argv")]
    [InlineData("password")]
    [InlineData("layered")]
    [InlineData("response")]
    [InlineData("json")]
    public async Task ScalarSettersHaveOneOrderAcrossAdapters(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        List<string> argv = ["input", "--force-version=1.5", "--force-version=1.7", "output"];
        if (route == "password") { argv.Insert(1, "--password="); }
        if (route == "layered")
        {
            host.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
            argv.Insert(1, "--job-json-file=settings");
        }
        if (route == "response")
        {
            host.AddInput(QpdfPath.From("arguments"), Encoding.UTF8.GetBytes(string.Join('\n', argv)), QpdfInputOwnership.Copy);
            argv = ["@arguments"];
        }
        QpdfExecutionResult result = route == "json"
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","forceVersion":"1.5","forceVersion":"1.7"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.True(output.AsSpan().StartsWith("%PDF-1.7"u8));
        using UglyToad.PdfPig.PdfDocument pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
