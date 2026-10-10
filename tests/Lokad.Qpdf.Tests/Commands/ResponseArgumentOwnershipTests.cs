using System.Reflection;
using System.Runtime.CompilerServices;
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

    private sealed class PausedHost(string outcome) : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
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
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            {
                if (outcome == "read-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                if (outcome == "no-progress") { return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); }
                return inner.ReadAsync(offset, count, token);
            }
            public async ValueTask DisposeAsync()
            {
                try { if (outcome == "close-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); } }
                finally { await inner.DisposeAsync(); }
            }
        }
    }

    private static ResourceGovernor FindGovernor(Task running)
    {
        const BindingFlags members = BindingFlags.Static | BindingFlags.NonPublic;
        return (ResourceGovernor)(typeof(ExecutionCounterTests).GetMethod("FindGovernor", members)?.Invoke(null,
            [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
            ?? throw new InvalidOperationException("Missing complete-job governor observer."));
    }

    private static object? FindExpansion(object current, int depth, HashSet<object> seen)
    {
        if (depth > 12 || current is string || !seen.Add(current)) { return null; }
        Type type = current.GetType();
        if (type.FullName == "Lokad.Qpdf.Commands.ExpandedArguments") { return current; }
        for (Type? next = type; next is not null; next = next.BaseType)
        foreach (FieldInfo field in next.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            object? value = field.GetValue(current);
            if (value is null || field.FieldType.IsPrimitive || field.FieldType.IsEnum) { continue; }
            object? found = FindExpansion(value, depth + 1, seen);
            if (found is not null) { return found; }
        }
        return null;
    }

    private sealed class CountingHost(InMemoryQpdfHost inner) : IQpdfHost
    {
        internal int PdfOpens;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            if (path.Value == "input.pdf") { PdfOpens++; }
            return inner.OpenInputAsync(path, token);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => inner.CreateOutputAsync(path, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortExpansionAdmissionRejectsBeforeOpeningThePdf(bool unicode)
    {
        InMemoryQpdfHost inner = new();
        CountingHost host = new(inner);
        QpdfPath target = QpdfPath.From(new string(unicode ? '\u00e9' : 'o', 1024) + ".pdf");
        inner.AddInput(QpdfPath.From("input.pdf"),
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        inner.AddInput(First, Encoding.UTF8.GetBytes("input.pdf\n--\n" + target.Value + "\n"), QpdfInputOwnership.Copy);
        inner.AddInput(Next, [], QpdfInputOwnership.Copy);
        byte[] prior = [17, 23, 41];
        await using (IQpdfOutputTransaction seed = await inner.CreateOutputAsync(target, CancellationToken.None))
        { await seed.WriteAsync(prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        string[] caller = ["@first.txt", "@next.txt"];
        QpdfCommandInvocation invocation = new(caller);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, invocation,
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxMemoryBytes = 6144L }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0, host.PdfOpens);
        Assert.Equal(0L, inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(caller, invocation.Arguments);
        Assert.True(inner.TryGetOutput(target, out byte[] published)); Assert.Equal(prior, published);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedResponseArgumentsStayFundedAtTheNextHostOpen(bool unicode)
    {
        PausedHost host = new("success");
        string name = new(unicode ? '\u00e9' : 'o', 1024);
        QpdfPath output = QpdfPath.From(name + ".pdf");
        host.Inner.AddInput(QpdfPath.From("input.pdf"),
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        host.Inner.AddInput(First, Encoding.UTF8.GetBytes("input.pdf\n--\n" + output.Value + "\n"), QpdfInputOwnership.Copy);
        host.Inner.AddInput(Next, [], QpdfInputOwnership.Copy);
        string[] caller = ["@first.txt", "@next.txt"];
        QpdfCommandInvocation invocation = new(caller);
        Task<QpdfExecutionResult> running = Qpdf.ExecuteAsync(host, invocation, CancellationToken.None).AsTask();
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        ResourceGovernor governor = FindGovernor(running);
        long admitted = governor.Snapshot().LiveMemoryBytes;
        log.WriteLine("ARGUMENT OBSERVATION token|{0}: minimum={1}, live={2}, peak={3}", unicode,
            GovernedText.StringBytes(output.Value.Length), admitted, governor.Snapshot().PeakMemoryBytes);
        host.Resume();
        QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(caller, invocation.Arguments);
        Assert.Same(caller[0], invocation.Arguments[0]);
        Assert.Same(caller[1], invocation.Arguments[1]);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(output, out byte[] published));
        InMemoryQpdfHost validation = new();
        QpdfPath input = QpdfPath.From("published.pdf");
        validation.AddInput(input, published, QpdfInputOwnership.Copy);
        QpdfCheckResult check = await Qpdf.CheckAsync(validation, input, CancellationToken.None);
        Assert.True(check.Passed); Assert.Equal(1, check.PageCount);
        Assert.True(admitted >= GovernedText.StringBytes(output.Value.Length),
            "The complete rewrite retains a generated " + GovernedText.StringBytes(output.Value.Length) +
            "-byte output token at the second response open while funding only " + admitted + " bytes.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpansionVectorGrowsWithinAdmissionAndBorrowsCallerStrings(bool response)
    {
        PausedHost host = new("success");
        QpdfPath target = QpdfPath.From(new string('o', 2048) + ".pdf");
        host.Inner.AddInput(QpdfPath.From("input.pdf"),
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        string[] original = ["input.pdf", .. Enumerable.Repeat("--stream-data=preserve", 40), "--", target.Value];
        string[] caller;
        if (response)
        {
            host.Inner.AddInput(First, Encoding.UTF8.GetBytes(string.Join('\n', original) + "\n"), QpdfInputOwnership.Copy);
            caller = ["@first.txt", "@next.txt"];
        }
        else { caller = [.. original, "@next.txt"]; }
        host.Inner.AddInput(Next, [], QpdfInputOwnership.Copy);
        QpdfCommandInvocation invocation = new(caller);
        Task<QpdfExecutionResult> running = Qpdf.ExecuteAsync(host, invocation, CancellationToken.None).AsTask();
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        ResourceGovernor governor = FindGovernor(running);
        long admitted = governor.Snapshot().LiveMemoryBytes;
        object? owner = FindExpansion(running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        List<string>? vector = owner?.GetType().GetField("_values", members)?.GetValue(owner) as List<string>;
        ParserMemoryScope? memory = owner?.GetType().GetField("_memory", members)?.GetValue(owner) as ParserMemoryScope;
        long reserved = memory?.ReservedBytes ?? 0L;
        long minimum = 32L + 32L + IntPtr.Size * (long)(vector?.Capacity ?? original.Length);
        log.WriteLine("ARGUMENT OBSERVATION vector|{0}: minimum={1}, reserved={2}, live={3}", response, minimum, reserved, admitted);
        host.Resume();
        QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.Equal(caller, invocation.Arguments);
        Assert.True(reserved >= minimum, "Expansion vector needs at least " + minimum + " bytes while its owner funds only " + reserved + ".");
        if (!response)
        {
            Assert.Same(target.Value, host.OutputName);
            Assert.True(admitted < GovernedText.StringBytes(target.Value.Length), "Caller-owned path text must remain borrowed.");
        }
        Assert.True(host.Inner.TryGetOutput(target, out byte[] published));
        InMemoryQpdfHost validation = new();
        QpdfPath input = QpdfPath.From("published.pdf");
        validation.AddInput(input, published, QpdfInputOwnership.Copy);
        QpdfCheckResult check = await Qpdf.CheckAsync(validation, input, CancellationToken.None);
        Assert.True(check.Passed); Assert.Equal(1, check.PageCount);
    }

    private sealed class Completed(PausedHost host, QpdfExecutionResult result, ResourceGovernor governor, WeakReference expansion)
    {
        internal readonly PausedHost Host = host;
        internal QpdfExecutionResult? Result = result;
        internal QpdfExecutionCounters? Counters = result.Counters;
        internal readonly ResourceGovernor Governor = governor;
        internal readonly WeakReference Expansion = expansion;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SnapshotExpansion(Task running)
        => new(FindExpansion(running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)));

    private static async Task<Completed> Complete(string outcome)
    {
        PausedHost host = new(outcome);
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
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        ResourceGovernor governor = FindGovernor(running);
        WeakReference expansion = SnapshotExpansion(running);
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
        return new(host, result, governor, expansion);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckAliases(PausedHost host, ResourceGovernor governor)
    {
        long minimum = 256L + GovernedText.StringBytes((host.FirstName ?? "").Length) + GovernedText.StringBytes((host.NextName ?? "").Length);
        if (host.GeneratedOutput) { minimum += GovernedText.StringBytes((host.OutputName ?? "").Length); }
        if (host.GeneratedInput) { minimum += GovernedText.StringBytes((host.InputName ?? "").Length); }
        Assert.True(governor.Snapshot().LiveMemoryBytes >= minimum, "Surviving generated host names require " + minimum + " funded bytes.");
        CheckString(host.FirstName, governor); CheckString(host.NextName, governor);
        if (host.GeneratedOutput) { CheckString(host.OutputName, governor); }
        if (host.GeneratedInput) { CheckString(host.InputName, governor); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckString(string? text, ResourceGovernor governor)
    {
        if (text is null || text.Length == 0) { return; }
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        object? owner = typeof(ResourceGovernor).GetField("_valueOwners", members)?.GetValue(governor);
        object? segment = owner?.GetType().GetField("_segments", members)?.GetValue(owner);
        while (segment is not null)
        {
            if (segment.GetType().GetField("Values", members)?.GetValue(segment) is ConditionalWeakTable<object, RetainedValueOwners.Receipt> values
                && values.TryGetValue(text, out RetainedValueOwners.Receipt? receipt))
            { Assert.True(receipt.Bytes >= GovernedText.StringBytes(text.Length)); return; }
            segment = segment.GetType().GetField("Next", members)?.GetValue(segment);
        }
        Assert.Fail("The independently surviving generated host name has no storage receipt.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropAliases(PausedHost host)
    { host.FirstName = null; host.NextName = null; host.InputName = null; host.OutputName = null; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReportRetained(ResourceGovernor governor)
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        object? owner = typeof(ResourceGovernor).GetField("_valueOwners", members)?.GetValue(governor);
        object? segment = owner?.GetType().GetField("_segments", members)?.GetValue(owner);
        while (segment is not null)
        {
            if (segment.GetType().GetField("Values", members)?.GetValue(segment) is ConditionalWeakTable<object, RetainedValueOwners.Receipt> values)
            {
                foreach (var row in values)
                {
                    if (row.Key is string text) { log.WriteLine("RETAINED TOKEN {0}: {1}", text.Length, text[..Math.Min(16, text.Length)]); }
                }
            }
            segment = segment.GetType().GetField("Next", members)?.GetValue(segment);
        }
    }

    [Fact]
    public async Task CompleteExpansionJobsFundSurvivingAliasesAndRetirePrivateStorage()
    {
        List<Completed> jobs = await CompleteJobs();
        ObserveCompletedJobs(jobs);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ObserveCompletedJobs(List<Completed> jobs)
    {
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            foreach (var job in jobs) { job.Governor.ReserveMemory(0L); }
        }
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            Assert.True(!job.Expansion.IsAlive, "Job " + i + " keeps its expansion owner alive.");
            CheckAliases(job.Host, job.Governor); DropAliases(job.Host);
        }
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            foreach (var job in jobs) { job.Governor.ReserveMemory(0L); }
        }
        foreach (var job in jobs)
        {
            Assert.False(job.Expansion.IsAlive);
            Assert.Equal(0L, job.Governor.Snapshot().OpenHandles);
            job.Result = null;
        }
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            foreach (var job in jobs) { job.Governor.ReserveMemory(0L); }
        }
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            long live = job.Governor.Snapshot().LiveMemoryBytes;
            if (live != 144L) { ReportRetained(job.Governor); }
            Assert.True(live == 144L, "Job " + i + " retains " + live + " bytes after its host/result aliases drop.");
            Assert.Equal(0L, job.Governor.Snapshot().OpenHandles);
            Assert.Equal(0L, job.Host.Inner.OpenLeaseCount);
            job.Counters = null;
        }
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            foreach (var job in jobs) { job.Governor.ReserveMemory(0L); }
        }
        foreach (var job in jobs) { Assert.Equal(0L, job.Governor.Snapshot().LiveMemoryBytes); }
        GC.KeepAlive(jobs);
    }
}
