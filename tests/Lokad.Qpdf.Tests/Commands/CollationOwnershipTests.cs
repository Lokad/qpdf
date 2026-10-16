using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CollationOwnershipTests(ITestOutputHelper log)
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [17, 23, 41];

    private sealed class Host : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
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

    private static async Task<Host> CreateHost()
    {
        Host host = new();
        host.Inner.AddInput(Input, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    [Fact]
    public async Task CollationScanHonorsOriginalWorkBeforeAllocatingItsVector()
    {
        List<string> defects = [];
        foreach ((int count, long work) in new[] { (4096, 128L), (100000, 1024L) })
        {
            Host warm = await CreateHost();
            await Qpdf.ExecuteAsync(warm, new QpdfCommandInvocation(
                [Input.Value, "--collate=1", "--pages", ".", "1", "--", Output.Value]),
                new QpdfExecutionOptions(QpdfLimits.Default with { MaxWork = work }), CancellationToken.None);
            Host host = await CreateHost();
            string value = "--collate=" + string.Join(',', Enumerable.Repeat("1", count));
            string[] arguments = [Input.Value, value, "--pages", ".", "1", "--", Output.Value];
            QpdfCommandInvocation invocation = new(arguments);
            QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxWork = work });
            long before = GC.GetAllocatedBytesForCurrentThread();
            ValueTask<QpdfExecutionResult> pending = Qpdf.ExecuteAsync(host, invocation, options, CancellationToken.None);
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(pending.IsCompleted);
            QpdfExecutionResult result = await pending;
            log.WriteLine("COLLATION WORK count={0} work={1} allocation={2} status={3} codes={4}",
                count, work, allocation, result.Status, string.Join(',', result.Diagnostics.Select(d => d.Code)));
            if (!result.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.BudgetExceeded)) { defects.Add("work:" + count); }
            if (allocation >= 32768L) { defects.Add("allocation:" + count + ":" + allocation); }
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(0, host.Opens); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.Equal(arguments, invocation.Arguments);
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
        }
        Assert.Empty(defects);
    }

    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static List<object> Walk(object root)
    {
        List<object> found = []; HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        void Visit(object value, int depth)
        {
            if (depth > 96 || !seen.Add(value)) { return; }
            found.Add(value); Type type = value.GetType();
            if (value is Task task)
            { if (type.GetField("StateMachine", Fields)?.GetValue(task) is object state) { Visit(state, depth + 1); } return; }
            if (!type.IsValueType && !(type.Namespace?.StartsWith("Lokad.Qpdf", StringComparison.Ordinal) ?? false)) { return; }
            foreach (FieldInfo field in type.GetFields(Fields))
            { if (field.GetValue(value) is object child && !child.GetType().IsPrimitive && !child.GetType().IsEnum) { Visit(child, depth + 1); } }
        }
        Visit(root, 0); return found;
    }

    private static long ArraySize(Array value)
    {
        using IDisposable capture = OwnershipBatch.CaptureWithoutCollection();
        object[] roots = new object[32];
        for (int i = 0; i < roots.Length; i++) { roots[i] = value.Clone(); }
        Array.Clear(roots); long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < roots.Length; i++) { roots[i] = value.Clone(); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, bytes % roots.Length); GC.KeepAlive(roots); return bytes / roots.Length;
    }

    private static (ResourceGovernor Governor, long Minimum, long Admission) Observe(Task task)
    {
        List<object> values = Walk(task);
        ResourceGovernor governor = values.OfType<ResourceGovernor>().First();
        JobPlan plan = values.OfType<JobPlan>().First();
        PdfPageCollation? policy = plan switch { MergePlan merge => merge.Collation, CheckPlan check => check.Collation, _ => null };
        Assert.NotNull(policy);
        long minimum = PdfVersionOwnershipTests.MeasurePhysicalSize(policy.GetType()) + ArraySize(Assert.IsType<int[]>(policy.GroupSizes));
        object? owner = typeof(JobPlan).GetProperty("CollationStorage", Fields)?.GetValue(plan);
        if (owner is not null)
        {
            minimum += PdfVersionOwnershipTests.MeasurePhysicalSize(owner.GetType());
            if (owner.GetType().GetField("_vectors", Fields)?.GetValue(owner) is System.Collections.IList vectors)
            {
                minimum += PdfVersionOwnershipTests.MeasurePhysicalSize(vectors.GetType());
                if (vectors.GetType().GetField("_items", Fields)?.GetValue(vectors) is Array roots) { minimum += ArraySize(roots); }
                foreach (object item in vectors)
                {
                    minimum += PdfVersionOwnershipTests.MeasurePhysicalSize(item.GetType());
                    if (item.GetType().GetField("_items", Fields)?.GetValue(item) is Array backing && backing.Length > 0)
                    { minimum += ArraySize(backing); }
                }
            }
        }
        long admission = owner?.GetType().GetField("_memory", Fields)?.GetValue(owner) is ParserMemoryScope memory
            ? memory.ReservedBytes : -1L;
        return (governor, minimum, admission);
    }

    private sealed class Completed(string id, ResourceGovernor governor, QpdfExecutionResult result, byte[]? output)
    {
        internal readonly string Id = id;
        internal readonly ResourceGovernor Governor = governor;
        internal QpdfExecutionResult? Result = result;
        internal readonly byte[]? Output = output;
    }

    private static Task<QpdfExecutionResult> Start(Host host, string route, CancellationToken token)
    {
        string json = JsonSerializer.Serialize(new { inputFile = Input.Value, outputFile = Output.Value,
            pages = new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } }, collate = "1,1" });
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Completed> Complete(string route, string outcome, List<string> defects, ITestOutputHelper log)
    {
        Host host = await CreateHost(); host.Pause = true; host.Outcome = outcome;
        using CancellationTokenSource cancel = new();
        string id = route + "|" + outcome;
        Task<QpdfExecutionResult> running = Start(host, route, cancel.Token);
        await host.InputReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var input = Observe(running);
        log.WriteLine("COLLATION MIN {0}|input minimum={1} admission={2}", id, input.Minimum, input.Admission);
        if (input.Admission < input.Minimum) { defects.Add(id + "|input"); }
        host.InputResume.SetResult();
        if (outcome != "open-fault")
        {
            await host.OutputReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var output = Observe(running);
            log.WriteLine("COLLATION MIN {0}|output minimum={1} admission={2}", id, output.Minimum, output.Admission);
            if (output.Admission < output.Minimum) { defects.Add(id + "|output"); }
            if (outcome == "cancel") { cancel.Cancel(); }
            host.OutputResume.SetResult();
        }
        QpdfExecutionResult result = await running;
        Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed
            : outcome is "cancel" or "host-cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        byte[]? bytes = null;
        if (outcome == "success")
        {
            Assert.True(host.Inner.TryGetOutput(Output, out bytes));
            InMemoryQpdfHost reader = new(); reader.AddInput(Input, bytes, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Input, CancellationToken.None);
            Assert.Equal(2, document.PageCount);
        }
        else if (outcome != "close-fault")
        { Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior); }
        Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        return new(id, input.Governor, result, bytes);
    }

    private static void Collect(List<Completed> jobs)
    {
        for (int i = 0; i < 4; i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); foreach (Completed job in jobs) { job.Governor.ReserveMemory(0L); } }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropResults(List<Completed> jobs)
    {
        foreach (Completed job in jobs)
        {
            Assert.NotNull(job.Result);
            Assert.True(job.Governor.Snapshot().LiveMemoryBytes >= PdfVersionOwnershipTests.MeasurePhysicalSize(job.Result.GetType()));
            job.Result = null;
        }
    }

    [Fact]
    public async Task CompleteCollationJobsFundPolicyThroughPublicationAndRetire()
    {
        List<string> defects = []; List<Completed> jobs = [];
        foreach (string route in new[] { "argv", "response", "mixed", "json", "file", "nested" })
        foreach (string outcome in new[] { "success", "open-fault", "create-fault", "write-fault", "commit-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await Complete(route, outcome, defects, log)); }
        await Task.Yield(); Collect(jobs); DropResults(jobs); Collect(jobs);
        foreach (Completed job in jobs)
        {
            int calls = 0;
            while (job.Governor.Snapshot().LiveMemoryBytes > job.Governor.CallerMetadataBytes && calls < 4096) { job.Governor.ReserveMemory(0L); calls++; }
            long live = job.Governor.Snapshot().LiveMemoryBytes;
            log.WriteLine("COLLATION FINAL {0} live={1} maintenance={2}", job.Id, live, calls);
            if (live != job.Governor.CallerMetadataBytes) { defects.Add(job.Id + "|final:" + live); }
            if (job.Output is byte[] bytes) { Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8)); }
        }
        Assert.Equal(48, jobs.Count); Assert.Empty(defects);
    }

    [Fact]
    public async Task CollationDefaultsValuesAndInspectionPreservePublicSemantics()
    {
        foreach (string route in new[] { "argv", "json" })
        foreach (string value in new[] { "", "0", "1", "1,1", "2147483647", "1,", ",1", "-1", " 1", "2147483648", "00000000001", "1,1,1" })
        {
            Host host = await CreateHost();
            bool valid = value is "" or "0" or "1" or "1,1" or "2147483647";
            string json = JsonSerializer.Serialize(new { inputFile = Input.Value, outputFile = Output.Value,
                pages = new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } }, collate = value });
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
                Assert.Equal(0, host.Opens);
                Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
            }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            log.WriteLine("COLLATION VALUE {0}|{1} status={2}", route, value, result.Status);
        }
        foreach (string route in new[] { "argv", "json" })
        {
            Host host = await CreateHost();
            string json = JsonSerializer.Serialize(new { inputFile = Input.Value, check = "", showNpages = "",
                pages = new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } }, collate = "1,1" });
            QpdfExecutionResult result = route == "json" ? await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
                    [Input.Value, "--collate=1,1", "--pages", ".", "1", ".", "1", "--", "--check", "--show-npages"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            log.WriteLine("COLLATION INSPECTION {0} preserved", route);
        }
    }
}
