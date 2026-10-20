using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;
using Xunit.Abstractions;
using Lokad.Qpdf.Runtime;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ArgumentFilteringOwnershipTests(ITestOutputHelper log)
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

    private static async Task<Host> CreateHost()
    {
        Host host = new();
        host.Inner.AddInput(Input, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    [Fact]
    public async Task FilteringHonorsOriginalLimitsBeforeOpeningItsPdf()
    {
        List<string> defects = [];
        foreach ((string shape, string limit, long bound) in new[]
            { ("global", "memory", 6656L), ("pages", "memory", 28672L) })
        {
            QpdfLimits limits = QpdfLimits.Default with
            { MaxMemoryBytes = limit == "memory" ? bound : QpdfLimits.Default.MaxMemoryBytes};
            Host warm = await CreateHost(); warm.Outcome = "open-fault";
            QpdfExecutionResult first = await Qpdf.ExecuteAsync(warm, new QpdfCommandInvocation([Input.Value, Output.Value]),
                new QpdfExecutionOptions(limits), CancellationToken.None);
            Assert.Contains(first.Diagnostics, d => d.Code == QpdfDiagnosticCode.HostOperationFailed);
            Assert.Equal(0L, warm.Inner.OpenLeaseCount);
            Assert.True(warm.Inner.TryGetOutput(Output, out byte[] warmPrior)); Assert.Equal(Prior, warmPrior);
            log.WriteLine("FILTER WARM {0}|{1} preserved", shape, limit);
            Host host = await CreateHost(); host.Outcome = "open-fault"; string[] arguments = Arguments(shape);
            QpdfCommandInvocation invocation = new(arguments); QpdfExecutionOptions options = new(limits);
            long before = GC.GetAllocatedBytesForCurrentThread();
            ValueTask<QpdfExecutionResult> pending = Qpdf.ExecuteAsync(host, invocation, options, CancellationToken.None);
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before; Assert.True(pending.IsCompleted);
            QpdfExecutionResult result = await pending;
            log.WriteLine("FILTER LIMIT {0}|{1} bound={2} allocation={3} opens={4} codes={5}",
                shape, limit, bound, allocation, host.Opens, string.Join(',', result.Diagnostics.Select(d => d.Code)));
            bool rejects = shape == "pages";
            if (host.Opens != (rejects ? 0 : 1) || !result.Diagnostics.Any(d => d.Code ==
                (rejects ? QpdfDiagnosticCode.BudgetExceeded : QpdfDiagnosticCode.HostOperationFailed)))
            { defects.Add(shape + "|" + limit + "|admission"); }
            if (allocation >= 65536L) { defects.Add(shape + "|" + limit + "|allocation:" + allocation); }
            if (rejects && result.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.BudgetExceeded))
            { Assert.Contains(result.Diagnostics, d => d.Message.Contains(nameof(QpdfLimits.MaxMemoryBytes), StringComparison.Ordinal)); }
            if (limit == "memory") { Assert.True(result.Counters.PeakMemoryBytes <= bound); }
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.Equal(arguments, invocation.Arguments);
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
        }
        Assert.Empty(defects);
    }

    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<string, long> PhysicalSizes = [];

    private static long Physical(object value)
    {
        string key = value.GetType().FullName + (value is Array a ? ":" + a.Length : "");
        if (PhysicalSizes.TryGetValue(key, out long cached)) { return cached; }
        long size;
        if (value is not Array array) { size = PdfVersionOwnershipTests.MeasurePhysicalSize(value.GetType()); }
        else
        {
            object[] roots = new object[32];
            for (int i = 0; i < roots.Length; i++) { roots[i] = array.Clone(); }
            Array.Clear(roots); long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < roots.Length; i++) { roots[i] = array.Clone(); }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, bytes % roots.Length); size = bytes / roots.Length; GC.KeepAlive(roots);
        }
        PhysicalSizes.Add(key, size); return size;
    }

    private static object Call(object value, string name, object[] arguments)
    {
        MethodInfo method = value.GetType().GetMethods(Fields).Single(m => m.Name == name && m.GetParameters().Length == arguments.Length
            && m.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(arguments[i])).All(x => x));
        return method.Invoke(value, arguments) ?? value;
    }

    private static long Minimum(object owner)
    {
        long minimum = Physical(owner);
        foreach (string name in new[] { "_markers", "_stripped", "_kept" })
        {
            if (owner.GetType().GetField(name, Fields)?.GetValue(owner) is object list)
            {
                minimum += Physical(list);
                if (list.GetType().GetField("_items", Fields)?.GetValue(list) is Array backing && backing.Length > 0)
                { minimum += Physical(backing); }
            }
        }
        return minimum;
    }

    [Fact]
    public void FilteringScratchAdmitsIndependentPhysicalMinimaAndGrowthOverlap()
    {
        Type type = Assert.IsAssignableFrom<Type>(typeof(Qpdf).Assembly.GetType("Lokad.Qpdf.Commands.ArgumentFiltering"));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        object owner = Assert.IsAssignableFrom<object>(type.GetMethod("Create", Fields)?.Invoke(null, [governor]));
        using (Assert.IsAssignableFrom<IDisposable>(owner))
        {
            object markers = Call(owner, "CreateMarkers", []), stripped = Call(owner, "CreateStripped", []), kept = Call(owner, "CreateKept", []);
            foreach (var vector in new[] { (Name: "markers", Value: markers), (Name: "stripped", Value: stripped), (Name: "kept", Value: kept) })
            for (int i = 0; i < 129; i++)
            {
                Array before = Assert.IsAssignableFrom<Array>(vector.Value.GetType().GetField("_items", Fields)?.GetValue(vector.Value));
                long previous = Minimum(owner);
                Call(owner, "Add", [vector.Value, vector.Name == "markers" ? i : "borrowed"]);
                Array after = Assert.IsAssignableFrom<Array>(vector.Value.GetType().GetField("_items", Fields)?.GetValue(vector.Value));
                if (ReferenceEquals(before, after)) { continue; }
                ParserMemoryScope memory = Assert.IsType<ParserMemoryScope>(type.GetField("_memory", Fields)?.GetValue(owner));
                long minimum = Minimum(owner), overlap = previous + Physical(after);
                Assert.True(memory.ReservedBytes >= minimum);
                Assert.True(governor.Snapshot().PeakMemoryBytes >= overlap);
                log.WriteLine("FILTER PHYSICAL {0}|{1} minimum={2} admission={3} overlap={4} peak={5}",
                    vector.Name, i, minimum, memory.ReservedBytes, overlap, governor.Snapshot().PeakMemoryBytes);
            }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        foreach (string field in new[] { "_governor", "_memory", "_markers", "_stripped", "_kept" })
        { Assert.Null(type.GetField(field, Fields)?.GetValue(owner)); }
    }

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
            foreach (FieldInfo field in type.GetFields(Fields & ~BindingFlags.Static))
            { if (field.GetValue(value) is object child && !child.GetType().IsPrimitive && !child.GetType().IsEnum) { Visit(child, depth + 1); } }
        }
        Visit(root, 0); return found;
    }

    private sealed class Completed(string id, ResourceGovernor governor, QpdfExecutionResult result, byte[]? output)
    {
        internal readonly string Id = id;
        internal readonly ResourceGovernor Governor = governor;
        internal QpdfExecutionResult? Result = result;
        internal readonly byte[]? Output = output;
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Completed> Complete(string shape, string route, string outcome, ITestOutputHelper log)
    {
        Host host = await CreateHost(); host.Pause = true; host.Outcome = outcome;
        using CancellationTokenSource cancel = new();
        string id = shape + "|" + route + "|" + outcome;
        Task<QpdfExecutionResult> running = Start(host, route, SmallArguments(shape), cancel.Token);
        await host.InputReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        List<object> input = Walk(running); ResourceGovernor governor = input.OfType<ResourceGovernor>().First();
        Assert.DoesNotContain(input, v => v.GetType().FullName == "Lokad.Qpdf.Commands.ArgumentFiltering");
        log.WriteLine("FILTER TERMINAL {0}|input scratch detached", id);
        host.InputResume.SetResult();
        if (outcome != "open-fault")
        {
            await host.OutputReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.DoesNotContain(Walk(running), v => v.GetType().FullName == "Lokad.Qpdf.Commands.ArgumentFiltering");
            log.WriteLine("FILTER TERMINAL {0}|output scratch detached", id);
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
            Assert.Equal(shape == "pages" ? 2 : 1, document.PageCount);
        }
        else if (outcome != "close-fault")
        { Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior); }
        Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        return new(id, governor, result, bytes);
    }

    private static void Collect()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropResults(List<Completed> jobs)
    {
        foreach (Completed job in jobs)
        {
            Assert.NotNull(job.Result); Assert.True(job.Governor.Snapshot().LiveMemoryBytes >= Physical(job.Result));
            job.Result = null;
        }
    }

    [Fact]
    public async Task CompleteFilteringConsumersRetireScratchAndPreserveOutputs()
    {
        List<Completed> jobs = [];
        foreach (string shape in new[] { "bare", "pages", "global" })
        foreach (string route in new[] { "argv", "response" })
        foreach (string outcome in new[] { "success", "open-fault", "create-fault", "write-fault", "commit-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await Complete(shape, route, outcome, log)); }
        await Task.Yield(); Collect(); DropResults(jobs); Collect();
        foreach (Completed job in jobs)
        {
            long live = job.Governor.Snapshot().LiveMemoryBytes;
            log.WriteLine("FILTER FINAL {0} live={1}", job.Id, live);
            Assert.Equal(job.Governor.CallerMetadataBytes + job.Governor.CallerStorageBytes, live);
            if (job.Output is byte[] bytes) { Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8)); }
        }
        Assert.Equal(48, jobs.Count);
    }

    [Fact]
    public async Task FilteringPreservesGlobalGrammarAndCallerCancellation()
    {
        foreach (string route in new[] { "argv", "response" })
        foreach ((string name, string[] arguments, bool valid) in new[]
        {
            ("empty", new[] { "--global", "--", Input.Value, Output.Value }, true),
            ("bounded", SmallArguments("global"), true),
            ("main", new[] { Input.Value, "--max-stream-filters=25", Output.Value }, true),
            ("repeated", new[] { "--global", "--", "--global", "--", Input.Value, Output.Value }, true),
            ("missing", new[] { "--global", "--max-stream-filters=25" }, false),
            ("unknown", new[] { "--global", "--bogus", "--", Input.Value, Output.Value }, false),
            ("nested", new[] { "--global", "--global", "--", Input.Value, Output.Value }, false),
            ("duplicate", new[] { Input.Value, "--max-stream-filters=25", "--max-stream-filters=25", Output.Value }, true),
        })
        {
            Host host = await CreateHost(); QpdfExecutionResult result = await Start(host, route, arguments, CancellationToken.None);
            Assert.Equal(valid ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
            if (!valid) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments); Assert.Equal(0, host.Opens); }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            log.WriteLine("FILTER GRAMMAR {0}|{1} status={2}", route, name, result.Status);
        }
        foreach (string shape in new[] { "bare", "pages", "global" })
        foreach (string route in new[] { "argv", "response" })
        {
            Host host = await CreateHost(); QpdfExecutionResult result = await Start(host, route, SmallArguments(shape), new CancellationToken(true));
            Assert.Equal(QpdfExecutionStatus.Canceled, result.Status); Assert.Equal(0, host.Opens); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
            log.WriteLine("FILTER PRE-CANCEL {0}|{1} preserved", shape, route);
        }
    }
}
