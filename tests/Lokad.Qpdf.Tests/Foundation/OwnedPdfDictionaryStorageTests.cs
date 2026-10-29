using System.Collections;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class OwnedPdfDictionaryStorageTests
{
    private static PdfName Key(int index) => new([(byte)index, 0, 255, 35]);

    private static PdfNameTable<QpdfObject> Populate(ParserMemoryScope memory, ResourceGovernor governor, int count)
    {
        PdfNameTable<QpdfObject> table = PdfNameTable<QpdfObject>.Create(memory, governor, 8L, CancellationToken.None);
        for (int index = 0; index < count; index++) { table.Assign(memory, governor, Key(index), PdfNull.Instance, CancellationToken.None); }
        return table;
    }

    [Fact]
    public void MutableTransferFailsWithoutChangingFundingAndCanRetryAfterFreeze()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<QpdfObject> table = Populate(memory, governor, 1);
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<InvalidOperationException>(() => PdfDictionary.FromOwnedEntries(table));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.False(table.IsFrozen);
        table.Assign(memory, governor, Key(1), PdfNull.Instance, CancellationToken.None);
        table.Freeze(memory, governor, CancellationToken.None);
        PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(table);
        Assert.Same(table, dictionary.AllocationCarrier);
        Assert.Equal(2, dictionary.Count);
        Assert.Throws<InvalidOperationException>(() => table.Assign(memory, governor, Key(2), PdfNull.Instance, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(32)]
    public void GenericAndNongenericKeyViewsPreserveOrderAndCachedIdentity(int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<QpdfObject> table = Populate(memory, governor, count);
        table.Freeze(memory, governor, CancellationToken.None);
        PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(table);
        Assert.Same(dictionary.Keys, dictionary.Keys);
        Assert.Equal(Enumerable.Range(0, count).Select(Key), dictionary.Keys);
        List<PdfName> nongenericKeys = new();
        foreach (object item in (IEnumerable)dictionary.Keys) { nongenericKeys.Add(Assert.IsType<PdfName>(item)); }
        Assert.Equal(dictionary.Keys, nongenericKeys);
        // The table itself continues to enumerate entries through either route.
        int entries = 0;
        foreach (object item in (IEnumerable)table) { Assert.IsType<KeyValuePair<PdfName, QpdfObject>>(item); entries++; }
        Assert.Equal(count, entries);
    }

    [Fact]
    public void PublicCopyRemainsIndependentOfMutableConstructionStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<QpdfObject> table = Populate(memory, governor, 1);
        PdfDictionary copy = new(table);
        table.Assign(memory, governor, Key(0), new PdfInteger("42", 42L), CancellationToken.None);
        table.Assign(memory, governor, Key(1), PdfNull.Instance, CancellationToken.None);
        Assert.Equal(1, copy.Count);
        Assert.True(copy.TryGet(Key(0), out QpdfObject value)); Assert.Same(PdfNull.Instance, value);
        Assert.False(copy.TryGet(Key(1), out value)); Assert.Same(PdfNull.Instance, value);
        Assert.IsType<Dictionary<PdfName, QpdfObject>>(copy.AllocationCarrier);
    }

    [Fact]
    public void EqualityAndPublicLookupMatchLegacyBinaryEmptyAndReplacementSemantics()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<QpdfObject> table = Populate(memory, governor, 1);
        PdfName empty = new([]); PdfArray borrowed = new([PdfNull.Instance]);
        table.Assign(memory, governor, empty, borrowed, CancellationToken.None);
        table.Assign(memory, governor, Key(0), new PdfInteger("42", 42L), CancellationToken.None);
        table.Freeze(memory, governor, CancellationToken.None);
        PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(table);
        PdfDictionary legacy = new(new Dictionary<PdfName, QpdfObject> { [empty] = borrowed, [Key(0)] = new PdfInteger("42", 42L) });
        Assert.True(dictionary.Equals(legacy)); Assert.True(legacy.Equals(dictionary));
        Assert.Equal(legacy.GetHashCode(), dictionary.GetHashCode()); Assert.False(dictionary.Equals(null));
        Assert.Equal(new[] { Key(0), empty }, dictionary.Keys);
        Assert.True(dictionary.TryGet(new PdfName([]), out QpdfObject value)); Assert.Same(borrowed, value);
        Assert.False(dictionary.TryGet(new PdfName([0, 0, 255, 36]), out value)); Assert.Same(PdfNull.Instance, value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedEntryCursorHonorsEitherCallerCancellationWithoutAdvancing(bool governorToken)
    {
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner);
        PdfNameTable<QpdfObject> table = Populate(memory, owner, 2); table.Freeze(memory, owner, CancellationToken.None);
        PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(table);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor caller = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
        Assert.True(entries.MoveNext(caller, CancellationToken.None)); Assert.Equal(Key(0), entries.Current.Key);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => entries.MoveNext(caller, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(Key(0), entries.Current.Key);
        ResourceGovernor retry = new(QpdfLimits.Default, CancellationToken.None);
        Assert.True(entries.MoveNext(retry, CancellationToken.None)); Assert.Equal(Key(1), entries.Current.Key);
        Assert.False(entries.MoveNext(retry, CancellationToken.None));         Assert.Equal(0L, retry.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RepeatedBorrowedViewsAndEntryVisitsAllocateNoStorage()
    {
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner);
        PdfNameTable<QpdfObject> table = Populate(memory, owner, 32); table.Freeze(memory, owner, CancellationToken.None);
        PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(table);
        ResourceGovernor caller = new(QpdfLimits.Default, CancellationToken.None);
        for (int round = 0; round < 32; round++) { Visit(dictionary, caller); }
        long live = owner.Snapshot().LiveMemoryBytes;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        int count = Visit(dictionary, caller);
        long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(32, count); Assert.Equal(0L, delta);
        Assert.Equal(live, owner.Snapshot().LiveMemoryBytes);
    }

    private static int Visit(PdfDictionary dictionary, ResourceGovernor caller)
    {
        GC.KeepAlive(dictionary.Keys);
        using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
        int count = 0;
        while (entries.MoveNext(caller, CancellationToken.None))
        { GC.KeepAlive(entries.Current.Key); GC.KeepAlive(entries.Current.Value); count++; }
        return count;
    }

    private sealed class Alias(IEnumerable<PdfName> keys, WeakReference wrapper, WeakReference table,
        WeakReference scope, WeakReference governor, long bytes)
    {
        internal IEnumerable<PdfName>? Keys = keys;
        internal readonly WeakReference Wrapper = wrapper, Table = table, Scope = scope, Governor = governor;
        internal readonly long Bytes = bytes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Publish(ResourceGovernor governor, int count)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<QpdfObject> table = Populate(memory, governor, count); table.Freeze(memory, governor, CancellationToken.None);
        memory.Reserve(24L); PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(table);
        // Borrowed key/value graphs stay caller-funded; this scope transfers
        // the table and wrapper through the production graph handoff.
        memory.RetainGraph();
        return new(dictionary.Keys, new(dictionary), new(table), new(memory), new(governor), table.StorageBytes);
    }

    private static void Collect()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(32)]
    public void EscapingKeysKeepTheirBackingFundedUntilTheLastAliasDies(int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None);
        Alias alias = Publish(governor, count); Collect();
        Assert.False(alias.Wrapper.IsAlive); Assert.False(alias.Scope.IsAlive); Assert.True(alias.Table.IsAlive);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= alias.Bytes);
        Assert.Equal(Enumerable.Range(0, count).Select(Key), alias.Keys);
        alias.Keys = null; Collect();
        Assert.False(alias.Table.IsAlive); governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(governor); GC.KeepAlive(alias);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Complete(int count) => Publish(new(QpdfLimits.Default, CancellationToken.None), count);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(32)]
    public void EscapingKeysDoNotRetainCompletedExecutionState(int count)
    {
        Alias alias = Complete(count); Collect();
        Assert.False(alias.Wrapper.IsAlive); Assert.False(alias.Scope.IsAlive); Assert.False(alias.Governor.IsAlive);
        Assert.True(alias.Table.IsAlive); Assert.Equal(Enumerable.Range(0, count).Select(Key), alias.Keys);
        GC.KeepAlive(alias);
    }
}
