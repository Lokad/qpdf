using System.Text;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class JobCheckTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static InMemoryQpdfHost Host(byte[] input)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        return host;
    }

    internal static byte[] Mismatch() => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Fixture("minimal-1page.pdf"))
        .Replace("/Count 1", "/Count 9", StringComparison.Ordinal));

    internal static byte[] CorruptStream() => CheckTests.Assemble(CheckTests.SinglePage("<< /Filter /FlateDecode /Length 14 >>", "not-deflate!!!"u8.ToArray()), 1);

    private static byte[] BrokenXref()
    {
        byte[] input = Fixture("minimal-1page.pdf");
        int marker = input.AsSpan().LastIndexOf("startxref\n"u8);
        Assert.True(marker >= 0);
        for (int at = marker + "startxref\n"u8.Length; at < input.Length && input[at] is >= (byte)'0' and <= (byte)'9'; at++) { input[at] = (byte)'9'; }
        return input;
    }

    public static IEnumerable<object?[]> BehaviorCases()
    {
        yield return ["valid", Fixture("minimal-2page.pdf"), QpdfExecutionStatus.Completed, QpdfExitCode.Success, null];
        yield return ["count-mismatch", Mismatch(), QpdfExecutionStatus.Completed, QpdfExitCode.Success, null];
        yield return ["truncated", Fixture("truncated-1page.pdf"), QpdfExecutionStatus.Failed, QpdfExitCode.Error, QpdfDiagnosticCode.InvalidArguments];
        yield return ["stream", CorruptStream(), QpdfExecutionStatus.Failed, QpdfExitCode.Error, QpdfDiagnosticCode.CheckFailed];
        string bad = Encoding.ASCII.GetString(Reading.TolerantObjectLoadingTests.Pdf(["null"], false))
            .Replace("5 0 obj", "6 0 obj", StringComparison.Ordinal);
        yield return ["unread-object", Encoding.ASCII.GetBytes(bad), QpdfExecutionStatus.Failed, QpdfExitCode.Error, QpdfDiagnosticCode.CheckFailed];
        foreach (bool flate in new[] { false, true })
        {
            // Repair-mode loading consumes one member value, as native does;
            // strict typed rejection of trailing data remains independently tested.
            yield return [flate ? "flate-member" : "raw-member", Reading.ReaderAcceptanceTests.ObjectStreamPdf("(kept)", "(bad) null", flate, true, null),
                QpdfExecutionStatus.Completed, QpdfExitCode.Success, null];
        }
    }

    [Theory]
    [MemberData(nameof(BehaviorCases))]
    public async Task JobRunsTheSharedChecker(string name, byte[] input, QpdfExecutionStatus status, QpdfExitCode exit, QpdfDiagnosticCode? finding)
    {
        InMemoryQpdfHost host = Host(input);
        ObservingHost observed = new(host);
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check"]), CancellationToken.None);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(observed, """{"inputFile":"inputs/a.pdf","check":""}""", CancellationToken.None);
        Assert.Equal(status, job.Status);
        Assert.Equal(exit, job.ExitCode);
        Assert.Equal(argv.Status, job.Status);
        Assert.Equal(argv.ExitCode, job.ExitCode);
        Assert.Equal(argv.Diagnostics.ToArray(), job.Diagnostics.ToArray());
        if (finding is not null) { Assert.Contains(job.Diagnostics, diagnostic => diagnostic.Code == finding); }
        Assert.DoesNotContain(job.Diagnostics, diagnostic => diagnostic.Message.Contains("Unsupported job key", StringComparison.Ordinal));
        Assert.True(observed.OpenCalls > 0);
        Assert.Equal(0, observed.OutputCalls);
        Assert.True(job.Counters.TotalWork > 0 && job.Counters.PeakMemoryBytes > 0);
        Assert.Equal(0L, job.Counters.OpenHandles);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out _));
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.NotEmpty(name);
    }

    [Fact]
    public async Task EmptyJobRunsThePageTreeCheck()
    {
        InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"empty":"","check":""}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed && diagnostic.Message.Contains("page-tree", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("\"checkLinearization\":\"\"", "--check-linearization")]
    [InlineData("\"showLinearization\":\"\"", "--show-linearization")]
    [InlineData("\"checkLinearization\":\"\",\"showLinearization\":\"\"", "both")]
    public async Task LinearizationInspectionComposesWithCheck(string extra, string flag)
    {
        InMemoryQpdfHost host = Host(Fixture("minimal-1page.pdf"));
        string[] flags = flag == "both" ? ["--check-linearization", "--show-linearization"] : [flag];
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", .. flags]), CancellationToken.None);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\",\"check\":\"\"," + extra + "}", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, job.Status);
        Assert.Equal(argv.ExitCode, job.ExitCode);
        Assert.Equal(argv.Diagnostics.ToArray(), job.Diagnostics.ToArray());
        Assert.Contains(job.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.LinearizationReport);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("\"warningExit0\":\"\"", "--warning-exit-0", QpdfExitCode.Success)]
    [InlineData("\"noWarn\":\"\"", "--no-warn", QpdfExitCode.Warning)]
    [InlineData("\"noWarn\":\"\",\"warningExit0\":\"\"", "both", QpdfExitCode.Success)]
    public async Task WarningModifiersPreserveCheckerFindings(string extra, string flag, QpdfExitCode exit)
    {
        InMemoryQpdfHost host = Host(BrokenXref());
        string[] flags = flag == "both" ? ["--no-warn", "--warning-exit-0"] : [flag];
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", .. flags]), CancellationToken.None);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\",\"check\":\"\"," + extra + "}", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, job.Status);
        Assert.Equal(exit, job.ExitCode);
        Assert.Equal(argv.Diagnostics.ToArray(), job.Diagnostics.ToArray());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void JobCheckForwardsReaderAndPresentationOptions()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WeakReference plan = VerifyReaderAndPresentationOptions(governor);
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            governor.ReserveMemory(0L);
        }
        Assert.False(plan.IsAlive);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference VerifyReaderAndPresentationOptions(ResourceGovernor governor)
    {
        const string json = """{"inputFile":"inputs/a.pdf","check":"","verbose":"","reportMemoryUsage":"","ignoreXrefStreams":"","suppressRecovery":"","global":{"maxStreamFilters":5,"parserMaxNesting":13,"parserMaxContainerSize":21,"parserMaxContainerSizeDamaged":8,"parserMaxErrors":0,"noDefaultLimits":""}}""";
        CheckPlan plan = Assert.IsType<CheckPlan>(JobJsonParser.Parse(json, QpdfLimits.Default, governor));
        Assert.Equal(QpdfRecoveryMode.Strict, plan.Recovery);
        Assert.Equal(5L, plan.MaxStreamFilters);
        Assert.Equal(13L, plan.ParserMaxNesting);
        Assert.Equal(21L, plan.MaxContainerSize);
        Assert.Equal(8L, plan.MaxContainerSizeDamaged);
        Assert.Equal(0L, plan.ParserMaxErrors);
        Assert.True(plan.NoDefaultLimits && plan.Verbose && plan.ReportMemoryUsage && plan.IgnoreXrefStreams);
        Assert.InRange(governor.Snapshot().LiveMemoryBytes,
            GovernedText.StringBytes(plan.Input.Value.Length), governor.Snapshot().PeakMemoryBytes);
        return new WeakReference(plan);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"yes\"")]
    [InlineData("true")]
    [InlineData("false")]
    public async Task InvalidFlagValuesFailBeforeOpening(string value)
    {
        InMemoryQpdfHost host = Host(Fixture("minimal-1page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\",\"check\":" + value + "}", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("\"check\":true,\"check\":\"\"", QpdfExecutionStatus.Completed)]
    [InlineData("\"check\":\"\",\"check\":false", QpdfExecutionStatus.Failed)]
    public async Task DuplicateCheckUsesTheLastValue(string properties, QpdfExecutionStatus expected)
    {
        InMemoryQpdfHost host = Host(Fixture("minimal-1page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\"," + properties + "}", CancellationToken.None);
        Assert.Equal(expected, result.Status);
        if (expected == QpdfExecutionStatus.Failed) { Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task HostMemoryCeilingRejectsActualCacheGrowth()
    {
        byte[] input = Reading.TolerantObjectLoadingTests.Pdf(Enumerable.Repeat("(" + new string('A', 32) + ")", 2000).ToArray(), false);
        InMemoryQpdfHost host = Host(input);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = input.Length + 450000L };
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None)) { Assert.Equal(1, control.PageCount); }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","check":""}""", new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(nameof(QpdfLimits.MaxMemoryBytes), StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
        QpdfExecutionResult generous = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","check":""}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, generous.Status);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationAfterTheFirstHostReadClosesItsLease()
    {
        InMemoryQpdfHost source = Host(Fixture("minimal-1page.pdf"));
        WaitingHost host = new(source);
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> checking = Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","check":""}""", cancellation.Token).AsTask();
        Task reached = await Task.WhenAny(host.ReadStarted.Task, checking);
        Assert.Same(host.ReadStarted.Task, reached);
        cancellation.Cancel();
        QpdfExecutionResult result = await checking.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
        Assert.True(result.Counters.TotalWork > 0 && result.Counters.HostOperations > 0);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.Equal(0, host.OutputCalls);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task PageCountComposesWithCheckerAndLinearization(bool checkLinearization, bool showLinearization, bool reversed)
    {
        InMemoryQpdfHost host = Host(Fixture("minimal-2page.pdf"));
        List<string> flags = ["--check", "--show-npages"];
        List<string> properties = ["\"check\":\"\"", "\"showNpages\":\"\""];
        if (checkLinearization) { flags.Add("--check-linearization"); properties.Add("\"checkLinearization\":\"\""); }
        if (showLinearization) { flags.Add("--show-linearization"); properties.Add("\"showLinearization\":\"\""); }
        if (reversed) { flags.Reverse(); properties.Reverse(); }
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", .. flags]), CancellationToken.None);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\"," + string.Join(',', properties) + "}", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, job.Status);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.Equal(argv.Diagnostics.ToArray(), job.Diagnostics.ToArray());
        Assert.EndsWith(": 2", Assert.Single(job.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.Equal(0L, job.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCheckerStopsAdditionalInspection(bool empty)
    {
        InMemoryQpdfHost host = empty ? new() : Host(CorruptStream());
        string input = empty ? "\"empty\":\"\"" : "\"inputFile\":\"inputs/a.pdf\"";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{" + input + ",\"check\":\"\",\"showNpages\":\"\",\"showLinearization\":\"\"}", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code is QpdfDiagnosticCode.PageCount or QpdfDiagnosticCode.LinearizationReport);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(1, 2, false, QpdfExecutionStatus.Failed)]
    [InlineData(2, 2, false, QpdfExecutionStatus.Completed)]
    [InlineData(2, 1, false, QpdfExecutionStatus.Failed)]
    [InlineData(2, 1, true, QpdfExecutionStatus.Failed)]
    public async Task GlobalFilterLimitControlsActualStreamChecking(int requested, int hostCeiling, bool noDefaults, QpdfExecutionStatus status)
    {
        byte[] stream = "3731323035313E>"u8.ToArray();
        byte[] input = CheckTests.Assemble(CheckTests.SinglePage("<< /Filter [/ASCIIHexDecode /ASCIIHexDecode] /Length " + stream.Length + " >>", stream), 1);
        InMemoryQpdfHost host = Host(input);
        string defaults = noDefaults ? ",\"noDefaultLimits\":\"\"" : string.Empty;
        string json = "{\"inputFile\":\"inputs/a.pdf\",\"check\":\"\",\"global\":{\"maxStreamFilters\":" + requested + defaults + "}}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, json, new QpdfExecutionOptions(QpdfLimits.Default with { MaxStreamFilters = hostCeiling }), CancellationToken.None);
        Assert.Equal(status, result.Status);
        if (status == QpdfExecutionStatus.Failed)
        {
            if (requested > hostCeiling)
            {
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded && diagnostic.Message.Contains(nameof(QpdfLimits.MaxStreamFilters), StringComparison.Ordinal));
            }
            else
            {
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed && diagnostic.Message.Contains("TooManyFilters", StringComparison.Ordinal));
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SuppressRecoveryControlsDamagedXrefOpening()
    {
        InMemoryQpdfHost host = Host(BrokenXref());
        QpdfExecutionResult repaired = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","check":""}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, repaired.Status);
        Assert.Equal(QpdfExitCode.Warning, repaired.ExitCode);
        QpdfExecutionResult strict = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","check":"","suppressRecovery":""}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, strict.Status);
        Assert.Equal(QpdfExitCode.Error, strict.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("\"outputFile\":\"outputs/a.pdf\"")]
    [InlineData("\"replaceInput\":\"\"")]
    [InlineData("\"json\":\"2\"")]
    public async Task InvalidOutputModesDoNotOpenOrPublish(string mode)
    {
        InMemoryQpdfHost source = Host(Fixture("minimal-1page.pdf"));
        await using (IQpdfOutputTransaction output = await source.CreateOutputAsync(QpdfPath.From("outputs/a.pdf"), CancellationToken.None))
        {
            await output.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await output.CommitAsync(CancellationToken.None);
        }
        ObservingHost host = new(source);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\",\"check\":\"\"," + mode + "}", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, host.OpenCalls);
        Assert.Equal(0, host.OutputCalls);
        Assert.True(source.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, source.OpenLeaseCount);
    }

    [Theory]
    [InlineData("password", false)]
    [InlineData("password", true)]
    [InlineData("passwordFile", false)]
    [InlineData("passwordFile", true)]
    [InlineData("encrypt", false)]
    [InlineData("encrypt", true)]
    [InlineData("copyEncryption", false)]
    [InlineData("copyEncryption", true)]
    public async Task CryptoKeysRejectBeforeHostAccessWithoutEchoingValues(string key, bool first)
    {
        ObservingHost host = new(new());
        string crypto = "\"" + key + "\":\"secret-not-for-diagnostics\"";
        string check = "\"inputFile\":\"inputs/a.pdf\",\"check\":\"\"";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{" + (first ? crypto + "," + check : check + "," + crypto) + "}", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("secret-not-for-diagnostics", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(0, host.OpenCalls);
        Assert.Equal(0, host.OutputCalls);
    }

    [Fact]
    public async Task EncryptedBytesRejectThroughTheCheckerRoute()
    {
        InMemoryQpdfHost source = Host(Fixture("encrypted-decl.pdf"));
        ObservingHost host = new(source);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","check":""}""", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(result.Diagnostics).Code);
        Assert.Contains("encrypt", result.Diagnostics[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(host.OpenCalls > 0);
        Assert.Equal(0, host.OutputCalls);
        Assert.Equal(0L, source.OpenLeaseCount);
    }

    [Fact]
    public async Task TypedCheckerSilentlyUsesTraversalAndSnapshotsAfterCleanup()
    {
        InMemoryQpdfHost host = Host(Mismatch());
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCountMismatch);
        Assert.True(result.Counters.TotalWork > 0 && result.Counters.PeakMemoryBytes > 0);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        await using QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Contains(control.Warnings, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCountMismatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageCountReportsTheDeclarationWhileCheckingUsesTraversal(bool checking)
    {
        InMemoryQpdfHost host = Host(Mismatch());
        string check = checking ? ",\"check\":\"\"" : string.Empty;
        string[] flags = checking ? ["--check", "--show-npages"] : ["--show-npages"];
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\",\"showNpages\":\"\"" + check + "}", CancellationToken.None);
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", .. flags]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, job.Status);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.Equal(argv.Diagnostics.ToArray(), job.Diagnostics.ToArray());
        Assert.EndsWith(": 9", Assert.Single(job.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(job.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCountMismatch);
        Assert.Equal(0L, job.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class ObservingHost(InMemoryQpdfHost source) : IQpdfHost
    {
        internal int OpenCalls { get; private set; }
        internal int OutputCalls { get; private set; }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            OpenCalls++;
            return source.OpenInputAsync(path, cancellationToken);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            OutputCalls++;
            return source.CreateOutputAsync(path, cancellationToken);
        }
    }

    private sealed class WaitingHost(InMemoryQpdfHost source) : IQpdfHost
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int OutputCalls { get; private set; }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
            => new WaitingInput(await source.OpenInputAsync(path, cancellationToken), ReadStarted);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            OutputCalls++;
            return source.CreateOutputAsync(path, cancellationToken);
        }
    }

    private sealed class WaitingInput(IQpdfInput inner, TaskCompletionSource started) : IQpdfInput
    {
        public QpdfPath Path => inner.Path;
        public string Identity => inner.Identity;
        public long Length => inner.Length;
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await inner.ReadAsync(offset, count, cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
