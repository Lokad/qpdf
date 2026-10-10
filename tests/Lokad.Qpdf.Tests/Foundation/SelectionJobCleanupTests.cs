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
    private static Aliases Keep(InMemoryQpdfHost host, PdfPageSelectionResult result)
    {
        Assert.Equal(1, result.PageCount); Assert.Single(result.Warnings);
        Assert.Equal(QpdfDiagnosticCode.PageCountMismatch, result.Warnings[0].Code);
        Assert.Equal("A /Pages /Count of 2 disagrees with traversal (1); traversal wins.", result.Warnings[0].Message);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        WeakReference<ResourceGovernor> owner = (WeakReference<ResourceGovernor>)Field(Field(result, "_storage"), "_owner");
        Assert.True(owner.TryGetTarget(out ResourceGovernor? governor));
        Array backing = result.Warnings as Array ?? result.Warnings.GetType().GetFields(Members)
            .Select(field => field.GetValue(result.Warnings)).OfType<Array>().Single();
        return new(governor ?? throw new InvalidOperationException(), backing, result.Warnings.ToArray(),
            [new(host), new(result), new(result.Counters), new(result.SourcePages)], output);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Aliases> Complete(bool compressed)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(compressed), QpdfInputOwnership.Copy);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, Input, Output,
            new PdfPageSelectionOptions(1), QpdfLimits.Default, CancellationToken.None);
        return Keep(host, result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static QpdfExecutionCounters Collect(ResourceGovernor governor)
    {
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            governor.ReserveMemory(0L);
        }
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
        { return await Qpdf.AddAttachmentsAsync(host, Input, Output,
            [new QpdfAttachmentSpec("attachment.bin", "new", "new.bin", null, null, null, null, false)], limits, token); }
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

    private sealed class CompletedAliases(ResourceGovernor governor, WeakReference[] metadata, byte[][] outputs, int[] pageCounts)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal readonly WeakReference[] Metadata = metadata;
        internal readonly byte[][] Outputs = outputs;
        internal readonly int[] PageCounts = pageCounts;
        internal string RetirementDetail = "";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CompletedAliases KeepAffected(InMemoryQpdfHost host, object result, string route)
    {
        if (result is QpdfExecutionResult command) { Assert.Equal(QpdfExecutionStatus.Completed, command.Status); }
        QpdfExecutionCounters counters = (QpdfExecutionCounters)Read(result, "Counters");
        WeakReference<ResourceGovernor> owner = (WeakReference<ResourceGovernor>)Field(Field(counters, "_storage"), "_owner");
        Assert.True(owner.TryGetTarget(out ResourceGovernor? governor));
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, counters.OpenHandles);
        QpdfPath[] paths = route == "typed-split" ? [QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf")] : [Output];
        byte[][] outputs = new byte[paths.Length][];
        for (int index = 0; index < paths.Length; index++)
        { Assert.True(host.TryGetOutput(paths[index], out byte[] output)); outputs[index] = output; }
        int pages = route is "typed-pages" or "typed-pages-rotation" or "typed-pages-options" or "argv-pages" or "job-pages" ? 2 : 1;
        return new(governor ?? throw new InvalidOperationException(), [new(host), new(result), new(counters)],
            outputs, Enumerable.Repeat(pages, outputs.Length).ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<CompletedAliases> CompleteAffected(string route, bool compressed)
    {
        InMemoryQpdfHost host = new(); AddInputs(host, route, compressed);
        object result = await Start(host, route, QpdfLimits.Default, CancellationToken.None);
        return KeepAffected(host, result, route);
    }

    public static IEnumerable<object[]> CompleteJobs()
    { foreach (string route in Routes) foreach (bool compressed in new[] { false, true }) { yield return [route, compressed]; } }

    private sealed class BoundaryHost(string boundary, QpdfPath boundaryPath, string outcome) : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal readonly HashSet<QpdfPath> Committed = [];
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellation)
            { ReadOnlyMemory<byte> bytes = await inner.ReadAsync(offset, count, cancellation); await host.Pause("read", Path, cancellation); return bytes; }
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string NonprepaidCarriers(ResourceGovernor governor)
    {
        object? ledger = governor.GetType().GetField("_valueOwners", Members)?.GetValue(governor);
        List<string> names = [];
        for (object? segment = ledger?.GetType().GetField("_segments", Members)?.GetValue(ledger);
            segment is not null; segment = segment.GetType().GetField("Next", Members)?.GetValue(segment))
        {
            ConditionalWeakTable<object, RetainedValueOwners.Receipt> values =
                (ConditionalWeakTable<object, RetainedValueOwners.Receipt>)Field(segment, "Values");
            foreach (KeyValuePair<object, RetainedValueOwners.Receipt> pair in values)
            { if (!pair.Value.PrepaidRetirement) { names.Add(pair.Key.GetType().FullName + ":" + pair.Value.Bytes); } }
        }
        return string.Join(", ", names);
    }

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
        BoundaryHost host = new(boundary, pausePath, outcome);
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
            return new(governor, [new(host), new(host.Inner), new(task), new(result), new(failure)],
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
        foreach (string budget in new[] { "memory", "work" }) { yield return [route, compressed, budget]; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<CompletedAliases> CompleteAdmission(string route, bool compressed, string budget)
    {
        BoundaryHost host = new("read", Input, "success");
        AddInputs(host.Inner, route, compressed); await Seed(host.Inner, route);
        QpdfLimits limits = budget == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 16384L }
            : QpdfLimits.Default with { MaxWork = 8192L };
        Task<object> task = Start(host, route, limits, CancellationToken.None);
        object? result = null;
        Exception? failure = null;
        ResourceGovernor? governor = null;
        Assert.True(GC.TryStartNoGCRegion(32L * 1024L * 1024L));
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            governor = RunningGovernor(task); host.Resume();
            try { result = await task; } catch (Exception exception) { failure = exception; }
            if (route.StartsWith("typed-", StringComparison.Ordinal))
            {
                QpdfLimitException limit = Assert.IsType<QpdfLimitException>(failure);
                Assert.Equal(budget == "memory" ? nameof(QpdfLimits.MaxMemoryBytes) : nameof(QpdfLimits.MaxWork), limit.LimitName);
            }
            else
            {
                Assert.Null(failure);
                QpdfExecutionResult execution = Assert.IsType<QpdfExecutionResult>(result);
                Assert.Equal(QpdfExecutionStatus.Failed, execution.Status);
                Assert.Contains(execution.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            }
            Assert.Empty(host.Committed); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
            Assert.True(governor.Snapshot().TotalWork <= limits.MaxWork);
            foreach (QpdfPath path in OutputPaths(route))
            { Assert.True(host.Inner.TryGetOutput(path, out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
            return new(governor, [new(host), new(host.Inner), new(task), new(result), new(failure)], [], [])
                { RetirementDetail = NonprepaidCarriers(governor) };
        }
        finally { host.Resume(); try { await task; } catch (QpdfException) { } GC.EndNoGCRegion(); }
    }

    [Theory]
    [Trait("Layer", "Contract")]
    [MemberData(nameof(AdmissionJobs))]
    public async Task OriginalShortMemoryAndWorkCeilingsRejectBeforePublication(string route, bool compressed, string budget)
    {
        CompletedAliases aliases = await CompleteAdmission(route, compressed, budget);
        Assert.True(aliases.RetirementDetail.Length == 0, "Unpaid carriers: " + aliases.RetirementDetail);
        output.WriteLine("CONTRACT PASS " + AdmissionId(route, compressed, budget));
    }

    private static string CleanupDetail(ResourceGovernor governor)
    {
        object? ledger = governor.GetType().GetField("_valueOwners", Members)?.GetValue(governor);
        if (ledger is null) { return "retained ledger empty"; }
        int pending = 0;
        for (object? receipt = ledger.GetType().GetField("_pending", Members)?.GetValue(ledger);
            receipt is not null && pending < 512; receipt = receipt.GetType().GetField("Next", Members)?.GetValue(receipt))
        { pending++; }
        return "live ledger slots " + Field(ledger, "_count") + "; pending retirements " + pending;
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
        Aliases aliases = await Complete(compressed);
        await Task.Yield();
        Collect(aliases.Governor);
        Assert.All(aliases.Metadata, reference => Assert.False(reference.IsAlive));
        ValidateBacking(aliases);
        long before = aliases.Governor.Snapshot().LiveMemoryBytes;
        aliases.Backing = null;
        QpdfExecutionCounters backingDead = Collect(aliases.Governor);
        Assert.False(aliases.BackingReference.IsAlive);
        Assert.True(before - backingDead.LiveMemoryBytes >= 32L);
        DropDiagnosticAliases(aliases);
        QpdfExecutionCounters completed = Collect(aliases.Governor);
        Assert.Equal(0L, completed.LiveMemoryBytes);
        Assert.Equal(0L, completed.OpenHandles);
        Assert.Equal(0L, (long)Field(aliases.Governor, "_reservedWork"));
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

    private sealed record Observation(string Id, string Route, CompletedAliases Aliases);

    private static void AssertRetired(CompletedAliases aliases)
    {
        QpdfExecutionCounters counters = aliases.Governor.Snapshot();
        Assert.All(aliases.Metadata, reference => Assert.False(reference.IsAlive));
        Assert.True(counters.LiveMemoryBytes == 0L, "Remaining memory " + counters.LiveMemoryBytes + "; " + CleanupDetail(aliases.Governor));
        Assert.Equal(0L, counters.OpenHandles);
        Assert.Equal(0L, (long)Field(aliases.Governor, "_reservedWork"));
    }

    // Jobs keep independent budgets/governors. Share only the global GC rounds;
    // each governor receives exactly the original four maintenance boundaries.
    private async Task ObserveBatch(IEnumerable<(string Id, string Route, Func<Task<CompletedAliases>> Complete)> jobs)
    {
        List<Observation> observations = [];
        Dictionary<string, string> failures = new(StringComparer.Ordinal);
        HashSet<ResourceGovernor> governors = new(ReferenceEqualityComparer.Instance);
        foreach ((string id, string route, Func<Task<CompletedAliases>> complete) in jobs)
        {
            try
            {
                CompletedAliases aliases = await complete();
                Assert.True(governors.Add(aliases.Governor), "Scenarios must have independent governors: " + id);
                observations.Add(new(id, route, aliases));
                Assert.True(aliases.RetirementDetail.Length == 0, "Unpaid carriers: " + aliases.RetirementDetail);
            }
            catch (Exception exception) { failures[id] = exception.ToString(); }
        }
        await Task.Yield(); // Finish producer completion frames before observing weak aliases.
        int collections = 0, maintenance = 0;
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            collections++;
            foreach (Observation observation in observations)
            {
                maintenance++;
                try { observation.Aliases.Governor.ReserveMemory(0L); }
                catch (Exception exception) { failures.TryAdd(observation.Id, exception.ToString()); }
            }
        }
        // Observe every cleanup before any output-check job can trigger another GC.
        foreach (Observation observation in observations)
        {
            try { AssertRetired(observation.Aliases); }
            catch (Exception exception) { failures.TryAdd(observation.Id, exception.ToString()); }
        }
        foreach (Observation observation in observations)
        {
            try { await ValidateOutputs(observation.Aliases, observation.Route); }
            catch (Exception exception) { failures.TryAdd(observation.Id, exception.ToString()); }
            if (!failures.ContainsKey(observation.Id)) { output.WriteLine("OWNERSHIP PASS " + observation.Id); }
        }
        output.WriteLine("OWNERSHIP BATCH cases=" + observations.Count + " collections=" + collections + " maintenance=" + maintenance);
        foreach ((string id, string failure) in failures) { output.WriteLine("OWNERSHIP FAIL " + id + "\n" + failure); }
        GC.KeepAlive(observations);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Select(pair => pair.Key + ": " + pair.Value)));
    }

    [Theory]
    [Trait("Layer", "Ownership")]
    [MemberData(nameof(CompleteJobs))]
    public Task CompleteFaultAndCancellationBatchesRetireEveryScenario(string route, bool compressed)
        => ObserveBatch(FailureJobs().Where(job => (string)job[0] == route && (bool)job[1] == compressed)
            .Select(job => (FailureId(route, compressed, (string)job[2], (string)job[3], (bool)job[4]), route,
                new Func<Task<CompletedAliases>>(() => CompleteFailure(route, compressed, (string)job[2], (string)job[3], (bool)job[4])))));

    [Theory]
    [Trait("Layer", "Ownership")]
    [InlineData(false, "memory")]
    [InlineData(false, "work")]
    [InlineData(true, "memory")]
    [InlineData(true, "work")]
    public Task OriginalShortAdmissionBatchesRetireEveryScenario(bool compressed, string budget)
        => ObserveBatch(Routes.Select(route => (AdmissionId(route, compressed, budget), route,
            new Func<Task<CompletedAliases>>(() => CompleteAdmission(route, compressed, budget)))));

    [Theory]
    [Trait("Layer", "Ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public Task SuccessfulPublicBatchesRetireWithSurvivingOutputs(bool compressed)
        => ObserveBatch(Routes.Select(route => (SuccessId(route, compressed), route,
            new Func<Task<CompletedAliases>>(() => CompleteAffected(route, compressed)))));
}
