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
        foreach (string operation in new[] { "whole", "linearized", "qdf", "pages", "merge", "split",
            "json-file", "json-inline", "json-sidecar", "json-import", "json-update", "empty", "check", "show-count", "filtered" })
        {
            foreach (string ceiling in new[] { "exact", "short" }) { yield return ["argv", operation, false, ceiling]; }
            yield return ["argv", operation, true, "exact"];
        }
        foreach (string invocation in new[] { "job", "mixed", "response" })
        foreach (string operation in new[] { "whole", "split", "json-sidecar", "filtered" })
        foreach (string ceiling in new[] { "exact", "short" }) { yield return [invocation, operation, false, ceiling]; }
    }

    private static async Task<PublicationHost> Prepare(string operation, bool compressed)
        => new(await PublicationTestJobs.PrepareAsync(operation, compressed, false));

    private static Task<QpdfExecutionResult> Run(PublicationHost host, string invocation, string operation,
        string flags, QpdfLimits limits, CancellationToken token)
        => PublicationTestJobs.ExecuteAsync(host, host.Inner, invocation, operation, flags, limits, token);

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
            long snapshotPeak = long.Parse(System.Text.RegularExpressions.Regex.Match(summaries[0].Message, @"peak (\d+) bytes").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(snapshotPeak, 0L, result.Counters.PeakMemoryBytes);
            Assert.Equal($"Peak memory usage: {snapshotPeak} bytes (estimated).", summaries[1].Message);
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
        foreach (bool noWarn in new[] { false, true })
        foreach (bool warningExitZero in new[] { false, true })
        foreach (bool shortCount in new[] { false, true }) { yield return ["argv", noWarn, warningExitZero, shortCount]; }
        foreach (string invocation in new[] { "job", "mixed", "response" })
        foreach (bool shortCount in new[] { false, true }) { yield return [invocation, true, true, shortCount]; }
    }

    [Theory]
    [MemberData(nameof(WarningJobs))]
    public async Task StoredWarningsAndSummariesShareTheDiagnosticLimit(string invocation, bool noWarn, bool warningExitZero, bool shortCount)
    {
        PublicationHost host = await Prepare("whole", false);
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray());
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Duplicate 1 /Duplicate 2 >>", null);
        host.Inner.AddInput(QpdfPath.From("input.pdf"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        string flags = "verbose progress report-memory-usage" + (noWarn ? " no-warn" : "") + (warningExitZero ? " warning-exit-0" : "");
        QpdfExecutionResult result = await Run(host, invocation, "whole", flags,
            QpdfLimits.Default with { MaxDiagnostics = (noWarn ? 6L : 7L) - (shortCount ? 1L : 0L) }, CancellationToken.None);
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
            // Whole-file rewriting reuses the primary document. Its accepted
            // warning counts even when presentation suppresses it.
            Assert.Equal(noWarn ? 6L : 7L, result.Counters.DiagnosticCount);
            Assert.Equal(6L, result.Diagnostics.LongCount(diagnostic => IsSummary(diagnostic.Code)));
            Assert.Equal(noWarn ? 0 : 1, result.Diagnostics.Count(diagnostic => diagnostic.Code == QpdfDiagnosticCode.DuplicateKey));
            Assert.Equal(1, host.Commits);
        }
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    public static IEnumerable<object[]> FailedJobs()
    {
        foreach (string operation in new[] { "whole", "linearized", "split", "json-sidecar", "json-inline", "filtered" })
        foreach (bool cancel in new[] { false, true })
        {
            yield return ["argv", operation, "read", cancel];
            if (operation is not ("json-inline" or "filtered"))
            { yield return ["argv", operation, "write", cancel]; yield return ["argv", operation, "commit", cancel]; }
        }
        foreach (string invocation in new[] { "job", "mixed", "response" })
        foreach (bool cancel in new[] { false, true }) { yield return [invocation, "whole", "commit", cancel]; }
        yield return ["job", "json-sidecar", "write", false];
        yield return ["mixed", "filtered", "read", true];
        yield return ["response", "json-inline", "read", false];
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
        Assert.InRange(result.Counters.DiagnosticCount, 0L, 6L);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => IsSummary(diagnostic.Code));
        Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
        Assert.Equal(0, host.Commits);
        Assert.True(result.Counters.HostOperations > 0L);
        if (boundary != "read") { Assert.True(result.Counters.TotalOutputBytes > 0L); }
        foreach (string target in Targets)
        { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] bytes)); Assert.Equal(Prior, bytes); }
        Assert.All(aliases, alias => Assert.Equal(Prior, alias));
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        GC.KeepAlive(result); GC.KeepAlive(aliases);
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
    [InlineData("argv", "json-inline", "progress", 1L, false)]
    [InlineData("job", "json-inline", "progress", 1L, false)]
    [InlineData("mixed", "json-inline", "progress", 1L, false)]
    [InlineData("response", "json-inline", "progress", 1L, false)]
    [InlineData("argv", "json-inline", "progress", 3L, false)]
    [InlineData("job", "json-inline", "progress", 3L, false)]
    [InlineData("mixed", "json-inline", "progress", 3L, false)]
    [InlineData("response", "json-inline", "progress", 3L, false)]
    [InlineData("argv", "json-inline", "progress", 4L, true)]
    [InlineData("job", "json-inline", "progress", 4L, true)]
    [InlineData("mixed", "json-inline", "progress", 4L, true)]
    [InlineData("response", "json-inline", "progress", 4L, true)]
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
            Assert.Equal(0, host.Commits); Assert.InRange(result.Counters.DiagnosticCount, 0L, 6L);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes)); Assert.Equal(Prior, bytes);
        }
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    private sealed class PublicationHost(InMemoryQpdfHost inner) : IQpdfHost
    {
        internal InMemoryQpdfHost Inner { get; } = inner;
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
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            { int bytes = await _inner.ReadAsync(offset, destination, token); _host.Observe("read", Path, token); return bytes; }
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
