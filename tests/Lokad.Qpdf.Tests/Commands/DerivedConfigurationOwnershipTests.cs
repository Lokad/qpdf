using Host = Lokad.Qpdf.Tests.Foundation.RecordingJobHost;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class DerivedConfigurationOwnershipTests(ITestOutputHelper log)
{
    private static readonly QpdfPath InputPath = QpdfPath.From("input.pdf");
    private static readonly QpdfPath OutputPath = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [17, 23, 41];
    private static readonly string Prefix = new('p', 1024);
    private static readonly string Range = string.Join(',', Enumerable.Repeat("1", 512));
    private sealed record Completed(QpdfExecutionResult Result, QpdfExecutionCounters Snapshot);

    private static async Task Seed(Host host)
    {
        host.Inner.AddInput(InputPath, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(OutputPath, CancellationToken.None);
        await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
    }

    private static async Task ValidateOutput(Host host, string profile, string prefix)
    {
        Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] published));
        if (profile == "json")
        {
            using JsonDocument json = JsonDocument.Parse(published);
            JsonElement objects = json.RootElement.GetProperty("qpdf")[1];
            Assert.True(objects.TryGetProperty("obj:1 0 R", out _));
            Assert.False(objects.TryGetProperty("obj:2 0 R", out _));
            return;
        }
        InMemoryQpdfHost validation = new(); QpdfPath input = QpdfPath.From("published.pdf");
        validation.AddInput(input, published, QpdfInputOwnership.Copy);
        QpdfCheckResult check = await Qpdf.CheckAsync(validation, input, CancellationToken.None);
        Assert.True(check.Passed); Assert.Equal(1, check.PageCount);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(validation, input, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject labels));
        PdfDictionary tree = Assert.IsType<PdfDictionary>(labels);
        Assert.True(tree.TryGet(PdfName.FromAscii("Nums"), out QpdfObject numbers));
        PdfDictionary label = Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(numbers)[1]);
        Assert.True(label.TryGet(PdfName.FromAscii("P"), out QpdfObject text));
        Assert.Equal(Encoding.Latin1.GetBytes(prefix), Assert.IsType<PdfByteString>(text).Value.ToArray());
        PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
        Assert.True(page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject rotation));
        Assert.Equal(90L, Assert.IsType<PdfInteger>(rotation).Value);
    }

    private async Task<Completed> Complete(string route, string profile, string outcome)
    {
        using Host host = Host.ForOutcome(outcome, InputPath, OutputPath); await Seed(host);
        using CancellationTokenSource cancel = new();
        string prefix = route == "argv-unicode" ? new string('\u00e9', 1024) : Prefix;
        string label = "r1:D/1/" + prefix;
        string rotation = "90:" + Range;
        string[] selectors = ["0000000001,0000000000", "2147483647,0000000000"];
        string[] argv = profile == "json"
            ? [InputPath.Value, "--json=2", "--json-key=qpdf", "--json-object=" + selectors[0], "--json-object=" + selectors[1], "--", OutputPath.Value]
            : [InputPath.Value, "--rotate", rotation, "--set-page-labels", label, "--", OutputPath.Value];
        string json = profile == "json"
            ? JsonSerializer.Serialize(new { inputFile = InputPath.Value, outputFile = OutputPath.Value, json = "2", jsonKey = new[] { "qpdf" }, jsonObject = selectors })
            : JsonSerializer.Serialize(new { inputFile = InputPath.Value, outputFile = OutputPath.Value, rotate = new[] { rotation }, setPageLabels = new[] { label } });
        Task<QpdfExecutionResult> running;
        if (route == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(new[] { "@args.txt" }), cancel.Token).AsTask();
        }
        else if (route == "mixed")
        {
            host.Inner.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
            running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--job-json-file=settings.json", .. argv]), cancel.Token).AsTask();
        }
        else if (route is "file" or "nested")
        {
            host.Inner.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes(route == "file" ? json : "{\"jobJsonFile\":\"child.json\"}"), QpdfInputOwnership.Copy);
            if (route == "nested")
            {
                host.Inner.AddInput(QpdfPath.From("child.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
                running = Qpdf.ExecuteJobAsync(host, "{\"jobJsonFile\":\"settings.json\"}", cancel.Token).AsTask();
            }
            else { running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(new[] { "--job-json-file=settings.json" }), cancel.Token).AsTask(); }
        }
        else if (route == "job") { running = Qpdf.ExecuteJobAsync(host, json, cancel.Token).AsTask(); }
        else { running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), cancel.Token).AsTask(); }
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "cancel") { cancel.Cancel(); }
            host.Resume();
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome switch { "success" => QpdfExecutionStatus.Completed,
                "cancel" or "host-cancel" => QpdfExecutionStatus.Canceled, _ => QpdfExecutionStatus.Failed }, result.Status);
            Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            if (outcome == "success") { await ValidateOutput(host, profile, prefix); }
            else { Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior)); Assert.Equal(Prior, prior); }
            Assert.True(host.Budget.ReservedBytes < host.Budget.PeakBytes);
            log.WriteLine("DERIVED CONTROL PASS {0}|{1}|{2}", route, profile, outcome);
            return new(result, result.Counters with { });
        }
        finally { host.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task CompleteJobsFundDerivedValuesAndRetirePlansIndependently()
    {
        List<Completed> jobs = [];
        foreach (string route in new[] { "argv", "argv-unicode", "response", "mixed", "job", "file", "nested" })
        foreach (string profile in new[] { "labels-rotation", "json" })
        { jobs.Add(await Complete(route, profile, "success")); }
        foreach (string profile in new[] { "labels-rotation", "json" })
        foreach (string outcome in new[] { "publication-fault", "open-fault", "read-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await Complete("argv-unicode", profile, outcome)); }
        foreach (Completed job in jobs) { Assert.Equal(job.Snapshot, job.Result.Counters); }
    }

    [Fact]
    public async Task DerivedRotationRangeRefusalPrecedesPdfOpen()
    {
        using Host host = Host.ForOutcome("budget", InputPath, OutputPath); await Seed(host);
        host.Budget.RefuseGrowth = true;
        string range = string.Join(',', Enumerable.Repeat("1", 1536));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([InputPath.Value, "--rotate", "+90:" + range, "--pages", ".", "1", "--", OutputPath.Value]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Empty(host.OpenedPaths); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior)); Assert.Equal(Prior, prior);
    }

    private async Task<QpdfJsonResult?> CompleteTyped(string outcome)
    {
        using Host host = Host.ForOutcome(outcome, InputPath, OutputPath); await Seed(host);
        using CancellationTokenSource cancel = new();
        QpdfJsonOptions options = new(2, ["qpdf"], "none", null, ["0000000001,0000000000", "2147483647,0000000000"], false);
        Task<QpdfJsonResult> running = Qpdf.GetDocumentJsonAsync(host, InputPath, OutputPath, options, cancel.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "cancel") { cancel.Cancel(); }
            host.Resume();
            QpdfJsonResult? result = null;
            if (outcome == "success")
            {
                result = await running.WaitAsync(TimeSpan.FromSeconds(10));
                await ValidateOutput(host, "json", string.Empty);
                Assert.Equal(0L, result.Counters.OpenHandles);
            }
            else
            {
                if (outcome is "cancel" or "host-cancel")
                { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10))); }
                else { await Assert.ThrowsAnyAsync<QpdfException>(() => running.WaitAsync(TimeSpan.FromSeconds(10))); }
                Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior)); Assert.Equal(Prior, prior);
            }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.True(host.Budget.ReservedBytes < host.Budget.PeakBytes);
            return result;
        }
        finally
        {
            host.Resume();
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task TypedJsonJobsFundCanonicalSelectorsAndRetirePrivateInspection()
    {
        foreach (string outcome in new[] { "success", "publication-fault", "cancel", "host-cancel" })
        {
            QpdfJsonResult? result = await CompleteTyped(outcome);
            if (result is not null)
            {
                using JsonDocument parsed = JsonDocument.Parse(result.Json);
                Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
            }
        }
    }
}
