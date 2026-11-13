using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class PdfVersionOwnershipTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");
    public static IEnumerable<object[]> DocumentCases()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (string catalog in new[] { "none", "direct", "indirect" })
        { yield return [compressed, catalog]; }
    }

    [Theory]
    [MemberData(nameof(DocumentCases))]
    [Trait("Layer", "Contract")]
    public async Task HeaderCatalogAndCallerValuesRemainUsableAfterDisposal(bool compressed, string catalog)
    {
        using RecordingJobHost host = new(null, null, "success", true);
        host.Inner.AddInput(Input, ExecutionJobFixtures.DocumentBytes(compressed, catalog), QpdfInputOwnership.Copy);
        QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        await document.DisposeAsync();
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Budget.ReservedBytes > 0L);
        PdfVersion header = document.HeaderVersion;
        PdfVersion effective = document.Version;
        Assert.Equal("1.4", header.ToString()); Assert.Equal(catalog == "none" ? "1.4" : "1.7", effective.ToString());
        Assert.Equal(catalog == "none", ReferenceEquals(header, effective));
        PdfVersion copiedHeader = header with { };
        PdfVersion copiedEffective = new(effective.Major, effective.Minor);
        Assert.Equal(header, copiedHeader); Assert.Equal(header.GetHashCode(), copiedHeader.GetHashCode());
        Assert.Equal(effective, copiedEffective); Assert.Equal(effective.GetHashCode(), copiedEffective.GetHashCode());
        Assert.False(typeof(PdfVersion).GetProperty(nameof(PdfVersion.Major))?.CanWrite);
        Assert.False(typeof(PdfVersion).GetProperty(nameof(PdfVersion.Minor))?.CanWrite);
        host.Dispose();
        Assert.Equal(0L, host.Budget.ReservedBytes);
        Assert.Equal(header, copiedHeader); Assert.Equal(effective, copiedEffective);
    }

    public static IEnumerable<object[]> WriterCases()
    {
        foreach (string profile in new[] { "borrowed", "minimum", "force", "object-streams", "merge", "json-create", "json-update", "empty" })
        {
            yield return ["argv", false, profile, "write", "success"];
            yield return ["job", true, profile, "write", "success"];
        }
        // Publication failures belong to the shared writer owner. Keep explicit
        // merge/import/update wiring without crossing every version profile.
        foreach ((string boundary, string outcome) in new[] { ("write", "fault"), ("write", "cancel"),
            ("commit", "fault"), ("output-close", "success"), ("output-close", "fault"), ("output-close", "cancel") })
        { yield return ["argv", true, "borrowed", boundary, outcome]; }
        yield return ["job", false, "merge", "commit", "fault"];
        yield return ["argv", false, "json-create", "write", "fault"];
        yield return ["job", false, "json-update", "output-close", "cancel"];
    }

    private static Task<RecordingJobHost> PrepareWriter(string profile, bool compressed, string boundary, string outcome)
    {
        string fixture = profile == "json-create" ? "typed-import" : profile == "json-update" ? "typed-update" : "typed-page";
        return ExecutionJobFixtures.PrepareAsync(fixture, compressed, boundary, outcome);
    }

    private static async Task<QpdfExecutionResult> StartWriter(IQpdfHost host, string route, string profile, CancellationToken token)
    {
        string input = profile == "json-create" ? "input.json" : "input.pdf";
        List<string> argv = profile == "empty" ? ["--empty", "--"] : [input];
        Dictionary<string, object> job = [];
        if (profile == "empty") { job.Add("empty", ""); } else { job.Add("inputFile", input); }
        if (profile is "minimum" or "merge" or "json-create" or "json-update")
        { argv.Add("--min-version=1.7"); job.Add("minVersion", "1.7"); }
        if (profile == "force") { argv.Add("--force-version=1.6"); job.Add("forceVersion", "1.6"); }
        if (profile is not ("json-create" or "json-update" or "empty"))
        {
            string objects = profile == "object-streams" ? "generate" : "disable";
            argv.Add("--object-streams=" + objects); job.Add("objectStreams", objects);
        }
        if (profile == "merge")
        {
            argv.AddRange(["--pages", ".", "1", "second.pdf", "1", "--"]);
            job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = "second.pdf", range = "1" } });
        }
        if (profile == "json-create") { argv.AddRange(["--json-input", "--"]); job.Add("jsonInput", ""); }
        if (profile == "json-update") { argv.AddRange(["--update-from-json=input.json", "--"]); job.Add("updateFromJson", "input.json"); }
        argv.Add("output.pdf"); job.Add("outputFile", "output.pdf");
        QpdfExecutionOptions options = new(QpdfLimits.Default);
        return route == "job" ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), options, token)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, token);
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    [Trait("Layer", "Contract")]
    public async Task WriterNegotiationsPreserveHeadersAndPublicationOutcomes(
        string route, bool compressed, string profile, string boundary, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        using RecordingJobHost host = await PrepareWriter(profile, compressed, boundary, outcome);
        InMemoryQpdfHost inner = host.Inner;
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        Task<QpdfExecutionResult> running = StartWriter(host, route, profile, cancellation.Token);
        try
        {
            Task entered = host.Entered;
            Task first = await Task.WhenAny(entered, running).WaitAsync(TimeSpan.FromSeconds(10));
            if (ReferenceEquals(first, running))
            {
                QpdfExecutionResult early = await running;
                Assert.Fail("Writer finished before its requested checkpoint: " + early.Status + "; "
                    + string.Join("; ", early.Diagnostics.Select(diagnostic => diagnostic.Message)));
            }
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "cancel") { cancellation.Cancel(); } else { host.Resume(); }
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel" ? QpdfExecutionStatus.Canceled
                : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(host.HostCalls, result.Counters.HostOperations);
            Assert.Equal(0L, inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(result.Counters.PeakMemoryBytes <= QpdfLimits.Default.MaxMemoryBytes);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(inner.TryGetOutput(Output, out byte[] output));
            if (host.Committed.Count == 0) { Assert.Equal(prior, output); }
            if (outcome == "success")
            {
                string expected = profile is "minimum" or "merge" or "json-create" or "json-update" ? "1.7"
                    : profile == "force" ? "1.6" : profile == "object-streams" ? "1.5" : profile == "empty" ? "1.3" : "1.4";
                Assert.StartsWith("%PDF-" + expected, Encoding.ASCII.GetString(output.AsSpan(0, 8)));
            }
            if (profile != "empty") { Assert.True(host.ReadBytes > 0L); }
            QpdfExecutionCounters snapshot = result.Counters with { };
            host.Dispose();
            Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
            Assert.Equal(snapshot, result.Counters);
        }
        finally
        {
            host.Resume();
            await running;
        }
    }

}
