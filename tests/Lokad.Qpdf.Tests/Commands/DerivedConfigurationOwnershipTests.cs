using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class DerivedConfigurationOwnershipTests(ITestOutputHelper log)
{
    private static readonly QpdfPath InputPath = QpdfPath.From("input.pdf");
    private static readonly QpdfPath OutputPath = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [17, 23, 41];
    private static readonly string Prefix = new('p', 1024);
    private static readonly string Range = string.Join(',', Enumerable.Repeat("1", 512));
    private static readonly Dictionary<int, long> PhysicalStrings = [];

    private static long PhysicalStringBytes(int length)
    {
        if (PhysicalStrings.TryGetValue(length, out long known)) { return known; }
        string[] roots = new string[32];
        for (int index = 0; index < roots.Length; index++) { roots[index] = new string('p', length); }
        Array.Clear(roots);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < roots.Length; index++) { roots[index] = new string('p', length); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, bytes % roots.Length);
        long measured = bytes / roots.Length;
        PhysicalStrings.Add(length, measured); GC.KeepAlive(roots);
        return measured;
    }

    private sealed class Host(string outcome) : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal int PdfOpens;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => _entered.Task;
        internal void Resume() => _resume.TrySetResult();

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            if (path == InputPath)
            {
                PdfOpens++;
                if (outcome != "budget") { _entered.TrySetResult(); await _resume.Task.WaitAsync(token); }
                if (outcome == "open-fault") { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
                if (outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
            }
            IQpdfInput input = await Inner.OpenInputAsync(path, token);
            return path == InputPath ? new Input(input, outcome) : input;
        }

        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
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

    private sealed class Completed(string route, string profile, string outcome, ResourceGovernor governor,
        QpdfExecutionResult result, WeakReference plan, List<string> aliases, List<string> missing)
    {
        internal readonly string Id = route + "|" + profile + "|" + outcome;
        internal readonly ResourceGovernor Governor = governor;
        internal readonly WeakReference Plan = plan;
        internal readonly List<string> Aliases = aliases, Missing = missing;
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

    private static async Task Seed(Host host)
    {
        host.Inner.AddInput(InputPath, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(OutputPath, CancellationToken.None);
        await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
    }

    private static async Task ValidateOutput(Host host, string profile, string prefix)
    {
        Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] published));
        if (profile == "json")
        {
            using JsonDocument json = JsonDocument.Parse(published);
            JsonElement objects = json.RootElement.GetProperty("qpdf")[1];
            Assert.True(objects.TryGetProperty("obj:1 0 R", out _));
            Assert.False(objects.TryGetProperty("obj:2 0 R", out _));
            return;
        }
        InMemoryQpdfHost validation = new(); QpdfPath input = QpdfPath.From("published.pdf");
        validation.AddInput(input, published, QpdfInputOwnership.Copy);
        QpdfCheckResult check = await Qpdf.CheckAsync(validation, input, CancellationToken.None);
        Assert.True(check.Passed); Assert.Equal(1, check.PageCount);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(validation, input, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject labels));
        PdfDictionary tree = Assert.IsType<PdfDictionary>(labels);
        Assert.True(tree.TryGet(PdfName.FromAscii("Nums"), out QpdfObject numbers));
        PdfDictionary label = Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(numbers)[1]);
        Assert.True(label.TryGet(PdfName.FromAscii("P"), out QpdfObject text));
        Assert.Equal(Encoding.Latin1.GetBytes(prefix), Assert.IsType<PdfByteString>(text).Value.ToArray());
        PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
        Assert.True(page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject rotation));
        Assert.Equal(90L, Assert.IsType<PdfInteger>(rotation).Value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<Completed> Complete(string route, string profile, string outcome)
    {
        Host host = new(outcome); await Seed(host);
        using CancellationTokenSource cancel = new();
        string prefix = route == "argv-unicode" ? new string('\u00e9', 1024) : Prefix;
        string label = "r1:D/1/" + prefix;
        string rotation = "90:" + Range;
        string[] selectors = ["0000000001,0000000000", "2147483647,0000000000"];
        string[] argv = profile == "json"
            ? [InputPath.Value, "--json=2", "--json-key=qpdf", "--json-object=" + selectors[0], "--json-object=" + selectors[1], "--", OutputPath.Value]
            : [InputPath.Value, "--rotate", rotation, "--set-page-labels", label, "--", OutputPath.Value];
        string json = profile == "json"
            ? JsonSerializer.Serialize(new { inputFile = InputPath.Value, outputFile = OutputPath.Value, json = "2", jsonKey = new[] { "qpdf" }, jsonObject = selectors })
            : JsonSerializer.Serialize(new { inputFile = InputPath.Value, outputFile = OutputPath.Value, rotate = new[] { rotation }, setPageLabels = new[] { label } });
        Task<QpdfExecutionResult> running;
        if (route == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(new[] { "@args.txt" }), cancel.Token).AsTask();
        }
        else if (route == "mixed")
        {
            host.Inner.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
            running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--job-json-file=settings.json", .. argv]), cancel.Token).AsTask();
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
        List<string> aliases = profile == "json" ? [.. Assert.IsType<JsonPlan>(plan).Objects]
            : [Assert.Single(plan.Labels).FirstPage, Assert.Single(plan.Labels).Prefix ?? throw new InvalidOperationException("Missing prefix."),
                Assert.Single(plan.Rotations).Range ?? throw new InvalidOperationException("Missing range.")];
        Assert.Equal(profile == "json" ? new[] { "obj:1 0 R", "obj:2147483647 0 R" } : new[] { "r1", prefix, Range }, aliases);
        long textBytes = aliases.Sum(value => PhysicalStringBytes(value.Length));
        List<string> missing = governor.Snapshot().LiveMemoryBytes < textBytes ? ["active-text-batch:" + textBytes] : [];
        // These observations belong to the test, beyond the private plan owner.
        aliases = aliases.Select(value => new string(value.AsSpan())).ToList();
        WeakReference planReference = new(plan);
        if (outcome == "cancel") { cancel.Cancel(); }
        host.Resume();
        QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(outcome switch { "success" => QpdfExecutionStatus.Completed, "cancel" or "host-cancel" => QpdfExecutionStatus.Canceled, _ => QpdfExecutionStatus.Failed }, result.Status);
        Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        if (outcome == "success") { await ValidateOutput(host, profile, prefix); }
        else { Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior)); Assert.Equal(Prior, prior); }
        log.WriteLine("DERIVED CONTROL PASS {0}|{1}|{2}", route, profile, outcome);
        return new Completed(route, profile, outcome, governor, result, planReference, aliases, missing);
    }

    private static void Collect()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<List<Completed>> CompleteJobs()
    {
        List<Completed> jobs = [];
        foreach (string route in new[] { "argv", "argv-unicode", "response", "mixed", "job", "file", "nested" })
        foreach (string profile in new[] { "labels-rotation", "json" })
        foreach (string outcome in new[] { "success", "publication-fault", "open-fault", "read-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await Complete(route, profile, outcome)); }
        return jobs;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<string> ObserveAliases(List<Completed> jobs)
    {
        List<string> defects = [];
        foreach (Completed job in jobs)
        {
            Assert.False(job.Plan.IsAlive);
            foreach (string missing in job.Missing) { defects.Add(job.Id + "|" + missing); }
            Assert.All(job.Aliases, alias => Assert.NotEmpty(alias));
            Assert.Equal(0L, job.Governor.PrivateRetainedBytes);
        }
        return defects;
    }

    [Fact]
    public async Task CompleteJobsFundDerivedValuesAndRetirePlansIndependently()
    {
        List<Completed> jobs = await CompleteJobs(); Collect();
        List<string> defects = ObserveAliases(jobs);
        foreach (Completed job in jobs) { job.Aliases.Clear(); job.Result = null; }
        Collect();
        foreach (Completed job in jobs)
        {
            log.WriteLine("DERIVED COUNTER {0} {1}", job.Id, job.Governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(job.Governor.CallerMetadataBytes + job.Governor.CallerStorageBytes, job.Governor.Snapshot().LiveMemoryBytes); job.Counters = null;
        }
        Collect();
        foreach (Completed job in jobs)
        {
            log.WriteLine("DERIVED FINAL {0} {1}", job.Id, job.Governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(job.Governor.CallerMetadataBytes + job.Governor.CallerStorageBytes, job.Governor.Snapshot().LiveMemoryBytes);
        }
        Assert.True(defects.Count == 0, string.Join('\n', defects));
    }

    [Fact]
    public async Task DerivedRotationRangeRejectsBeforePdfOpenAtFourKiB()
    {
        Host host = new("budget"); await Seed(host);
        string range = string.Join(',', Enumerable.Repeat("1", 1536));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(new[] { InputPath.Value, "--rotate", "+90:" + range, "--pages", ".", "1", "--", OutputPath.Value }),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxMemoryBytes = 4096L }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0, host.PdfOpens); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior)); Assert.Equal(Prior, prior);
    }

    private sealed class TypedCompleted(ResourceGovernor governor, QpdfJsonResult? result,
        WeakReference inspection, List<string> aliases, bool missing)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal readonly WeakReference Inspection = inspection;
        internal readonly List<string> Aliases = aliases;
        internal readonly bool Missing = missing;
        internal QpdfJsonResult? Result = result;
        internal QpdfExecutionCounters? Counters = result?.Counters;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<TypedCompleted> CompleteTyped(string outcome)
    {
        Host host = new(outcome); await Seed(host);
        using CancellationTokenSource cancel = new();
        QpdfJsonOptions options = new(2, ["qpdf"], "none", null, ["0000000001,0000000000", "2147483647,0000000000"], false);
        Task<QpdfJsonResult> running = Qpdf.GetDocumentJsonAsync(host, InputPath, OutputPath, options, cancel.Token).AsTask();
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        ResourceGovernor governor = Find<ResourceGovernor>(running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
            ?? throw new InvalidOperationException("Missing typed job governor.");
        JsonInspection inspection = Find<JsonInspection>(running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
            ?? throw new InvalidOperationException("Missing typed JSON inspection.");
        List<string> aliases = [.. inspection.Objects];
        Assert.Equal(new[] { "obj:1 0 R", "obj:2147483647 0 R" }, aliases);
        bool missing = governor.Snapshot().LiveMemoryBytes < aliases.Sum(value => PhysicalStringBytes(value.Length));
        aliases = aliases.Select(value => new string(value.AsSpan())).ToList();
        WeakReference weak = new(inspection);
        if (outcome == "cancel") { cancel.Cancel(); }
        host.Resume();
        QpdfJsonResult? result = null;
        if (outcome == "success")
        {
            result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            await ValidateOutput(host, "json", string.Empty);
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        else
        {
            if (outcome is "cancel" or "host-cancel")
            { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10))); }
            else { await Assert.ThrowsAnyAsync<QpdfException>(() => running.WaitAsync(TimeSpan.FromSeconds(10))); }
            Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior)); Assert.Equal(Prior, prior);
        }
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        log.WriteLine("DERIVED TYPED CONTROL PASS {0}", outcome);
        return new TypedCompleted(governor, result, weak, aliases, missing);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<List<TypedCompleted>> CompleteTypedJobs()
    {
        List<TypedCompleted> jobs = [];
        foreach (string outcome in new[] { "success", "publication-fault", "open-fault", "read-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await CompleteTyped(outcome)); }
        return jobs;
    }

    private static void CollectTyped()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ObserveTyped(List<TypedCompleted> jobs)
    {
        bool missing = false;
        foreach (TypedCompleted job in jobs)
        {
            Assert.False(job.Inspection.IsAlive);
            missing |= job.Missing;
            Assert.Equal(new[] { "obj:1 0 R", "obj:2147483647 0 R" }, job.Aliases);
            Assert.Equal(0L, job.Governor.PrivateRetainedBytes);
        }
        return missing;
    }

    [Fact]
    public async Task TypedJsonJobsFundCanonicalSelectorsAndRetirePrivateInspection()
    {
        List<TypedCompleted> jobs = await CompleteTypedJobs(); CollectTyped();
        bool missing = ObserveTyped(jobs);
        foreach (TypedCompleted job in jobs) { job.Aliases.Clear(); job.Result = null; job.Counters = null; }
        CollectTyped();
        foreach (TypedCompleted job in jobs) { Assert.Equal(job.Governor.CallerMetadataBytes + job.Governor.CallerStorageBytes, job.Governor.Snapshot().LiveMemoryBytes); }
        Assert.False(missing, "Typed JSON canonical selectors exceed their active storage allowance.");
    }
}
