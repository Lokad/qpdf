using System.Text.Json;
using System.Reflection;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class PageContentCoverageTests
{
    internal static byte[] Fixture(string contents, string? filter, bool mixed)
    {
        byte[] data = filter == "RunLengthDecode" ? new byte[] { 2, 113, 32, 81, 128 } : filter is null ? "q Q"u8.ToArray() : "not-flate"u8.ToArray();
        string header = $"<< /Length {data.Length}" + (filter is null ? "" : " /Filter /" + filter) + " >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(K) 6 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >>" + (contents.Length == 0 ? "" : " /Contents " + contents) + " >>", null),
            (4, mixed ? "<< /Length 3 >>" : header, mixed ? "q Q"u8.ToArray() : data),
            (5, mixed ? header : "null", mixed ? data : null),
            (6, "<< /Type /Filespec /F (note) /EF << /F 7 0 R >> >>", null),
            (7, "<< /Type /EmbeddedFile /Length 1 >>", "X"u8.ToArray())], 1);
    }

    private static async Task<QpdfExecutionResult> Inspect(IQpdfHost host, bool job, string level)
        => job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", check = "", decodeLevel = level, showAttachment = "K" }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--check", "--decode-level=" + level, "--show-attachment=K"]), CancellationToken.None);

    [Theory]
    [InlineData("42")]
    [InlineData("[4 0 R 42]")]
    [InlineData("[4 0 R 99 0 R]")]
    public async Task MalformedCollectionsFailAndWithholdFollowingPayload(string contents)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(contents, null, false), QpdfInputOwnership.Transfer);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.False(check.Passed); Assert.Equal(QpdfCheckStatus.Failed, Assert.Single(check.Checks, c => c.Name == "page-content").Status);
        Assert.Contains(check.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed && d.Message.Contains("Page 1 content", StringComparison.Ordinal));
        foreach (bool job in new[] { false, true })
        {
            QpdfExecutionResult result = await Inspect(host, job, "all");
            Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Null(result.OutputBytes); Assert.Null(result.OutputText);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed); Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("99 0 R")]
    [InlineData("[]")]
    [InlineData("4 0 R")]
    public async Task EmptyNullAndReadablePagesRemainComplete(string contents)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(contents, null, false), QpdfInputOwnership.Transfer);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.True(check.Passed); Assert.False(check.HasSkipped); Assert.Empty(check.Diagnostics);
        Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, c => c.Name == "page-content").Status);
        Assert.Contains("0 skipped", Assert.Single(check.Checks, c => c.Name == "page-content").Detail, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("Bogus", false, QpdfStreamDecodeLevel.All, QpdfDiagnosticSeverity.Warning)]
    [InlineData("Bogus", true, QpdfStreamDecodeLevel.All, QpdfDiagnosticSeverity.Warning)]
    [InlineData("RunLengthDecode", false, QpdfStreamDecodeLevel.Generalized, QpdfDiagnosticSeverity.Info)]
    public async Task IncompleteContentInspectionReportsItsStage(string filter, bool mixed, QpdfStreamDecodeLevel level, QpdfDiagnosticSeverity severity)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(mixed ? "[4 0 R 5 0 R]" : "4 0 R", filter, mixed), QpdfInputOwnership.Transfer);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("input"), new() { DecodeLevel = level }, QpdfLimits.Default, CancellationToken.None);
        Assert.True(check.Passed); Assert.True(check.HasSkipped);
        Assert.Equal(QpdfCheckStatus.Skipped, Assert.Single(check.Checks, c => c.Name == "page-content").Status);
        Assert.Contains(check.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckSkipped && d.Severity == severity && d.Message.Contains("page-content", StringComparison.Ordinal));
        foreach (bool job in new[] { false, true })
        {
            QpdfExecutionResult result = await Inspect(host, job, level == QpdfStreamDecodeLevel.All ? "all" : "generalized");
            Assert.Equal(severity == QpdfDiagnosticSeverity.Warning ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            Assert.True(result.OutputBytes.HasValue); Assert.Equal("X"u8.ToArray(), result.OutputBytes.Value.ToArray());
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CorruptDecodingFailsTheContentStage()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("4 0 R", "FlateDecode", false), QpdfInputOwnership.Transfer);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.False(check.Passed); Assert.Equal(QpdfCheckStatus.Failed, Assert.Single(check.Checks, c => c.Name == "page-content").Status);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class Host(IQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothCancellationRoutesRetireOnlyContentCheckScratch(bool governorToken)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("4 0 R", "Bogus", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
        var run = (typeof(Lokad.Qpdf.Reading.DocumentCheck).GetMethod("CheckPageContent", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing live page-content check."))
            .CreateDelegate<Func<QpdfDocument, QpdfCheckOptions, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken, QpdfCheckOutcome>>();
        long before = governor.Snapshot().LiveMemoryBytes; cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => run(document, new(), QpdfLimits.Default, governor, new(governor),
            governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(71L, prior.ReservedBytes);
    }

    [Fact]
    public async Task SharedRefusalRefundsUnreturnedCoverageAndEscapedDetailsKeepTheirOwner()
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture("4 0 R", "Bogus", false), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget baseline = new(4194304L); QpdfCheckResult check;
        using (IMemoryScope caller = baseline.CreateScope())
        {
            check = await Qpdf.CheckAsync(new Host(storage, caller), QpdfPath.From("input"), CancellationToken.None);
            storage.Dispose(); Assert.True(check.HasSkipped); Assert.Contains("skipped", Assert.Single(check.Checks, c => c.Name == "page-content").Detail, StringComparison.Ordinal);
            Assert.InRange(baseline.ReservedBytes, 1L, 16384L);
        }
        Assert.Equal(0L, baseline.ReservedBytes);
        using InMemoryQpdfHost failures = new(); failures.AddInput(QpdfPath.From("input"), Fixture("4 0 R", "Bogus", false), QpdfInputOwnership.Transfer);
        for (int fail = 1; fail <= baseline.GrowthCalls; fail++)
        {
            RecordingMemoryBudget root = new(4194304L) { RefuseOnGrowth = fail }; using IMemoryScope caller = root.CreateScope();
            await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Qpdf.CheckAsync(new Host(failures, caller), QpdfPath.From("input"), CancellationToken.None).AsTask());
            Assert.Equal(0L, failures.OpenLeaseCount); Assert.Equal(0L, root.ReservedBytes);
        }
        GC.KeepAlive(check);
    }
}
