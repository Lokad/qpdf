using System.IO.Compression;
using System.Reflection;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ExecutionCounterTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath InputPath = QpdfPath.From("input.pdf");
    private static readonly QpdfPath OutputPath = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Jobs()
    {
        foreach (string route in new[] { "argv-whole", "argv-linearized", "argv-pages", "argv-merge", "job-whole",
            "mixed-whole", "response-whole", "argv-json", "argv-filtered", "job-check", "typed-page",
            "typed-page-rotation", "typed-pages", "typed-pages-rotation", "typed-pages-options", "typed-check",
            "typed-json", "typed-import", "typed-update", "typed-extract", "typed-add", "typed-remove", "typed-copy",
            "qdf", "zlib-compress", "zlib-uncompress" })
        foreach (bool compressed in new[] { false, true })
        {
            bool readOnly = route is "argv-json" or "argv-filtered" or "job-check" or "typed-check";
            foreach (string outcome in new[] { "success", "fault", "cancel" })
            { yield return [route, compressed, readOnly ? "input-close" : "write", outcome]; }
            if (!readOnly)
            {
                yield return [route, compressed, "commit", "fault"];
                yield return [route, compressed, "input-close", "fault"];
                yield return [route, compressed, "output-close", "success"];
                yield return [route, compressed, "output-close", "fault"];
                yield return [route, compressed, "output-close", "cancel"];
            }
        }
    }

    private static ResourceGovernor? FindGovernor(object value, int depth, HashSet<object> seen)
    {
        if (depth > 96 || !seen.Add(value)) { return null; }
        if (value is ResourceGovernor governor) { return governor; }
        if (value is Task task)
        {
            object? state = task.GetType().GetField("StateMachine", Fields)?.GetValue(task);
            return state is null ? null : FindGovernor(state, depth + 1, seen);
        }
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        { if (field.GetValue(value) is ResourceGovernor found) { return found; } }
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        {
            if (field.GetValue(value) is object nested &&
                (nested is Task || nested.GetType().IsValueType && !nested.GetType().IsPrimitive && !nested.GetType().IsEnum))
            {
                ResourceGovernor? found = FindGovernor(nested, depth + 1, seen);
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    private static byte[] Deflate(byte[] bytes)
    {
        using MemoryStream output = new();
        using (ZLibStream encoder = new(output, CompressionLevel.Optimal, true)) { encoder.Write(bytes); }
        return output.ToArray();
    }

    private static QpdfAttachmentSpec Attachment()
        => new("attachment.bin", "payload", "payload.bin", null, null, null, null, false);

    private static async Task<CounterHost> Prepare(string route, bool compressed, string boundary, string outcome)
    {
        byte[] content = "q Q\n"u8.ToArray();
        byte[] body = compressed ? Deflate(content) : content;
        byte[] source = CheckTests.Assemble(CheckTests.SinglePage(
            "<< /Length " + body.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>", body), 1);
        InMemoryQpdfHost setup = new();
        setup.AddInput(InputPath, source, QpdfInputOwnership.Copy);
        setup.AddInput(QpdfPath.From("attachment.bin"), content, QpdfInputOwnership.Copy);
        if (route is "typed-extract" or "typed-remove" or "typed-copy")
        {
            await Qpdf.AddAttachmentsAsync(setup, InputPath, QpdfPath.From("attached.pdf"), [Attachment()], CancellationToken.None);
            Assert.True(setup.TryGetOutput(QpdfPath.From("attached.pdf"), out source));
        }
        if (route == "qdf")
        {
            QpdfExecutionResult qdf = await Qpdf.ExecuteAsync(setup,
                new QpdfCommandInvocation(["input.pdf", "--qdf", "--object-streams=disable", "qdf.pdf"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, qdf.Status);
            Assert.True(setup.TryGetOutput(QpdfPath.From("qdf.pdf"), out source));
        }
        CounterHost host = new(boundary, outcome);
        host.Inner.AddInput(InputPath, source, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("second.pdf"), source, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("attachment.bin"), content, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("bytes.bin"), route == "zlib-uncompress" ? Deflate(content) : content, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("args.txt"), "input.pdf\noutput.pdf\n"u8.ToArray(), QpdfInputOwnership.Copy);
        if (route is "typed-import" or "typed-update")
        {
            QpdfJsonResult json = await Qpdf.GetDocumentJsonAsync(setup, InputPath, QpdfPath.From("document.json"),
                new QpdfJsonOptions(2, [], "inline", null, [], false), CancellationToken.None);
            host.Inner.AddInput(QpdfPath.From("input.json"), Encoding.UTF8.GetBytes(json.Json), QpdfInputOwnership.Copy);
        }
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(OutputPath, CancellationToken.None);
        await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
        await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    private static async Task<object> Start(CounterHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        QpdfExecutionOptions options = new(limits);
        if (route == "job-whole")
        { return await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input.pdf\",\"outputFile\":\"output.pdf\"}", options, token); }
        if (route == "job-check")
        { return await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input.pdf\",\"check\":\"\"}", options, token); }
        if (route.StartsWith("argv-", StringComparison.Ordinal) || route is "mixed-whole" or "response-whole")
        {
            string[] argv = route switch
            {
                "argv-whole" => ["input.pdf", "output.pdf"],
                "argv-linearized" => ["input.pdf", "--linearize", "output.pdf"],
                "argv-pages" => ["input.pdf", "--pages", ".", "1", "--", "output.pdf"],
                "argv-merge" => ["input.pdf", "--pages", ".", "1", "second.pdf", "1", "--", "output.pdf"],
                "argv-json" => ["input.pdf", "--json", "--json-stream-data=inline"],
                "argv-filtered" => ["input.pdf", "--show-object=4", "--filtered-stream-data"],
                "mixed-whole" => ["input.pdf", "--job-json-file=settings.json", "output.pdf"],
                "response-whole" => ["@args.txt"],
                _ => throw new InvalidOperationException(route),
            };
            return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, token);
        }
        if (route is "typed-page" or "typed-page-rotation")
        {
            return route == "typed-page"
                ? await Qpdf.SelectPageAsync(host, InputPath, OutputPath, new PdfPageSelectionOptions(1), token)
                : await Qpdf.SelectPageAsync(host, InputPath, OutputPath, new PdfPageSelectionOptions(1), [new PdfPageRotation(PdfRotationMode.Set, 90, [1])], token);
        }
        if (route.StartsWith("typed-pages", StringComparison.Ordinal))
        {
            PdfPageSource[] sources = [new(InputPath, new PdfPageSelectionOptions(1)), new(QpdfPath.From("second.pdf"), new PdfPageSelectionOptions(1))];
            if (route == "typed-pages") { return await Qpdf.SelectPagesAsync(host, sources, OutputPath, token); }
            if (route == "typed-pages-rotation") { return await Qpdf.SelectPagesAsync(host, sources, OutputPath, [new PdfPageRotation(PdfRotationMode.Set, 90, [1])], token); }
            return await Qpdf.SelectPagesAsync(host, sources, OutputPath, new PdfPageMergeOptions(), token);
        }
        if (route == "typed-check") { return await Qpdf.CheckAsync(host, InputPath, token); }
        if (route == "typed-json") { return await Qpdf.GetDocumentJsonAsync(host, InputPath, OutputPath, new QpdfJsonOptions(2, []), token); }
        if (route == "typed-import") { return await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("input.json"), OutputPath, token); }
        if (route == "typed-update") { return await Qpdf.UpdatePdfFromJsonAsync(host, InputPath, QpdfPath.From("input.json"), OutputPath, token); }
        if (route == "typed-extract") { return await Qpdf.ExtractAttachmentAsync(host, InputPath, "payload", OutputPath, token); }
        if (route == "typed-add") { return await Qpdf.AddAttachmentsAsync(host, InputPath, OutputPath, [Attachment()], token); }
        if (route == "typed-remove") { return await Qpdf.RemoveAttachmentsAsync(host, InputPath, OutputPath, ["payload"], token); }
        if (route == "typed-copy") { return await Qpdf.CopyAttachmentsAsync(host, InputPath, OutputPath, [new QpdfAttachmentCopy(QpdfPath.From("second.pdf"), "copy-")], token); }
        if (route == "qdf") { return await Qpdf.FixQdfAsync(host, InputPath, OutputPath, limits, token); }
        return await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("bytes.bin"), OutputPath,
            new QpdfZlibFlateOptions(route == "zlib-compress" ? QpdfZlibFlateMode.Compress : QpdfZlibFlateMode.Uncompress, null), limits, token);
    }

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task PublicJobsReturnTheActualFinalSnapshot(string route, bool compressed, string boundary, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        CounterHost host = await Prepare(route, compressed, boundary, outcome);
        Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] priorAlias));
        Task<object> executing = Start(host, route, QpdfLimits.Default, cancellation.Token);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = FindGovernor(executing, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
                ?? throw new InvalidOperationException("Expected the actual public job governor.");
            Assert.True(host.ReadBytes > 0L);
            Assert.True(governor.Snapshot().HostOperations > 0L);
            if (boundary != "input-close" || route is not ("response-whole" or "typed-import" or "qdf" or "zlib-compress" or "zlib-uncompress"))
            { }
            Assert.False(executing.IsCompleted);
            if (outcome == "cancel") { cancellation.Cancel(); } else { host.Resume(); }
            object? result = null;
            if (route.StartsWith("typed-", StringComparison.Ordinal) && outcome != "success")
            {
                if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await executing); }
                else { await Assert.ThrowsAnyAsync<QpdfException>(async () => await executing); }
            }
            else
            {
                result = await executing.WaitAsync(TimeSpan.FromSeconds(10));
                if (result is QpdfExecutionResult command)
                {
                    Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel"
                        ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, command.Status);
                    if (outcome == "success" && route == "argv-json") { Assert.NotNull(command.OutputText); }
                    if (outcome == "success" && route == "argv-filtered") { Assert.Equal("q Q\n"u8.ToArray(), command.OutputBytes?.ToArray()); }
                    if (outcome != "success") { Assert.Null(command.OutputText); Assert.Null(command.OutputBytes); }
                }
                PropertyInfo countersProperty = result.GetType().GetProperty("Counters") ?? throw new InvalidOperationException();
                Assert.Null(countersProperty.GetSetMethod());
                if (result is not QpdfExecutionResult) { Assert.False(countersProperty.CanWrite); }
                QpdfExecutionCounters counters = Assert.IsType<QpdfExecutionCounters>(countersProperty.GetValue(result));
                Assert.Equal(governor.Snapshot(), counters);
                Assert.Equal(0L, counters.OpenHandles);
                if (outcome == "success") { }
            }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.Equal(new byte[] { 17, 23, 41 }, priorAlias);
            if (host.Commits == 0) { Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] kept)); Assert.Equal(priorAlias, kept); }
            GC.KeepAlive(executing); GC.KeepAlive(result); GC.KeepAlive(priorAlias);
        }
        finally
        {
            host.Resume();
            {
                try { await executing; } catch (QpdfException) { } catch (OperationCanceledException) { }
            }
        }
    }

    public static IEnumerable<object[]> Configurations()
    {
        foreach (string route in new[] { "response-help", "response-utf8", "response-parse", "response-zopfli",
            "nested-job", "multi-job", "mixed-job" })
        foreach (string outcome in new[] { "success", "fault", "cancel" })
        { yield return [route, outcome]; }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task ConfigurationFailuresKeepObservedCounters(string route, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        CounterHost host = await Prepare("argv-whole", false, "input-close", outcome);
        byte[] response = route switch
        {
            "response-help" => "--help\n"u8.ToArray(),
            "response-utf8" => [0xff, 0x00],
            "response-parse" => "--not-an-option\n"u8.ToArray(),
            _ => "--zopfli\n"u8.ToArray(),
        };
        host.Inner.AddInput(QpdfPath.From("args.txt"), response, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("invalid.json"), "{\"unknownOption\":\"\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("outer.json"), "{\"jobJsonFile\":\"invalid.json\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        host.PausePath = QpdfPath.From(route.StartsWith("response-", StringComparison.Ordinal) ? "args.txt" : "invalid.json");
        Task<QpdfExecutionResult> executing = route == "nested-job"
            ? Qpdf.ExecuteJobAsync(host, "{\"jobJsonFile\":\"outer.json\"}", cancellation.Token).AsTask()
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(route switch
            {
                "multi-job" => ["--job-json-file=settings.json", "--job-json-file=invalid.json"],
                "mixed-job" => ["input.pdf", "--job-json-file=invalid.json", "output.pdf"],
                _ => ["@args.txt"],
            }), cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = FindGovernor(executing, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
                ?? throw new InvalidOperationException("Expected the actual configuration governor.");
            Assert.True(host.ReadBytes > 0L);
            if (outcome == "cancel") { cancellation.Cancel(); } else { host.Resume(); }
            QpdfExecutionResult result = await executing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome == "cancel" ? QpdfExecutionStatus.Canceled : outcome == "success" && route == "response-help"
                ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(governor.Snapshot(), result.Counters);
            Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] kept));
            Assert.Equal(new byte[] { 17, 23, 41 }, kept);
            Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
            if (outcome != "cancel") { Assert.NotEmpty(result.Diagnostics); }
            GC.KeepAlive(executing);
        }
        finally { host.Resume(); await executing; }
    }

    public static IEnumerable<object[]> LimitedJobs()
    {
        foreach (string route in new[] { "argv-whole", "argv-pages", "job-whole", "mixed-whole", "response-whole",
            "qdf", "zlib-compress", "zlib-uncompress" })
        foreach (bool compressed in new[] { false, true })
        { yield return [route, compressed]; }
    }

    [Theory]
    [MemberData(nameof(LimitedJobs))]
    public async Task RealHostBudgetFailureKeepsTheCompletedRead(string route, bool compressed)
    {
        CounterHost host = await Prepare(route, compressed, "read", "success");
        host.ShortReads = true;
        QpdfLimits limits = QpdfLimits.Default with { MaxHostOperations = 2L };
        Task<object> executing = Start(host, route, limits, CancellationToken.None);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = FindGovernor(executing, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
                ?? throw new InvalidOperationException("Expected the actual limited public governor.");
            Assert.Equal(1L, host.ReadBytes);
            Assert.Equal(2L, governor.Snapshot().HostOperations);
            host.Resume();
            QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await executing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(governor.Snapshot(), result.Counters);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal(1L, host.ReadBytes);
            Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] kept));
            Assert.Equal(new byte[] { 17, 23, 41 }, kept);
            GC.KeepAlive(executing);
        }
        finally { host.Resume(); await executing; }
    }

    private sealed class CounterHost(string boundary, string outcome) : IQpdfHost
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        internal InMemoryQpdfHost Inner { get; } = new();
        internal Task Entered => _entered.Task;
        internal int Commits;
        internal long ReadBytes;
        internal bool ShortReads;
        internal QpdfPath? PausePath;
        internal void Resume() => _resume.TrySetResult();

        private async ValueTask Pause(string phase, QpdfPath path, CancellationToken token)
        {
            if (_paused || phase != boundary || PausePath.HasValue && !PausePath.Value.Equals(path)) { return; }
            _paused = true;
            _entered.TrySetResult();
            await _resume.Task.WaitAsync(token);
            if (outcome == "fault") { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
        }

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(this, await Inner.OpenInputAsync(path, token), token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Output(this, await Inner.CreateOutputAsync(path, token), path, token);

        private sealed class Input(CounterHost host, IQpdfInput inner, CancellationToken token) : IQpdfInput
        {
            private bool _closed;
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
            {
                ReadOnlyMemory<byte> bytes = await inner.ReadAsync(offset, host.ShortReads ? Math.Min(count, 1) : count, cancellationToken);
                host.ReadBytes += bytes.Length;
                await host.Pause("read", Path, cancellationToken);
                return bytes;
            }
            public async ValueTask DisposeAsync()
            {
                if (_closed) { return; }
                _closed = true;
                try { await host.Pause("input-close", Path, token); }
                finally { await inner.DisposeAsync(); }
            }
        }

        private sealed class Output(CounterHost host, IQpdfOutputTransaction inner, QpdfPath path, CancellationToken token) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask DiscardAsync(CancellationToken cancellationToken) => inner.DiscardAsync(cancellationToken);
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
            { await host.Pause("write", path, cancellationToken); await inner.WriteAsync(chunk, cancellationToken); }
            public async ValueTask CommitAsync(CancellationToken cancellationToken)
            { await host.Pause("commit", path, cancellationToken); await inner.CommitAsync(cancellationToken); host.Commits++; }
            public async ValueTask DisposeAsync()
            {
                try { await host.Pause("output-close", path, token); }
                finally { await inner.DisposeAsync(); }
            }
        }
    }
}
