using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ConfigurationNameOwnershipTests(ITestOutputHelper log)
{
    private static readonly QpdfPath InputPath = QpdfPath.From("input.pdf");
    private static readonly QpdfPath OutputPath = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [17, 23, 41];

    private sealed class Host(string outcome) : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
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
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
                => outcome == "read-fault" ? throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed) : inner.ReadAsync(offset, count, token);
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

    private sealed class Completed(string route, string outcome, ResourceGovernor governor, Host host,
        QpdfExecutionResult result, WeakReference plan, bool borrowed)
    {
        internal readonly string Route = route, Outcome = outcome;
        internal readonly ResourceGovernor Governor = governor;
        internal readonly Host Host = host;
        internal readonly WeakReference Plan = plan;
        internal readonly bool Borrowed = borrowed;
        internal QpdfExecutionResult? Result = result;
        internal QpdfExecutionCounters? Counters = result.Counters;
    }

    private static T? Find<T>(object value, int depth, HashSet<object> seen) where T : class
    {
        if (depth > 96 || !seen.Add(value)) { return null; }
        if (value is T found) { return found; }
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        if (value is Task task)
        {
            object? state = task.GetType().GetField("StateMachine", fields)?.GetValue(task);
            return state is null ? null : Find<T>(state, depth + 1, seen);
        }
        foreach (FieldInfo field in value.GetType().GetFields(fields))
        { if (field.GetValue(value) is T direct) { return direct; } }
        foreach (FieldInfo field in value.GetType().GetFields(fields))
        {
            if (field.GetValue(value) is object nested &&
                (nested is Task || nested.GetType().IsValueType && !nested.GetType().IsPrimitive && !nested.GetType().IsEnum))
            {
                T? result = Find<T>(nested, depth + 1, seen);
                if (result is not null) { return result; }
            }
        }
        return null;
    }

    private static bool HasReceipt(ResourceGovernor governor, string value)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo ownerField = typeof(ResourceGovernor).GetField("_valueOwners", fields)
            ?? throw new InvalidOperationException("Missing retained-string observer.");
        object? owner = ownerField.GetValue(governor);
        object? segment = owner?.GetType().GetField("_segments", fields)?.GetValue(owner);
        while (segment is not null)
        {
            if (segment.GetType().GetField("Values", fields)?.GetValue(segment) is ConditionalWeakTable<object, RetainedValueOwners.Receipt> values
                && values.TryGetValue(value, out RetainedValueOwners.Receipt? receipt))
            { return receipt.Bytes >= GovernedText.StringBytes(value.Length); }
            segment = segment.GetType().GetField("Next", fields)?.GetValue(segment);
        }
        return false;
    }

    private static async Task Seed(Host host, QpdfPath pass)
    {
        host.Inner.AddInput(InputPath, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        foreach (QpdfPath path in new[] { OutputPath, pass })
        {
            await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(path, CancellationToken.None);
            await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<Completed> Complete(string route, string outcome)
    {
        Host host = new(outcome);
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
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        ResourceGovernor governor = Find<ResourceGovernor>(running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
            ?? throw new InvalidOperationException("Missing complete-job governor.");
        JobPlan plan = Find<JobPlan>(running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
            ?? throw new InvalidOperationException("Missing complete-job plan.");
        bool borrowed = ReferenceEquals(plan.LinearizePass1?.Value, pass.Value);
        Assert.Equal(route == "borrowed", borrowed);
        WeakReference planReference = new(plan);
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
        return new Completed(route, outcome, governor, host, result, planReference, borrowed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<List<Completed>> CompleteJobs()
    {
        List<Completed> jobs = [];
        foreach (string route in new[] { "argv", "argv-unicode", "borrowed", "response", "mixed", "job", "file", "nested" })
        foreach (string outcome in new[] { "success", "publication-fault", "open-fault", "read-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await Complete(route, outcome)); }
        return jobs;
    }

    private static void Collect(List<Completed> jobs)
    {
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            foreach (Completed job in jobs) { job.Governor.ReserveMemory(0L); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<string> CheckAliases(List<Completed> jobs)
    {
        List<string> defects = [];
        foreach (Completed job in jobs)
        {
            Assert.False(job.Plan.IsAlive);
            if (job.Host.PassName is string pass)
            {
                if (job.Borrowed) { Assert.False(HasReceipt(job.Governor, pass)); }
                else if (!HasReceipt(job.Governor, pass) || job.Governor.Snapshot().LiveMemoryBytes < GovernedText.StringBytes(pass.Length))
                { defects.Add(job.Route + "|" + job.Outcome + "|pass"); }
            }
            bool generatedInput = job.Route is "response" or "mixed" or "job" or "file" or "nested";
            if (generatedInput && job.Host.InputName is string input && !HasReceipt(job.Governor, input))
            { defects.Add(job.Route + "|" + job.Outcome + "|input"); }
            foreach (string name in job.Host.ConfigurationNames)
            { if (!HasReceipt(job.Governor, name)) { defects.Add(job.Route + "|" + job.Outcome + "|include:" + name); } }
            if (generatedInput && job.Host.OutputName is string output && !HasReceipt(job.Governor, output))
            { defects.Add(job.Route + "|" + job.Outcome + "|output"); }
        }
        return defects;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Observe(List<Completed> jobs)
    {
        Collect(jobs);
        List<string> defects = CheckAliases(jobs);
        foreach (string defect in defects) { log.WriteLine("SETTING OWNERSHIP FAIL {0}", defect); }
        Assert.True(defects.Count == 0, string.Join(Environment.NewLine, defects));
        foreach (Completed job in jobs)
        { job.Host.InputName = null; job.Host.OutputName = null; job.Host.PassName = null; job.Host.ConfigurationNames.Clear(); }
        Collect(jobs);
        foreach (Completed job in jobs) { job.Result = null; }
        Collect(jobs);
        foreach (Completed job in jobs)
        {
            Assert.Equal(job.Governor.CallerMetadataBytes, job.Governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(0L, job.Host.Inner.OpenLeaseCount);
            job.Counters = null;
        }
        Collect(jobs);
        foreach (Completed job in jobs) { Assert.Equal(job.Governor.CallerMetadataBytes, job.Governor.Snapshot().LiveMemoryBytes); }
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
    public async Task ShortInlineNameAdmissionRejectsBeforePdfOpeningAndBorrowsSeparateNames(bool unicode)
    {
        QpdfPath pass = QpdfPath.From(new string(unicode ? '\u00e9' : 'p', 2048) + ".pdf");
        foreach (bool inline in new[] { true, false })
        {
            Host host = new("budget"); await Seed(host, pass);
            string[] argv = inline
                ? [InputPath.Value, "--linearize", "--linearize-pass1=" + pass.Value, OutputPath.Value]
                : [InputPath.Value, "--linearize", "--linearize-pass1", pass.Value, OutputPath.Value];
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv),
                new QpdfExecutionOptions(QpdfLimits.Default with { MaxMemoryBytes = 4096L }), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal(inline ? 0 : 1, host.PdfOpens);
            Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior)); Assert.Equal(Prior, prior);
            Assert.True(host.Inner.TryGetOutput(pass, out byte[] sidecar)); Assert.Equal(Prior, sidecar);
            log.WriteLine("SETTING ADMISSION PASS {0}|{1}", unicode, inline);
        }
    }
}
