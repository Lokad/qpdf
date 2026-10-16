using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class DiagnosticStageTests
{
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    [Fact]
    public void DeferredWarningsFitBesideInputAndRetainEveryEntryAfterItCloses()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        governor.ReserveMemory(84024L);
        byte[]? input = new byte[84000];
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        string[] messages = Enumerable.Range(0, 100).Select(index => new string('A', 78) + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        foreach (string message in messages) { bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, message); }
        Assert.Equal(100L, governor.Snapshot().DiagnosticCount);
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, 84024L + messages.Sum(message => message.Length), limits.MaxMemoryBytes);
        GC.KeepAlive(input);
        input = null;
        governor.ReleaseMemory(84024L);
        stage.Flush();
        Assert.Equal(messages, bag.Entries.Select(entry => entry.Message));
        Assert.All(bag.Entries, entry => { Assert.Equal(QpdfDiagnosticCode.RepairedObject, entry.Code); Assert.Equal(QpdfDiagnosticSeverity.Warning, entry.Severity); });
        Assert.Equal(100L, governor.Snapshot().DiagnosticCount);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 84024L, limits.MaxMemoryBytes);
        Assert.NotSame(messages[0], bag.Entries[0].Message);
        GC.KeepAlive(bag);
    }

    [Fact]
    public void StagingPreservesRawUtf16AndPairsAcrossChunkBoundaries()
    {
        string prepared = new string('A', 511) + "\ud800\udc00\ud800X\udfff\u00e9\u20ac" + new string('B', 1024);
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = Encoding.UTF8.GetByteCount(prepared), MaxDiagnostics = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning, prepared);
        stage.Flush();
        QpdfDiagnostic diagnostic = Assert.Single(bag.Entries);
        Assert.Equal(prepared, diagnostic.Message);
        Assert.NotSame(prepared, diagnostic.Message);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.Equal(QpdfDiagnosticCode.DuplicateKey, diagnostic.Code);
        Assert.Equal(QpdfDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void StagedQuotaFailurePreservesEarlierMessageAndDoesNotCountItTwice()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 10L, MaxDiagnostics = 2L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "123456"));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), failure.LimitName);
        Assert.Equal(11L, failure.Requested);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        stage.Flush();
        Assert.Equal("prior", Assert.Single(bag.Entries).Message);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void FailedStagedGrowthRollsBackNewChunksAndPreservesPriorText()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        long held = limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 2000L;
        governor.ReserveMemory(held);
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, new string('B', 8192)));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        governor.ReleaseMemory(held);
        stage.Flush();
        Assert.Equal("prior", Assert.Single(bag.Entries).Message);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "later");
        Assert.Equal(new[] { "prior", "later" }, bag.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void FailedPromotionCanRetryWithoutRecountingAcceptedWarnings()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, new string('A', 2048));
        long held = limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 1L;
        governor.ReserveMemory(held);
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(stage.Flush);
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        governor.ReleaseMemory(held);
        stage.Flush();
        Assert.Equal(new string('A', 2048), Assert.Single(bag.Entries).Message);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void OwnedTransferKeepsItsExactAliasAndStagedOrder()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "first");
        using GovernedText text = CanonicalDisplay.Render(new PdfByteString(new byte[1024], false), QpdfLimits.Default, governor);
        string alias = text.Value;
        bag.AddOwned(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, text);
        stage.Flush();
        Assert.Equal("first", bag.Entries[0].Message);
        Assert.Same(alias, bag.Entries[1].Message);
        Assert.Equal(2L, governor.Snapshot().DiagnosticCount);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= alias.Length * 2L);
    }

    [Fact]
    public void ReadingStagedEntriesPreservesLiveListAppendSemantics()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "first");
        IReadOnlyList<QpdfDiagnostic> exposed = bag.Entries;
        Assert.Null(bag.BeginStaging());
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "second");
        Assert.Same(exposed, bag.Entries);
        Assert.Equal(new[] { "first", "second" }, exposed.Select(entry => entry.Message));
        Assert.Equal(2L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void EqualStagedTextSharesOneOwnedStringAndNeverBorrowsCallerText()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        string caller = new('A', 4096);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, caller);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, caller);
        stage.Flush();
        Assert.Same(bag.Entries[0].Message, bag.Entries[1].Message);
        Assert.NotSame(caller, bag.Entries[0].Message);
        Assert.Equal(2L, governor.Snapshot().DiagnosticCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Emit(ResourceGovernor governor)
    {
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, new string('A', 4096));
        stage.Flush();
        IReadOnlyList<QpdfDiagnostic> entries = bag.Entries;
        QpdfDiagnostic entry = Assert.Single(entries);
        return [new(bag), new(stage), new(entries), new(entry), new(entry.Message)];
    }

    [Fact]
    public void RepeatedDiagnosticStagesRefundWhenTheirCallerScopesClose()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L, MaxDiagnosticBytes = 1048576L };
        RecordingMemoryBudget root = new(limits.MaxMemoryBytes);
        List<WeakReference[]> references = [];
        long diagnostics = 0L;
        for (int iteration = 0; iteration < 32; iteration++)
        {
            using IMemoryScope caller = root.CreateScope();
            using InMemoryQpdfHost host = new(limits, null, caller);
            ResourceGovernor governor = new(limits, CancellationToken.None, host);
            references.Add(Emit(governor));
            governor.CompletePrivateStorage();
            diagnostics += governor.Snapshot().DiagnosticCount;
            Assert.True(root.ReservedBytes > 0L);
            caller.Dispose();
            Assert.Equal(0L, root.ReservedBytes);
        }
        Collect();
        Assert.All(references.SelectMany(group => group), reference => Assert.False(reference.IsAlive));
        Assert.Equal(32L, diagnostics);
    }

    [Fact]
    public void CancelledExportReleasesPrivateStorageAndPreservesObservedCounters()
    {
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, cancellation.Token);
        DiagnosticBag bag = new(governor);
        using (DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage."))
        {
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, new string('A', 8192));
            Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
            Assert.True(governor.Snapshot().TotalWork > 0L);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(stage.Flush);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 8192L, limits.MaxMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 0L);
    }
}
