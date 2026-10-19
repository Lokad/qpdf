using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class CompletedPdfNameTableTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PdfNameTable<object> Table, WeakReference Scope, WeakReference Governor, object Borrowed) Published(bool convert)
    {
        PdfName key = PdfNameTableTests.Key(1, 32); object borrowed = new(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        table.Assign(memory, governor, key, borrowed, CancellationToken.None);
        if (convert)
        {
            Assert.True(table.TryMoveValues(memory, governor, IntPtr.Size, static value => value,
                CancellationToken.None, out PdfNameTable<object>? result));
            table = Assert.IsType<PdfNameTable<object>>(result);
        }
        else { table.Freeze(memory, governor, CancellationToken.None); }
        memory.RetainGraph(); memory.Dispose();
        Assert.True(governor.Snapshot().LiveMemoryBytes >= table.StorageBytes);
        return (table, new WeakReference(memory), new WeakReference(governor), borrowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedFrozenStorageAndKeyAliasesDoNotRetainCompletedExecutionState(bool convert)
    {
        var state = Published(convert); IEnumerable<PdfName> keys = state.Table.Keys;
        for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(state.Scope.IsAlive); Assert.False(state.Governor.IsAlive);
        Assert.Same(state.Table, keys); Assert.Same(state.Borrowed, state.Table[PdfNameTableTests.Key(1, 32)]); Assert.Single(keys); Assert.Single(state.Table.Values);
        GC.KeepAlive(state); GC.KeepAlive(keys);
    }
}
