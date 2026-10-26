using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class FormNestingCoverageTests
{
    internal static byte[] Fixture(int count, int badDepth, bool cycle)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null)];
        for (int index = 0; index < count; index++)
        {
            int number = index + 4;
            string parent = index == 0 ? "" : $" /Parent {(index == badDepth ? 2 : number - 1)} 0 R";
            string kids = index + 1 < count ? $" /Kids [{number + 1} 0 R]" : cycle ? " /Kids [4 0 R]" : "";
            objects.Add((number, $"<< /T (F{index}) /FT /Tx{parent}{kids} >>", null));
        }
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(9, 8L, true)]
    [InlineData(10, 8L, false)]
    [InlineData(64, 256L, true)]
    [InlineData(65, 32L, false)]
    [InlineData(500, 499L, true)]
    [InlineData(501, 499L, false)]
    public async Task FormTraversalUsesTheConfiguredCeilingAndReportsExhaustion(int count, long maximum, bool passed)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(count, -1, false), QpdfInputOwnership.Transfer);
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = maximum };
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), new(), limits, CancellationToken.None);
        QpdfCheckOutcome form = Assert.Single(result.Checks, c => c.Name == "forms");
        Assert.Equal(passed, result.Passed); Assert.Equal(passed ? QpdfCheckStatus.Passed : QpdfCheckStatus.Failed, form.Status);
        if (passed) { Assert.StartsWith(count + " field nodes traversed", form.Detail, StringComparison.Ordinal); }
        else { Assert.Contains(nameof(QpdfLimits.MaxNestingDepth), form.Detail, StringComparison.Ordinal); }
        Assert.False(result.HasSkipped); Assert.Equal(0L, host.OpenLeaseCount);
        if (!passed)
        {
            QpdfExecutionOptions options = new(limits);
            foreach (bool job in new[] { false, true })
            {
                QpdfExecutionResult inspection = job
                    ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","check":"","showNpages":""}""", options, CancellationToken.None)
                    : await Qpdf.ExecuteAsync(host, new(["input", "--check", "--show-npages"]), options, CancellationToken.None);
                Assert.Equal(QpdfExitCode.Error, inspection.ExitCode);
                Assert.Contains(inspection.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed && d.Message.Contains(nameof(QpdfLimits.MaxNestingDepth), StringComparison.Ordinal));
                Assert.DoesNotContain(inspection.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount);
                Assert.Null(inspection.OutputBytes); Assert.Equal(0L, host.OpenLeaseCount);
            }
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(40)]
    public async Task DeepParentDefectsKeepTheWarnOnlyPolicy(int depth)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(64, depth, false), QpdfInputOwnership.Transfer);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.True(check.Passed); Assert.False(check.HasSkipped);
        Assert.StartsWith("64 field nodes traversed", Assert.Single(check.Checks, c => c.Name == "forms").Detail, StringComparison.Ordinal);
        Assert.Single(check.Diagnostics, d => d.Code == QpdfDiagnosticCode.FormStructureWarning);
        foreach (bool job in new[] { false, true })
        {
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","check":""}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--check"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode); Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.FormStructureWarning);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task DeepCyclesStillStopAtTheVisitedGuard()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(64, -1, true), QpdfInputOwnership.Transfer);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), deadline.Token);
        Assert.True(result.Passed); Assert.StartsWith("64 field nodes traversed", Assert.Single(result.Checks, c => c.Name == "forms").Detail, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class Host(IQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    [Fact]
    public async Task SharedRefusalDoesNotRetainUnreturnedDeepTraversal()
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture(64, -1, false), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget baseline = new(4194304L);
        using (IMemoryScope caller = baseline.CreateScope())
        { Assert.True((await Qpdf.CheckAsync(new Host(storage, caller), QpdfPath.From("input"), CancellationToken.None)).Passed); }
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int fail = 1; fail <= baseline.GrowthCalls; fail++)
        {
            RecordingMemoryBudget root = new(4194304L) { RefuseOnGrowth = fail }; using IMemoryScope caller = root.CreateScope();
            await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Qpdf.CheckAsync(new Host(storage, caller), QpdfPath.From("input"), CancellationToken.None).AsTask());
            Assert.InRange(root.ReservedBytes, 0L, 2048L); Assert.Equal(0L, storage.OpenLeaseCount);
            caller.Dispose(); Assert.Equal(0L, root.ReservedBytes);
        }
    }
}
