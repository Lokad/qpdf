using Lokad.Qpdf.TestSupport;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ConfigurationNameOwnershipTests(ITestOutputHelper log)
{
    private static readonly QpdfPath InputPath = QpdfPath.From("input.pdf");
    private static readonly QpdfPath OutputPath = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [17, 23, 41];

    private sealed class Host(string outcome) : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal readonly RecordingMemoryBudget Budget = new(16L << 20);
        public IMemoryBudget MemoryBudget => Budget;
        public void Dispose() { Inner.Dispose(); Budget.Dispose(); }

        internal readonly List<string> ConfigurationNames = [];
        internal string? InputName, OutputName, PassName;
        internal int PdfOpens;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => _entered.Task;
        internal void Resume() => _resume.TrySetResult();

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            if (path == InputPath)
            {
                InputName = path.Value; PdfOpens++;
                if (outcome != "budget") { _entered.TrySetResult(); await _resume.Task.WaitAsync(token); }
                if (outcome == "open-fault") { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
                if (outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
            }
            else if (path.Value != "args.txt") { ConfigurationNames.Add(path.Value); }
            IQpdfInput input = await Inner.OpenInputAsync(path, token);
            return path == InputPath ? new Input(input, outcome) : input;
        }

        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            if (path.Value.Length > 100) { PassName = path.Value; }
            else { OutputName = path.Value; }
            IQpdfOutputTransaction output = await Inner.CreateOutputAsync(path, token);
            return outcome == "publication-fault" ? new Output(output) : output;
        }

        private sealed class Input(IQpdfInput inner, string outcome) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
                => outcome == "read-fault" ? throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed) : inner.ReadAsync(offset, destination, token);
            public async ValueTask DisposeAsync()
            {
                try { if (outcome == "close-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); } }
                finally { await inner.DisposeAsync(); }
            }
        }

        private sealed class Output(IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => inner.WriteAsync(bytes, token);
            public ValueTask CommitAsync(CancellationToken token) => throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed record Completed(string Route, string Outcome, Host Host, QpdfExecutionResult Result,
        QpdfExecutionCounters Snapshot);

    private static async Task Seed(Host host, QpdfPath pass)
    {
        host.Inner.AddInput(InputPath, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        foreach (QpdfPath path in new[] { OutputPath, pass })
        {
            await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(path, CancellationToken.None);
            await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        }
    }

    private async Task<Completed> Complete(string route, string outcome)
    {
        using Host host = new(outcome);
        QpdfPath pass = QpdfPath.From(new string(route == "argv-unicode" ? '\u00e9' : 'p', 1024) + ".pdf");
        await Seed(host, pass);
        using CancellationTokenSource cancel = new();
        string[] argv = route == "borrowed"
            ? [InputPath.Value, "--linearize", "--linearize-pass1", pass.Value, OutputPath.Value]
            : [InputPath.Value, "--linearize", "--linearize-pass1=" + pass.Value, OutputPath.Value];
        string json = JsonSerializer.Serialize(new { inputFile = InputPath.Value, outputFile = OutputPath.Value, linearize = "", linearizePass1 = pass.Value });
        Task<QpdfExecutionResult> running;
        if (route == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(new[] { "@args.txt" }), cancel.Token).AsTask();
        }
        else if (route == "mixed")
        {
            host.Inner.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
            string[] mixed = ["--job-json-file=settings.json", .. argv];
            running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(mixed), cancel.Token).AsTask();
        }
        else if (route is "file" or "nested")
        {
            host.Inner.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes(route == "file" ? json : "{\"jobJsonFile\":\"child.json\"}"), QpdfInputOwnership.Copy);
            if (route == "nested")
            {
                host.Inner.AddInput(QpdfPath.From("child.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
                running = Qpdf.ExecuteJobAsync(host, "{\"jobJsonFile\":\"settings.json\"}", cancel.Token).AsTask();
            }
            else { running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(new[] { "--job-json-file=settings.json" }), cancel.Token).AsTask(); }
        }
        else if (route == "job") { running = Qpdf.ExecuteJobAsync(host, json, cancel.Token).AsTask(); }
        else { running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), cancel.Token).AsTask(); }
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            // Observe the host's allowance while the actual input callback is pending.
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "cancel") { cancel.Cancel(); }
            host.Resume();
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome switch
            { "success" => QpdfExecutionStatus.Completed, "cancel" or "host-cancel" => QpdfExecutionStatus.Canceled, _ => QpdfExecutionStatus.Failed }, result.Status);
            Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            if (outcome == "success")
            {
                Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] published));
                InMemoryQpdfHost validation = new(); QpdfPath input = QpdfPath.From("published.pdf");
                validation.AddInput(input, published, QpdfInputOwnership.Copy);
                QpdfCheckResult check = await Qpdf.CheckAsync(validation, input, CancellationToken.None);
                Assert.True(check.Passed); Assert.Equal(1, check.PageCount);
                Assert.True(host.Inner.TryGetOutput(pass, out byte[] sidecar)); Assert.True(sidecar.Length > 100);
            }
            else { Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior)); Assert.Equal(Prior, prior); }
            log.WriteLine("SETTING CONTROL PASS {0}|{1}", route, outcome);
            Assert.True(host.Budget.ReservedBytes < host.Budget.PeakBytes);
            return new Completed(route, outcome, host, result, result.Counters with { });
        }
        finally { host.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }

    }

    private async Task<List<Completed>> CompleteJobs()
    {
        List<Completed> jobs = [];
        foreach (string route in new[] { "argv", "argv-unicode", "borrowed", "response", "mixed", "job", "file", "nested" })
        { jobs.Add(await Complete(route, "success")); }
        // The shared lifetime owns failures, independently of the syntax adapter.
        foreach (string outcome in new[] { "publication-fault", "open-fault", "read-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await Complete("argv-unicode", outcome)); }
        return jobs;
    }

    private static List<string> CheckAliases(List<Completed> jobs)
    {
        List<string> defects = [];
        foreach (Completed job in jobs)
        {
            // Host callbacks own any identifiers they retain after completion.
            // Check persisted content and the invocation's private refunds.
            if (job.Host.PassName is string pass && pass != new string(job.Route == "argv-unicode" ? '\u00e9' : 'p', 1024) + ".pdf")
            { defects.Add(job.Route + "|" + job.Outcome + "|pass"); }
            if (job.Host.InputName is string input && input != InputPath.Value)
            { defects.Add(job.Route + "|" + job.Outcome + "|input"); }
            foreach (string name in job.Host.ConfigurationNames)
            { if (name is not ("settings.json" or "child.json")) { defects.Add(job.Route + "|" + job.Outcome + "|include:" + name); } }
            if (job.Host.OutputName is string output && output != OutputPath.Value)
            { defects.Add(job.Route + "|" + job.Outcome + "|output"); }
        }
        return defects;
    }

    private void Observe(List<Completed> jobs)
    {

        List<string> defects = CheckAliases(jobs);
        foreach (string defect in defects) { log.WriteLine("SETTING OWNERSHIP FAIL {0}", defect); }
        Assert.True(defects.Count == 0, string.Join(Environment.NewLine, defects));
        foreach (Completed job in jobs)
        { job.Host.InputName = null; job.Host.OutputName = null; job.Host.PassName = null; job.Host.ConfigurationNames.Clear(); }

        foreach (Completed job in jobs)
        {
            Assert.Equal(0L, job.Host.Budget.ReservedBytes);
            Assert.Equal(0, job.Host.Budget.LiveReservations);
            Assert.Equal(job.Snapshot, job.Result.Counters);
            Assert.Equal(0L, job.Host.Inner.OpenLeaseCount);
        }
        GC.KeepAlive(jobs);
    }

    [Fact]
    public async Task CompleteConfigurationJobsFundNamesAndRetirePrivateState()
    {
        List<Completed> jobs = await CompleteJobs();
        Observe(jobs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationNameRefusalPreservesEveryTarget(bool unicode)
    {
        QpdfPath pass = QpdfPath.From(new string(unicode ? '\u00e9' : 'p', 2048) + ".pdf");
        foreach (bool inline in new[] { true, false })
        {
            using Host host = new("budget"); await Seed(host, pass);
            host.Budget.RefuseGrowth = true;
            string[] argv = inline
                ? [InputPath.Value, "--linearize", "--linearize-pass1=" + pass.Value, OutputPath.Value]
                : [InputPath.Value, "--linearize", "--linearize-pass1", pass.Value, OutputPath.Value];
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv),
                CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal(0, host.PdfOpens);
            Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior)); Assert.Equal(Prior, prior);
            Assert.True(host.Inner.TryGetOutput(pass, out byte[] sidecar)); Assert.Equal(Prior, sidecar);
            log.WriteLine("SETTING ADMISSION PASS {0}|{1}", unicode, inline);
        }
    }
}
