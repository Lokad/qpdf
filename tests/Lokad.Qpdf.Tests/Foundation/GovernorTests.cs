using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class GovernorTests
{
    private static ResourceGovernor CreateGovernor()
    {
        return new ResourceGovernor(QpdfLimits.Default, CancellationToken.None);
    }

    [Fact]
    public void ReserveAndReleaseTracksLiveAndPeak()
    {
        ResourceGovernor governor = CreateGovernor();
        governor.ReserveMemory(100L);
        governor.ReserveMemory(50L);
        QpdfExecutionCounters mid = governor.Snapshot();
        Assert.Equal(150L, mid.LiveMemoryBytes);
        Assert.Equal(150L, mid.PeakMemoryBytes);
        governor.ReleaseMemory(100L);
        QpdfExecutionCounters after = governor.Snapshot();
        Assert.Equal(50L, after.LiveMemoryBytes);
        Assert.Equal(150L, after.PeakMemoryBytes);
    }

    [Fact]
    public void ReservationBeyondBudgetFailsWithoutAllocating()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 128L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException ex = Assert.Throws<QpdfLimitException>(() => governor.ReserveMemory(129L));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), ex.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CumulativeChargesEnforceEachBudget()
    {
        ResourceGovernor governor = CreateGovernor();
        governor.ChargeWork(10L);
        governor.ChargeDecodedBytes(20L);
        governor.ChargeOutputBytes(30L);
        governor.NoteHostOperation();
        governor.AcquireHandle();
        QpdfExecutionCounters counters = governor.Snapshot();
        Assert.Equal(10L, counters.TotalWork);
        Assert.Equal(20L, counters.TotalDecodedBytes);
        Assert.Equal(30L, counters.TotalOutputBytes);
        Assert.Equal(1L, counters.HostOperations);
        Assert.Equal(1L, counters.OpenHandles);
        governor.ReleaseHandle();
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
    }

    [Fact]
    public void HandleBudgetIsEnforced()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOpenHandles = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        governor.AcquireHandle();
        Assert.Throws<QpdfLimitException>(() => governor.AcquireHandle());
    }

    [Fact]
    public void DiagnosticBudgetCountsAndBytes()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L, MaxDiagnosticBytes = 8L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.Add(QpdfDiagnosticCode.BudgetExceeded, QpdfDiagnosticSeverity.Error, "12345678");
        Assert.Single(bag.Entries);
        Assert.Throws<QpdfLimitException>(() => bag.Add(QpdfDiagnosticCode.BudgetExceeded, QpdfDiagnosticSeverity.Error, "x"));
    }

    [Fact]
    public void NegativeChargesAreRejected()
    {
        ResourceGovernor governor = CreateGovernor();
        Assert.Throws<ArgumentOutOfRangeException>(() => governor.ReserveMemory(-1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => governor.ChargeWork(-1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => governor.ChargeDecodedBytes(-1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => governor.ChargeOutputBytes(-1L));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        Assert.Throws<OperationCanceledException>(() => governor.CheckCancellation());
    }

    [Fact]
    public void PeriodicCancellationChecksHonorInterval()
    {
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 2L };
        using CancellationTokenSource source = new();
        ResourceGovernor governor = new(limits, CancellationToken.None);
        governor.NoteOperation();
        source.Cancel();
        ResourceGovernor cancelling = new(limits, source.Token);
        cancelling.NoteOperation();
        Assert.Throws<OperationCanceledException>(() => cancelling.NoteOperation());
    }

    [Fact]
    public void InvalidLimitsAreRejected()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 0L };
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceGovernor(limits, CancellationToken.None));
    }
}

