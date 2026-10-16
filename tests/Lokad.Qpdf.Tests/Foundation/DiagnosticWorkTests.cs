using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class DiagnosticWorkTests
{
    private static void SpendUntilRejected(ResourceGovernor governor, long ceiling)
    {
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
        {
            for (long index = 0; index <= ceiling; index++) { governor.ChargeWork(1L); }
        });
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.True(governor.Snapshot().TotalWork <= ceiling);
    }

    [Fact]
    public void LaterWorkCannotConsumeAnAcceptedWarningsPresentationCapacity()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        SpendUntilRejected(governor, limits.MaxWork);
        long before = governor.Snapshot().TotalWork;
        Assert.True(before < limits.MaxWork);
        stage.Flush();
        Assert.Equal("prior", Assert.Single(bag.Entries).Message);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.InRange(governor.Snapshot().TotalWork, before + 1L, limits.MaxWork);
        governor.ChargeWork(governor.RemainingWorkUnits);
        Assert.Throws<QpdfLimitException>(() => governor.ChargeWork(1L));
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
        Assert.Equal("prior", Assert.Single(bag.Entries).Message);
    }

    [Fact]
    public void AcceptanceRejectsUnfundedFutureWorkBeforeAddingARecord()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 20L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using (DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage."))
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior"));
            Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
            Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
            Assert.Empty(bag.Entries);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().TotalWork, 1L, limits.MaxWork - 1L);
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork);
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
    }

    [Fact]
    public void RejectedFutureWorkPreservesEarlierStagedTextAndItsPromise()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 500L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        long beforeMemory = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, new string('B', 200)));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.Equal(beforeMemory, governor.Snapshot().LiveMemoryBytes);
        SpendUntilRejected(governor, limits.MaxWork);
        stage.Flush();
        Assert.Equal("prior", Assert.Single(bag.Entries).Message);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void EmptyMessageBurstsChargeMetadataAndKeepAcceptedEntriesExportable()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 500L, MaxDiagnostics = 2048L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        int accepted = 0;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
        {
            for (; accepted < 1000; accepted++) { bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, string.Empty); }
        });
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.InRange(accepted, 1, 999);
        Assert.Equal(accepted, governor.Snapshot().DiagnosticCount);
        Assert.True(governor.Snapshot().TotalWork > 0L);
        stage.Flush();
        Assert.Equal(accepted, bag.Entries.Count);
        Assert.All(bag.Entries, entry => Assert.Equal(string.Empty, entry.Message));
        Assert.InRange(governor.Snapshot().TotalWork, 1L, limits.MaxWork);
    }

    [Fact]
    public void UnusedCapacityIsReturnedWithoutInventingExecutedWork()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        long observed;
        using (DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage."))
        {
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
            observed = governor.Snapshot().TotalWork;
            Assert.InRange(observed, 1L, 30L);
            Assert.Throws<QpdfLimitException>(() => governor.ChargeWork(limits.MaxWork - observed));
            Assert.Equal(observed, governor.Snapshot().TotalWork);
        }
        Assert.Equal(observed, governor.Snapshot().TotalWork);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        governor.ChargeWork(limits.MaxWork - observed);
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
    }

    [Fact]
    public void CancellationReturnsUnusedCapacityAndPreservesObservedWork()
    {
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1000L };
        ResourceGovernor governor = new(limits, cancellation.Token);
        DiagnosticBag bag = new(governor);
        using (DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage."))
        {
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(stage.Flush);
        }
        long observed = governor.Snapshot().TotalWork;
        Assert.True(observed > 0L);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        // A single abstract charge is below the configured check interval;
        // the export itself checked the canceled token explicitly above.
        governor.ChargeWork(limits.MaxWork - observed);
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
    }

    [Fact]
    public void HashCollisionsAndEqualTextRemainExportableAtTheWorkBoundary()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 10000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        string[] messages = Enumerable.Range(0, 12).Select(index => index % 2 == 0 ? " a" : "!@").ToArray();
        foreach (string message in messages) { bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, message); }
        SpendUntilRejected(governor, limits.MaxWork);
        stage.Flush();
        Assert.Equal(messages, bag.Entries.Select(entry => entry.Message));
        Assert.Same(bag.Entries[0].Message, bag.Entries[2].Message);
        Assert.Same(bag.Entries[1].Message, bag.Entries[3].Message);
        Assert.NotSame(bag.Entries[0].Message, bag.Entries[1].Message);
    }

    private static PdfByteString RetainOther(ResourceGovernor governor)
    {
        PdfByteString value = new(new byte[1], false);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(80L);
        memory.Retain(value);
        return value;
    }

    [Fact]
    public void LaterUnrelatedCarriersCannotStealTheGrowingExportPromise()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 10000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        List<PdfByteString> values = new();
        for (int index = 0; index < 32; index++) { values.Add(RetainOther(governor)); }
        SpendUntilRejected(governor, limits.MaxWork);
        stage.Flush();
        Assert.Equal("prior", Assert.Single(bag.Entries).Message);
        Assert.All(values, value => Assert.Equal(new byte[1], value.Value));
        GC.KeepAlive(values);
    }

    [Fact]
    public void LaterOrdinaryMemoGrowthCannotInvalidateTheEarlierPromise()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 10000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        DiagnosticBag other = new(governor);
        for (int index = 0; index < 32; index++) { other.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, index.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        SpendUntilRejected(governor, limits.MaxWork);
        stage.Flush();
        Assert.Equal("prior", Assert.Single(bag.Entries).Message);
        Assert.Equal(32, other.Entries.Count);
        Assert.Equal(33L, governor.Snapshot().DiagnosticCount);
        GC.KeepAlive(other);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] DropOthers(ResourceGovernor governor)
        => Enumerable.Range(0, 96).Select(_ => new WeakReference(RetainOther(governor))).ToArray();

    [Fact]
    public void PendingRetirementMaintenanceFitsTheReservedExportWork()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 20000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        WeakReference[] dropped = DropOthers(governor);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(dropped, weak => Assert.False(weak.IsAlive));
        SpendUntilRejected(governor, limits.MaxWork);
        stage.Flush();
        Assert.Equal("prior", Assert.Single(bag.Entries).Message);
        Assert.InRange(governor.Snapshot().TotalWork, 1L, limits.MaxWork);
    }
}
