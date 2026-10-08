using System.Globalization;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class CheckOutcomeFormattingTests
{
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RejectLargeDetail(ResourceGovernor governor, string prepared)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            CheckOutcome.Create(governor, "test", QpdfCheckStatus.Failed, ["prefix ", prepared, " suffix"]));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void LargeDetailRejectsAdmissionBeforeAllocatingItsFinalString()
    {
        string prepared = new('A', 10000);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2048L };
        _ = RejectLargeDetail(new(limits, CancellationToken.None), prepared);
        ResourceGovernor governor = new(limits, CancellationToken.None);
        long allocation = RejectLargeDetail(governor, prepared);
        Assert.InRange(allocation, 0L, 4096L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Fact]
    public void DetailsPreserveRawUtf16WithoutConsumingAnExhaustedDiagnosticQuota()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L, MaxDiagnosticBytes = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "X");
        QpdfCheckOutcome outcome = CheckOutcome.Create(governor, "test", QpdfCheckStatus.Passed,
            ["\ud800", "\udc00\u00e9\udfff", (string?)null, DiagnosticPart.Invariant(long.MinValue)]);
        Assert.Equal("\ud800\udc00\u00e9\udfff-9223372036854775808", outcome.Detail);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.Equal("X", Assert.Single(bag.Entries).Message);
    }

    [Fact]
    public void BuilderFragmentsPreserveCustomSignsAndRawNameEscaping()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "\u2212" + new string('S', 1024);
        CultureInfo.CurrentCulture = culture;
        try
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using DisplayBuffer buffer = DisplayBuffer.Create(governor, false);
            PdfName name = new([0, 32, 35, 65, 255]);
            buffer.AppendParts(["number=", long.MinValue, "; name=", DiagnosticPart.Name(name), "; invariant=", DiagnosticPart.Invariant(long.MinValue)]);
            QpdfCheckOutcome outcome;
            using (GovernedText detail = buffer.Finish())
            {
                outcome = CheckOutcome.Create(governor, "test", QpdfCheckStatus.Passed, detail);
            }
            Assert.Equal($"number={long.MinValue}; name=#00#20#23A#FF; invariant=-9223372036854775808", outcome.Detail);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void FailedPromotionPreservesTheOwnedDetailAndEarlierOutcomeForRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfCheckOutcome prior = CheckOutcome.Create(governor, "prior", QpdfCheckStatus.Passed, ["prior ", 7]);
        DiagnosticPart[] parts = ["later ", new string('B', 4000)];
        using GovernedText detail = GovernedText.Create(governor, parts, DiagnosticMessage.Measure(governor, parts));
        string owned = detail.Value;
        long filler = limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 40L;
        governor.ReserveMemory(filler);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => CheckOutcome.Create(governor, "later", QpdfCheckStatus.Failed, detail));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(owned, detail.Value);
        Assert.Equal("prior 7", prior.Detail);
        governor.ReleaseMemory(filler);
        QpdfCheckOutcome accepted = CheckOutcome.Create(governor, "later", QpdfCheckStatus.Failed, detail);
        Assert.Same(owned, accepted.Detail);
        detail.Dispose();
        Assert.Equal("prior 7", prior.Detail);
        Assert.Equal("later " + new string('B', 4000), accepted.Detail);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void WorkFailureDuringFinalCopyReleasesTemporaryStringAdmission()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        string prepared = new('A', 1000);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            CheckOutcome.Create(governor, "test", QpdfCheckStatus.Passed, [prepared]));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().TotalWork, 1000L, limits.MaxWork);
        Assert.True(governor.Snapshot().PeakMemoryBytes >= 2L * prepared.Length);
    }

    [Fact]
    public void CancellationAfterBuildingDetailReleasesPrivateChunksAndKeepsPriorOutcome()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        QpdfCheckOutcome prior = CheckOutcome.Create(governor, "prior", QpdfCheckStatus.Passed, ["prior ", 7]);
        long before = governor.Snapshot().LiveMemoryBytes;
        long workBefore = governor.Snapshot().TotalWork;
        using (DisplayBuffer buffer = DisplayBuffer.Create(governor, false))
        {
            buffer.AppendParts([new string('A', 4000)]);
            Assert.True(governor.Snapshot().TotalWork > workBefore);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => buffer.Finish());
        }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal("prior 7", prior.Detail);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LiteralRecord(ResourceGovernor governor)
    {
        QpdfCheckOutcome outcome = CheckOutcome.Literal(governor, "trailer", QpdfCheckStatus.Passed, "Catalog root is reachable and input is unencrypted.");
        return new(outcome);
    }

    [Fact]
    public void BorrowedLiteralDoesNotAnchorAnOtherwiseDeadLedger()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WeakReference record = LiteralRecord(governor);
        Collect();
        Assert.False(record.IsAlive);
        governor.ReserveMemory(0L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(governor);
    }
}
