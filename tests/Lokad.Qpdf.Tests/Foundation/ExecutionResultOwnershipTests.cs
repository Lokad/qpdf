using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ExecutionResultOwnershipTests
{
    public static IEnumerable<object[]> FileJsonAliases()
    {
        yield return ["argv", false];
        yield return ["argv", true];
        foreach (string route in new[] { "job", "mixed", "response" }) { yield return [route, false]; }
    }

    private static async Task<QpdfExecutionResult> CompleteFileJson(string route, bool compressed, bool failure)
    {
        QpdfLimits limits = QpdfLimits.Default;
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync("argv-json", compressed, failure ? "read" : "input-close", "success");
        host.PausePath = QpdfPath.From("input.pdf");
        InMemoryQpdfHost inner = host.Inner;
        if (failure)
        {
            byte[] content = Enumerable.Repeat((byte)' ', 65536).ToArray();
            byte[] source = Inspection.CheckTests.Assemble(Inspection.CheckTests.SinglePage(
                "<< /Length " + content.Length + " >>", content), 1);
            inner.AddInput(QpdfPath.From("input.pdf"), source, QpdfInputOwnership.Copy);
        }
        await using (IQpdfOutputTransaction seed = await inner.CreateOutputAsync(QpdfPath.From("output.json"), CancellationToken.None))
        {
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        Assert.True(inner.TryGetOutput(QpdfPath.From("output.json"), out byte[] prior));
        List<string> argv = ["input.pdf", "--json=2", "--json-stream-data=inline", "--", "output.json"];
        Task<QpdfExecutionResult> running;
        if (route == "job")
        {
            running = Qpdf.ExecuteJobAsync(host,
                "{\"inputFile\":\"input.pdf\",\"outputFile\":\"output.json\",\"json\":\"2\",\"jsonStreamData\":\"inline\"}",
                new QpdfExecutionOptions(limits), CancellationToken.None).AsTask();
        }
        else
        {
            if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
            if (route == "response")
            {
                inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
                argv = ["@args.txt"];
            }
            running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv),
                new QpdfExecutionOptions(limits), CancellationToken.None).AsTask();
        }
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            if (failure)
            {
                // The host refuses subsequent coarse growth after a real read.
                host.Budget.RefuseGrowth = true;
            }
            host.Resume();
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(failure ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            Assert.Null(result.OutputText);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.True(result.Counters.PeakMemoryBytes <= limits.MaxMemoryBytes);
            Assert.True(host.ReadBytes > 0L);
            Assert.True(inner.TryGetOutput(QpdfPath.From("output.json"), out byte[] bytes));
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            if (failure)
            {
                Assert.Equal(prior, bytes);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.Null(result.OutputBytes);
            }
            else
            {
                Assert.Empty(result.Diagnostics);
                Assert.Equal(1378, bytes.Length);
                using JsonDocument parsed = JsonDocument.Parse(bytes);
                Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
            }
            Assert.Equal(host.HostCalls, result.Counters.HostOperations);
            QpdfExecutionCounters snapshot = result.Counters with { };
            host.Dispose();
            Assert.Equal(0L, host.Budget.ReservedBytes);
            Assert.Equal(snapshot, result.Counters);
            return result;
        }
        finally
        {
            host.Resume(); await running;
        }
    }

    private static void VerifyCopies(QpdfExecutionResult result)
    {
        QpdfExecutionResult clone = result with { };
        Assert.Equal(result, clone); Assert.Equal(result.GetHashCode(), clone.GetHashCode());
        Assert.Same(result.Diagnostics, clone.Diagnostics); Assert.Same(result.Counters, clone.Counters);
        QpdfExecutionCounters c = result.Counters;
        QpdfExecutionCounters[] copies = [c with { }, new(c.LiveMemoryBytes, c.PeakMemoryBytes,
            c.TotalDecodedBytes, c.TotalOutputBytes, c.HostOperations, c.OpenHandles, c.DiagnosticCount)];
        foreach (QpdfExecutionCounters copy in copies)
        {
            Assert.Equal(c, copy); Assert.Equal(c.GetHashCode(), copy.GetHashCode());
            Assert.True(c == copy); Assert.False(c.Equals(null));
        }
    }

    [Theory]
    [MemberData(nameof(FileJsonAliases))]
    [Trait("Layer", "Contract")]
    public async Task FileJsonResultsPreserveImmutableCounters(string route, bool compressed)
        => VerifyCopies(await CompleteFileJson(route, compressed, false));

    [Fact]
    [Trait("Layer", "Contract")]
    public async Task AfterReadBudgetFailuresPreserveTargetsAndCounters()
        => VerifyCopies(await CompleteFileJson("argv", false, true));
}
