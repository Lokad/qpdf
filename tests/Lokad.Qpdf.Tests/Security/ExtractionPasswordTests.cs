using System.IO.Compression;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class ExtractionPasswordTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("payload.bin");

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (int revision in new[] { 4, 6 })
        foreach (string operation in new[] { "attachment", "raw", "filtered" }) { yield return [job, revision, operation]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExplicitOutputsContainAuthenticatedPlaintext(bool job, int revision, string operation)
    {
        using InMemoryQpdfHost host = new(); Prepare(host, revision);
        QpdfExecutionResult result = await Execute(host, job, operation, revision == 4 ? "café•" : null, false, false, false, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        AssertPayload(host, operation); Assert.Equal(0, result.Counters.OpenHandles);
        if (operation == "attachment") { Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.AttachmentListing); }
    }

    [Theory]
    [InlineData(false, "attachment")]
    [InlineData(true, "attachment")]
    [InlineData(false, "raw")]
    [InlineData(true, "raw")]
    [InlineData(false, "filtered")]
    [InlineData(true, "filtered")]
    public async Task FileAndHexOwnerCredentialsRemainOwnedThroughExtraction(bool job, string operation)
    {
        using InMemoryQpdfHost host = new(); Prepare(host, 4);
        host.AddInput(QpdfPath.From("password"), "6f776e6572e991\r\n"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, job, operation, null, true, true, true, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); AssertPayload(host, operation); Assert.Equal(0, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExtractionHonorsPasswordEncodingRecovery(bool job, bool suppress)
    {
        using InMemoryQpdfHost host = new(); Prepare(host, 4);
        QpdfExecutionResult result = await Execute(host, job, "attachment", "café•", false, false, suppress, CancellationToken.None);
        Assert.Equal(suppress ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
        if (suppress) { Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code); Assert.False(host.TryGetOutput(Output, out _)); }
        else { AssertPayload(host, "attachment"); }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "attachment")]
    [InlineData(true, "attachment")]
    [InlineData(false, "raw")]
    [InlineData(true, "raw")]
    [InlineData(false, "filtered")]
    [InlineData(true, "filtered")]
    public async Task InvalidCredentialsPreservePriorBinaryOutput(bool job, string operation)
    {
        using InMemoryQpdfHost host = new(); Prepare(host, 4);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Execute(host, job, operation, "private-wrong", false, false, false, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics); Assert.Equal(QpdfDiagnosticCode.InvalidPassword, diagnostic.Code);
        Assert.DoesNotContain("private-wrong", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledExtractionNeverPublishes(bool job)
    {
        using InMemoryQpdfHost host = new(); Prepare(host, 4); using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        QpdfExecutionResult result = await Execute(host, job, "filtered", "café•", false, false, false, cancellation.Token);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status); Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("attachment")]
    [InlineData("raw")]
    [InlineData("filtered")]
    public async Task EveryExtractionAdmissionRefusalRefundsBeforePublication(string operation)
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost storage = new(); Prepare(storage, 4);
            storage.AddInput(QpdfPath.From("password"), "6f776e6572e991\n"u8.ToArray(), QpdfInputOwnership.Copy);
            using IMemoryScope caller = budget.CreateScope(); BudgetHost host = new(storage, caller);
            QpdfExecutionResult result = await Execute(host, true, operation, null, true, true, true, CancellationToken.None);
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses) { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded); Assert.False(storage.TryGetOutput(Output, out _)); }
            else { AssertPayload(storage, operation); }
            Assert.Equal(0, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, bool job, string operation, string? password,
        bool file, bool hex, bool suppress, CancellationToken cancellationToken)
    {
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value };
        List<string> argv = [Input.Value];
        if (operation == "attachment")
        { fields["showAttachment"] = "private.bin"; fields["listAttachments"] = ""; argv.AddRange(["--list-attachments", "--show-attachment=private.bin"]); }
        else
        { fields["showObject"] = "9"; fields[operation == "raw" ? "rawStreamData" : "filteredStreamData"] = ""; argv.AddRange(["--show-object=9", operation == "raw" ? "--raw-stream-data" : "--filtered-stream-data"]); }
        if (password is not null) { fields["password"] = password; argv.Add("--password=" + password); }
        if (file) { fields["passwordFile"] = "password"; argv.Add("--password-file=password"); }
        if (hex) { fields["passwordMode"] = "hex-bytes"; argv.Add("--password-mode=hex-bytes"); }
        if (suppress) { fields["suppressPasswordRecovery"] = ""; argv.Add("--suppress-password-recovery"); }
        argv.AddRange(["--", Output.Value]);
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), cancellationToken) : Qpdf.ExecuteAsync(host, new(argv), cancellationToken);
    }

    private static void AssertPayload(InMemoryQpdfHost host, string operation)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        if (operation == "raw")
        {
            using MemoryStream compressed = new(bytes); using ZLibStream zlib = new(compressed, CompressionMode.Decompress); using MemoryStream decoded = new();
            zlib.CopyTo(decoded); bytes = decoded.ToArray();
        }
        Assert.Equal(operation == "attachment" ? "Private attachment payload.\n"u8.ToArray() : "BT (Page) Tj ET"u8.ToArray(), bytes);
        Assert.Equal(0, host.OpenLeaseCount);
    }
    private static void Prepare(InMemoryQpdfHost host, int revision)
        => host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"security-r{revision}-attachment.pdf")), QpdfInputOwnership.Copy);
    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
}
