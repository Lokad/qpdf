using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class NestedJobTests
{
    private static InMemoryQpdfHost Host()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        return host;
    }

    private static void Add(InMemoryQpdfHost host, string path, string json)
        => host.AddInput(QpdfPath.From(path), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);

    [Theory]
    [InlineData(1, "direct")]
    [InlineData(3, "direct")]
    [InlineData(1, "argv")]
    [InlineData(3, "argv")]
    [InlineData(1, "joined")]
    [InlineData(3, "joined")]
    public async Task NestedFilesShareTheChecker(int levels, string route)
    {
        InMemoryQpdfHost host = Host();
        Add(host, "child.json", """{"inputFile":"inputs/a.pdf","check":"","showNpages":""}""");
        string json = """{"jobJsonFile":"child.json"}""";
        for (int index = 1; index < levels; index++)
        {
            Add(host, $"level{index}.json", json);
            json = "{\"jobJsonFile\":\"level" + index + ".json\"}";
        }
        Add(host, "configs/outer.json", json);
        QpdfExecutionResult result = route switch
        {
            "direct" => await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None),
            "argv" => await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--job-json-file", "configs/outer.json"]), CancellationToken.None),
            _ => await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--job-json-file=configs/outer.json"]), CancellationToken.None),
        };
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.EndsWith(": 1", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.True(result.Counters.HostOperations > levels && result.Counters.TotalWork > 0);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("forceVersion", "1.5", false)]
    [InlineData("forceVersion", "1.5", true)]
    [InlineData("minVersion", "1.6", false)]
    [InlineData("minVersion", "1.6", true)]
    public async Task ScalarPrecedenceUsesNativeKeyOrder(string key, string expected, bool reversed)
    {
        InMemoryQpdfHost host = Host();
        Add(host, "child.json", "{\"" + key + "\":\"1.5\"}");
        string fields = "\"" + key + "\":\"1.6\",\"jobJsonFile\":\"child.json\"";
        if (reversed) { fields = "\"jobJsonFile\":\"child.json\",\"" + key + "\":\"1.6\""; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\",\"outputFile\":\"outputs/a.pdf\"," + fields + "}", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        Assert.StartsWith("%PDF-" + expected, Encoding.ASCII.GetString(output), StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("inputFile", "\"inputs/a.pdf\"")]
    [InlineData("outputFile", "\"outputs/a.pdf\"")]
    [InlineData("pages", "[{\"file\":\"inputs/a.pdf\",\"range\":\"1\"}]")]
    public async Task RepeatedExclusiveDeclarationsRejectAfterLoading(string key, string value)
    {
        InMemoryQpdfHost host = Host();
        Add(host, "child.json", "{\"" + key + "\":" + value + "}");
        string parent = key == "inputFile"
            ? """{"inputFile":"inputs/a.pdf","showNpages":"","jobJsonFile":"child.json"}"""
            : "{\"inputFile\":\"inputs/a.pdf\",\"outputFile\":\"outputs/a.pdf\",\"jobJsonFile\":\"child.json\"" + (key == "pages" ? ",\"pages\":" + value : string.Empty) + "}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, parent, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.True(result.Counters.HostOperations > 0);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out _));
    }

    [Fact]
    public async Task OuterJobCompletesPartialChildAndRepeatsEmptyFlag()
    {
        InMemoryQpdfHost host = new();
        Add(host, "child.json", """{"empty":""}""");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"empty":"","jobJsonFile":"child.json","showNpages":""}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.EndsWith(": 0", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CumulativeConfigurationBytesAreBounded()
    {
        InMemoryQpdfHost host = new();
        Add(host, "end.json", """{"empty":"","showNpages":""}""");
        Add(host, "middle.json", """{"jobJsonFile":"end.json"}""");
        const string json = """{"jobJsonFile":"middle.json"}""";
        QpdfExecutionResult limited = await Qpdf.ExecuteJobAsync(host, json, new QpdfExecutionOptions(QpdfLimits.Default with { MaxInputBytes = 65 }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, limited.Status);
        Assert.Contains(limited.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded && d.Message.Contains(nameof(QpdfLimits.MaxInputBytes), StringComparison.Ordinal));
        Assert.True(limited.Counters.HostOperations > 0);
        Assert.Equal(0L, limited.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(QpdfExecutionStatus.Completed, (await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task NestingLimitRejectsBeforeOpeningAnotherFile()
    {
        InMemoryQpdfHost host = new();
        Add(host, "end.json", """{"empty":"","showNpages":""}""");
        Add(host, "middle.json", """{"jobJsonFile":"end.json"}""");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"middle.json"}""", new QpdfExecutionOptions(QpdfLimits.Default with { MaxNestingDepth = 1 }), CancellationToken.None);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded && d.Message.Contains(nameof(QpdfLimits.MaxNestingDepth), StringComparison.Ordinal));
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CyclesAreDetectedUsingHostIdentity()
    {
        InMemoryQpdfHost host = new();
        Add(host, "loop.json", """{"jobJsonFile":"loop.json"}""");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"loop.json"}""", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Contains("cycle", result.Diagnostics[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Counters.HostOperations >= 3);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"\"")]
    public async Task InvalidFileValuesRejectBeforeHostAccess(string value)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(new InMemoryQpdfHost(), "{\"jobJsonFile\":" + value + "}", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.HostOperations);
    }

    [Theory]
    [InlineData("""{"jobJsonFile":"\ud800"}""")]
    [InlineData("""{"\ud800":""}""")]
    [InlineData("""{"jobJsonFile":"\u0000"}""")]
    public async Task InvalidUnicodeAndNulPathsReturnExplicitOutcomes(string json)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(new InMemoryQpdfHost(), json, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"check\":true}")]
    [InlineData("{\"invented\":\"\"}")]
    public async Task BadChildDoesNotAccessPdfOrPublish(string child)
    {
        InMemoryQpdfHost host = Host();
        Add(host, "child.json", child);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"child.json","inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf"}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.True(result.Counters.HostOperations > 0);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out _));
    }

    [Fact]
    public async Task PlainJobUsesTheWholeFileWriter()
    {
        InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf"}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bytes));
        host.AddInput(QpdfPath.From("outputs/a.pdf"), bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("outputs/a.pdf"), CancellationToken.None)) { Assert.Equal(1, document.PageCount); }
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RepeatedRotationRangesReplaceAndLabelsReset()
    {
        InMemoryQpdfHost host = Host();
        Add(host, "child.json", """{"rotate":["+90:1"],"setPageLabels":["1:D/5/child"]}""");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","pages":[{"file":".","range":"1"}],"jobJsonFile":"child.json","rotate":["+180:1"],"setPageLabels":["1:D/8/parent"]}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bytes));
        host.AddInput(QpdfPath.From("outputs/a.pdf"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("outputs/a.pdf"), CancellationToken.None);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject rotate));
        Assert.Equal(180L, Assert.IsType<PdfInteger>(rotate).Value);
        Assert.Contains("parent", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("child", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("work")]
    [InlineData("host")]
    public async Task LoadingBudgetsStopBeforePdfAccess(string budget)
    {
        InMemoryQpdfHost source = new();
        Add(source, "child.json", """{"empty":"","showNpages":""}""");
        TrackingHost host = new(source, "normal");
        QpdfLimits limits = budget switch
        {
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 7000L },
            "work" => QpdfLimits.Default with { MaxWork = 75L },
            _ => QpdfLimits.Default with { MaxHostOperations = 1L },
        };
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"child.json"}""", new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        // Loading may now fit further beneath the same ceiling after removing
        // the graph ledger. Only configuration storage may be opened.
        Assert.All(host.Opens, path => Assert.Equal(QpdfPath.From("child.json"), path));
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.Equal(0, host.Outputs);
    }

    [Theory]
    [InlineData("short", QpdfExecutionStatus.Completed)]
    [InlineData("no-progress", QpdfExecutionStatus.Failed)]
    [InlineData("missing", QpdfExecutionStatus.Failed)]
    [InlineData("invalid-utf8", QpdfExecutionStatus.Failed)]
    public async Task HostReadFailuresAndShortReadsCloseLeases(string mode, QpdfExecutionStatus expected)
    {
        InMemoryQpdfHost source = new();
        if (mode == "invalid-utf8") { source.AddInput(QpdfPath.From("child.json"), new byte[] { 0xff, 0xfe }, QpdfInputOwnership.Copy); }
        else if (mode != "missing") { Add(source, "child.json", """{"empty":"","showNpages":""}"""); }
        TrackingHost host = new(source, mode);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"child.json"}""", CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.Equal(QpdfPath.From("child.json"), Assert.Single(host.Opens));
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.Equal(0, host.Outputs);
        if (mode == "short") { Assert.True(result.Counters.HostOperations > 8); }
    }

    [Fact]
    public async Task DifferentPathsWithTheSameIdentityFormACycle()
    {
        InMemoryQpdfHost source = new();
        Add(source, "child.json", """{"jobJsonFile":"alias.json"}""");
        Add(source, "alias.json", """{"jobJsonFile":"alias.json"}""");
        TrackingHost host = new(source, "alias");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"child.json"}""", CancellationToken.None);
        Assert.Contains("cycle", Assert.Single(result.Diagnostics).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, host.Opens.Count);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, source.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterJobReadStartsPreservesPriorOutput(bool argv)
    {
        InMemoryQpdfHost source = new();
        Add(source, "child.json", """{"empty":"","showNpages":""}""");
        await using (IQpdfOutputTransaction output = await source.CreateOutputAsync(QpdfPath.From("outputs/a.pdf"), CancellationToken.None))
        {
            await output.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await output.CommitAsync(CancellationToken.None);
        }
        TrackingHost host = new(source, "blocking");
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> running = (argv
            ? Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--job-json-file=child.json"]), cancellation.Token)
            : Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"child.json"}""", cancellation.Token)).AsTask();
        Assert.Same(host.ReadStarted.Task, await Task.WhenAny(running, host.ReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(10)));
        cancellation.Cancel();
        QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
        Assert.True(result.Counters.TotalWork > 0 && result.Counters.PeakMemoryBytes > 0);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.True(source.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0, host.Outputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CryptoDeclarationsRejectBeforePdfOrPasswordAccess(bool outer)
    {
        InMemoryQpdfHost source = new();
        Add(source, "child.json", outer ? """{"empty":"","showNpages":""}""" : """{"passwordFile":"secret-not-for-diagnostics","inputFile":"inputs/a.pdf","check":""}""");
        TrackingHost host = new(source, "normal");
        string json = outer ? """{"jobJsonFile":"child.json","password":"secret-not-for-diagnostics"}""" : """{"jobJsonFile":"child.json"}""";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("secret-not-for-diagnostics", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(outer ? 0 : 1, host.Opens.Count);
        Assert.Equal(0, host.Outputs);
        Assert.Equal(0L, source.OpenLeaseCount);
    }

    [Fact]
    public async Task DuplicateKeysWithinOneLayerUseTheLastValue()
    {
        InMemoryQpdfHost source = new();
        Add(source, "child.json", """{"empty":"bad","empty":"","showNpages":""}""");
        TrackingHost host = new(source, "normal");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"child.json"}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.EndsWith(": 0", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.Single(host.Opens);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, source.OpenLeaseCount);
    }

    private sealed class TrackingHost(InMemoryQpdfHost source, string mode) : IQpdfHost
    {
        internal List<QpdfPath> Opens { get; } = new();
        internal int Outputs { get; private set; }
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            Opens.Add(path);
            return new TrackingInput(await source.OpenInputAsync(path, cancellationToken), mode, ReadStarted);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            Outputs++;
            return source.CreateOutputAsync(path, cancellationToken);
        }
    }

    private sealed class TrackingInput(IQpdfInput inner, string mode, TaskCompletionSource started) : IQpdfInput
    {
        public QpdfPath Path => inner.Path;
        public string Identity => mode == "alias" ? "same-job-input" : inner.Identity;
        public long Length => inner.Length;
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            if (mode == "blocking") { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            if (mode == "no-progress") { return ReadOnlyMemory<byte>.Empty; }
            return await inner.ReadAsync(offset, mode == "short" ? Math.Min(count, 3) : count, cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
