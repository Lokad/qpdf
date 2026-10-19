using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class BorrowedResourceEntryTests
{
    private sealed class Prepared : IDisposable
    {
        private readonly ParserMemoryScope _memory;
        private readonly PdfDictionary? _legacy;
        private readonly PdfNameTable<QpdfObject>? _owned;
        internal readonly PdfName Key = new([0, 255, 35]);
        internal readonly QpdfObject Value = new PdfByteString(new byte[2 * 1024 * 1024], false);
        internal PdfDictionary.EntryEnumerator Entries() => _owned is null
            ? (_legacy ?? throw new InvalidOperationException("Missing legacy resources.")).GetEntryEnumerator() : new(_owned);
        internal Prepared(bool owned, ResourceGovernor construction)
        {
            _memory = ParserMemoryScope.Create(construction);
            if (owned)
            {
                _owned = PdfNameTable<QpdfObject>.Create(_memory, construction, IntPtr.Size, CancellationToken.None);
                _owned.Assign(_memory, construction, Key, Value, CancellationToken.None); _owned.Freeze(_memory, construction, CancellationToken.None);
            }
            else { _legacy = new(new Dictionary<PdfName, QpdfObject> { [Key] = Value }); }
        }
        public void Dispose() => _memory.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GovernedEntriesBorrowBinaryKeysAndPayloadWithoutQueryLookup(bool owned)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None), governor = new(QpdfLimits.Default, CancellationToken.None);
        using Prepared prepared = new(owned, construction); using PdfDictionary.EntryEnumerator entries = prepared.Entries();
        Assert.True(entries.MoveNext(governor, CancellationToken.None)); Assert.Same(prepared.Key, entries.Current.Key); Assert.Same(prepared.Value, entries.Current.Value);
        Assert.False(entries.MoveNext(governor, CancellationToken.None)); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EitherEntryCancellationContextStopsBeforeAdvancingTheBorrowedCursor(bool owned, bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using Prepared prepared = new(owned, construction); using PdfDictionary.EntryEnumerator entries = prepared.Entries(); source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => entries.MoveNext(governor, governorToken ? CancellationToken.None : source.Token));
Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.True(entries.MoveNext()); Assert.Same(prepared.Value, entries.Current.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmedBorrowedEntryPassesAllocateNoEnumeratorKeyViewOrPayloadCopy(bool owned)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None), governor = new(QpdfLimits.Default, CancellationToken.None);
        using Prepared prepared = new(owned, construction);
        for (int index = 0; index < 32; index++) { using var warm = prepared.Entries(); while (warm.MoveNext(governor, CancellationToken.None)) { } }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 128; index++) { using var entries = prepared.Entries(); while (entries.MoveNext(governor, CancellationToken.None)) { } }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(prepared);
    }
}
