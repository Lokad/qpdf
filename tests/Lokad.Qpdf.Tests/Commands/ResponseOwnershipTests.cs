using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ResponseOwnershipTests(ITestOutputHelper output)
{
    private static readonly QpdfPath Response = QpdfPath.From("arguments.txt");
    private static readonly QpdfPath Output = QpdfPath.From(new string('o', 1024) + ".pdf");
    private static readonly byte[] Prior = [17, 23, 41];

    private sealed class BoundaryHost(string outcome) : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        private readonly string _outcome = outcome;
        internal readonly InMemoryQpdfHost Inner = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly RecordingMemoryBudget Budget = new(64L << 20);
        public IMemoryBudget MemoryBudget => Budget;
        public void Dispose() { Inner.Dispose(); Budget.Dispose(); }
        internal int Reads;
        internal int Closes;
        internal int PdfOpens;
        internal Task Entered => _entered.Task;
        internal void Resume() => _resume.TrySetResult();

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfInput input = await Inner.OpenInputAsync(path, token);
            if (path != Response) { PdfOpens++; }
            if (path == Response && _outcome is "empty" or "memory")
            {
                _entered.TrySetResult();
                await _resume.Task.WaitAsync(token);
            }
            return new Input(this, input);
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => Inner.CreateOutputAsync(path, token);

        private sealed class Input(BoundaryHost host, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;

            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                int bytes = await inner.ReadAsync(offset, destination, token);
                if (Path == Response)
                {
                    host.Reads++;
                    host._entered.TrySetResult();
                    await host._resume.Task.WaitAsync(token);
                    if (host._outcome == "read-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                    if (host._outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
                    if (host._outcome == "no-progress") { return 0; }
                }
                return bytes;
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (Path == Response)
                    {
                        host.Closes++;
                        if (host._outcome == "close-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                    }
                }
                finally { await inner.DisposeAsync(); }
            }
        }
    }

    private static async Task<(BoundaryHost Host, QpdfExecutionResult Result, int Length)> Execute(string outcome, bool unicode, string route, QpdfLimits limits)
    {
        using BoundaryHost host = new(outcome);
        string padding = unicode ? new string('\u00e9', 1024) : new string('o', 1024);
        QpdfPath target = unicode ? QpdfPath.From(padding + ".pdf") : Output;
        host.Inner.AddInput(QpdfPath.From("input.pdf"),
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        string text = route == "response" ? "input.pdf\n--\n" + target.Value + "\n"
            : JsonSerializer.Serialize(new Dictionary<string, string> { ["inputFile"] = "input.pdf", ["outputFile"] = target.Value });
        if (outcome == "memory") { text = new string(' ', 65536) + text; }
        byte[] bytes = outcome == "invalid-utf8" ? [0xff, 0xfe]
            : outcome == "empty" ? [] : Encoding.UTF8.GetBytes(text);
        host.Inner.AddInput(Response, bytes, QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(target, CancellationToken.None))
        {
            await seed.WriteAsync(Prior, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        Assert.True(host.Inner.TryGetOutput(target, out byte[] prior));
        using CancellationTokenSource cancellation = new();
        string[] caller = route == "response" ? ["@arguments.txt"] : ["--job-json-file=arguments.txt"];
        QpdfCommandInvocation invocation = new(caller);
        Task<QpdfExecutionResult> running = route == "job"
            ? Qpdf.ExecuteJobAsync(host, "{\"jobJsonFile\":\"arguments.txt\"}", new QpdfExecutionOptions(limits), cancellation.Token).AsTask()
            : Qpdf.ExecuteAsync(host, invocation, new QpdfExecutionOptions(limits), cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "memory") { host.Budget.RefuseGrowth = true; }
            if (outcome == "cancel") { cancellation.Cancel(); }
            host.Resume();
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(caller, invocation.Arguments);
            Assert.Same(caller[0], invocation.Arguments[0]);
            Assert.Equal(Prior, prior);
            // A newly admitted configuration owner may reject before its first
            // byte read. Keep the same memory limit and before-PDF rejection.
            if (outcome == "memory") { Assert.InRange(host.Reads, 0, 1); }
            else { Assert.Equal(outcome == "empty" ? 0 : 1, host.Reads); }
            Assert.Equal(1, host.Closes);
            Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(host.Inner.TryGetOutput(target, out byte[] published));
            if (outcome == "success")
            {
                Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
                InMemoryQpdfHost validation = new();
                QpdfPath input = QpdfPath.From("published.pdf");
                validation.AddInput(input, published, QpdfInputOwnership.Copy);
                QpdfCheckResult check = await Qpdf.CheckAsync(validation, input, CancellationToken.None);
                Assert.True(check.Passed); Assert.Equal(1, check.PageCount);
            }
            else
            {
                Assert.Equal(outcome is "cancel" or "host-cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
                Assert.Equal(prior, published);
            }
            Assert.True(host.Budget.ReservedBytes < host.Budget.PeakBytes);
            return (host, result, text.Length);
        }
        finally { host.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedTextStaysAdmittedThroughResponseInputClose(bool unicode)
    {
        using RecordingJobHost host = new("input-close", Response, "success", true);
        string text = new(unicode ? '\u00e9' : 'o', 1024);
        host.Inner.AddInput(Response, Encoding.UTF8.GetBytes(text), QpdfInputOwnership.Copy);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        Task<GovernedText> running = ResponseFileExpander.ReadTextFileAsync(host, Response,
            QpdfLimits.Default, governor, CancellationToken.None).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(governor.Snapshot().LiveMemoryBytes >= GovernedText.StringBytes(text.Length));
            host.Resume();
            using GovernedText decoded = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(text, decoded.Value);
        }
        finally
        {
            host.Resume(); (await running.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
            governor.CompleteStorage();
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, host.Budget.ReservedBytes);
        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseAdmissionRefusalPrecedesOpeningThePdf(bool unicode)
    {
        var job = await Execute("memory", unicode, "response", QpdfLimits.Default);
        Assert.Contains(job.Result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0, job.Host.PdfOpens);
    }

    [Fact]
    public async Task CompleteResponseReadAttemptsRetireOnEveryTerminalOutcome()
    {
        var jobs = await CompleteReadAttempts();
        ObserveReadAttempts(jobs);
    }

    private async Task<List<(BoundaryHost Host, QpdfExecutionResult Result, int Length)>> CompleteReadAttempts()
    {
        List<(BoundaryHost Host, QpdfExecutionResult Result, int Length)> jobs = [];
        foreach (string route in new[] { "response", "job", "mixed" })
        { jobs.Add(await Execute("success", false, route, QpdfLimits.Default)); }
        foreach (string outcome in new[] { "read-fault", "close-fault", "invalid-utf8", "cancel", "host-cancel", "no-progress", "empty" })
        { jobs.Add(await Execute(outcome, false, "response", QpdfLimits.Default)); }
        foreach (string outcome in new[] { "invalid-utf8", "empty" })
        { jobs.Add(await Execute(outcome, false, "job", QpdfLimits.Default)); }
        jobs.Add(await Execute("cancel", false, "mixed", QpdfLimits.Default));
        output.WriteLine("CONFIGURATION CONTROLS {0}", jobs.Count);
        return jobs;
    }

    private static void ObserveReadAttempts(List<(BoundaryHost Host, QpdfExecutionResult Result, int Length)> jobs)
    {
        foreach (var job in jobs)
        {
            Assert.Equal(0L, job.Host.Budget.ReservedBytes);
            Assert.Equal(0, job.Host.Budget.LiveReservations);
            Assert.Equal(0L, job.Result.Counters.OpenHandles);
            Assert.Equal(0L, job.Host.Inner.OpenLeaseCount);
        }
        GC.KeepAlive(jobs);
    }
}
