using System.Text;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CheckInspectionTests
{
    private static readonly QpdfDiagnosticCode[] Listings =
        [QpdfDiagnosticCode.PageCount, QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticCode.XrefListing,
         QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticCode.PageListing, QpdfDiagnosticCode.AttachmentListing];

    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf"));

    private static InMemoryQpdfHost Host(byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string route, string extra, string[] flags, QpdfExecutionOptions options)
    {
        string job = "{\"inputFile\":\"a.pdf\",\"check\":\"\"," + extra + "}";
        if (route == "job") { return Qpdf.ExecuteJobAsync(host, job, options, CancellationToken.None); }
        if (route == "file")
        {
            host.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes("{" + extra + "}"), QpdfInputOwnership.Copy);
            return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["a.pdf", "--check", "--job-json-file=settings.json"]), options, CancellationToken.None);
        }
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["a.pdf", .. flags]), options, CancellationToken.None);
    }

    [Theory]
    [InlineData("job", false)]
    [InlineData("job", true)]
    [InlineData("argv", false)]
    [InlineData("argv", true)]
    [InlineData("file", false)]
    [InlineData("file", true)]
    public async Task CombinedInspectionsRunInNativeOrderOnOriginalIdentities(string route, bool reversed)
    {
        InMemoryQpdfHost host = Host(Fixture());
        string[] properties = ["\"showNpages\":\"\"", "\"showPages\":\"\"", "\"showXref\":\"\"", "\"showObject\":\"1\"", "\"listAttachments\":\"\"", "\"showLinearization\":\"\""];
        string[] flags = ["--check", "--show-npages", "--show-pages", "--show-xref", "--show-object=1", "--list-attachments", "--show-linearization"];
        if (reversed) { Array.Reverse(properties); Array.Reverse(flags); }
        QpdfExecutionResult result = await Execute(host, route, string.Join(',', properties), flags,
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxOpenHandles = 1L }));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.EndsWith(": 2", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message == "<< /Pages 2 0 R /Type /Catalog >>");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 1: 3 0 R");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 2: 4 0 R");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.XrefListing && d.Message == "3/0: uncompressed; offset = 121");
        Assert.EndsWith("has no embedded files", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.AttachmentListing).Message, StringComparison.Ordinal);
        int[] order = result.Diagnostics.Where(d => Listings.Contains(d.Code)).Select(d => Array.IndexOf(Listings, d.Code)).ToArray();
        Assert.Equal(order.Order().ToArray(), order);
        Assert.True(result.Counters.PeakMemoryBytes > 0);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job", false)]
    [InlineData("argv", false)]
    [InlineData("file", false)]
    [InlineData("job", true)]
    [InlineData("argv", true)]
    [InlineData("file", true)]
    public async Task RecoveryWarningsPreserveCombinedInspectionAndWarningExitPolicy(string route, bool warningExitZero)
    {
        string text = Encoding.ASCII.GetString(Fixture());
        text = System.Text.RegularExpressions.Regex.Replace(text, "(?<=startxref\\n)\\d+", "999999999");
        InMemoryQpdfHost host = Host(Encoding.ASCII.GetBytes(text));
        string extra = "\"showPages\":\"\",\"showObject\":\"1\"" + (warningExitZero ? ",\"warningExit0\":\"\"" : string.Empty);
        string[] flags = warningExitZero ? ["--check", "--show-pages", "--show-object=1", "--warning-exit-0"] : ["--check", "--show-pages", "--show-object=1"];
        QpdfExecutionResult result = await Execute(host, route, extra, flags, new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(warningExitZero ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("file")]
    public async Task DeclaredCountAndTraversedPagesStayDistinct(string route)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Fixture()).Replace("/Count 2", "/Count 9", StringComparison.Ordinal));
        InMemoryQpdfHost host = Host(bytes);
        QpdfExecutionResult result = await Execute(host, route, "\"showNpages\":\"\",\"showPages\":\"\"",
            ["--check", "--show-npages", "--show-pages"], new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.EndsWith(": 9", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("page ", StringComparison.Ordinal)));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("file")]
    public async Task FatalStreamCheckingStopsEveryFollowingInspection(string route)
    {
        byte[] data = "invalid-flate"u8.ToArray();
        byte[] bytes = CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + data.Length + " /Filter /FlateDecode >>", data), 1);
        InMemoryQpdfHost host = Host(bytes);
        QpdfExecutionResult result = await Execute(host, route,
            "\"showNpages\":\"\",\"showPages\":\"\",\"showXref\":\"\",\"showObject\":\"1\",\"listAttachments\":\"\"",
            ["--check", "--show-npages", "--show-pages", "--show-xref", "--show-object=1", "--list-attachments"], new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed);
        Assert.DoesNotContain(result.Diagnostics, d => Listings.Contains(d.Code));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job", "qdf", "--qdf")]
    [InlineData("argv", "qdf", "--qdf")]
    [InlineData("file", "qdf", "--qdf")]
    [InlineData("job", "linearize", "--linearize")]
    [InlineData("argv", "linearize", "--linearize")]
    [InlineData("file", "linearize", "--linearize")]
    public async Task SerializationControlsDoNotRewriteInspectedObjects(string route, string key, string flag)
    {
        InMemoryQpdfHost host = Host(Fixture());
        QpdfExecutionResult result = await Execute(host, route,
            "\"" + key + "\":\"\",\"showObject\":\"3\",\"showLinearization\":\"\"",
            ["--check", flag, "--show-object=3", "--show-linearization"], new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Contains("/Contents 5 0 R", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.LinearizationReport && d.Message.EndsWith("is not linearized", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", "unref-1page.pdf", "3", "/F2 6 0 R")]
    [InlineData("job", "undefined-form-resources.pdf", "4", "/Unused 6 0 R")]
    [InlineData("file", "unref-1page.pdf", "3", "/F2 6 0 R")]
    public async Task PruningDoesNotRewriteFilelessInspectionResources(string route, string fixture, string number, string unused)
    {
        InMemoryQpdfHost host = Host(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)));
        QpdfExecutionResult result = await Execute(host, route,
            "\"removeUnreferencedResources\":\"yes\",\"showObject\":\"" + number + "\"",
            ["--check", "--remove-unreferenced-resources=yes", "--show-object=" + number], new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing && d.Message.Contains(unused, StringComparison.Ordinal));
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("file")]
    public async Task PageImageInspectionRunsAfterChecking(string route)
    {
        InMemoryQpdfHost host = Host(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images-1page.pdf")));
        QpdfExecutionResult result = await Execute(host, route, "\"showPages\":\"\",\"withImages\":\"\"",
            ["--check", "--show-pages", "--with-images"], new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(new[] { "page 1: 3 0 R", "  images:", "    /Im0: 5 0 R, 2 x 2", "  content:", "    4 0 R" },
            result.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.PageListing).Select(d => d.Message).ToArray());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("file")]
    public async Task ExistingAttachmentsAreListedWithTheirOriginalStreamIdentity(string route)
    {
        var objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(payload) 5 0 R] >> >> >>", null);
        objects.Add((5, "<< /Type /Filespec /F (p.bin) /EF << /F 6 0 R >> >>", null));
        objects.Add((6, "<< /Type /EmbeddedFile /Length 3 >>", new byte[] { 0, 127, 255 }));
        InMemoryQpdfHost host = Host(CheckTests.Assemble(objects, 1));
        QpdfExecutionResult result = await Execute(host, route, "\"listAttachments\":\"\"",
            ["--check", "--list-attachments"], new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.AttachmentListing && d.Message == "payload -> 6,0");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.AttachmentListing && d.Message == "  filename: p.bin");
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job", "diagnostics")]
    [InlineData("argv", "diagnostics")]
    [InlineData("file", "diagnostics")]
    [InlineData("job", "diagnostic-bytes")]
    [InlineData("argv", "diagnostic-bytes")]
    [InlineData("file", "diagnostic-bytes")]
    public async Task InspectionCeilingsPreserveTargetsAndCloseTheInput(string route, string budget)
    {
        InMemoryQpdfHost host = Host(Fixture());
        const string extra = "\"showXref\":\"\",\"showPages\":\"\",\"showObject\":\"1\"";
        string[] flags = ["--check", "--show-xref", "--show-pages", "--show-object=1"];
        QpdfExecutionResult control = await Execute(host, route, extra, flags, new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExitCode.Success, control.ExitCode);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("prior.pdf"), CancellationToken.None))
        {
            await prior.WriteAsync("prior bytes"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfLimits limits = budget switch
        {
            "diagnostics" => QpdfLimits.Default with { MaxDiagnostics = 3L },
            _ => QpdfLimits.Default with { MaxDiagnosticBytes = 100L },
        };
        QpdfExecutionResult result = await Execute(host, route, extra, flags, new QpdfExecutionOptions(limits));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(result.Counters.PeakMemoryBytes > 0);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("prior.pdf"), out byte[] bytes));
        Assert.Equal("prior bytes"u8.ToArray(), bytes);
    }

    [Fact]
    public async Task AllLinearizationAndListingStagesKeepOneInputLease()
    {
        InMemoryQpdfHost source = Host(Fixture());
        TrackingHost host = new(source);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"a.pdf","check":"","checkLinearization":"","showLinearization":"","showPages":"","showXref":"","showObject":"1","listAttachments":""}""",
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxOpenHandles = 1L }), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(1, host.InputOpens);
        Assert.Equal(0, host.OutputCreates);
        Assert.Equal(0L, source.OpenLeaseCount);
    }

    private sealed class TrackingHost(InMemoryQpdfHost source) : IQpdfHost
    {
        internal int InputOpens { get; private set; }
        internal int OutputCreates { get; private set; }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            InputOpens++;
            return source.OpenInputAsync(path, cancellationToken);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            OutputCreates++;
            return source.CreateOutputAsync(path, cancellationToken);
        }
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("file")]
    public async Task CancellationAfterAnObservedReadStopsTheCombinedJob(string route)
    {
        var completed = await CompleteCancellation(route);
        CompletedCancellationObservation.Retire(completed.Governor, completed.Result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ResourceGovernor Governor, QpdfExecutionResult Result)> CompleteCancellation(string route)
    {
        InMemoryQpdfHost source = Host(Fixture());
        source.AddInput(QpdfPath.From("settings.json"), "{\"showPages\":\"\",\"showXref\":\"\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await source.CreateOutputAsync(QpdfPath.From("prior.pdf"), CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        WaitingHost host = new(source);
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> running = route == "job"
            ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"a.pdf","check":"","showPages":"","showXref":""}""", cancellation.Token).AsTask()
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(route == "file"
                ? ["a.pdf", "--check", "--job-json-file=settings.json"]
                : ["a.pdf", "--check", "--show-pages", "--show-xref"]), cancellation.Token).AsTask();
        try
        {
            Assert.Same(host.ReadStarted.Task, await Task.WhenAny(running, host.ReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(10)));
            ResourceGovernor governor = CompletedCancellationObservation.FindGovernor(running);
            cancellation.Cancel();
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
            Assert.True(result.Counters.HostOperations > 0);
            // The immutable snapshot can include independently owned names
            // awaiting natural collection; exact retirement is observed later.
            Assert.InRange(result.Counters.LiveMemoryBytes, ExecutionResultAdmission.MemoryBytes, result.Counters.PeakMemoryBytes);
            Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.DoesNotContain(result.Diagnostics, d => Listings.Contains(d.Code));
            Assert.Equal(0, host.OutputCreates);
            Assert.Equal(0L, source.OpenLeaseCount);
            Assert.True(source.TryGetOutput(QpdfPath.From("prior.pdf"), out byte[] prior));
            Assert.Equal("prior"u8.ToArray(), prior);
            return (governor, result);
        }
        finally { cancellation.Cancel(); }
    }

    private sealed class WaitingHost(InMemoryQpdfHost source) : IQpdfHost
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int OutputCreates { get; private set; }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            IQpdfInput input = await source.OpenInputAsync(path, cancellationToken);
            return path.Value == "a.pdf" ? new WaitingInput(input, ReadStarted) : input;
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            OutputCreates++;
            return source.CreateOutputAsync(path, cancellationToken);
        }
    }

    private sealed class WaitingInput(IQpdfInput inner, TaskCompletionSource started) : IQpdfInput
    {
        public QpdfPath Path => inner.Path;
        public string Identity => inner.Identity;
        public long Length => inner.Length;
        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            int data = await inner.ReadAsync(offset, destination, cancellationToken);
            Assert.True(data > 0);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return data;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Theory]
    [InlineData("--progress")]
    [InlineData("--verbose")]
    [InlineData("--report-memory-usage")]
    public async Task RepeatedPresentationFlagsRetainNativeSetterSemantics(string flag)
    {
        InMemoryQpdfHost host = Host(Fixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["a.pdf", "--check", flag, flag, "--show-pages"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("page ", StringComparison.Ordinal)));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("--parser-max-errors=10")]
    [InlineData("--parser-max-nesting=100")]
    [InlineData("--parser-max-container-size=100")]
    [InlineData("--no-default-limits")]
    public async Task ExplicitGlobalBlocksKeepNativeRepeatedSettings(string flag)
    {
        InMemoryQpdfHost host = Host(Fixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["a.pdf", "--global", flag, flag, "--", "--check", "--show-pages"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("page ", StringComparison.Ordinal)));
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> SelectorCases()
    {
        foreach (string route in new[] { "job", "argv", "file" })
        {
            foreach (string selector in new[] { "x", "", "0", "-1", "1junk", "1,2,3", "1,-1", "1," })
            {
                yield return [route, selector];
            }
        }
    }

    [Theory]
    [MemberData(nameof(SelectorCases))]
    public async Task ObjectInspectionUsesNativeIntegerPrefixesAndDisabledSelectors(string route, string selector)
    {
        InMemoryQpdfHost host = Host(Fixture());
        QpdfExecutionResult result = await Execute(host, route,
            "\"showNpages\":\"\",\"showObject\":\"" + selector + "\"",
            ["--check", "--show-npages", "--show-object=" + selector], new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.EndsWith(": 2", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        QpdfDiagnostic[] listing = result.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.ObjectListing).ToArray();
        if (selector is "x" or "" or "0" or "-1") { Assert.Empty(listing); }
        else if (selector is "1,2,3" or "1,-1") { Assert.Equal("null", Assert.Single(listing).Message); }
        else { Assert.Equal("<< /Pages 2 0 R /Type /Catalog >>", Assert.Single(listing).Message); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SelectorGenerationPersistsAcrossSettersButNotOverwrittenJsonKeys()
    {
        InMemoryQpdfHost host = Host(Fixture());
        host.AddInput(QpdfPath.From("first.json"), "{\"showObject\":\"3,1\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("last.json"), "{\"showObject\":\"1\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult layered = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["a.pdf", "--check", "--job-json-file=first.json", "--job-json-file=last.json"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, layered.ExitCode);
        Assert.Equal("null", Assert.Single(layered.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message);
        QpdfExecutionResult duplicate = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"a.pdf","check":"","showObject":"3,1","showObject":"1"}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, duplicate.ExitCode);
        Assert.Equal("<< /Pages 2 0 R /Type /Catalog >>", Assert.Single(duplicate.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("file")]
    public async Task SelectorOverflowRejectsBeforePdfAccess(string route)
    {
        InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Execute(host, route, "\"showObject\":\"999999999999999999999999999\"",
            ["--check", "--show-object=999999999999999999999999999"], new QpdfExecutionOptions(QpdfLimits.Default));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
