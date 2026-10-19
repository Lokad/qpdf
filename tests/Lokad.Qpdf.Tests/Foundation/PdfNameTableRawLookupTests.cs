using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class PdfNameTableRawLookupTests
{
    private static byte[] Bytes(byte last)
    { byte[] raw = new byte[8192]; Array.Fill(raw, (byte)97); raw[^1] = last; return raw; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedRawLookupHonorsEitherCancellationRouteAfterCompletedBytes(bool governorToken)
    {
        byte[] raw = Bytes(97), query = Bytes(98); PdfArray value = new([PdfNull.Instance]); PdfName key = new(raw);
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner);
        PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, owner, 8L, CancellationToken.None);
        Assert.True(names.TryAdd(memory, owner, key, value, CancellationToken.None)); names.Freeze(memory, owner, CancellationToken.None);
        long funded = owner.Snapshot().LiveMemoryBytes;
        using CancellationTokenSource cancellation = new();
        ResourceGovernor lookup = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ManualResetEventSlim reached = new(), resume = new();
        Task worker = Task.Factory.StartNew(() => names.TryGetObserved(query.AsSpan(), lookup,
            governorToken ? CancellationToken.None : cancellation.Token,
            () => { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Raw name observation did not resume."); } }, out _),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));             cancellation.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(funded, owner.Snapshot().LiveMemoryBytes); Assert.Equal(0L, lookup.Snapshot().LiveMemoryBytes);
            Assert.True(names.TryGetValue(key, out QpdfObject? found)); Assert.Same(value, found);
        }
        finally { cancellation.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PdfNameTable<QpdfObject> Names, WeakReference Governor, WeakReference Observer) Complete()
    {
        byte[] raw = Bytes(97); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, governor, 8L, CancellationToken.None);
        Assert.True(names.TryAdd(memory, governor, new PdfName(raw), PdfNull.Instance, CancellationToken.None)); names.Freeze(memory, governor, CancellationToken.None);
        object callbackState = new(); Action observer = () => GC.KeepAlive(callbackState);
        Assert.True(names.TryGetObserved(raw.AsSpan(), governor, CancellationToken.None, observer, out _));
        return (names, new WeakReference(governor), new WeakReference(observer));
    }

    [Fact]
    public void FrozenRawLookupRetainsNeitherCallerNorObserver()
    {
        var state = Complete(); for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(state.Governor.IsAlive); Assert.False(state.Observer.IsAlive);
        Assert.Single((IEnumerable<KeyValuePair<PdfName, QpdfObject>>)state.Names); GC.KeepAlive(state);
    }
}
