using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ContentEntriesTests
{
    private static PdfStream Stream() => new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), new byte[4096]);

    [Fact]
    public void DisposedOwnerDropsScratchAndRejectsFurtherAccess()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfStream stream = Stream(); ContentEntries entries = ContentEntries.Create(governor, CancellationToken.None);
        for (int index = 0; index < 17; index++) { entries.Add(new(index, index, stream)); }
        ContentEntries.Entry borrowed = entries.Get(16);
        Assert.Equal(17, entries.Count); Assert.True(governor.Snapshot().LiveMemoryBytes >= 960L);
        entries.Dispose(); entries.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => entries.Get(0));
        Assert.Throws<ObjectDisposedException>(() => entries.Add(new(null, 0, stream)));
        Assert.Throws<ObjectDisposedException>(() => entries.Count);
        Assert.Same(stream, borrowed.Stream); Assert.Equal(4096, borrowed.Stream.RawMemory.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterConstructionPreventsReadsAndGrowth(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ContentEntries entries = ContentEntries.Create(governor, caller ? cancellation.Token : CancellationToken.None);
        PdfStream stream = Stream(); for (int index = 0; index < 4; index++) { entries.Add(new(null, index, stream)); }
        long live = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => entries.Get(0));
        Assert.Throws<OperationCanceledException>(() => entries.Add(new(null, 4, stream)));
        Assert.Equal(4, entries.Count); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoolResolutionChecksCancellationAfterTheBorrowedLookup(bool caller)
    {
        using CancellationTokenSource cancellation = new(); PdfStream stream = Stream();
        CancellingPool pool = new(cancellation, stream);
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => PdfObjectLookup.TryResolve(new PdfReference(7, 99), pool, governor,
            caller ? cancellation.Token : CancellationToken.None, out _));
        Assert.Equal(1, pool.Calls); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(stream, pool[7]);
    }

    [Fact]
    public void PrivateReaderForwardsCallerCancellationBeforeTraversal()
    {
        MethodInfo method = typeof(ContentTransforms).GetMethod("ReadPageContents", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException();
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary page = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Contents")] = Stream() });
        TargetInvocationException wrapper = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [null, page, governor, new DiagnosticBag(governor), cancellation.Token]));
        Assert.IsType<OperationCanceledException>(wrapper.InnerException);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RootedDisposedOwnerDoesNotKeepItsGovernorOrTokenSource()
    {
        var prepared = PrepareDisposed();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(prepared.Governor.IsAlive); Assert.False(prepared.Cancellation.IsAlive);
        Assert.Equal(4096, prepared.Stream.RawMemory.Length);
        GC.KeepAlive(prepared.Owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ContentEntries Owner, PdfStream Stream, WeakReference Governor, WeakReference Cancellation) PrepareDisposed()
    {
        CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        ContentEntries entries = ContentEntries.Create(governor, cancellation.Token); PdfStream stream = Stream(); entries.Add(new(null, 0, stream));
        WeakReference governorWitness = new(governor), cancellationWitness = new(cancellation);
        entries.Dispose(); cancellation.Dispose();
        return (entries, stream, governorWitness, cancellationWitness);
    }

    private sealed class CancellingPool : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        private readonly CancellationTokenSource _cancellation;
        internal int Calls;
        internal CancellingPool(CancellationTokenSource cancellation, PdfStream stream) { _cancellation = cancellation; Add(7, stream); }
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, out QpdfObject value)
        {
            Calls++; _cancellation.Cancel();
            if (TryGetValue(key, out QpdfObject? found)) { value = found; return true; }
            value = PdfNull.Instance; return false;
        }
    }
}
