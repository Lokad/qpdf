using Lokad.Qpdf.Commands;
using Lokad.Qpdf.TestSupport;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ResponseArgumentOwnershipTests(ITestOutputHelper log)
{
    private static readonly QpdfPath First = QpdfPath.From("first.txt");
    private static readonly QpdfPath Next = QpdfPath.From("next.txt");

    private sealed class PausedHost(string outcome) : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal readonly RecordingMemoryBudget Budget = new(64L << 20);
        public IMemoryBudget MemoryBudget => Budget;
        public void Dispose() { Inner.Dispose(); Budget.Dispose(); }
        internal string? FirstName, NextName, InputName, OutputName;
        internal int PdfOpens;
        internal bool GeneratedOutput => outcome is "success" or "crlf" or "publication-fault";
        internal bool GeneratedInput => outcome != "empty";
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => _entered.Task;
        internal void Resume() => _resume.TrySetResult();

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            if (path == First) { FirstName = path.Value; }
            if (path == Next)
            {
                NextName = path.Value;
                _entered.TrySetResult();
                await _resume.Task.WaitAsync(token);
                if (outcome == "open-fault") { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
                if (outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
            }
            else if (path != First) { InputName = path.Value; PdfOpens++; }
            IQpdfInput input = await Inner.OpenInputAsync(path, token);
            return path == Next ? new Input(input, outcome) : input;
        }

        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            OutputName = path.Value;
            IQpdfOutputTransaction transaction = await Inner.CreateOutputAsync(path, token);
            return outcome == "publication-fault" ? new Output(transaction) : transaction;
        }

        private sealed class Output(IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => inner.WriteAsync(bytes, token);
            public ValueTask CommitAsync(CancellationToken token) => throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class Input(IQpdfInput inner, string outcome) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                if (outcome == "read-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                if (outcome == "no-progress") { return ValueTask.FromResult(0); }
                return inner.ReadAsync(offset, destination, token);
            }
            public async ValueTask DisposeAsync()
            {
                try { if (outcome == "close-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); } }
                finally { await inner.DisposeAsync(); }
            }
        }
    }

    [Fact]
    public void ExpansionRefusalPreservesEarlierBorrowedValuesAndRefundsItsOwner()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        string caller = new('c', 1024);
        using (ExpandedArguments expanded = ExpandedArguments.Create(governor))
        {
            expanded.AddBorrowed(caller);
            host.Budget.RefuseGrowth = true;
            long before = governor.Snapshot().LiveMemoryBytes;
            string large = new('x', 65536);
            Assert.Throws<QpdfMemoryAdmissionException>(() => expanded.CopySubstring(large, 1, large.Length - 1));
            Assert.Same(caller, Assert.Single(expanded.Values));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        governor.CompleteStorage();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, host.Budget.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedResponseArgumentsStayFundedAtTheNextHostOpen(bool unicode)
    {
        using PausedHost host = new("success");
        string name = new(unicode ? '\u00e9' : 'o', 1024);
        QpdfPath target = QpdfPath.From(name + ".pdf");
        host.Inner.AddInput(First, Encoding.UTF8.GetBytes("input.pdf\n--\n" + target.Value + "\n"), QpdfInputOwnership.Copy);
        host.Inner.AddInput(Next, [], QpdfInputOwnership.Copy);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        Task<ExpandedArguments> running = ResponseFileExpander.ExpandAsync(host, ["@first.txt", "@next.txt"],
            QpdfLimits.Default, governor, CancellationToken.None).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(governor.Snapshot().LiveMemoryBytes >= GovernedText.StringBytes(target.Value.Length));
            host.Resume();
            using ExpandedArguments expanded = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "input.pdf", "--", target.Value }, expanded.Values);
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
    public async Task ExpansionVectorGrowsWithinAdmissionAndBorrowsCallerStrings(bool response)
    {
        using PausedHost host = new("success");
        QpdfPath target = QpdfPath.From(new string('o', 2048) + ".pdf");
        string[] original = ["input.pdf", .. Enumerable.Repeat("--stream-data=preserve", 40), "--", target.Value];
        string[] caller;
        if (response)
        {
            host.Inner.AddInput(First, Encoding.UTF8.GetBytes(string.Join('\n', original) + "\n"), QpdfInputOwnership.Copy);
            caller = ["@first.txt", "@next.txt"];
        }
        else { caller = [.. original, "@next.txt"]; }
        host.Inner.AddInput(Next, [], QpdfInputOwnership.Copy);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        Task<ExpandedArguments> running = ResponseFileExpander.ExpandAsync(host, caller,
            QpdfLimits.Default, governor, CancellationToken.None).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            long admitted = governor.Snapshot().LiveMemoryBytes;
            host.Resume();
            using ExpandedArguments expanded = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(original, expanded.Values);
            Assert.True(admitted >= IntPtr.Size * (long)original.Length);
            if (!response)
            {
                for (int index = 0; index < original.Length; index++) { Assert.Same(original[index], expanded.Values[index]); }
                Assert.True(admitted < GovernedText.StringBytes(target.Value.Length));
            }
        }
        finally
        {
            host.Resume(); (await running.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
            governor.CompleteStorage();
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, host.Budget.ReservedBytes);
        }
    }

    private sealed record Completed(PausedHost Host, QpdfExecutionResult Result, QpdfExecutionCounters Snapshot);

    private static async Task<Completed> Complete(string outcome)
    {
        using PausedHost host = new(outcome);
        QpdfPath target = QpdfPath.From(new string('o', 1024) + ".pdf");
        host.Inner.AddInput(QpdfPath.From("input.pdf"),
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        string first = outcome == "whole" ? "input.pdf" : outcome == "empty" ? "" : "input.pdf\n--\n" + target.Value + "\n";
        if (outcome == "crlf") { first = first.Replace("\n", "\r\n", StringComparison.Ordinal); }
        if (outcome == "empty-token") { first += "\n"; }
        host.Inner.AddInput(First, Encoding.UTF8.GetBytes(first), QpdfInputOwnership.Copy);
        byte[] next = outcome == "invalid-utf8" ? [0xff, 0xfe] : outcome is "read-fault" or "no-progress" ? "unused\n"u8.ToArray() : [];
        host.Inner.AddInput(Next, next, QpdfInputOwnership.Copy);
        byte[] prior = [17, 23, 41];
        await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(target, CancellationToken.None))
        { await seed.WriteAsync(prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource cancel = new();
        string[] caller = outcome == "whole" ? ["@first.txt", "--", target.Value, "@next.txt"]
            : outcome == "empty" ? ["input.pdf", "--", target.Value, "@first.txt", "@next.txt"] : ["@first.txt", "@next.txt"];
        QpdfCommandInvocation invocation = new(caller);
        Task<QpdfExecutionResult> running = Qpdf.ExecuteAsync(host, invocation, cancel.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "cancel") { cancel.Cancel(); }
            host.Resume();
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(caller, invocation.Arguments);
            for (int i = 0; i < caller.Length; i++) { Assert.Same(caller[i], invocation.Arguments[i]); }
            Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(host.Inner.TryGetOutput(target, out byte[] published));
            bool success = outcome is "success" or "whole" or "empty" or "crlf";
            Assert.Equal(success ? QpdfExecutionStatus.Completed : outcome is "cancel" or "host-cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (success)
            {
                InMemoryQpdfHost validation = new();
                QpdfPath input = QpdfPath.From("published.pdf");
                validation.AddInput(input, published, QpdfInputOwnership.Copy);
                QpdfCheckResult check = await Qpdf.CheckAsync(validation, input, CancellationToken.None);
                Assert.True(check.Passed); Assert.Equal(1, check.PageCount);
            }
            else { Assert.Equal(prior, published); }
            Assert.True(host.Budget.ReservedBytes < host.Budget.PeakBytes);
            return new(host, result, result.Counters with { });
        }
        finally { host.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }

    }

    private static void CheckAliases(PausedHost host)
    {
        // Identifiers retained by callbacks belong to the host after completion.
        Assert.Equal(First.Value, host.FirstName);
        Assert.Equal(Next.Value, host.NextName);
        if (host.InputName is string input) { Assert.Equal("input.pdf", input); }
        if (host.OutputName is string output) { Assert.Equal(new string('o', 1024) + ".pdf", output); }
    }

    [Fact]
    public async Task CompleteExpansionJobsFundSurvivingAliasesAndRetirePrivateStorage()
    {
        List<Completed> jobs = await CompleteJobs();
        ObserveCompletedJobs(jobs);
    }

    private async Task<List<Completed>> CompleteJobs()
    {
        List<Completed> jobs = [];
        foreach (string outcome in new[] { "success", "whole", "empty", "crlf", "empty-token", "open-fault", "read-fault", "close-fault", "invalid-utf8", "no-progress", "cancel", "host-cancel", "publication-fault" })
        {
            jobs.Add(await Complete(outcome));
            log.WriteLine("ARGUMENT CONTROL PASS {0}", outcome);
        }
        return jobs;
    }

    private void ObserveCompletedJobs(List<Completed> jobs)
    {
        foreach (Completed job in jobs)
        {
            CheckAliases(job.Host);
            Assert.Equal(0L, job.Host.Budget.ReservedBytes);
            Assert.Equal(0, job.Host.Budget.LiveReservations);
            Assert.Equal(job.Snapshot, job.Result.Counters);
            Assert.Equal(0L, job.Result.Counters.OpenHandles);
            Assert.Equal(0L, job.Host.Inner.OpenLeaseCount);
        }
    }
}
