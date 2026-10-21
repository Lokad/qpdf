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
        PdfName tailKey = PdfName.FromAscii("Tail"); entries.Add(tailKey, PdfNull.Instance);
        PdfDictionary source = new(entries); long before = governor.Snapshot().LiveMemoryBytes;
        using WriterValue owner = WriterValue.StreamHeader(source, 42, map, governor, CancellationToken.None);
        PdfDictionary header = Assert.IsType<PdfDictionary>(owner.Value);
        Assert.Equal(42, Assert.IsType<PdfReference>(Entry(header, "Length")).ObjectNumber);
        Assert.Equal(0, Assert.IsType<PdfReference>(Entry(header, "Length")).Generation);
        Assert.Equal(2, Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(Entry(header, "Link"))[0]).ObjectNumber);
        Assert.Same(linkKey, header.Keys.Single(key => key.Equals(linkKey)));
        Assert.Equal(new[] { linkKey, tailKey, lengthKey }, header.Keys);
        if (existingLength) { Assert.Same(lengthKey, header.Keys.Single(key => key.Equals(lengthKey))); Assert.Equal(999, Assert.IsType<PdfReference>(Entry(source, "Length")).ObjectNumber); }
        else { Assert.False(source.TryGet(lengthKey, out _)); }
        owner.Dispose(); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(2, map.Count);
    }

    [Fact]
    public void IdentityMappingBorrowsValidatedContainersAndPreservesPublicCopies()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> map = WriterIndex<int>.Create(sizeof(int), governor, CancellationToken.None);
        map.Add(7, 7);
        PdfReference reference = new(7, 0);
        PdfArray array = new([reference, PdfNull.Instance]);
        PdfDictionary header = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Links")] = array });
        PdfStream stream = new(header, new byte[] { 1, 2, 3 });
        using (WriterValue owner = WriterValue.Remap(stream, map, governor, CancellationToken.None))
        {
            Assert.Same(stream, owner.Value);
            Assert.Same(reference, Assert.IsType<PdfArray>(Entry(stream.Dictionary, "Links"))[0]);
        }
        byte[] publicCopy = stream.Data;
        publicCopy[0] = 255;
        Assert.Equal(new byte[] { 1, 2, 3 }, stream.Data);
        Assert.Same(array, Entry(header, "Links"));
    }

    [Fact]
    public void IdentityPrefixStillRejectsAnUnmappedNestedReference()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> map = WriterIndex<int>.Create(sizeof(int), governor, CancellationToken.None);
        map.Add(7, 7);
        PdfArray source = new([new PdfReference(7, 0), new PdfArray([new PdfReference(8, 0)])]);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfException>(() => WriterValue.Remap(source, map, governor, CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(8, Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(source[1])[0]).ObjectNumber);
    }

    [Fact]
    public void ChangedSuffixCopiesTheImmutablePrefixAndNormalizesGeneration()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> map = WriterIndex<int>.Create(sizeof(int), governor, CancellationToken.None);
        map.Add(7, 7);
        PdfArray prefix = new([new PdfReference(7, 0)]);
        PdfArray suffix = new([prefix, new PdfReference(7, 4)]);
        PdfName first = PdfName.FromAscii("First"), last = PdfName.FromAscii("Last");
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject> { [first] = prefix, [last] = suffix });
        using WriterValue owner = WriterValue.Remap(source, map, governor, CancellationToken.None);
        PdfDictionary mapped = Assert.IsType<PdfDictionary>(owner.Value);
        Assert.Equal(new[] { first, last }, mapped.Keys);
        Assert.Same(prefix, Entry(mapped, "First"));
        PdfArray changed = Assert.IsType<PdfArray>(Entry(mapped, "Last"));
        Assert.NotSame(suffix, changed); Assert.Same(prefix, changed[0]);
        Assert.Equal(0, Assert.IsType<PdfReference>(changed[1]).Generation);
        Assert.Equal(4, Assert.IsType<PdfReference>(suffix[1]).Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedDictionaryElidesClrNullEntriesWithoutReordering(bool writerIndex)
    {
        PdfName first = PdfName.FromAscii("First"), absent = PdfName.FromAscii("Absent"), last = PdfName.FromAscii("Last");
        Dictionary<PdfName, QpdfObject?> entries = new()
        { [first] = PdfNull.Instance, [absent] = null, [last] = PdfBoolean.True };
        PdfDictionary source = Activator.CreateInstance(typeof(PdfDictionary), [entries]) as PdfDictionary ?? throw new InvalidOperationException();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> map = Map(governor);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using WriterValue? owner = writerIndex ? WriterValue.Remap(source, map, governor, CancellationToken.None) : null;
        PdfDictionary result = Assert.IsType<PdfDictionary>(owner is null
            ? ObjectRemap.RemapIds(source, new Dictionary<int, int>(), memory, governor, CancellationToken.None) : owner.Value);
        Assert.NotSame(source, result); Assert.Equal(new[] { first, last }, result.Keys);
        Assert.Same(PdfNull.Instance, Entry(result, "First"));
        Assert.Equal(3, source.Count); Assert.Equal(2, result.Count);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignOwnerRejectsBeforeAdmission(bool header)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None), other = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> map = Map(governor); long before = governor.Snapshot().LiveMemoryBytes;
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>());
        Assert.Throws<InvalidOperationException>(() => Create(header, source, map, other, CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, other.Snapshot().LiveMemoryBytes);     }

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
        long before = governor.Snapshot().LiveMemoryBytes; cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Create(header, source, map, governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);     }

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
