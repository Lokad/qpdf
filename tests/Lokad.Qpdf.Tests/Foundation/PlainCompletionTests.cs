using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class PlainCompletionTests
{
    [Theory]
    [InlineData(QpdfDiagnosticSeverity.Info, false, QpdfExitCode.Success)]
    [InlineData(QpdfDiagnosticSeverity.Warning, false, QpdfExitCode.Warning)]
    [InlineData(QpdfDiagnosticSeverity.Warning, true, QpdfExitCode.Success)]
    [InlineData(QpdfDiagnosticSeverity.Error, false, QpdfExitCode.Error)]
    [InlineData(QpdfDiagnosticSeverity.Error, true, QpdfExitCode.Error)]
    public void CompletionBorrowsAdmittedDiagnosticsAfterPreparationEvenWhenCanceledAndFull(
        QpdfDiagnosticSeverity severity, bool warningExitZero, QpdfExitCode exit)
    {
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, cancellation.Token);
        DiagnosticBag bag = new(governor);
        using ExecutionPresentation? presentation = ExecutionPresentation.Create(governor, bag,
            false, false, false, false, warningExitZero, cancellation.Token);
        Assert.NotNull(presentation);
        bag.Add(QpdfDiagnosticCode.CheckFailed, severity, "accepted before publication");
        IReadOnlyList<QpdfDiagnostic> admitted = bag.Entries;
        governor.PrepareExecutionPresentation();
        governor.ReserveMemory(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes);
        QpdfExecutionCounters before = governor.Snapshot();
        cancellation.Cancel();

        PresentedDiagnostics completed = governor.CompleteExecutionPresentation();

        Assert.Same(admitted, completed.Diagnostics);
        Assert.Equal(exit, completed.ExitCode);
        Assert.Equal(before, governor.Snapshot());
        Assert.Equal("accepted before publication", Assert.Single(completed.Diagnostics).Message);
    }

    [Fact]
    public void EmptyCompletionReusesTheEmptyDiagnosticStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using ExecutionPresentation? presentation = ExecutionPresentation.Create(governor, bag,
            false, false, false, false, false, CancellationToken.None);
        Assert.NotNull(presentation);
        governor.PrepareExecutionPresentation();
        PresentedDiagnostics completed = governor.CompleteExecutionPresentation();
        Assert.Same(bag.Entries, completed.Diagnostics);
        Assert.Empty(completed.Diagnostics);
        Assert.Equal(QpdfExitCode.Success, completed.ExitCode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IReadOnlyList<QpdfDiagnostic> Diagnostics, WeakReference[] Owners) CompleteAndDropOwners()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using ExecutionPresentation? presentation = ExecutionPresentation.Create(governor, bag,
            false, false, false, false, false, CancellationToken.None);
        Assert.NotNull(presentation);
        bag.Add(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning, "surviving warning");
        PresentedDiagnostics completed = governor.CompleteExecutionPresentation();
        presentation.Dispose();
        governor.CompleteStorage();
        return (completed.Diagnostics, [new(governor), new(bag), new(presentation)]);
    }

    [Fact]
    public void EscapedDiagnosticsOutliveTheirOwnersWithoutRetainingTheJob()
    {
        (IReadOnlyList<QpdfDiagnostic> diagnostics, WeakReference[] owners) = CompleteAndDropOwners();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(owners, owner => Assert.False(owner.IsAlive));
        Assert.Equal("surviving warning", Assert.Single(diagnostics).Message);
        GC.KeepAlive(diagnostics);
    }
}
