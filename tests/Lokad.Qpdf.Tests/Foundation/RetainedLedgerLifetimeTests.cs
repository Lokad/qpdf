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
    private OwnershipBatch.Observation? _collection;

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
        => [new(governor), new(Field(governor, "_valueOwners"))];


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
            memory.RetainAllocation(minimum);
        }
        else if (kind == "table")
        {
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            PdfNameTable<QpdfObject> table = PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
            table.Assign(memory, governor, new PdfName([0, 255, 35]), PdfNull.Instance, CancellationToken.None);
            table.Freeze(memory, governor, CancellationToken.None);
            minimum = table.StorageBytes; memory.RetainGraph(); value = table;
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
    public void AdmissionRemainsUntilTheCoarseOwnerEnds(string kind)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None);
        Alias alias = Capture(governor, kind); Collect(governor);
        Assert.All(alias.Metadata, reference => Assert.True(reference.IsAlive));
        Assert.True(governor.Snapshot().LiveMemoryBytes >= alias.MinimumBytes); Verify(alias, kind);
        alias.Value = null; Collect(governor);
        governor.CompletePrivateStorage();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Collect(governor); Assert.All(alias.Metadata.Skip(1), reference => Assert.False(reference.IsAlive)); GC.KeepAlive(governor);
    }

    [Fact]
    public void RetainingAdmittedStorageNeedsNoLedgerAdmission()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        long bytes = GovernedText.StringBytes(1024); memory.Reserve(bytes); string value = new('W', 1024);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 1L); long before = governor.Snapshot().LiveMemoryBytes;
        memory.RetainAllocation(bytes);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, memory.ReservedBytes); Assert.Equal(97L, prior.ReservedBytes);
        filler.Dispose();
        memory.Dispose(); Collect(governor); Assert.True(governor.Snapshot().LiveMemoryBytes >= bytes + 97L); GC.KeepAlive(value);
    }

    [Fact]
    public void AdditionalTransferPreservesEarlierAliasesAtTheMemoryCeiling()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        Alias first = Capture(governor, "string"); Collect(governor);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        long bytes = GovernedText.StringBytes(1024); memory.Reserve(bytes); string value = new('V', 1024);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 1L); long before = governor.Snapshot().LiveMemoryBytes;
        memory.RetainAllocation(bytes);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, memory.ReservedBytes); Verify(first, "string");
        filler.Dispose(); memory.Dispose(); Collect(governor);
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
    public void RetentionDoesNotEnumerateBorrowedRoots()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Alias first = Capture(governor, "string");
        QpdfObject[] borrowed = Enumerable.Range(1, 64).Select(index => (QpdfObject)new PdfReference(index, 0)).ToArray();
        ObservedRoots roots = new(borrowed, () => throw new InvalidOperationException("Borrowed graphs must not be traversed for admission."));
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(32L);
        object wrapper = new();
        long work = governor.Snapshot().TotalWork;
        memory.RetainGraph();
        Assert.Equal(work, governor.Snapshot().TotalWork);
        Assert.Equal(0L, memory.ReservedBytes);
        Verify(first, "string");
        GC.KeepAlive(first); GC.KeepAlive(wrapper); GC.KeepAlive(borrowed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias CompleteWithQueuedRetirements()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None);
        Alias first = Capture(governor, "string"); AddUnrootedAliases(governor);
        // Queue finalizer notifications while the governor is rooted, without
        // draining the job-thread index. The surviving first alias must not
        // keep that abandoned queue or the rest of its metadata alive later.
        Collect(null);
        return new(first.Value ?? throw new InvalidOperationException("Missing first alias."), first.Metadata, first.MinimumBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddUnrootedAliases(ResourceGovernor governor)
    {
        for (int index = 0; index < 16; index++)
        {
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor); long bytes = GovernedText.StringBytes(32);
            memory.Reserve(bytes); string value = new('V', 32); memory.RetainAllocation(bytes);
        }
    }

    [Fact]
    public void SurvivingValuesDoNotAnchorCompletedCoarseOwners()
    {
        Alias alias = CompleteWithQueuedRetirements(); Collect(null);
        Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive)); Verify(alias, "string"); GC.KeepAlive(alias);
    }

    internal async Task RetiredLedgersDoNotInterfereWithThirtyTwoLaterFiniteCycles()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None);
        for (int cycle = 0; cycle < 32; cycle++)
        {
            Alias alias = Capture(governor, "string"); await (_collection ?? throw new InvalidOperationException("Missing stress batch.")).CollectAsync(governor, 4, 4, false, true); Verify(alias, "string");
            Assert.True(governor.Snapshot().LiveMemoryBytes >= alias.MinimumBytes);
            alias.Value = null; await (_collection ?? throw new InvalidOperationException("Missing stress batch.")).CollectAsync(governor, 4, 4, false, true); governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.All(alias.Metadata.Skip(1), reference => Assert.False(reference.IsAlive));
        }
        GC.KeepAlive(governor);
    }

    internal static IEnumerable<(string Id, Func<OwnershipBatch.Observation, Task> Run)> StressCases()
        => (new object[][] { [] }).Select(row =>
            (OwnershipBatch.Id(nameof(RetainedLedgerLifetimeTests), nameof(RetiredLedgersDoNotInterfereWithThirtyTwoLaterFiniteCycles), row),
            (Func<OwnershipBatch.Observation, Task>)(async observation =>
            {
                RetainedLedgerLifetimeTests fixture = new() { _collection = observation };
                await fixture.RetiredLedgersDoNotInterfereWithThirtyTwoLaterFiniteCycles();
            })));
}
