using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ResponseOwnershipTests(ITestOutputHelper output)
{
    private static readonly QpdfPath Response = QpdfPath.From("arguments.txt");
    private static readonly QpdfPath Output = QpdfPath.From(new string('o', 1024) + ".pdf");
    private static readonly byte[] Prior = [17, 23, 41];

    private sealed class BoundaryHost(string outcome) : IQpdfHost
    {
        private readonly string _outcome = outcome;
        internal readonly InMemoryQpdfHost Inner = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ResourceGovernor? Governor;
        internal long ReadMemory;
        internal long CloseMemory;
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

            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            {
                ReadOnlyMemory<byte> bytes = await inner.ReadAsync(offset, count, token);
                if (Path == Response)
                {
                    host.Reads++;
                    host._entered.TrySetResult();
                    await host._resume.Task.WaitAsync(token);
                    host.ReadMemory = (host.Governor ?? throw new InvalidOperationException("Missing complete-job governor.")).Snapshot().LiveMemoryBytes;
                    if (host._outcome == "read-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                    if (host._outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
                    if (host._outcome == "no-progress") { return ReadOnlyMemory<byte>.Empty; }
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
                        host.CloseMemory = (host.Governor ?? throw new InvalidOperationException("Missing complete-job governor.")).Snapshot().LiveMemoryBytes;
                        if (host._outcome == "close-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                    }
                }
                finally { await inner.DisposeAsync(); }
            }
        }
    }

    private static ResourceGovernor FindGovernor(Task running)
    {
        const BindingFlags members = BindingFlags.Static | BindingFlags.NonPublic;
        return (ResourceGovernor)(typeof(ExecutionCounterTests).GetMethod("FindGovernor", members)?.Invoke(null,
            [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
            ?? throw new InvalidOperationException("Missing original complete-job governor observer."));
    }

    private static async Task<(BoundaryHost Host, QpdfExecutionResult Result, ResourceGovernor Governor, int Length)> Execute(string outcome, bool unicode, string route, QpdfLimits limits)
    {
        BoundaryHost host = new(outcome);
        string padding = unicode ? new string('\u00e9', 1024) : new string('o', 1024);
        QpdfPath target = unicode ? QpdfPath.From(padding + ".pdf") : Output;
        host.Inner.AddInput(QpdfPath.From("input.pdf"),
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        string text = route == "response" ? "input.pdf\n--\n" + target.Value + "\n"
            : JsonSerializer.Serialize(new Dictionary<string, string> { ["inputFile"] = "input.pdf", ["outputFile"] = target.Value });
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
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        ResourceGovernor governor = FindGovernor(running);
        host.Governor = governor;
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
        return (host, result, governor, text.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedTextStaysAdmittedThroughResponseInputClose(bool unicode)
    {
        var job = await Execute("success", unicode, "response", QpdfLimits.Default);
        Assert.True(job.Host.CloseMemory >= GovernedText.StringBytes(job.Length),
            "Response close retains decoded text of " + GovernedText.StringBytes(job.Length) +
            " bytes while the actual job funds only " + job.Host.CloseMemory + ".");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortDecodedTextAdmissionRejectsBeforeOpeningThePdf(bool unicode)
    {
        var job = await Execute("memory", unicode, "response", QpdfLimits.Default with { MaxMemoryBytes = 3000L });
        Assert.Contains(job.Result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0, job.Host.PdfOpens);
    }

    [Fact]
    public async Task CompleteResponseReadAttemptsRetireOnEveryTerminalOutcome()
    {
        var jobs = await CompleteReadAttempts();
        ObserveReadAttempts(jobs);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<List<(BoundaryHost Host, QpdfExecutionResult Result, ResourceGovernor Governor, int Length)>> CompleteReadAttempts()
    {
        List<(BoundaryHost Host, QpdfExecutionResult Result, ResourceGovernor Governor, int Length)> jobs = [];
        foreach (string route in new[] { "response", "job", "mixed" })
        foreach (string outcome in new[] { "success", "read-fault", "close-fault", "invalid-utf8", "cancel", "host-cancel", "no-progress", "empty" })
        {
            jobs.Add(await Execute(outcome, false, route, QpdfLimits.Default));
            output.WriteLine("CONFIGURATION CONTROL PASS {0}|{1}", route, outcome);
        }
        return jobs;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ObserveReadAttempts(List<(BoundaryHost Host, QpdfExecutionResult Result, ResourceGovernor Governor, int Length)> jobs)
    {
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            foreach (var job in jobs) { job.Governor.ReserveMemory(0L); }
        }
        foreach (var job in jobs)
        {
            Assert.Equal(256L, job.Governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(0L, job.Governor.Snapshot().OpenHandles);
            Assert.Equal(0L, job.Host.Inner.OpenLeaseCount);
        }
        GC.KeepAlive(jobs);
    }
}
