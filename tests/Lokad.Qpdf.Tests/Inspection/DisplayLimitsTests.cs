using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Reading;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class DisplayLimitsTests
{
    [Theory]
    [InlineData(false, "memory")]
    [InlineData(true, "memory")]
    [InlineData(false, "bytes")]
    [InlineData(true, "bytes")]
    [InlineData(false, "count")]
    [InlineData(true, "count")]
    public async Task PublicInspectionHonorsHostLimitsAndPreservesCounters(bool job, string limit)
    {
        string body = "(" + new string('\0', 16384) + ")";
        if (limit == "count") { body = "<< /Audit " + body + " /Length 0 >>\nstream\n\nendstream"; }
        byte[] pdf = TolerantObjectLoadingTests.Pdf([body], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfPath existing = QpdfPath.From("outputs/prior.pdf");
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(existing, CancellationToken.None))
        {
            await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        QpdfLimits limits = limit switch
        {
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 100000L },
            "bytes" => QpdfLimits.Default with { MaxDiagnosticBytes = 16L },
            _ => QpdfLimits.Default with { MaxDiagnostics = 1L },
        };
        QpdfExecutionOptions options = new(limits);
        if (limit == "memory")
        {
            // Prove that this ceiling admits the input and parsed value. The
            // following public failure must concern additional display work.
            await using QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None);
            Assert.IsType<PdfByteString>(control.GetObject(5));
            Assert.InRange(control.Counters.PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        }
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","showObject":"5"}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=5"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, error.Code);
        string expectedLimit = limit switch
        {
            "memory" => nameof(QpdfLimits.MaxMemoryBytes),
            "bytes" => nameof(QpdfLimits.MaxDiagnosticBytes),
            _ => nameof(QpdfLimits.MaxDiagnostics),
        };
        Assert.Contains(expectedLimit, error.Message, StringComparison.Ordinal);
        Assert.True(result.Counters.TotalWork > 0L);
        Assert.True(result.Counters.PeakMemoryBytes > 0L);
        Assert.InRange(result.Counters.PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        Assert.True(result.Counters.HostOperations > 0L);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.True(host.TryGetOutput(existing, out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulInspectionCountsRetainedTextAfterInputDisposal(bool job)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), TolerantObjectLoadingTests.Pdf(["<41414141ff>"], false), QpdfInputOwnership.Copy);
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxDiagnostics = 1L, MaxDiagnosticBytes = 8L });
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","showObject":"5"}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=5"]), options, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal("(AAAA\u00ff)", Assert.Single(result.Diagnostics).Message);
        Assert.Equal(1L, result.Counters.DiagnosticCount);
        Assert.True(result.Counters.LiveMemoryBytes >= 2L * result.Diagnostics[0].Message.Length);
        Assert.True(result.Counters.PeakMemoryBytes > result.Counters.LiveMemoryBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
