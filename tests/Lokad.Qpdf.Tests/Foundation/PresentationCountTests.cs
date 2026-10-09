using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class PresentationCountTests
{
    private static readonly byte[] Prior = [17, 23, 41];
    private static readonly string[] Targets = ["output.pdf", "output-1.pdf", "output-2.pdf", "side-4"];

    public static IEnumerable<object[]> Jobs()
    {
        foreach (string invocation in new[] { "argv", "job", "mixed", "response" })
        foreach (string operation in new[] { "whole", "linearized", "qdf", "pages", "merge", "split",
            "json-file", "json-inline", "json-sidecar", "json-import", "json-update", "empty", "check", "show-count", "filtered" })
        foreach (bool compressed in new[] { false, true })
        foreach (string ceiling in new[] { "default", "exact", "short" })
        { yield return [invocation, operation, compressed, ceiling]; }
    }

    private static byte[] Source(bool compressed)
    {
        byte[] body = "q Q\n"u8.ToArray();
        if (compressed)
        {
            using MemoryStream buffer = new();
            using (ZLibStream encoder = new(buffer, CompressionLevel.Optimal, true)) { encoder.Write(body); }
            body = buffer.ToArray();
        }
        return CheckTests.Assemble(CheckTests.SinglePage(
            "<< /Length " + body.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>", body), 1);
    }

    private static async Task<PublicationHost> Prepare(string operation, bool compressed)
    {
        PublicationHost host = new();
        host.Inner.AddInput(QpdfPath.From("input.pdf"), Source(compressed), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("second.pdf"), Source(compressed), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        if (operation is "json-import" or "json-update")
        {
            QpdfJsonResult json = await Qpdf.GetDocumentJsonAsync(host.Inner, QpdfPath.From("input.pdf"),
                QpdfPath.From("setup.json"), new QpdfJsonOptions(2, [], "inline", null, [], false), CancellationToken.None);
            host.Inner.AddInput(QpdfPath.From("input.json"), Encoding.UTF8.GetBytes(json.Json), QpdfInputOwnership.Copy);
        }
        foreach (string target in Targets)
        {
            await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(QpdfPath.From(target), CancellationToken.None);
            await seed.WriteAsync(Prior, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        return host;
    }

    private static async Task<QpdfExecutionResult> Run(PublicationHost host, string invocation, string operation,
        string flags, QpdfLimits limits, CancellationToken token)
    {
        List<string> argv = [operation == "empty" ? "--empty" : operation == "json-import" ? "input.json" : "input.pdf"];
        Dictionary<string, object> job = new(StringComparer.Ordinal);
        if (operation == "empty") { job.Add("empty", ""); }
        else { job.Add("inputFile", operation == "json-import" ? "input.json" : "input.pdf"); }
        foreach (string flag in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            argv.Add("--" + flag);
            job.Add(flag switch { "report-memory-usage" => "reportMemoryUsage", "no-warn" => "noWarn",
                "warning-exit-0" => "warningExit0", _ => flag }, "");
        }
        switch (operation)
        {
            case "empty":
                argv.AddRange(["--pages", "input.pdf", "1", "--"]);
                job.Add("pages", new[] { new { file = "input.pdf", range = "1" } });
                break;
            case "linearized": argv.Add("--linearize"); job.Add("linearize", ""); break;
            case "qdf": argv.Add("--qdf"); job.Add("qdf", ""); break;
            case "pages":
                argv.AddRange(["--pages", ".", "1", "--"]);
                job.Add("pages", new[] { new { file = ".", range = "1" } });
                break;
            case "merge":
                argv.AddRange(["--pages", ".", "1", "second.pdf", "1", "--"]);
                job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = "second.pdf", range = "1" } });
                break;
            case "split":
                argv.AddRange(["--split-pages=1", "--pages", ".", "1", ".", "1", "--"]);
                job.Add("splitPages", "1");
                job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } });
                break;
            case "json-file":
            case "json-inline":
            case "json-sidecar":
                argv.AddRange(["--json=2", "--json-stream-data=" + (operation == "json-sidecar" ? "file" : "inline")]);
                job.Add("json", "2"); job.Add("jsonStreamData", operation == "json-sidecar" ? "file" : "inline");
                if (operation == "json-sidecar") { argv.Add("--json-stream-prefix=side"); job.Add("jsonStreamPrefix", "side"); }
                if (operation != "json-inline") { argv.Add("--"); }
                break;
            case "json-import": argv.AddRange(["--json-input", "--"]); job.Add("jsonInput", ""); break;
            case "json-update": argv.AddRange(["--update-from-json=input.json", "--"]); job.Add("updateFromJson", "input.json"); break;
            case "check": argv.Add("--check"); job.Add("check", ""); break;
            case "show-count": argv.Add("--show-npages"); job.Add("showNpages", ""); break;
            case "filtered":
                argv.AddRange(["--show-object=4", "--filtered-stream-data"]);
                job.Add("showObject", "4"); job.Add("filteredStreamData", "");
                break;
        }
        if (operation is not ("check" or "show-count" or "filtered" or "json-inline"))
        { argv.Add("output.pdf"); job.Add("outputFile", "output.pdf"); }
        QpdfExecutionOptions options = new(limits);
        if (invocation == "job") { return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), options, token); }
        if (invocation == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
        if (invocation == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            argv = ["@args.txt"];
        }
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, token);
    }

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task PublicJobsReserveSummariesBeforePublication(string invocation, string operation, bool compressed, string ceiling)
    {
        PublicationHost plainHost = await Prepare(operation, compressed);
        QpdfExecutionResult plain = await Run(plainHost, invocation, operation, "", QpdfLimits.Default, CancellationToken.None);
        Assert.True(plain.Status == QpdfExecutionStatus.Completed, string.Join("; ", plain.Diagnostics.Select(d => d.Message)));
        long notes = plain.Counters.TotalOutputBytes > 0L ? 6L : 2L;
        long required = plain.Counters.DiagnosticCount + notes;
        QpdfLimits limits = ceiling == "default" ? QpdfLimits.Default
            : QpdfLimits.Default with { MaxDiagnostics = required - (ceiling == "short" ? 1L : 0L) };
        PublicationHost host = await Prepare(operation, compressed);
        byte[][] priorAliases = Targets.Select(target =>
        { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] bytes)); return bytes; }).ToArray();
        QpdfExecutionResult result = await Run(host, invocation, operation, "verbose progress report-memory-usage", limits, CancellationToken.None);
        if (ceiling == "short")
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded
                && diagnostic.Message.Contains(nameof(QpdfLimits.MaxDiagnostics), StringComparison.Ordinal));
            Assert.DoesNotContain(result.Diagnostics, diagnostic => IsSummary(diagnostic.Code));
            Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
            Assert.Equal(0, host.Commits);
            foreach (string target in Targets)
            { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] bytes)); Assert.Equal(Prior, bytes); }
        }
        else
        {
            Assert.Equal(plain.Status, result.Status); Assert.Equal(plain.ExitCode, result.ExitCode);
            Assert.Equal(required, result.Counters.DiagnosticCount);
            Assert.Equal(notes, result.Diagnostics.LongCount(diagnostic => IsSummary(diagnostic.Code)));
            Assert.Equal(plain.Counters.TotalOutputBytes, result.Counters.TotalOutputBytes);
            Assert.Equal(plainHost.Commits, host.Commits);
            Assert.Equal(plain.OutputText, result.OutputText);
            Assert.Equal(plain.OutputBytes?.ToArray(), result.OutputBytes?.ToArray());
            foreach (string target in Targets)
            {
                Assert.True(plainHost.Inner.TryGetOutput(QpdfPath.From(target), out byte[] expected));
                Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] actual)); Assert.Equal(expected, actual);
            }
            QpdfDiagnostic[] summaries = result.Diagnostics.Where(diagnostic => IsSummary(diagnostic.Code)).ToArray();
            Assert.Equal(QpdfDiagnosticCode.VerboseSummary, summaries[0].Code);
            Assert.Equal(QpdfDiagnosticCode.MemoryUsage, summaries[1].Code);
            Assert.Contains($"peak {result.Counters.PeakMemoryBytes} bytes.", summaries[0].Message, StringComparison.Ordinal);
            Assert.Equal($"Peak memory usage: {result.Counters.PeakMemoryBytes} bytes.", summaries[1].Message);
            for (int i = 2; i < summaries.Length; i++)
            { Assert.Equal($"Write progress: {(i - 1) * 25}% of {result.Counters.TotalOutputBytes} bytes.", summaries[i].Message); }
        }
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.All(priorAliases, alias => Assert.Equal(Prior, alias));
        GC.KeepAlive(result); GC.KeepAlive(priorAliases);
    }

    private static bool IsSummary(QpdfDiagnosticCode code)
        => code is QpdfDiagnosticCode.VerboseSummary or QpdfDiagnosticCode.MemoryUsage or QpdfDiagnosticCode.ProgressReport;

    public static IEnumerable<object[]> WarningJobs()
    {
        foreach (string invocation in new[] { "argv", "job", "mixed", "response" })
        foreach (bool noWarn in new[] { false, true })
        foreach (bool warningExitZero in new[] { false, true })
        foreach (bool shortCount in new[] { false, true })
        { yield return [invocation, noWarn, warningExitZero, shortCount]; }
    }

    [Theory]
    [MemberData(nameof(WarningJobs))]
    public async Task OrdinaryWarningsCannotConsumeReservedSummarySlots(string invocation, bool noWarn, bool warningExitZero, bool shortCount)
    {
        PublicationHost host = await Prepare("whole", false);
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray());
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Duplicate 1 /Duplicate 2 >>", null);
        host.Inner.AddInput(QpdfPath.From("input.pdf"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        string flags = "verbose progress report-memory-usage" + (noWarn ? " no-warn" : "") + (warningExitZero ? " warning-exit-0" : "");
        QpdfExecutionResult result = await Run(host, invocation, "whole", flags,
            QpdfLimits.Default with { MaxDiagnostics = shortCount ? 7L : 8L }, CancellationToken.None);
        if (shortCount)
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal(0, host.Commits);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(Prior, bytes);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => IsSummary(diagnostic.Code));
        }
        else
        {
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(warningExitZero ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
            // Whole-file rewriting opens the primary for selection and copying;
            // both accepted warnings count even when presentation suppresses them.
            Assert.Equal(8L, result.Counters.DiagnosticCount);
            Assert.Equal(6L, result.Diagnostics.LongCount(diagnostic => IsSummary(diagnostic.Code)));
            Assert.Equal(noWarn ? 0 : 2, result.Diagnostics.Count(diagnostic => diagnostic.Code == QpdfDiagnosticCode.DuplicateKey));
            Assert.Equal(1, host.Commits);
        }
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    public static IEnumerable<object[]> FailedJobs()
    {
        foreach (string invocation in new[] { "argv", "job", "mixed", "response" })
        foreach (string operation in new[] { "whole", "linearized", "split", "json-sidecar", "json-inline", "filtered" })
        foreach (bool cancel in new[] { false, true })
        {
            yield return [invocation, operation, "read", cancel];
            if (operation is not ("json-inline" or "filtered"))
            { yield return [invocation, operation, "write", cancel]; yield return [invocation, operation, "commit", cancel]; }
        }
    }

    [Theory]
    [MemberData(nameof(FailedJobs))]
    public async Task FailedPublicJobsReleasePendingSlotsAndCloseLeases(string invocation, string operation, string boundary, bool cancel)
    {
        PublicationHost host = await Prepare(operation, true);
        using CancellationTokenSource cancellation = new();
        host.Boundary = boundary; host.Cancellation = cancel ? cancellation : null;
        byte[][] aliases = Targets.Select(target =>
        { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] bytes)); return bytes; }).ToArray();
        QpdfExecutionResult result = await Run(host, invocation, operation, "verbose progress report-memory-usage",
            QpdfLimits.Default, cancellation.Token);
        Assert.True(host.Hit);
        Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0L, result.Counters.DiagnosticCount);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => IsSummary(diagnostic.Code));
        Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
        Assert.Equal(0, host.Commits);
        Assert.True(result.Counters.HostOperations > 0L);
        if (boundary != "read") { Assert.True(result.Counters.TotalWork > 0L); Assert.True(result.Counters.TotalOutputBytes > 0L); }
        foreach (string target in Targets)
        { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] bytes)); Assert.Equal(Prior, bytes); }
        Assert.All(aliases, alias => Assert.Equal(Prior, alias));
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        GC.KeepAlive(result); GC.KeepAlive(aliases);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("work")]
    [InlineData("count")]
    [InlineData("governor-cancel")]
    [InlineData("caller-cancel")]
    public void FailedReservationDoesNotLeaveAnOwnerOrPendingSlots(string failure)
    {
        QpdfLimits limits = failure switch
        {
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 47L },
            "work" => QpdfLimits.Default with { MaxWork = 1L },
            "count" => QpdfLimits.Default with { MaxDiagnostics = 1L },
            _ => QpdfLimits.Default,
        };
        using CancellationTokenSource cancellation = new();
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) { cancellation.Cancel(); }
        ResourceGovernor governor = new(limits, failure == "governor-cancel" ? cancellation.Token : CancellationToken.None);
        if (failure == "work") { governor.ChargeWork(1L); }
        if (failure.EndsWith("cancel", StringComparison.Ordinal))
        {
            Assert.Throws<OperationCanceledException>(() => PresentationCountReservation.Create(governor, true, true, true,
                failure == "caller-cancel" ? cancellation.Token : CancellationToken.None));
        }
        else
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
                PresentationCountReservation.Create(governor, true, true, true, CancellationToken.None));
            Assert.Equal(failure switch { "memory" => nameof(QpdfLimits.MaxMemoryBytes),
                "work" => nameof(QpdfLimits.MaxWork), _ => nameof(QpdfLimits.MaxDiagnostics) }, error.LimitName);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        governor.ReserveDiagnostic(0L);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void AbandonedReservationRetiresBeforeAnotherOperation()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDiagnostics = 6L }, CancellationToken.None);
        using (PresentationCountReservation? reservation = PresentationCountReservation.Create(governor, true, true, true, CancellationToken.None))
        {
            Assert.NotNull(reservation);
            Assert.Equal(48L, governor.Snapshot().LiveMemoryBytes);
            governor.ChargeOutputBytes(1L);
            Assert.Throws<QpdfLimitException>(() => governor.ReserveDiagnostic(0L));
            Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        governor.ReserveDiagnostic(0L);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void ReservationOwnerFitsItsAdmittedAllowance()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PresentationCountReservation.Create(governor, true, true, true, CancellationToken.None)?.Dispose();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++)
        { PresentationCountReservation.Create(governor, true, true, true, CancellationToken.None)?.Dispose(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 1L, 48L * 1024L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        QpdfExecutionCounters snapshot = governor.Snapshot();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++)
        { Assert.Null(PresentationCountReservation.Create(governor, false, false, false, CancellationToken.None)); }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(snapshot, governor.Snapshot());
    }

    [Theory]
    [InlineData("argv", "whole", "verbose", 1L, true)]
    [InlineData("job", "whole", "verbose", 1L, true)]
    [InlineData("mixed", "whole", "report-memory-usage", 1L, true)]
    [InlineData("response", "whole", "report-memory-usage", 1L, true)]
    [InlineData("argv", "whole", "progress", 4L, true)]
    [InlineData("job", "whole", "progress", 3L, false)]
    [InlineData("mixed", "whole", "progress", 1L, false)]
    [InlineData("response", "whole", "verbose progress report-memory-usage", 1L, false)]
    [InlineData("argv", "json-inline", "progress", 1L, true)]
    [InlineData("job", "json-inline", "progress", 1L, true)]
    [InlineData("mixed", "json-inline", "progress", 1L, true)]
    [InlineData("response", "json-inline", "progress", 1L, true)]
    public async Task SlotsMatchTheSelectedFlagsAndActualOutput(string invocation, string operation, string flags, long count, bool success)
    {
        PublicationHost host = await Prepare(operation, false);
        QpdfExecutionResult result = await Run(host, invocation, operation, flags,
            QpdfLimits.Default with { MaxDiagnostics = count }, CancellationToken.None);
        Assert.Equal(success ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
        if (success) { Assert.Equal(result.Diagnostics.Count, result.Counters.DiagnosticCount); }
        else
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal(0, host.Commits); Assert.Equal(0L, result.Counters.DiagnosticCount);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(Prior, bytes);
        }
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    private sealed class PublicationHost : IQpdfHost
    {
        internal InMemoryQpdfHost Inner { get; } = new();
        internal int Commits { get; private set; }
        internal string? Boundary { get; set; }
        internal CancellationTokenSource? Cancellation { get; set; }
        internal bool Hit { get; private set; }
        private void Observe(string boundary, QpdfPath path, CancellationToken token)
        {
            if (Hit || Boundary != boundary || boundary == "read" && path != QpdfPath.From("input.pdf")) { return; }
            Hit = true;
            if (Cancellation is CancellationTokenSource cancellation) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new QpdfHostException(path, boundary switch { "read" => QpdfHostFailureReason.ReadFailed,
                "write" => QpdfHostFailureReason.WriteFailed, _ => QpdfHostFailureReason.CommitFailed });
        }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(this, await Inner.OpenInputAsync(path, token));
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(this, await Inner.CreateOutputAsync(path, token));

        private sealed class Input : IQpdfInput
        {
            private readonly PublicationHost _host;
            private readonly IQpdfInput _inner;
            internal Input(PublicationHost host, IQpdfInput inner) { _host = host; _inner = inner; }
            public QpdfPath Path => _inner.Path;
            public string Identity => _inner.Identity;
            public long Length => _inner.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            { ReadOnlyMemory<byte> bytes = await _inner.ReadAsync(offset, count, token); _host.Observe("read", Path, token); return bytes; }
            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }

        private sealed class Transaction : IQpdfOutputTransaction
        {
            private readonly PublicationHost _host;
            private readonly IQpdfOutputTransaction _inner;
            internal Transaction(PublicationHost host, IQpdfOutputTransaction inner) { _host = host; _inner = inner; }
            public QpdfPath Path => _inner.Path;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            { await _inner.WriteAsync(data, token); _host.Observe("write", Path, token); }
            public async ValueTask CommitAsync(CancellationToken token)
            { _host.Observe("commit", Path, token); _host.Commits++; await _inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => _inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }
}
