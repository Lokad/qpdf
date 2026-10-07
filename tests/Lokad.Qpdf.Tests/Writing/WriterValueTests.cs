using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterValueTests
{
    internal static WriterIndex<int> Map(ResourceGovernor governor)
    {
        WriterIndex<int> map = WriterIndex<int>.Create(sizeof(int), governor, CancellationToken.None);
        map.Add(1, 2); map.Add(2, 3); return map;
    }

    internal static QpdfObject Entry(PdfDictionary dictionary, string name)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject? value));
        return Assert.IsAssignableFrom<QpdfObject>(value);
    }

    [Fact]
    public void NestedGraphRemapsOnceAndBorrowsImmutableValuesAndKeys()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> map = Map(governor);
        PdfName key = PdfName.FromAscii("Nested"); PdfByteString text = new("opaque"u8.ToArray(), false);
        PdfArray source = new([new PdfReference(1, 7), text, PdfNull.Instance]);
        PdfDictionary original = new(new Dictionary<PdfName, QpdfObject> { [key] = source });
        long before = governor.Snapshot().LiveMemoryBytes;
        using WriterValue owner = WriterValue.Remap(original, map, governor, CancellationToken.None);
        PdfDictionary rebuilt = Assert.IsType<PdfDictionary>(owner.Value);
        PdfArray array = Assert.IsType<PdfArray>(Entry(rebuilt, "Nested"));
        Assert.NotSame(original, rebuilt); Assert.NotSame(source, array); Assert.Same(key, Assert.Single(rebuilt.Keys));
        PdfReference reference = Assert.IsType<PdfReference>(array[0]); Assert.Equal(2, reference.ObjectNumber); Assert.Equal(0, reference.Generation);
        Assert.Same(text, array[1]); Assert.Same(PdfNull.Instance, array[2]);
        Assert.Equal(1, Assert.IsType<PdfReference>(source[0]).ObjectNumber); Assert.Equal(7, Assert.IsType<PdfReference>(source[0]).Generation);
        Assert.True(governor.Snapshot().LiveMemoryBytes > before);
        owner.Dispose(); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Same(source, Entry(original, "Nested"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderOverridesLengthBeforeMappingAndMapsOtherReferencesOnce(bool existingLength)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> map = Map(governor);
        PdfName lengthKey = PdfName.FromAscii("Length"), linkKey = PdfName.FromAscii("Link");
        Dictionary<PdfName, QpdfObject> entries = new() { [linkKey] = new PdfArray([new PdfReference(1, 9)]) };
        if (existingLength) { entries.Add(lengthKey, new PdfReference(999, 8)); }
        PdfDictionary source = new(entries); long before = governor.Snapshot().LiveMemoryBytes;
        using WriterValue owner = WriterValue.StreamHeader(source, 42, map, governor, CancellationToken.None);
        PdfDictionary header = Assert.IsType<PdfDictionary>(owner.Value);
        Assert.Equal(42, Assert.IsType<PdfReference>(Entry(header, "Length")).ObjectNumber);
        Assert.Equal(0, Assert.IsType<PdfReference>(Entry(header, "Length")).Generation);
        Assert.Equal(2, Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(Entry(header, "Link"))[0]).ObjectNumber);
        Assert.Same(linkKey, header.Keys.Single(key => key.Equals(linkKey)));
        if (existingLength) { Assert.Same(lengthKey, header.Keys.Single(key => key.Equals(lengthKey))); Assert.Equal(999, Assert.IsType<PdfReference>(Entry(source, "Length")).ObjectNumber); }
        else { Assert.False(source.TryGet(lengthKey, out _)); }
        owner.Dispose(); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(2, map.Count);
    }

    [Fact]
    public void ExactArrayMetadataAdmissionIncludesWrapperScopeBackingAndReferences()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> map = Map(governor);
        PdfArray source = new([PdfNull.Instance, new PdfReference(1, 0), PdfBoolean.True]);
        long before = governor.Snapshot().LiveMemoryBytes;
        using WriterValue owner = WriterValue.Remap(source, map, governor, CancellationToken.None);
        Assert.Equal(before + 128L + 64L + 3L * IntPtr.Size + 32L, governor.Snapshot().LiveMemoryBytes);
        owner.Dispose(); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void OneByteShortAdmissionPreservesPriorOwnersAndSupportsRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using WriterIndex<int> map = Map(governor);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 127L);
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => WriterValue.Remap(PdfNull.Instance, map, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 32768L);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes); Assert.Equal(2, map.Count);
        filler.Dispose(); before = governor.Snapshot().LiveMemoryBytes;
        using (WriterValue retry = WriterValue.Remap(PdfNull.Instance, map, governor, CancellationToken.None))
        { Assert.Same(PdfNull.Instance, retry.Value); Assert.Equal(before + 128L, governor.Snapshot().LiveMemoryBytes); }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("depth")]
    [InlineData("memory")]
    public void NestedFailureRollsBackOnlyNewMetadata(string mode)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L, MaxNestingDepth = 2L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using WriterIndex<int> map = Map(governor);
        QpdfObject source = mode switch
        {
            "missing" => new PdfArray([new PdfReference(1, 0), new PdfArray([new PdfReference(999, 0)])]),
            "depth" => new PdfArray([new PdfArray([new PdfArray([new PdfReference(1, 0)])])]),
            "memory" => new PdfArray(Enumerable.Repeat<QpdfObject>(new PdfReference(1, 0), 1024).ToArray()),
            _ => throw new InvalidOperationException("Unknown remap failure."),
        };
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfException failure = Assert.ThrowsAny<QpdfException>(() => WriterValue.Remap(source, map, governor, CancellationToken.None));
        if (mode != "missing") { Assert.Equal(mode == "depth" ? nameof(QpdfLimits.MaxNestingDepth) : nameof(QpdfLimits.MaxMemoryBytes), Assert.IsType<QpdfLimitException>(failure).LimitName); }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes); Assert.Equal(2, map.Count);
        using (WriterValue retry = WriterValue.Remap(new PdfReference(1, 0), map, governor, CancellationToken.None))
        { Assert.Equal(2, Assert.IsType<PdfReference>(retry.Value).ObjectNumber); }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(source);
    }

    [Fact]
    public void ExhaustedWorkRejectsBeforeLargeArrayAllocation()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using WriterIndex<int> map = Map(governor);
        PdfArray source = new(Enumerable.Repeat<QpdfObject>(new PdfReference(1, 0), 8192).ToArray());
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork);
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => WriterValue.Remap(source, map, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 32768L);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(8192, source.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignOwnerRejectsBeforeAdmission(bool header)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None), other = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> map = Map(governor); long before = governor.Snapshot().LiveMemoryBytes;
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>());
        Assert.Throws<InvalidOperationException>(() => Create(header, source, map, other, CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, other.Snapshot().LiveMemoryBytes); Assert.Equal(0L, other.Snapshot().TotalWork);
    }

    private static WriterValue Create(bool header, PdfDictionary source, WriterIndex<int> map, ResourceGovernor governor, CancellationToken token)
        => header ? WriterValue.StreamHeader(source, 42, map, governor, token) : WriterValue.Remap(source, map, governor, token);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EitherPreCanceledRouteRejectsBeforeAdmission(bool header, bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using WriterIndex<int> map = Map(governor); PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>());
        long before = governor.Snapshot().LiveMemoryBytes, work = governor.Snapshot().TotalWork; cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Create(header, source, map, governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(work, governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeStreamPayloadIsBorrowedUnderFiniteMetadataCeiling(bool headerOnly)
    {
        PdfStream original = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), new byte[2 * 1024 * 1024]);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using WriterIndex<int> map = Map(governor); long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        using WriterValue owner = headerOnly ? WriterValue.StreamHeader(original.Dictionary, 42, map, governor, CancellationToken.None)
            : WriterValue.Remap(original, map, governor, CancellationToken.None);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 32768L); Assert.True(governor.Snapshot().PeakMemoryBytes <= 4096L);
        Assert.True(MemoryMarshal.TryGetArray(original.RawMemory, out ArraySegment<byte> source));
        if (headerOnly) { Assert.IsType<PdfDictionary>(owner.Value); }
        else { Assert.True(MemoryMarshal.TryGetArray(Assert.IsType<PdfStream>(owner.Value).RawMemory, out ArraySegment<byte> borrowed)); Assert.Same(source.Array, borrowed.Array); }
        owner.Dispose(); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        byte[] publicCopy = original.Data; Assert.NotSame(source.Array, publicCopy); publicCopy[0] = 255; Assert.Equal(0, original.RawMemory.Span[0]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WriterValue Owner, WeakReference Graph, WeakReference Backing, WeakReference Governor) Disposed()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using WriterIndex<int> map = Map(governor);
        WriterValue owner = WriterValue.Remap(new PdfArray([new PdfReference(1, 0)]), map, governor, CancellationToken.None);
        PdfArray graph = Assert.IsType<PdfArray>(owner.Value);
        var result = (owner, new WeakReference(graph), new WeakReference(QdfEmissionPlanTests.Field(graph, "_items")), new WeakReference(governor));
        owner.Dispose(); map.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return result;
    }

    [Fact]
    public void RootedDisposedOwnerDropsGraphBackingAndCompletedGovernor()
    {
        var state = Disposed(); QdfEmissionPlanTests.Collected();
        Assert.False(state.Graph.IsAlive); Assert.False(state.Backing.IsAlive); Assert.False(state.Governor.IsAlive);
        Assert.Null(state.Owner.GetType().GetField("_memory", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(state.Owner));
        Assert.Throws<ObjectDisposedException>(() => state.Owner.Value); state.Owner.Dispose(); GC.KeepAlive(state);
    }
}
