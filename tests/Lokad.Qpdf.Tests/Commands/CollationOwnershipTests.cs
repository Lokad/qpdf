using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;
using Host = Lokad.Qpdf.Tests.Foundation.RecordingJobHost;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CollationOwnershipTests(ITestOutputHelper log)
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [17, 23, 41];

    private static async Task<Host> CreateHost(string outcome)
    {
        Host host = outcome switch
        {
            "success" or "cancel" => new("create", Output, "success", true),
            "close-fault" => new("output-close", Output, "fault", true),
            "host-cancel" => new("create", Output, "host-cancel", true),
            _ => Host.ForOutcome(outcome, Input, Output),
        };
        host.Inner.AddInput(Input, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    [Fact]
    public void CallerRetainedVectorsAndPolicySurviveAdmissionOwnerDisposal()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        List<int> vector; PdfPageCollation policy;
        using (CollationPreparation owner = CollationPreparation.Create(governor))
        {
            Assert.False(owner.HasStorage);
            vector = owner.CreateVector();
            Assert.True(owner.HasStorage);
            for (int index = 1; index <= 129; index++) { owner.Add(vector, index); }
            policy = owner.CreatePolicy(vector);
            Assert.True(governor.Snapshot().LiveMemoryBytes > 0L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(Enumerable.Range(1, 129), vector); Assert.Equal(vector, policy.GroupSizes);
        vector[0] = 999; Assert.Equal(1, policy.GroupSizes[0]);
    }

    [Fact]
    public void SharedGrowthRefusalRefundsPreparationAndPreservesAnEarlierPolicy()
    {
        using Host host = new(null, null, "success", true);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        PdfPageCollation policy;
        using (CollationPreparation owner = CollationPreparation.Create(governor))
        {
            List<int> vector = owner.CreateVector(); owner.Add(vector, 1);
            policy = owner.CreatePolicy(vector);
            host.Budget.RefuseGrowth = true;
            Assert.Throws<QpdfMemoryAdmissionException>(() =>
            {
                for (int index = 0; index < 262144; index++) { owner.Add(vector, 1); }
            });
            Assert.Equal(new[] { 1 }, policy.GroupSizes);
        }
        governor.CompleteStorage();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, host.Budget.ReservedBytes);
        Assert.Equal(new[] { 1 }, policy.GroupSizes);
    }

    private static Task<QpdfExecutionResult> Start(Host host, string route, CancellationToken token)
    {
        string json = JsonSerializer.Serialize(new
        {
            inputFile = Input.Value,
            outputFile = Output.Value,
            pages = new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } },
            collate = "1,1"
        });
        string[] arguments = [Input.Value, "--collate=1,1", "--pages", ".", "1", ".", "1", "--", Output.Value];
        if (route == "json") { return Qpdf.ExecuteJobAsync(host, json, token).AsTask(); }
        if (route == "nested")
        {
            host.Inner.AddInput(QpdfPath.From("job.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
            return Qpdf.ExecuteJobAsync(host, "{\"jobJsonFile\":\"job.json\"}", token).AsTask();
        }
        if (route == "file")
        {
            host.Inner.AddInput(QpdfPath.From("job.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
            arguments = ["--job-json-file=job.json"];
        }
        else if (route == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', arguments)), QpdfInputOwnership.Copy);
            arguments = ["@args.txt"];
        }
        else if (route == "mixed")
        {
            host.Inner.AddInput(QpdfPath.From("job.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
            arguments = ["--job-json-file=job.json", .. arguments];
        }
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(arguments), token).AsTask();
    }

    private async Task<QpdfExecutionResult> Complete(string route, string outcome)
    {
        using Host host = await CreateHost(outcome);
        using CancellationTokenSource cancel = new();
        Task<QpdfExecutionResult> running = Start(host, route, cancel.Token);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "cancel") { cancel.Cancel(); }
            host.Resume();
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed
                : outcome is "cancel" or "host-cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (outcome is "success" or "close-fault")
            {
                Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes));
                using InMemoryQpdfHost reader = new(); reader.AddInput(Input, bytes, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Input, CancellationToken.None);
                Assert.Equal(2, document.PageCount);
            }
            else { Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior); }
            Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(host.Budget.ReservedBytes < host.Budget.PeakBytes);
            log.WriteLine("COLLATION CONTROL {0}|{1}", route, outcome);
            return result;
        }
        finally { host.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task CompleteCollationJobsFundPolicyThroughPublicationAndRetire()
    {
        foreach (string route in new[] { "argv", "response", "mixed", "json", "file", "nested" })
        { await Complete(route, "success"); }
        foreach (string outcome in new[] { "open-fault", "create-fault", "write-fault", "commit-fault", "close-fault", "cancel", "host-cancel" })
        { await Complete("json", outcome); }
    }

    [Fact]
    public async Task CollationDefaultsValuesAndInspectionPreservePublicSemantics()
    {
        foreach (string route in new[] { "argv", "json" })
        foreach (string value in new[] { "", "0", "1", "1,1", "2147483647", "1,", ",1", "-1", " 1", "2147483648", "00000000001", "1,1,1" })
        {
            using Host host = await CreateHost("success");
            host.Resume();
            bool valid = value is "" or "0" or "1" or "1,1" or "2147483647";
                string json = JsonSerializer.Serialize(new
                {
                    inputFile = Input.Value,
                    outputFile = Output.Value,
                    pages = new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } },
                    collate = value
                });
            QpdfExecutionResult result = route == "json" ? await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
                    [Input.Value, "--collate=" + value, "--pages", ".", "1", ".", "1", "--", Output.Value]), CancellationToken.None);
            Assert.Equal(valid ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
            if (valid)
            {
                Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes));
                InMemoryQpdfHost reader = new(); reader.AddInput(Input, bytes, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Input, CancellationToken.None);
                Assert.Equal(value == "0" ? 0 : 2, document.PageCount);
            }
            else
            {
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
                Assert.Empty(host.OpenedPaths);
                Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
            }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            log.WriteLine("COLLATION VALUE {0}|{1} status={2}", route, value, result.Status);
        }
        foreach (string route in new[] { "argv", "json" })
        {
            using Host host = await CreateHost("success");
            host.Resume();
            string json = JsonSerializer.Serialize(new
            {
                inputFile = Input.Value,
                check = "",
                showNpages = "",
                pages = new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } },
                collate = "1,1"
            });
            QpdfExecutionResult result = route == "json" ? await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
                    [Input.Value, "--collate=1,1", "--pages", ".", "1", ".", "1", "--", "--check", "--show-npages"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            log.WriteLine("COLLATION INSPECTION {0} preserved", route);
        }
    }
}
