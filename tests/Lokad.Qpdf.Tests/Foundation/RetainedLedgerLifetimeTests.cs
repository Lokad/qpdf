using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class RetainedLedgerLifetimeTests
{
    private sealed class Alias(object value, WeakReference[] metadata, long minimumBytes)
    {
        internal object? Value = value;
        internal readonly WeakReference[] Metadata = metadata;
        internal readonly long MinimumBytes = minimumBytes;
    }

    private static object Field(object value, string name)
        => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing retained ownership field: " + name);

    private static WeakReference[] Metadata(ResourceGovernor governor)
    {
        object ledger = Field(governor, "_valueOwners"), segment = Field(ledger, "_segments");
        return [new(governor), new(ledger), new(Field(ledger, "_storage")), new(Field(ledger, "_heads")),
            new(Field(ledger, "_slots")), new(segment), new(Field(segment, "Values"))];
    }

    private static void Collect(ResourceGovernor? governor)
    { for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); governor?.ReserveMemory(0L); } }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Capture(ResourceGovernor governor, string kind)
    {
        object value; long minimum;
        if (kind is "string" or "box")
        {
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            minimum = kind == "string" ? GovernedText.StringBytes(1024) : 40L;
            memory.Reserve(minimum);
            value = kind == "string" ? new string('W', 1024) : (object)new FilterStage(FlateFilter.FilterName, null, null);
            memory.RetainAllocation(value, minimum);
        }
        else if (kind == "table")
        {
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            PdfNameTable<QpdfObject> table = PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
            table.Assign(memory, governor, new PdfName([0, 255, 35]), PdfNull.Instance, CancellationToken.None);
            table.Freeze(memory, governor, CancellationToken.None);
            minimum = table.StorageBytes; memory.Retain(table, minimum, Array.Empty<QpdfObject>()); value = table;
        }
        else
        {
            PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
            QpdfObject parsed = parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(kind == "byte-buffer" ? "(ABC)" : kind == "array" ? "[null]" : "00000123")), 0);
            value = kind == "integer-text" ? ((PdfInteger)parsed).Text : kind == "byte-buffer" ? ((PdfByteString)parsed).AllocationCarrier : parsed;
            minimum = kind == "integer-text" ? 16L : kind == "byte-buffer" ? 3L : 24L;
        }
        Assert.True(governor.Snapshot().LiveMemoryBytes >= minimum);
        return new(value, Metadata(governor), minimum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Complete(string kind) => Capture(new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None), kind);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Verify(Alias alias, string kind)
    {
        object value = alias.Value ?? throw new InvalidOperationException("Retained alias is missing.");
        switch (kind)
        {
            case "string": Assert.Equal(new string('W', 1024), Assert.IsType<string>(value)); break;
            case "box": Assert.Equal(FlateFilter.FilterName, Assert.IsType<FilterStage>(value).Name); break;
            case "integer": Assert.Equal(123L, Assert.IsType<PdfInteger>(value).Value); break;
            case "integer-text": Assert.Equal("00000123", Assert.IsType<string>(value)); break;
            case "byte-buffer": Assert.Equal(new byte[] { 65, 66, 67 }, Assert.IsType<byte[]>(value)); break;
            case "array": Assert.Same(PdfNull.Instance, Assert.Single(Assert.IsType<PdfArray>(value).Items)); break;
            case "table": Assert.Same(PdfNull.Instance, Assert.IsType<PdfNameTable<QpdfObject>>(value)[new PdfName([0, 255, 35])]); break;
            default: throw new InvalidOperationException("Unknown alias fixture.");
        }
    }

    [Theory]
    [InlineData("string")]
    [InlineData("box")]
    [InlineData("integer")]
    [InlineData("integer-text")]
    [InlineData("byte-buffer")]
    [InlineData("array")]
    [InlineData("table")]
    public void EscapingCarriersDoNotRetainCompletedLedgerMetadata(string kind)
    {
        Alias alias = Complete(kind); Collect(null);
        Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive)); Verify(alias, kind); GC.KeepAlive(alias);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("box")]
    [InlineData("integer")]
    [InlineData("integer-text")]
    [InlineData("byte-buffer")]
    [InlineData("array")]
    [InlineData("table")]
    public void ActiveGovernorKeepsAdmissionUntilTheLastCarrierRetires(string kind)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None);
        Alias alias = Capture(governor, kind); Collect(governor);
        Assert.All(alias.Metadata, reference => Assert.True(reference.IsAlive));
        Assert.True(governor.Snapshot().LiveMemoryBytes >= alias.MinimumBytes); Verify(alias, kind);
        alias.Value = null; Collect(governor);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.All(alias.Metadata.Skip(1), reference => Assert.False(reference.IsAlive)); GC.KeepAlive(governor);
    }

    [Fact]
    public void FailedLedgerAdmissionPreservesScopeAndRetriesTheSameTransfer()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        long bytes = GovernedText.StringBytes(1024); memory.Reserve(bytes); string value = new('W', 1024);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 1L); long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => memory.RetainAllocation(value, bytes));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(bytes, memory.ReservedBytes); Assert.Equal(97L, prior.ReservedBytes);
        filler.Dispose(); memory.RetainAllocation(value, bytes); Assert.Equal(0L, memory.ReservedBytes);
        memory.Dispose(); Collect(governor); Assert.True(governor.Snapshot().LiveMemoryBytes >= bytes + 97L); GC.KeepAlive(value);
    }

    [Fact]
    public void FailedAdditionalTransferPreservesEarlierAliases()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        Alias first = Capture(governor, "string"); Collect(governor);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        long bytes = GovernedText.StringBytes(1024); memory.Reserve(bytes); string value = new('V', 1024);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 1L); long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => memory.RetainAllocation(value, bytes));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(bytes, memory.ReservedBytes); Verify(first, "string");
        filler.Dispose(); memory.RetainAllocation(value, bytes); memory.Dispose(); Collect(governor);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= first.MinimumBytes + bytes); Verify(first, "string"); GC.KeepAlive(value); GC.KeepAlive(first);
    }

    private sealed class ObservedRoots(QpdfObject[] values, Action observe) : IReadOnlyList<QpdfObject>
    {
        public int Count => values.Length;
        public QpdfObject this[int index] { get { if (index == 32) { observe(); } return values[index]; } }
        public IEnumerator<QpdfObject> GetEnumerator() { for (int index = 0; index < values.Length; index++) { yield return this[index]; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task CancellationAfterAcceptedRootVisitsRollsBackWithoutRetiringEarlierAliases()
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        Alias first = Capture(governor, "string"); Collect(governor);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(4096L);
        QpdfObject[] values = Enumerable.Range(1, 64).Select(index => (QpdfObject)new PdfReference(index, 0)).ToArray(); object wrapper = new();
        using ManualResetEventSlim reached = new(), resume = new(); long startWork = governor.Snapshot().TotalWork;
        ObservedRoots roots = new(values, () => { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Ledger root observation did not resume."); } });
        Task worker = Task.Factory.StartNew(() => memory.Retain(wrapper, 24L, roots), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10))); Assert.True(governor.Snapshot().TotalWork > startWork + 32L);
            cancellation.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(4096L, memory.ReservedBytes); Verify(first, "string");
            memory.Dispose(); Collect(governor); Assert.True(governor.Snapshot().LiveMemoryBytes >= first.MinimumBytes); GC.KeepAlive(first);
        }
        finally { cancellation.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias CompleteWithQueuedRetirements()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None);
        Alias first = Capture(governor, "string"); AddUnrootedAliases(governor);
        // Queue finalizer notifications while the governor is rooted, without
        // draining the job-thread index. The surviving first alias must not
        // keep that abandoned queue or the rest of its metadata alive later.
        Collect(null); object ledger = Field(governor, "_valueOwners"); object pending = Field(ledger, "_pending");
        return new(first.Value ?? throw new InvalidOperationException("Missing first alias."), [.. first.Metadata, new(pending)], first.MinimumBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddUnrootedAliases(ResourceGovernor governor)
    {
        for (int index = 0; index < 16; index++)
        {
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor); long bytes = GovernedText.StringBytes(32);
            memory.Reserve(bytes); string value = new('V', 32); memory.RetainAllocation(value, bytes);
        }
    }

    [Fact]
    public void QueuedRetirementsDoNotAnchorCompletedJobsThroughSurvivingCarriers()
    {
        Alias alias = CompleteWithQueuedRetirements(); Collect(null);
        Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive)); Verify(alias, "string"); GC.KeepAlive(alias);
    }

    [Fact]
    public void RetiredLedgersDoNotInterfereWithThirtyTwoLaterFiniteCycles()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None);
        for (int cycle = 0; cycle < 32; cycle++)
        {
            Alias alias = Capture(governor, "string"); Collect(governor); Verify(alias, "string");
            Assert.True(governor.Snapshot().LiveMemoryBytes >= alias.MinimumBytes);
            alias.Value = null; Collect(governor); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.All(alias.Metadata.Skip(1), reference => Assert.False(reference.IsAlive));
        }
        GC.KeepAlive(governor);
    }
}
