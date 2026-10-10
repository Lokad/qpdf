using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class DiagnosticOwnershipTests
{
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private static List<QpdfDiagnostic>? Stored(DiagnosticBag bag)
        => (List<QpdfDiagnostic>?)typeof(DiagnosticBag).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(bag);

    private static void Add(DiagnosticBag bag, ResourceGovernor governor, bool owned, int length)
    {
        if (owned)
        {
            CanonicalDisplay.AddDiagnostic(new PdfByteString(Enumerable.Repeat((byte)'A', length).ToArray(), false),
                QpdfLimits.Default, governor, null, bag);
        }
        else { bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, new string('A', length)); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] EmitAndDrop(ResourceGovernor governor, bool owned)
    {
        DiagnosticBag bag = new(governor);
        Add(bag, governor, owned, 4096);
        List<QpdfDiagnostic> stored = Stored(bag) ?? throw new InvalidOperationException("Expected accepted diagnostic storage.");
        QpdfDiagnostic diagnostic = stored[0];
        return [new(bag), new(stored), new(diagnostic), new(diagnostic.Message)];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexposedDiagnosticsReleaseTheirWholeStorageAfterCollection(bool owned)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L, MaxDiagnosticBytes = 1048576L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        for (int iteration = 0; iteration < 32; iteration++)
        {
            WeakReference[] dropped = EmitAndDrop(governor, owned);
            Assert.True(governor.Snapshot().LiveMemoryBytes >= 8192L);
            Collect();
            Assert.All(dropped, reference => Assert.False(reference.IsAlive));
            governor.ReserveMemory(0L);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(32L, governor.Snapshot().DiagnosticCount);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 8192L, limits.MaxMemoryBytes);
        GC.KeepAlive(governor);
    }

    private sealed class Alias(object? value, WeakReference[] references, int messageLength)
    {
        internal object? Value = value;
        internal readonly WeakReference[] References = references;
        internal readonly int MessageLength = messageLength;
    }

    private sealed class TextAlias(string value, WeakReference[] references)
    {
        internal string? Value = value;
        internal readonly WeakReference[] References = references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TextAlias CaptureBeforeTransfer(ResourceGovernor governor)
    {
        DiagnosticBag bag = new(governor);
        using GovernedText text = CanonicalDisplay.Render(new PdfByteString(new byte[4096], false), QpdfLimits.Default, governor);
        string message = text.Value;
        bag.AddOwned(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, text);
        List<QpdfDiagnostic> stored = Stored(bag) ?? throw new InvalidOperationException("Expected diagnostic storage.");
        Assert.Same(message, stored[0].Message);
        return new(message, [new(bag), new(stored), new(stored[0]), new(message)]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearTextAlias(TextAlias alias, ResourceGovernor governor)
    {
        Assert.False(alias.References[0].IsAlive);
        Assert.False(alias.References[1].IsAlive);
        Assert.False(alias.References[2].IsAlive);
        Assert.True(alias.References[3].IsAlive);
        string value = alias.Value ?? throw new InvalidOperationException("Expected a surviving text alias.");
        Assert.True(governor.Snapshot().LiveMemoryBytes >= value.Length * 2L);
        alias.Value = null;
    }

    [Fact]
    public void AMessageAliasCapturedBeforeOwnedTransferStaysFundedWithoutEntryExposure()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        TextAlias alias = CaptureBeforeTransfer(governor);
        Collect();
        governor.ReserveMemory(0L);
        ClearTextAlias(alias, governor);
        Collect();
        Assert.All(alias.References, reference => Assert.False(reference.IsAlive));
        governor.ReserveMemory(0L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(governor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Capture(ResourceGovernor governor, bool owned, string kind)
    {
        DiagnosticBag bag = new(governor);
        Add(bag, governor, owned, 4096);
        Add(bag, governor, owned, 40000);
        IReadOnlyList<QpdfDiagnostic> entries = bag.Entries;
        QpdfDiagnostic first = entries[0];
        QpdfDiagnostic sibling = entries[1];
        object? value = kind == "list" ? entries : kind == "message" ? first.Message : kind == "record" ? first : null;
        return new(value, [new(bag), new(entries), new(first), new(first.Message), new(sibling), new(sibling.Message)], first.Message.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyAndClearAlias(Alias alias, ResourceGovernor governor, string kind)
    {
        Assert.False(alias.References[0].IsAlive);
        Assert.False(alias.References[1].IsAlive);
        Assert.Equal(kind == "record", alias.References[2].IsAlive);
        Assert.Equal(kind != "none", alias.References[3].IsAlive);
        Assert.False(alias.References[4].IsAlive);
        Assert.False(alias.References[5].IsAlive);
        long live = governor.Snapshot().LiveMemoryBytes;
        if (kind == "none") { Assert.Equal(0L, live); }
        else
        {
            string message = alias.Value is QpdfDiagnostic diagnostic ? diagnostic.Message
                : alias.Value as string ?? throw new InvalidOperationException("Expected a surviving message.");
            Assert.Equal(alias.MessageLength, message.Length);
            Assert.Contains(new string('A', 4096), message, StringComparison.Ordinal);
            Assert.InRange(live, alias.MessageLength * 2L, 20000L);
        }
        alias.Value = null;
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(false, "record")]
    [InlineData(false, "message")]
    [InlineData(true, "none")]
    [InlineData(true, "record")]
    [InlineData(true, "message")]
    public void EscapingRecordsAndMessagesKeepOnlyTheirActualStorageFunded(bool owned, string kind)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Alias alias = Capture(governor, owned, kind);
        Collect();
        governor.ReserveMemory(0L);
        VerifyAndClearAlias(alias, governor, kind);
        Collect();
        Assert.All(alias.References, reference => Assert.False(reference.IsAlive));
        governor.ReserveMemory(0L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(governor);
    }

    [Fact]
    public void OrdinaryRecordsAndMessagesCannotExceedAFiniteMemoryCeiling()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L, MaxDiagnostics = 2048L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        string prepared = new('A', 16);
        int accepted = 0;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
        {
            for (; accepted < 1000; accepted++) { bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, prepared); }
        });
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.InRange(accepted, 1, 999);
        Assert.Equal(accepted, Stored(bag)?.Count);
        Assert.Equal(accepted, governor.Snapshot().DiagnosticCount);
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, accepted * 32L, limits.MaxMemoryBytes);
        GC.KeepAlive(bag);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(64L)]
    [InlineData(1024L)]
    public void FailedFirstAdmissionReleasesItsNewContainerAndAllowsNoRecord(long memory)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = memory };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, new string('A', 4096)));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        Assert.Empty(bag.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddingAfterEntryExposureFundsFutureIndependentAliases(bool owned)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WeakReference[] dropped = AddThroughEscapedList(governor, owned);
        Collect();
        Assert.All(dropped, reference => Assert.False(reference.IsAlive));
        governor.ReserveMemory(0L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(2L, governor.Snapshot().DiagnosticCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] AddThroughEscapedList(ResourceGovernor governor, bool owned)
    {
        DiagnosticBag bag = new(governor);
        Add(bag, governor, owned, 4096);
        IReadOnlyList<QpdfDiagnostic> entries = bag.Entries;
        Add(bag, governor, owned, 4096);
        Assert.Equal(2, entries.Count);
        QpdfDiagnostic second = entries[1];
        return [new(bag), new(entries), new(second), new(second.Message)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyAndClearList(Alias alias, ResourceGovernor governor)
    {
        Assert.False(alias.References[0].IsAlive);
        Assert.All(alias.References.Skip(1), reference => Assert.True(reference.IsAlive));
        IReadOnlyList<QpdfDiagnostic> entries = Assert.IsAssignableFrom<IReadOnlyList<QpdfDiagnostic>>(alias.Value);
        Assert.Equal(2, entries.Count);
        Assert.Equal(alias.MessageLength, entries[0].Message.Length);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= entries.Sum(entry => entry.Message.Length * 2L));
        alias.Value = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReturnedListKeepsItsStorageAndAllMessagesFunded(bool owned)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Alias alias = Capture(governor, owned, "list");
        Collect();
        governor.ReserveMemory(0L);
        VerifyAndClearList(alias, governor);
        Collect();
        Assert.All(alias.References, reference => Assert.False(reference.IsAlive));
        governor.ReserveMemory(0L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EqualMessagesShareOwnedStorageWithinOneJobOnly()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag first = new(governor);
        DiagnosticBag second = new(governor);
        string prepared = new('A', 4096);
        first.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, prepared);
        second.Add(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Info, new string('A', 4096));
        Assert.NotSame(prepared, first.Entries[0].Message);
        Assert.Same(first.Entries[0].Message, second.Entries[0].Message);
        Assert.Equal(2L, governor.Snapshot().DiagnosticCount);
        ResourceGovernor otherGovernor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag other = new(otherGovernor);
        other.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, prepared);
        Assert.Equal(first.Entries[0].Message, other.Entries[0].Message);
        Assert.NotSame(first.Entries[0].Message, other.Entries[0].Message);
    }

    [Fact]
    public void CollidingMessageHashesStillCompareTheEntireText()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        foreach (string message in new[] { " a", "!@", " a", "!@" })
        {
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, message);
        }
        Assert.Equal(new[] { " a", "!@", " a", "!@" }, bag.Entries.Select(entry => entry.Message));
        Assert.Same(bag.Entries[0].Message, bag.Entries[2].Message);
        Assert.Same(bag.Entries[1].Message, bag.Entries[3].Message);
        Assert.NotSame(bag.Entries[0].Message, bag.Entries[1].Message);
    }

    [Fact]
    public void SharedMessagesStillChargeEveryDiagnosticByte()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        string prepared = new('A', 2048);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, prepared);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, prepared);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, prepared));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), failure.LimitName);
        Assert.InRange(failure.Requested, limits.MaxDiagnosticBytes + 1L, 6144L);
        Assert.Equal(2L, governor.Snapshot().DiagnosticCount);
        Assert.Equal(2, bag.Entries.Count);
    }

    [Fact]
    public void ACancelledEmptyMessageCannotAddARecordAndPriorEntriesRemainReadable()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        DiagnosticBag bag = new(governor);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        IReadOnlyList<QpdfDiagnostic> entries = bag.Entries;
        QpdfExecutionCounters before = governor.Snapshot();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, string.Empty));
        Assert.Same(entries, bag.Entries);
        Assert.Equal(before, governor.Snapshot());
        Assert.Equal("prior", Assert.Single(entries).Message);
    }

    [Fact]
    public void ExhaustedWorkDoesNotPreventReadingPriorDiagnostics()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        long retirement = Assert.IsType<long>(typeof(ResourceGovernor).GetField("_reservedResultWork",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(governor));
        Assert.True(retirement > 0L);
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork - retirement);
        Assert.Throws<QpdfLimitException>(() => governor.ChargeWork(1L));
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "next"));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        QpdfExecutionCounters beforeRead = governor.Snapshot();
        Assert.Equal("prior", Assert.Single(bag.Entries).Message);
        Assert.Same(bag.Entries, bag.Entries);
        Assert.Equal(beforeRead, governor.Snapshot());
    }

    [Fact]
    public void FailedListGrowthPreservesEarlierRecordMessageAndAdmission()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        IReadOnlyList<QpdfDiagnostic> entries = bag.Entries;
        QpdfDiagnostic prior = Assert.Single(entries);
        long held = limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 1L;
        governor.ReserveMemory(held);
        long admitted = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "next"));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Same(entries, bag.Entries);
        Assert.Same(prior, Assert.Single(entries));
        Assert.Equal("prior", prior.Message);
        Assert.Equal(admitted, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        governor.ReleaseMemory(held);
    }

    [Fact]
    public void Utf8QuotaCountsASurrogatePairAcrossWorkChunks()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 258L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        string prepared = new string('A', 255) + "\ud800\udc00";
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, prepared));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), failure.LimitName);
        Assert.Equal(259L, failure.Requested);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Empty(bag.Entries);
    }

    [Fact]
    public void TheWeakMessageIndexRetiresWhileAnUnrelatedValueKeepsTheLedgerAlive()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L, MaxDiagnosticBytes = 1048576L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfByteString value = new(new byte[1], false);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            memory.Reserve(80L);
            memory.Retain(value);
        }
        long baseline = governor.Snapshot().LiveMemoryBytes;
        for (int iteration = 0; iteration < 32; iteration++)
        {
            WeakReference[] dropped = EmitAndDrop(governor, false);
            Collect();
            Assert.All(dropped, reference => Assert.False(reference.IsAlive));
            governor.ReserveMemory(0L);
            Assert.InRange(governor.Snapshot().LiveMemoryBytes, baseline, baseline + 128L);
        }
        Assert.Equal(new byte[1], value.Value);
        GC.KeepAlive(value);
    }
}
