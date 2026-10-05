using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class ParserOwnershipTests
{
    public static IEnumerable<object[]> AbortedContainers()
    {
        string value = "(" + new string('A', 4096) + ")";
        foreach (string body in new[]
        {
            "[ " + value + " @ ]",
            "<< /A " + value + " /B @ >>",
            "[ << /A " + value + " >> @ ]",
            "<< /A [ " + value + " ] /B @ >>",
            "[ " + value,
            "<< /A " + value,
            "<< " + value + " @ >>",
            "<< /A 1 /A " + value + " >>",
            "[ /" + new string('A', 4096) + " @ ]",
            "[ <" + string.Concat(Enumerable.Repeat("41", 4096)) + "> @ ]",
            "[ " + new string('0', 4096) + "7 @ ]",
            "[ 0." + new string('0', 4096) + "1 @ ]",
        })
        {
            foreach (bool member in new[] { false, true }) { yield return [body, member]; }
        }
    }

    [Theory]
    [MemberData(nameof(AbortedContainers))]
    public void RepeatedNullRepairsReleaseAbandonedValues(string body, bool member)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 1L, MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = true };
        byte[] input = Encoding.ASCII.GetBytes(member ? body : "5 0 obj " + body + " endobj");
        for (int i = 0; i < 50; i++)
        {
            QpdfObject value = member
                ? parser.ParseObjectValue(new PdfTokenizer(input), 0)
                : parser.ParseIndirectObject(new PdfTokenizer(input), 0).Value;
            Assert.Same(PdfNull.Instance, value);
            Assert.True(parser.ObjectAborted);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork >= 50L);
        Assert.NotEmpty(diagnostics.Entries);
        Assert.Equal(diagnostics.Entries.Count, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void StandaloneValueFailureReleasesOwnedNamesAndContainers()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        byte[] input = Encoding.ASCII.GetBytes("<< /A (" + new string('A', 4096) + ") /B @ >>");
        Assert.Throws<QpdfParseException>(() => parser.ParseValue(new PdfTokenizer(input), 0));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 0L);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 4096L, limits.MaxMemoryBytes);
    }
    [Fact]
    public void AbortsPreserveValuesRetainedByPreviousLoads()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 1L, MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null) { TolerateObjectErrors = true };
        string text = new('A', 4096);
        PdfByteString kept = Assert.IsType<PdfByteString>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes("(" + text + ")")), 0));
        long retained = governor.Snapshot().LiveMemoryBytes;
        Assert.True(retained >= 4096L);
        byte[] abandoned = Encoding.ASCII.GetBytes("[ (" + text + ") @ ]");
        for (int i = 0; i < 20; i++)
        {
            Assert.Same(PdfNull.Instance, parser.ParseObjectValue(new PdfTokenizer(abandoned), 0));
            Assert.Equal(retained, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(Encoding.ASCII.GetBytes(text), kept.Value);
        Assert.Equal(7L, Assert.IsType<PdfInteger>(parser.ParseObjectValue(new PdfTokenizer("7"u8.ToArray()), 0)).Value);
        Assert.False(parser.ObjectAborted);
    }

    [Theory]
    [InlineData("syntax")]
    [InlineData("memory")]
    [InlineData("diagnostics")]
    [InlineData("endobj")]
    public void FailuresReleaseAttemptWithoutResettingObservedCounters(string kind)
    {
        QpdfLimits limits = QpdfLimits.Default with
        {
            MaxParserErrors = 1L,
            MaxMemoryBytes = 20000L,
            MaxDiagnostics = kind == "diagnostics" ? 1L : 256L,
        };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = kind == "diagnostics" };
        string text = new('A', 4096);
        string body = kind switch
        {
            "memory" => "[ (" + text + ") (" + new string('B', 100000) + ") ]",
            "endobj" => "[ (" + text + ") ] missing",
            _ => "[ (" + text + ") @ ] endobj",
        };
        byte[] input = Encoding.ASCII.GetBytes("5 0 obj " + body);
        if (kind is "syntax" or "endobj")
        {
            Assert.Throws<QpdfParseException>(() => parser.ParseIndirectObject(new PdfTokenizer(input), 0));
        }
        else
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseIndirectObject(new PdfTokenizer(input), 0));
            Assert.Equal(kind == "memory" ? nameof(QpdfLimits.MaxMemoryBytes) : nameof(QpdfLimits.MaxDiagnostics), error.LimitName);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 0L);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 4096L, limits.MaxMemoryBytes);
        Assert.Equal(kind == "diagnostics" ? 1L : 0L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void FailedOuterStreamKeepsNestedResolverValueAndItsReservation()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        RetainingResolver resolver = new(limits, governor, diagnostics);
        PdfParser parser = new(limits, governor, diagnostics, resolver);
        byte[] input = "5 0 obj << /Length 6 0 R /Audit (discard) >>\nstream\nabc\nendobj\nendobj"u8.ToArray();
        Assert.Throws<QpdfParseException>(() => parser.ParseIndirectObject(new PdfTokenizer(input), 0));
        Assert.Equal(resolver.RetainedBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.True(resolver.RetainedBytes >= 4096L);
        Assert.Equal(3L, Assert.IsType<PdfInteger>(resolver.Value).Value);
    }

    [Fact]
    public async Task FailedPublicObjectLoadRetainsFreshNestedLengthCache()
    {
        string length = new string('0', 4096) + "3";
        byte[] pdf = TolerantObjectLoadingTests.Pdf(["<< /Length 6 0 R /Audit (discard) >>\nstream\nabc\nendobj", length], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None))
        await using (QpdfDocument failed = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None))
        {
            Assert.Equal(3L, Assert.IsType<PdfInteger>(control.GetObject(6)).Value);
            Assert.Throws<QpdfParseException>(() => failed.GetObject(5));
            PdfInteger cached = Assert.IsType<PdfInteger>(failed.GetObject(6));
            Assert.Equal(3L, cached.Value);
            Assert.Same(cached, failed.GetObject(6));
            Assert.Equal(control.Counters.LiveMemoryBytes, failed.Counters.LiveMemoryBytes);
            for (int i = 0; i < 20; i++)
            {
                Assert.Throws<QpdfParseException>(() => failed.GetObject(5));
                Assert.Equal(control.Counters.LiveMemoryBytes, failed.Counters.LiveMemoryBytes);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicPartialParseFailurePreservesTargetsAndObservedWork(bool job)
    {
        byte[] pdf = TolerantObjectLoadingTests.Pdf(
            ["<< /Length 6 0 R /Audit (discard) >>\nstream\nabc\nendobj", new string('0', 4096) + "3"], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfPath priorPath = QpdfPath.From("outputs/prior.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(priorPath, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = 100000L });
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","showObject":"5"}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=5"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains("endstream", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
        Assert.True(result.Counters.TotalWork > 0L);
        Assert.InRange(result.Counters.PeakMemoryBytes, pdf.Length, options.Limits.MaxMemoryBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(priorPath, out byte[] priorBytes));
        Assert.Equal("prior"u8.ToArray(), priorBytes);
    }

    [Fact]
    public async Task PublicObjectCancellationReleasesAttemptAfterObservedContainerWork()
    {
        byte[] pdf = TolerantObjectLoadingTests.Pdf(
            ["[ (" + new string('A', 4096) + ") (" + new string('B', 16 * 1024 * 1024) + ") ]"], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, cancellation.Token))
        {
            long retained = document.Counters.LiveMemoryBytes;
            long work = document.Counters.TotalWork;
            Task<QpdfObject> loading = Task.Factory.StartNew(() => document.GetObject(5),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            bool observed = SpinWait.SpinUntil(() => document.Counters.LiveMemoryBytes >= retained + 4096L || loading.IsCompleted, TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.True(observed);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await loading.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(document.Counters.TotalWork > work);
            Assert.True(document.Counters.PeakMemoryBytes >= retained + 4096L);
            Assert.Equal(retained, document.Counters.LiveMemoryBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
    [Fact]
    public async Task CanceledPublicStreamLoadPreservesFreshNestedLengthReservation()
    {
        const int bytes = 16 * 1024 * 1024;
        string length = new string('0', 4096) + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] pdf = TolerantObjectLoadingTests.Pdf(
            ["<< /Length 6 0 R >>\nstream\n" + new string('A', bytes) + "\nendstream", length], false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        using CancellationTokenSource cancellation = new();
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None))
        await using (QpdfDocument canceled = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, cancellation.Token))
        {
            Assert.Equal(bytes, Assert.IsType<PdfInteger>(control.GetObject(6)).Value);
            long retained = control.Counters.LiveMemoryBytes;
            long work = canceled.Counters.TotalWork;
            Task<QpdfObject> loading = Task.Factory.StartNew(() => canceled.GetObject(5),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            bool observed = SpinWait.SpinUntil(() => canceled.Counters.LiveMemoryBytes >= retained + 2L * bytes || loading.IsCompleted, TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.True(observed);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await loading.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(canceled.Counters.TotalWork > work);
            Assert.True(canceled.Counters.PeakMemoryBytes >= retained + 2L * bytes);
            Assert.Equal(retained, canceled.Counters.LiveMemoryBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
    private sealed class RetainingResolver : IIndirectResolver
    {
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;
        private readonly DiagnosticBag _diagnostics;

        internal RetainingResolver(QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics)
        {
            _limits = limits;
            _governor = governor;
            _diagnostics = diagnostics;
        }

        internal QpdfObject? Value { get; private set; }
        internal long RetainedBytes { get; private set; }

        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            long before = _governor.Snapshot().LiveMemoryBytes;
            PdfParser nested = new(_limits, _governor, _diagnostics, null);
            Value = nested.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(new string('0', 4096) + "3")), 0);
            RetainedBytes = _governor.Snapshot().LiveMemoryBytes - before;
            value = Value;
            return true;
        }
    }

    [Fact]
    public async Task CancellationAfterContainerWorkReleasesAbandonedAttempt()
    {
        byte[] input = Encoding.ASCII.GetBytes("[ (" + new string('A', 4096) + ") (" + new string('B', 16 * 1024 * 1024) + ") ]");
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        ResourceGovernor governor = new(limits, cancellation.Token);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        Task<QpdfObject> parse = Task.Factory.StartNew(() => parser.ParseObjectValue(new PdfTokenizer(input), 0),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool observed = SpinWait.SpinUntil(() => governor.Snapshot().LiveMemoryBytes >= 4096L || parse.IsCompleted, TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.True(observed);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await parse.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(governor.Snapshot().TotalWork > 0L);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 4096L, limits.MaxMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicJsonLoadsManyNullRepairsWithinFiniteHostMemory(bool job, bool brokenXref)
    {
        string body = "[ (" + new string('A', 4096) + ") @ ]";
        string[] bodies = Enumerable.Repeat(body, 50).ToArray();
        byte[] pdf = TolerantObjectLoadingTests.Pdf(bodies, brokenXref);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/control.pdf"), TolerantObjectLoadingTests.Pdf(bodies, false), QpdfInputOwnership.Copy);
        QpdfPath priorPath = QpdfPath.From("outputs/prior.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(priorPath, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = pdf.Length + 40000L, MaxParserErrors = 1L };
        Assert.InRange(pdf.Length, 200000, 220000);
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/control.pdf"), limits, CancellationToken.None))
        {
            Assert.Equal(1, control.PageCount);
            Assert.InRange(control.Counters.PeakMemoryBytes, pdf.Length, limits.MaxMemoryBytes);
        }
        QpdfExecutionOptions options = new(limits);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","outputFile":"outputs/report.json","json":"2","jsonKey":["qpdf"],"global":{"parserMaxErrors":1}}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--json=2", "--json-key=qpdf", "--parser-max-errors=1", "--", "outputs/report.json"]), options, CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(100, result.Diagnostics.Count(d => d.Code == QpdfDiagnosticCode.RepairedObject));
        Assert.InRange(result.Counters.PeakMemoryBytes, pdf.Length, limits.MaxMemoryBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/report.json"), out byte[] reportBytes));
        using JsonDocument report = JsonDocument.Parse(reportBytes);
        JsonElement objects = report.RootElement.GetProperty("qpdf")[1];
        for (int number = 5; number < 55; number++)
        {
            Assert.Equal(JsonValueKind.Null, objects.GetProperty($"obj:{number} 0 R").GetProperty("value").ValueKind);
        }
        Assert.True(host.TryGetOutput(priorPath, out byte[] priorBytes));
        Assert.Equal("prior"u8.ToArray(), priorBytes);
    }
}
