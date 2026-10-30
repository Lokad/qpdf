using System.Reflection;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class SelectionJobCleanupTests(ITestOutputHelper output)
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");
    private static readonly string[] Routes = ["typed-page", "typed-page-rotation", "typed-pages", "typed-pages-rotation",
        "typed-pages-options", "typed-split", "typed-copy", "argv-page", "job-page", "argv-pages", "job-pages",
        "argv-rewrite", "job-rewrite", "argv-qdf", "job-qdf", "argv-linearized", "job-linearized",
        "argv-overlay", "job-overlay", "argv-underlay", "job-underlay", "argv-copy", "job-copy",
        "typed-add", "typed-remove", "argv-add", "job-add", "argv-remove", "job-remove",
        "argv-overlay-dual", "job-overlay-dual", "argv-normalized", "job-normalized"];

    private static object Field(object value, string name)
        => value.GetType().GetField(name, Members)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing public-job observation field: " + name);

    private static byte[] Fixture(bool compressed)
    {
        byte[] bytes = (byte[])(typeof(PdfVersionOwnershipTests).GetMethod("DocumentBytes", Members)?.Invoke(null, [compressed, "none"])
            ?? throw new InvalidOperationException("Missing preserved selection fixture."));
        int at = bytes.AsSpan().IndexOf("/Count 1"u8);
        Assert.True(at >= 0); bytes[at + 7] = (byte)'2';
        Assert.Equal(compressed ? "6CADFAF14B1B13BEAF037018E714711F256561B7A83C36373FCBBEAF81C6987C"
            : "E5ED2A5F82933E43037A90E3DADC7A6233863ECF5C23E6DBA0D2B59841E4B0F9",
            Convert.ToHexString(SHA256.HashData(bytes)));
        return bytes;
    }

    private sealed class Aliases(ResourceGovernor governor, Array backing, QpdfDiagnostic[] elements,
        WeakReference[] metadata, byte[] output)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal Array? Backing = backing;
        internal readonly WeakReference BackingReference = new(backing);
        internal readonly QpdfDiagnostic[] Elements = elements;
        internal readonly WeakReference[] Metadata = metadata;
        internal readonly byte[] Output = output;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Aliases Keep(InMemoryQpdfHost host, PdfPageSelectionResult result, ResourceGovernor governor)
    {
        Assert.Equal(1, result.PageCount); Assert.Single(result.Warnings);
        Assert.Equal(QpdfDiagnosticCode.PageCountMismatch, result.Warnings[0].Code);
        Assert.Equal("A /Pages /Count of 2 disagrees with traversal (1); traversal wins.", result.Warnings[0].Message);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Array backing = result.Warnings as Array ?? result.Warnings.GetType().GetFields(Members)
            .Select(field => field.GetValue(result.Warnings)).OfType<Array>().Single();
        return new(governor ?? throw new InvalidOperationException(), backing, result.Warnings.ToArray(),
            [new(host), new(result), new(result.Counters), new(result.SourcePages)], output);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Aliases Complete(bool compressed)
    {
        BoundaryHost host = new("read", Input, "success", false);
        host.Inner.AddInput(Input, Fixture(compressed), QpdfInputOwnership.Copy);
        SynchronizationContext? context = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            Task<object> running = Start(host, "typed-page", QpdfLimits.Default, CancellationToken.None);
            try
            {
                host.Entered.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                ResourceGovernor governor = RunningGovernor(running);
                Task<int> completedOn = running.ContinueWith(static _ => Environment.CurrentManagedThreadId,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                host.Resume();
                // The memory host completes on this stack. Resume must unwind
                // every producer frame before the caller tests collectability.
                Assert.True(completedOn.IsCompletedSuccessfully);
                Assert.Equal(Environment.CurrentManagedThreadId, completedOn.GetAwaiter().GetResult());
                return Keep(host.Inner, Assert.IsType<PdfPageSelectionResult>(running.GetAwaiter().GetResult()), governor);
            }
            finally { host.Resume(); running.GetAwaiter().GetResult(); }
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static QpdfExecutionCounters Collect(ResourceGovernor governor)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return governor.Snapshot();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ValidateBacking(Aliases aliases)
    {
        Array backing = aliases.Backing ?? throw new InvalidOperationException();
        Assert.Equal(aliases.Elements.Length, backing.Length);
        for (int index = 0; index < backing.Length; index++)
        { Assert.Same(aliases.Elements[index], backing.GetValue(index)); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropDiagnosticAliases(Aliases aliases)
    {
        Assert.Equal("A /Pages /Count of 2 disagrees with traversal (1); traversal wins.", aliases.Elements[0].Message);
        Array.Clear(aliases.Elements);
    }

    private static object Read(object value, string name)
        => value.GetType().GetProperty(name, Members)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing public result property: " + name);

    private static void AddInputs(InMemoryQpdfHost host, string route, bool compressed)
    {
        host.AddInput(Input, route.EndsWith("add", StringComparison.Ordinal) || route.EndsWith("remove", StringComparison.Ordinal)
            ? AttachmentFixture(compressed) : Fixture(compressed), QpdfInputOwnership.Copy);
        byte[] second = route.Contains("copy", StringComparison.Ordinal)
            ? (byte[])(typeof(Lokad.Qpdf.Tests.Writing.AttachmentCopyTests).GetMethod("SourceFixture", Members)?.Invoke(null, [])
                ?? throw new InvalidOperationException("Missing complete attachment-copy fixture.")) : Fixture(compressed);
        host.AddInput(QpdfPath.From("second.pdf"), second, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("overlay.pdf"), Fixture(compressed), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("overlay-2.pdf"), Fixture(!compressed), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("attachment.bin"), "Added payload.\n"u8.ToArray(), QpdfInputOwnership.Copy);
    }

    private static byte[] AttachmentFixture(bool compressed)
    {
        byte[] content = "q Q\n"u8.ToArray();
        if (compressed)
        {
            using MemoryStream encoded = new();
            using (ZLibStream encoder = new(encoded, CompressionLevel.Optimal, true)) { encoder.Write(content); }
            content = encoded.ToArray();
        }
        List<(int, string, byte[]?)> objects = CheckTests.SinglePage(
            "<< /Length " + content.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>", content);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 10 0 R >> >>", null);
        objects[1] = (2, "<< /Type /Pages /Kids [3 0 R] /Count 2 >>", null);
        objects.AddRange([
            (6, "<< /Type /EmbeddedFile /Length 4 >>", "q Q\n"u8.ToArray()),
            (7, "<< /Type /Filespec /F (payload.bin) /EF << /F 6 0 R >> >>", null),
            (8, "<< /Type /EmbeddedFile /Length 4 >>", "q Q\n"u8.ToArray()),
            (9, "<< /Type /Filespec /F (kept.bin) /EF << /F 8 0 R >> >>", null),
            (10, "<< /Names [(payload) 7 0 R (kept) 9 0 R] >>", null)]);
        return CheckTests.Assemble(objects, 1);
    }

    private static async Task<object> Start(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        if (route == "typed-add")
        {
            return await Qpdf.AddAttachmentsAsync(host, Input, Output,
            [new QpdfAttachmentSpec("attachment.bin", "new", "new.bin", null, null, null, null, false)], limits, token);
        }
        if (route == "typed-split")
        {
            return await Qpdf.SelectPagesAsync(host,
                [new PdfPageSource(Input, new PdfPageSelectionOptions(1)),
                 new PdfPageSource(QpdfPath.From("second.pdf"), new PdfPageSelectionOptions(1))],
                Output, new PdfPageMergeOptions { SplitPages = 1 }, limits, token);
        }
        if (route.StartsWith("typed-", StringComparison.Ordinal))
        {
            Task<object> running = (Task<object>)(typeof(TypedCounterOwnershipTests).GetMethod("Start", Members)?.Invoke(null,
                [host, route, limits, token]) ?? throw new InvalidOperationException());
            return await running;
        }
        string operation = route[(route.IndexOf('-') + 1)..];
        bool dual = operation.EndsWith("-dual", StringComparison.Ordinal);
        if (dual) { operation = operation[..^5]; }
        List<string> argv = [Input.Value];
        Dictionary<string, object> job = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value };
        if (operation is "page" or "pages")
        {
            argv.AddRange(["--pages", Input.Value, "1"]);
            List<object> pages = [new Dictionary<string, object> { ["file"] = Input.Value, ["range"] = "1" }];
            if (operation == "pages")
            {
                argv.AddRange(["second.pdf", "1"]);
                pages.Add(new Dictionary<string, object> { ["file"] = "second.pdf", ["range"] = "1" });
            }
            argv.Add("--"); job["pages"] = pages;
        }
        else if (operation is "overlay" or "underlay")
        {
            argv.AddRange(["--" + operation, "overlay.pdf", "--from=1", "--to=1", "--"]);
            job[operation] = new[] { new Dictionary<string, object> { ["file"] = "overlay.pdf", ["from"] = "1", ["to"] = "1" } };
            if (dual)
            {
                argv.AddRange(["--" + operation, "overlay-2.pdf", "--from=1", "--to=1", "--"]);
                job[operation] = new[] {
                    new Dictionary<string, object> { ["file"] = "overlay.pdf", ["from"] = "1", ["to"] = "1" },
                    new Dictionary<string, object> { ["file"] = "overlay-2.pdf", ["from"] = "1", ["to"] = "1" } };
            }
        }
        else if (operation == "copy")
        {
            argv.AddRange(["--copy-attachments-from", "second.pdf", "--prefix=copy-", "--"]);
            job["copyAttachmentsFrom"] = new[] { new Dictionary<string, object> { ["file"] = "second.pdf", ["prefix"] = "copy-" } };
        }
        else if (operation is "qdf" or "linearized")
        {
            string flag = operation == "qdf" ? "qdf" : "linearize";
            argv.Add("--" + flag); job[flag] = "";
        }
        else if (operation == "add")
        {
            argv.AddRange(["--add-attachment", "attachment.bin", "--key=new", "--filename=new.bin", "--"]);
            job["addAttachment"] = new[] { new Dictionary<string, object>
                { ["file"] = "attachment.bin", ["key"] = "new", ["filename"] = "new.bin" } };
        }
        else if (operation == "remove") { argv.Add("--remove-attachment=payload"); job["removeAttachment"] = new[] { "payload" }; }
        else if (operation == "normalized") { argv.Add("--normalize-content=y"); job["normalizeContent"] = "y"; }
        else if (operation != "rewrite") { throw new InvalidOperationException("Unknown complete-job route."); }
        if (operation is "page" or "pages") { argv.Add(Output.Value); }
        else { argv.AddRange(["--", Output.Value]); }
        if (route.StartsWith("job-", StringComparison.Ordinal))
        { return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), token); }
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), token);
    }

    private sealed class CompletedAliases(ResourceGovernor governor, byte[][] outputs, int[] pageCounts)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal readonly byte[][] Outputs = outputs;
        internal readonly int[] PageCounts = pageCounts;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CompletedAliases KeepAffected(InMemoryQpdfHost host, object result, string route, ResourceGovernor governor)
    {
        if (result is QpdfExecutionResult command) { Assert.Equal(QpdfExecutionStatus.Completed, command.Status); }
        QpdfExecutionCounters counters = (QpdfExecutionCounters)Read(result, "Counters");
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, counters.OpenHandles);
        QpdfPath[] paths = route == "typed-split" ? [QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf")] : [Output];
        byte[][] outputs = new byte[paths.Length][];
        for (int index = 0; index < paths.Length; index++)
        { Assert.True(host.TryGetOutput(paths[index], out byte[] output)); outputs[index] = output; }
        int pages = route is "typed-pages" or "typed-pages-rotation" or "typed-pages-options" or "argv-pages" or "job-pages" ? 2 : 1;
        return new(governor ?? throw new InvalidOperationException(),
            outputs, Enumerable.Repeat(pages, outputs.Length).ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<CompletedAliases> CompleteAffected(string route, bool compressed)
    {
        BoundaryHost host = new("read", Input, "success", true);
        AddInputs(host.Inner, route, compressed);
        Task<object> running = Start(host, route, QpdfLimits.Default, CancellationToken.None);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = RunningGovernor(running);
            host.Resume();
            object result = await running;
            return KeepAffected(host.Inner, result, route, governor);
        }
        finally { host.Resume(); await running; }
    }

    public static IEnumerable<object[]> CompleteJobs()
    { foreach (string route in Routes) foreach (bool compressed in new[] { false, true }) { yield return [route, compressed]; } }

    private sealed class BoundaryHost(string boundary, QpdfPath boundaryPath, string outcome, bool asyncResume) : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal readonly HashSet<QpdfPath> Committed = [];
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(asyncResume ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None);
        private bool _paused;
        internal Task Entered => _entered.Task;
        internal void Resume() => _resume.TrySetResult();
        private async ValueTask Pause(string phase, QpdfPath path, CancellationToken token)
        {
            if (_paused || phase != boundary || path != boundaryPath) { return; }
            _paused = true; _entered.TrySetResult();
            await _resume.Task.WaitAsync(token);
            if (outcome == "fault") { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
            if (outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
        }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new InputLease(this, await Inner.OpenInputAsync(path, token), token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            await Pause("create", path, token);
            return new OutputLease(this, await Inner.CreateOutputAsync(path, token), token);
        }
        private sealed class InputLease(BoundaryHost host, IQpdfInput inner, CancellationToken token) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellation)
            { int bytes = await inner.ReadAsync(offset, destination, cancellation); await host.Pause("read", Path, cancellation); return bytes; }
            public async ValueTask DisposeAsync()
            { try { await host.Pause("input-close", Path, token); } finally { await inner.DisposeAsync(); } }
        }
        private sealed class OutputLease(BoundaryHost host, IQpdfOutputTransaction inner, CancellationToken token) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
            { await host.Pause("write", Path, cancellation); await inner.WriteAsync(bytes, cancellation); }
            public async ValueTask CommitAsync(CancellationToken cancellation)
            { await host.Pause("commit", Path, cancellation); await inner.CommitAsync(cancellation); host.Committed.Add(Path); }
            public ValueTask DiscardAsync(CancellationToken cancellation) => inner.DiscardAsync(cancellation);
            public async ValueTask DisposeAsync()
            { try { await host.Pause("output-close", Path, token); } finally { await inner.DisposeAsync(); } }
        }
    }

    private static QpdfPath[] OutputPaths(string route) => route == "typed-split"
        ? [QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf")] : [Output];

    private static async Task Seed(InMemoryQpdfHost host, string route)
    {
        foreach (QpdfPath path in OutputPaths(route))
        {
            await using IQpdfOutputTransaction seed = await host.CreateOutputAsync(path, CancellationToken.None);
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
    }

    private static ResourceGovernor RunningGovernor(Task task) => (ResourceGovernor)(typeof(ExecutionCounterTests)
        .GetMethod("FindGovernor", Members)?.Invoke(null, [task, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
        ?? throw new InvalidOperationException("Missing actual public-job governor."));

    public static IEnumerable<object[]> FailureJobs()
    {
        foreach (string route in Routes) foreach (bool compressed in new[] { false, true })
        foreach (string boundary in new[] { "read", "create", "write", "commit", "input-close", "output-close" })
        foreach (string outcome in new[] { "fault", "caller-cancel", "host-cancel" })
        { yield return [route, compressed, boundary, outcome, false]; }
        foreach (string route in Routes.Where(route => route.Contains("overlay", StringComparison.Ordinal)
            || route.EndsWith("underlay", StringComparison.Ordinal) || route.EndsWith("copy", StringComparison.Ordinal)
            || route.EndsWith("add", StringComparison.Ordinal)))
        foreach (bool compressed in new[] { false, true }) foreach (string boundary in new[] { "read", "input-close" })
        foreach (string outcome in new[] { "fault", "caller-cancel", "host-cancel" })
        { yield return [route, compressed, boundary, outcome, true]; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<CompletedAliases> CompleteFailure(string route, bool compressed, string boundary,
        string outcome, bool secondary)
    {
        QpdfPath[] paths = OutputPaths(route);
        QpdfPath pausePath = boundary is "read" or "input-close"
            ? secondary ? QpdfPath.From(route.EndsWith("copy", StringComparison.Ordinal) ? "second.pdf"
                : route.EndsWith("add", StringComparison.Ordinal) ? "attachment.bin" : "overlay.pdf") : Input
            : paths[0];
        BoundaryHost host = new(boundary, pausePath, outcome, true);
        AddInputs(host.Inner, route, compressed); await Seed(host.Inner, route);
        using CancellationTokenSource cancellation = new();
        Task<object> task = Start(host, route, QpdfLimits.Default, cancellation.Token);
        ResourceGovernor? governor = null;
        object? result = null;
        Exception? failure = null;
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            governor = RunningGovernor(task);
            if (outcome == "caller-cancel") { cancellation.Cancel(); }
            host.Resume();
            try { result = await task; }
            catch (Exception exception) { failure = exception; }
            if (route.StartsWith("typed-", StringComparison.Ordinal))
            {
                Assert.NotNull(failure);
                if (outcome == "fault") { Assert.IsAssignableFrom<QpdfException>(failure); }
                else { Assert.IsAssignableFrom<OperationCanceledException>(failure); }
            }
            else
            {
                Assert.Null(failure);
                QpdfExecutionResult execution = Assert.IsType<QpdfExecutionResult>(result);
                Assert.Equal(outcome == "fault" ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Canceled, execution.Status);
            }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            List<byte[]> surviving = [];
            foreach (QpdfPath path in paths)
            {
                Assert.True(host.Inner.TryGetOutput(path, out byte[] bytes));
                if (host.Committed.Contains(path)) { surviving.Add(bytes); }
                else { Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
            }
            int pageCount = route.Contains("pages", StringComparison.Ordinal) ? 2 : 1;
            return new(governor,
                surviving.ToArray(), Enumerable.Repeat(pageCount, surviving.Count).ToArray());
        }
        finally { host.Resume(); try { await task; } catch (QpdfException) { } catch (OperationCanceledException) { } }
    }

    [Theory]
    [Trait("Layer", "Contract")]
    [MemberData(nameof(FailureJobs))]
    public async Task FaultAndCancellationJobsPreserveOutputsAndCloseLeases(string route, bool compressed,
        string boundary, string outcome, bool secondary)
    {
        CompletedAliases aliases = await CompleteFailure(route, compressed, boundary, outcome, secondary);
        await ValidateOutputs(aliases, route);
        GC.KeepAlive(aliases.Outputs);
        output.WriteLine("CONTRACT PASS " + FailureId(route, compressed, boundary, outcome, secondary));
    }

    public static IEnumerable<object[]> AdmissionJobs()
    {
        foreach (string route in Routes) foreach (bool compressed in new[] { false, true })
        foreach (string budget in new[] { "memory"}) { yield return [route, compressed, budget]; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<CompletedAliases> CompleteAdmission(string route, bool compressed, string budget)
    {
        BoundaryHost host = new("read", Input, "success", true);
        AddInputs(host.Inner, route, compressed); await Seed(host.Inner, route);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 16384L };
        Task<object> task = Start(host, route, limits, CancellationToken.None);
        object? result = null;
        Exception? failure = null;
        ResourceGovernor? governor = null;
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            governor = RunningGovernor(task); host.Resume();
            try { result = await task; } catch (Exception exception) { failure = exception; }
            Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
            // Removing bookkeeping and discarded token copies may let these
            // jobs fit the original ceilings. Keep the ceilings and validate
            // successful publication rather than requiring obsolete costs.
            if (failure is null
                && result is not QpdfExecutionResult { Status: not QpdfExecutionStatus.Completed })
            {
                Assert.NotNull(result);
                Assert.All(OutputPaths(route), path => Assert.Contains(path, host.Committed));
                return KeepAffected(host.Inner, result, route, governor);
            }
            if (route.StartsWith("typed-", StringComparison.Ordinal))
            {
                QpdfLimitException limit = Assert.IsType<QpdfLimitException>(failure);
                Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), limit.LimitName);
            }
            else
            {
                Assert.Null(failure);
                QpdfExecutionResult execution = Assert.IsType<QpdfExecutionResult>(result);
                Assert.Equal(QpdfExecutionStatus.Failed, execution.Status);
                Assert.Contains(execution.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            }
            Assert.Empty(host.Committed); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            foreach (QpdfPath path in OutputPaths(route))
            { Assert.True(host.Inner.TryGetOutput(path, out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
            return new(governor, [], []);
        }
        finally { host.Resume(); try { await task; } catch (QpdfException) { } }
    }

    [Theory]
    [Trait("Layer", "Contract")]
    [MemberData(nameof(AdmissionJobs))]
    public async Task MemoryCeilingsPreservePublicationAndCleanup(string route, bool compressed, string budget)
    {
        CompletedAliases aliases = await CompleteAdmission(route, compressed, budget);
        await ValidateOutputs(aliases, route);
        Assert.Equal(0L, aliases.Governor.PrivateRetainedBytes);
        output.WriteLine("CONTRACT PASS " + AdmissionId(route, compressed, budget));
    }

    private static async Task ValidateOutputs(CompletedAliases aliases, string route)
    {
        Dictionary<string, byte[]> expected = [];
        if (route.EndsWith("copy", StringComparison.Ordinal))
        {
            expected["copy-alpha.txt"] = "Copy source alpha."u8.ToArray();
            expected["copy-beta.txt"] = "Copy source beta, slightly longer."u8.ToArray();
        }
        if (route.EndsWith("add", StringComparison.Ordinal) || route.EndsWith("remove", StringComparison.Ordinal))
        {
            expected["kept"] = "q Q\n"u8.ToArray();
            if (route.EndsWith("add", StringComparison.Ordinal))
            { expected["payload"] = "q Q\n"u8.ToArray(); expected["new"] = "Added payload.\n"u8.ToArray(); }
        }
        for (int index = 0; index < aliases.Outputs.Length; index++)
        {
            InMemoryQpdfHost validation = new(); validation.AddInput(Input, aliases.Outputs[index], QpdfInputOwnership.Copy);
            QpdfCheckResult check = await Qpdf.CheckAsync(validation, Input, CancellationToken.None);
            Assert.True(check.Passed); Assert.Equal(aliases.PageCounts[index], check.PageCount);
            if (expected.Count != 0)
            {
                var attachments = await Qpdf.ListAttachmentsAsync(validation, Input, CancellationToken.None);
                Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), attachments.Select(attachment => attachment.Key));
                foreach ((string key, byte[] bytes) in expected)
                {
                    QpdfPath extracted = QpdfPath.From("extracted.bin");
                    await Qpdf.ExtractAttachmentAsync(validation, Input, key, extracted, CancellationToken.None);
                    Assert.True(validation.TryGetOutput(extracted, out byte[] actual)); Assert.Equal(bytes, actual);
                }
            }
            Assert.Equal(0L, validation.OpenLeaseCount);
        }
    }

    [Theory]
    [Trait("Layer", "Contract")]
    [MemberData(nameof(CompleteJobs))]
    public async Task AffectedPublicJobsPreservePublishedOutputsAndCloseLeases(string route, bool compressed)
    {
        CompletedAliases aliases = await CompleteAffected(route, compressed);
        await ValidateOutputs(aliases, route);
        GC.KeepAlive(aliases.Outputs);
        output.WriteLine("CONTRACT PASS " + SuccessId(route, compressed));
    }

    [Theory]
    [Trait("Layer", "Ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalDefaultLimitJobsRetireAfterDroppingEveryWarningAlias(bool compressed)
    {
        Aliases aliases = Complete(compressed);
        Collect(aliases.Governor);
        Assert.All(aliases.Metadata, reference => Assert.False(reference.IsAlive));
        ValidateBacking(aliases);
        long before = aliases.Governor.Snapshot().LiveMemoryBytes;
        aliases.Backing = null;
        QpdfExecutionCounters backingDead = Collect(aliases.Governor);
        Assert.False(aliases.BackingReference.IsAlive);
        Assert.Equal(before, backingDead.LiveMemoryBytes);
        DropDiagnosticAliases(aliases);
        QpdfExecutionCounters completed = Collect(aliases.Governor);
        Assert.Equal(aliases.Governor.CallerMetadataBytes + aliases.Governor.CallerStorageBytes, completed.LiveMemoryBytes);
        Assert.Equal(0L, completed.OpenHandles);
        InMemoryQpdfHost validation = new();
        validation.AddInput(Input, aliases.Output, QpdfInputOwnership.Copy);
        QpdfCheckResult check = await Qpdf.CheckAsync(validation, Input, CancellationToken.None);
        Assert.True(check.Passed); Assert.Equal(1, check.PageCount);
        Assert.Equal(0L, validation.OpenLeaseCount);
        GC.KeepAlive(aliases.Output);
        output.WriteLine("ORIGINAL PASS " + EncodingId(compressed));
    }

    private static string EncodingId(bool compressed) => compressed ? "flate" : "raw";
    private static string SuccessId(string route, bool compressed) => "success|" + route + "|" + EncodingId(compressed);
    private static string AdmissionId(string route, bool compressed, string budget)
        => "admission|" + route + "|" + EncodingId(compressed) + "|" + budget;
    private static string FailureId(string route, bool compressed, string boundary, string outcome, bool secondary)
        => "failure|" + route + "|" + EncodingId(compressed) + "|" + boundary + "|" + outcome + "|" + (secondary ? "secondary" : "primary");

}
