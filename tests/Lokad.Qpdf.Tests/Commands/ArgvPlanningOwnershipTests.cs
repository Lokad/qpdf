using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class ArgvPlanningOwnershipTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [17, 23, 41];

    private sealed class Host(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
        public IMemoryBudget MemoryBudget => budget;
        internal int Opens;
        internal bool Pause;
        internal string Outcome = "success";
        internal readonly TaskCompletionSource InputReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource InputResume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource OutputReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource OutputResume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            if (path == Input)
            {
                Opens++;
                if (Pause && Opens == 1) { InputReached.SetResult(); await InputResume.Task; }
                if (Outcome == "open-fault") { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
            }
            return await Inner.OpenInputAsync(path, token);
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            if (Pause && !OutputReached.Task.IsCompleted) { OutputReached.SetResult(); await OutputResume.Task; }
            if (Outcome == "create-fault") { throw new QpdfHostException(path, QpdfHostFailureReason.WriteFailed); }
            if (Outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
            return new Transaction(await Inner.CreateOutputAsync(path, token), Outcome);
        }
        private sealed class Transaction(IQpdfOutputTransaction inner, string outcome) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
                => outcome == "write-fault" ? throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed) : inner.WriteAsync(data, token);
            public ValueTask CommitAsync(CancellationToken token)
                => outcome == "commit-fault" ? throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            {
                try { if (outcome == "close-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); } }
                finally { await inner.DisposeAsync(); }
            }
        }
    }

    private static string[] Arguments(string shape)
    {
        List<string> arguments = [];
        if (shape == "global")
        { for (int i = 0; i < 128; i++) { arguments.Add("--global"); arguments.Add("--"); } }
        arguments.Add(Input.Value);
        if (shape == "pages")
        {
            arguments.Add("--pages");
            for (int i = 0; i < 1000; i++) { arguments.Add("."); arguments.Add("1"); }
            arguments.Add("--");
        }
        arguments.Add(Output.Value); return arguments.ToArray();
    }

    private static async Task<Host> CreateHost(IMemoryBudget budget)
    {
        Host host = new(budget);
        host.Inner.AddInput(Input, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    private static string[] SmallArguments(string shape)
        => shape switch
        {
            "global" => ["--global", "--", "--global", "--max-stream-filters=25", "--", Input.Value, Output.Value],
            "pages" => [Input.Value, "--pages", ".", "1", ".", "1", "--", Output.Value],
            _ => [Input.Value, Output.Value],
        };

    private static Task<QpdfExecutionResult> Start(Host host, string route, string[] arguments, CancellationToken token)
    {
        if (route == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', arguments)), QpdfInputOwnership.Copy);
            arguments = ["@args.txt"];
        }
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(arguments), token).AsTask();
    }

    [Theory]
    [InlineData("global")]
    [InlineData("pages")]
    public async Task ConfigurationRefusalPrecedesPdfAccess(string shape)
    {
        RecordingMemoryBudget root = new(16777216L);
        using IMemoryScope caller = root.CreateScope();
        Host host = await CreateHost(caller);
        string[] arguments = Arguments(shape);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(arguments),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxMemoryBytes = 8192L }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0, host.Opens);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
        caller.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("pages", "argv", "success")]
    [InlineData("global", "response", "success")]
    [InlineData("bare", "response", "open-fault")]
    [InlineData("pages", "argv", "write-fault")]
    [InlineData("global", "response", "commit-fault")]
    [InlineData("pages", "argv", "close-fault")]
    [InlineData("pages", "response", "cancel")]
    [InlineData("bare", "argv", "host-cancel")]
    public async Task PublicConsumersKeepAdmissionThroughPendingIoAndRefundAtOwnerClose(string shape, string route, string outcome)
    {
        RecordingMemoryBudget root = new(16777216L);
        using IMemoryScope caller = root.CreateScope();
        Host host = await CreateHost(caller); host.Pause = true; host.Outcome = outcome;
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> running = Start(host, route, SmallArguments(shape), cancellation.Token);
        try
        {
            await host.InputReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(root.ReservedBytes > 0);
            host.InputResume.TrySetResult();
            if (outcome != "open-fault")
            {
                await host.OutputReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(root.ReservedBytes > 0);
                Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
                if (outcome == "cancel") { cancellation.Cancel(); }
                host.OutputResume.TrySetResult();
            }
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed
                : outcome is "cancel" or "host-cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes));
            if (outcome is "success" or "close-fault")
            {
                using InMemoryQpdfHost reader = new(); reader.AddInput(Input, bytes, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Input, CancellationToken.None);
                Assert.Equal(shape == "pages" ? 2 : 1, document.PageCount);
            }
            else { Assert.Equal(Prior, bytes); }
            caller.Dispose();
            Assert.Equal(0L, root.ReservedBytes);
        }
        finally
        {
            host.InputResume.TrySetResult(); host.OutputResume.TrySetResult();
            await running;
        }
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("response")]
    public async Task GlobalsGrammarAndPreCancellationRemainObservable(string route)
    {
        foreach ((string[] arguments, bool valid) in new[]
        {
            (new[] { "--global", "--", Input.Value, Output.Value }, true),
            (SmallArguments("global"), true),
            (new[] { Input.Value, "--max-stream-filters=25", Output.Value }, true),
            (new[] { "--global", "--", "--global", "--", Input.Value, Output.Value }, true),
            (new[] { "--global", "--max-stream-filters=25" }, false),
            (new[] { "--global", "--bogus", "--", Input.Value, Output.Value }, false),
            (new[] { "--global", "--global", "--", Input.Value, Output.Value }, false),
            (new[] { Input.Value, "--max-stream-filters=25", "--max-stream-filters=26", Output.Value }, true),
        })
        {
            RecordingMemoryBudget root = new(16777216L);
            using IMemoryScope caller = root.CreateScope();
            Host host = await CreateHost(caller);
            QpdfExecutionResult result = await Start(host, route, arguments, CancellationToken.None);
            Assert.Equal(valid ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
            if (!valid) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments); Assert.Equal(0, host.Opens); }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            caller.Dispose(); Assert.Equal(0L, root.ReservedBytes);
        }
        RecordingMemoryBudget canceledRoot = new(16777216L);
        using IMemoryScope canceledCaller = canceledRoot.CreateScope();
        Host canceledHost = await CreateHost(canceledCaller);
        QpdfExecutionResult canceled = await Start(canceledHost, route, SmallArguments("pages"), new CancellationToken(true));
        Assert.Equal(QpdfExecutionStatus.Canceled, canceled.Status);
        Assert.Equal(0, canceledHost.Opens); Assert.Equal(0L, canceledHost.Inner.OpenLeaseCount);
        Assert.True(canceledHost.Inner.TryGetOutput(Output, out byte[] kept)); Assert.Equal(Prior, kept);
        canceledCaller.Dispose(); Assert.Equal(0L, canceledRoot.ReservedBytes);
    }
}
