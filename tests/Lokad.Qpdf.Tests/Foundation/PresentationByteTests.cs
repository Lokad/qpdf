using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class PresentationByteTests
{
    private static readonly byte[] Prior = [17, 23, 41];
    private static readonly string[] Targets = ["output.pdf", "output-1.pdf", "output-2.pdf", "side-4", "pass1.pdf"];

    public static IEnumerable<object[]> Jobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string operation in new[] { "whole", "linearized", "qdf", "pages", "merge", "split",
            "split-linearized", "split-qdf", "split-streamed", "split-pass1",
            "json-file", "json-inline", "json-sidecar", "json-import", "json-update", "empty", "check", "show-count", "filtered" })
        foreach (bool compressed in new[] { false, true })
        foreach (string ceiling in new[] { "default", "exact", "short", "tiny" })
        { yield return [route, operation, compressed, ceiling]; }
    }

    private static byte[] Source(bool compressed, bool warning)
    {
        byte[] body = "q Q\n"u8.ToArray();
        if (compressed)
        {
            using MemoryStream buffer = new();
            using (ZLibStream encoder = new(buffer, CompressionLevel.Optimal, true)) { encoder.Write(body); }
            body = buffer.ToArray();
        }
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage(
            "<< /Length " + body.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>", body);
        if (warning) { objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Duplicate 1 /Duplicate 2 >>", null); }
        return CheckTests.Assemble(objects, 1);
    }

    private static async Task<ByteHost> Prepare(string operation, bool compressed, bool warning)
    {
        ByteHost host = new();
        host.Inner.AddInput(QpdfPath.From("input.pdf"), Source(compressed, warning), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("second.pdf"), Source(compressed, false), QpdfInputOwnership.Copy);
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
            await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        }
        return host;
    }

    private static async Task<QpdfExecutionResult> Run(ByteHost host, string route, string operation, string flags,
        QpdfLimits limits, CancellationToken token)
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
                job.Add("pages", new[] { new { file = "input.pdf", range = "1" } }); break;
            case "linearized": argv.Add("--linearize"); job.Add("linearize", ""); break;
            case "qdf": argv.Add("--qdf"); job.Add("qdf", ""); break;
            case "pages":
                argv.AddRange(["--pages", ".", "1", "--"]);
                job.Add("pages", new[] { new { file = ".", range = "1" } }); break;
            case "merge":
                argv.AddRange(["--pages", ".", "1", "second.pdf", "1", "--"]);
                job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = "second.pdf", range = "1" } }); break;
            case "split":
            case "split-linearized":
            case "split-qdf":
            case "split-streamed":
            case "split-pass1":
                if (operation is "split-linearized" or "split-pass1") { argv.Add("--linearize"); job.Add("linearize", ""); }
                if (operation == "split-qdf") { argv.Add("--qdf"); job.Add("qdf", ""); }
                if (operation == "split-streamed") { argv.Add("--object-streams=generate"); job.Add("objectStreams", "generate"); }
                if (operation == "split-pass1") { argv.Add("--linearize-pass1=pass1.pdf"); job.Add("linearizePass1", "pass1.pdf"); }
                argv.AddRange(["--split-pages=1", "--pages", ".", "1", ".", "1", "--"]);
                job.Add("splitPages", "1");
                job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } }); break;
            case "json-file":
            case "json-inline":
            case "json-sidecar":
                argv.AddRange(["--json=2", "--json-stream-data=" + (operation == "json-sidecar" ? "file" : "inline")]);
                job.Add("json", "2"); job.Add("jsonStreamData", operation == "json-sidecar" ? "file" : "inline");
                if (operation == "json-sidecar") { argv.Add("--json-stream-prefix=side"); job.Add("jsonStreamPrefix", "side"); }
                if (operation != "json-inline") { argv.Add("--"); } break;
            case "json-import": argv.AddRange(["--json-input", "--"]); job.Add("jsonInput", ""); break;
            case "json-update": argv.AddRange(["--update-from-json=input.json", "--"]); job.Add("updateFromJson", "input.json"); break;
            case "check": argv.Add("--check"); job.Add("check", ""); break;
            case "show-count": argv.Add("--show-npages"); job.Add("showNpages", ""); break;
            case "filtered":
                argv.AddRange(["--show-object=4", "--filtered-stream-data"]);
                job.Add("showObject", "4"); job.Add("filteredStreamData", ""); break;
        }
        if (operation is not ("check" or "show-count" or "filtered" or "json-inline"))
        { argv.Add("output.pdf"); job.Add("outputFile", "output.pdf"); }
        QpdfExecutionOptions options = new(limits);
        if (route == "job") { return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), options, token); }
        if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
        if (route == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            argv = ["@args.txt"];
        }
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, token);
    }

    private static long MessageBytes(QpdfExecutionResult result) => result.Diagnostics.Sum(diagnostic => (long)Encoding.UTF8.GetByteCount(diagnostic.Message));

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task PublicJobsHoldExactSummaryByteQuotas(string route, string operation, bool compressed, string ceiling)
    {
        // Both jobs must format the same peak: collection can retire owners
        // between them and change the number of summary digits.
        Assert.True(GC.TryStartNoGCRegion(16 * 1024 * 1024));
        try
        {
            const string flags = "verbose progress report-memory-usage";
            ByteHost baselineHost = await Prepare(operation, compressed, false);
            QpdfExecutionResult baseline = await Run(baselineHost, route, operation, flags, QpdfLimits.Default, CancellationToken.None);
            Assert.True(baseline.Status == QpdfExecutionStatus.Completed, string.Join("; ", baseline.Diagnostics.Select(diagnostic => diagnostic.Message)));
            long bytes = MessageBytes(baseline);
            QpdfLimits limits = ceiling == "default" ? QpdfLimits.Default : QpdfLimits.Default with
            { MaxDiagnosticBytes = ceiling == "tiny" ? 16L : bytes - (ceiling == "short" ? 1L : 0L) };
            ByteHost host = await Prepare(operation, compressed, false);
            byte[][] aliases = Targets.Select(target =>
            { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] old)); return old; }).ToArray();
            QpdfExecutionResult result = await Run(host, route, operation, flags, limits, CancellationToken.None);
            if (ceiling is "short" or "tiny")
            {
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded
                    && diagnostic.Message.Contains(nameof(QpdfLimits.MaxDiagnosticBytes), StringComparison.Ordinal));
                Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code is QpdfDiagnosticCode.VerboseSummary
                    or QpdfDiagnosticCode.MemoryUsage or QpdfDiagnosticCode.ProgressReport);
                Assert.Null(result.OutputText); Assert.Null(result.OutputBytes); Assert.Equal(0, host.Commits);
                foreach (string target in Targets)
                { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] current)); Assert.Equal(Prior, current); }
            }
            else
            {
                Assert.Equal(baseline.Counters.PeakMemoryBytes, result.Counters.PeakMemoryBytes);
                Assert.Equal(baseline.Status, result.Status); Assert.Equal(baseline.ExitCode, result.ExitCode);
                Assert.Equal(bytes, MessageBytes(result)); Assert.InRange(MessageBytes(result), 0L, limits.MaxDiagnosticBytes);
                Assert.Equal(baseline.Counters.DiagnosticCount, result.Counters.DiagnosticCount);
                Assert.Equal(baselineHost.Commits, host.Commits);
                Assert.Equal(baseline.OutputText, result.OutputText); Assert.Equal(baseline.OutputBytes?.ToArray(), result.OutputBytes?.ToArray());
                foreach (string target in Targets)
                {
                    Assert.True(baselineHost.Inner.TryGetOutput(QpdfPath.From(target), out byte[] expected));
                    Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] actual)); Assert.Equal(expected, actual);
                }
            }
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.All(aliases, alias => Assert.Equal(Prior, alias)); GC.KeepAlive(result); GC.KeepAlive(aliases);
        }
        finally { GC.EndNoGCRegion(); }
    }

    [Theory]
    [InlineData("argv", false, false)]
    [InlineData("job", false, true)]
    [InlineData("mixed", true, false)]
    [InlineData("response", true, true)]
    public async Task SuppressedWarningsStillConsumeTheirMessageBytes(string route, bool noWarn, bool warningExitZero)
    {
        const string flags = "verbose progress report-memory-usage";
        ByteHost baselineHost = await Prepare("whole", false, true);
        QpdfExecutionResult baseline = await Run(baselineHost, route, "whole", flags, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        Assert.Equal(2, baseline.Diagnostics.Count(diagnostic => diagnostic.Code == QpdfDiagnosticCode.DuplicateKey));
        long required = MessageBytes(baseline);
        ByteHost host = await Prepare("whole", false, true);
        QpdfExecutionResult result = await Run(host, route, "whole", flags + (noWarn ? " no-warn" : "")
            + (warningExitZero ? " warning-exit-0" : ""), QpdfLimits.Default with { MaxDiagnosticBytes = required }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(warningExitZero ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(8L, result.Counters.DiagnosticCount);
        Assert.Equal(noWarn ? 0 : 2, result.Diagnostics.Count(diagnostic => diagnostic.Code == QpdfDiagnosticCode.DuplicateKey));
        Assert.Equal(1, host.Commits); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        ByteHost shortHost = await Prepare("whole", false, true);
        QpdfExecutionResult failed = await Run(shortHost, route, "whole", flags + (noWarn ? " no-warn" : ""),
            QpdfLimits.Default with { MaxDiagnosticBytes = required - 1L }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, failed.Status);
        Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0, shortHost.Commits); Assert.Equal(0L, shortHost.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    [InlineData("response")]
    public async Task ExactSidecarQuotasPreserveTheOneHandlePath(string route)
    {
        ByteHost baselineHost = await Prepare("json-sidecar", true, false);
        const string flags = "verbose progress report-memory-usage";
        QpdfExecutionResult baseline = await Run(baselineHost, route, "json-sidecar", flags,
            QpdfLimits.Default with { MaxOpenHandles = 1L }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        ByteHost host = await Prepare("json-sidecar", true, false);
        QpdfExecutionResult result = await Run(host, route, "json-sidecar", flags,
            QpdfLimits.Default with { MaxOpenHandles = 1L, MaxDiagnosticBytes = MessageBytes(baseline) }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(2, host.Commits); Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    public static IEnumerable<object[]> DecimalBoundaries()
    {
        for (long value = 1L; value <= 1_000_000_000_000_000_000L; value = checked(value * 10L))
        {
            yield return [value - 1L]; yield return [value]; yield return [value + 1L];
            if (value == 1_000_000_000_000_000_000L) { break; }
        }
        yield return [long.MaxValue];
    }

    public static IEnumerable<object[]> SelectedFlags()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string operation in new[] { "whole", "json-inline" })
        foreach (string flags in new[] { "verbose", "report-memory-usage", "progress", "verbose report-memory-usage",
            "verbose progress", "report-memory-usage progress", "verbose report-memory-usage progress" })
        { yield return [route, operation, flags]; }
    }

    [Theory]
    [MemberData(nameof(SelectedFlags))]
    public async Task ExactBytesMatchTheSelectedSummaries(string route, string operation, string flags)
    {
        ByteHost baselineHost = await Prepare(operation, false, false);
        QpdfExecutionResult baseline = await Run(baselineHost, route, operation, flags, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        long bytes = MessageBytes(baseline);
        ByteHost exactHost = await Prepare(operation, false, false);
        QpdfExecutionResult exact = await Run(exactHost, route, operation, flags,
            QpdfLimits.Default with { MaxDiagnosticBytes = Math.Max(1L, bytes) }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, exact.Status); Assert.Equal(bytes, MessageBytes(exact));
        if (bytes > 0L)
        {
            ByteHost shortHost = await Prepare(operation, false, false);
            QpdfExecutionResult failed = await Run(shortHost, route, operation, flags,
                QpdfLimits.Default with { MaxDiagnosticBytes = bytes - 1L }, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, failed.Status); Assert.Equal(0, shortHost.Commits);
            Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal(0L, failed.Counters.OpenHandles); Assert.Equal(0L, shortHost.Inner.OpenLeaseCount);
        }
        Assert.Equal(0L, exact.Counters.OpenHandles); Assert.Equal(0L, exactHost.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("fr-FR")]
    public async Task CurrentCulturePreservesTheExactByteQuota(string culture)
    {
        CultureInfo prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await ExactBytesMatchTheSelectedSummaries("argv", "whole", "verbose progress report-memory-usage");
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Theory]
    [MemberData(nameof(DecimalBoundaries))]
    public void MessageSizingMatchesCompleteDecimalFormatting(long value)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using PresentationCountReservation? reservation = PresentationCountReservation.Create(governor, true, true, true, CancellationToken.None);
        Assert.NotNull(reservation);
        string verbose = $"Verbose summary: {value} output bytes, {value} decoded bytes, peak {value} bytes (estimated).";
        string memory = $"Peak memory usage: {value} bytes (estimated).";
        long expected = Encoding.UTF8.GetByteCount(verbose) + Encoding.UTF8.GetByteCount(memory);
        if (value > 0L)
        { for (int i = 1; i <= 4; i++) { expected += Encoding.UTF8.GetByteCount($"Write progress: {i * 25}% of {value} bytes."); } }
        Assert.Equal(expected, reservation.MessageBytes(value, value, value));
    }

    [Fact]
    public void AcceptedSummariesChargeTheirExactBytesOnce()
    {
        ResourceGovernor baseline = new(QpdfLimits.Default, CancellationToken.None);
        using PresentationCountReservation? preview = PresentationCountReservation.Create(baseline, true, true, true, CancellationToken.None);
        Assert.NotNull(preview);
        baseline.ChargeOutputBytes(999L);
        long bytes = preview.MessageBytes(999L, 0L, baseline.Snapshot().PeakMemoryBytes);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDiagnosticBytes = bytes }, CancellationToken.None);
        using PresentationCountReservation? reservation = PresentationCountReservation.Create(governor, true, true, true, CancellationToken.None);
        governor.ChargeOutputBytes(999L);
        governor.PresentDiagnosticCount();
        Assert.Equal(6L, governor.Snapshot().DiagnosticCount);
        QpdfLimitException exceeded = Assert.Throws<QpdfLimitException>(() => governor.ReserveDiagnostic(1L));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), exceeded.LimitName);
        Assert.Equal(bytes + 1L, exceeded.Requested);
        governor.ReserveDiagnostic(0L);
        Assert.Equal(7L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void AnticipatedOutputReservesSlotsBeforeLaterWarnings()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDiagnostics = 6L }, CancellationToken.None);
        using PresentationCountReservation? reservation = PresentationCountReservation.Create(governor, true, true, true, CancellationToken.None);
        governor.AnticipatePresentationOutput(1000L);
        QpdfLimitException exceeded = Assert.Throws<QpdfLimitException>(() => governor.ReserveDiagnostic(0L));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnostics), exceeded.LimitName);
        Assert.Equal(0L, governor.Snapshot().TotalOutputBytes);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
    }

    private sealed class ByteHost : IQpdfHost
    {
        internal InMemoryQpdfHost Inner { get; } = new();
        internal int Commits { get; private set; }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => Inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(this, await Inner.CreateOutputAsync(path, token));
        private sealed class Transaction : IQpdfOutputTransaction
        {
            private readonly ByteHost _host;
            private readonly IQpdfOutputTransaction _inner;
            internal Transaction(ByteHost host, IQpdfOutputTransaction inner) { _host = host; _inner = inner; }
            public QpdfPath Path => _inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => _inner.WriteAsync(data, token);
            public async ValueTask CommitAsync(CancellationToken token) { _host.Commits++; await _inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => _inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }
}
