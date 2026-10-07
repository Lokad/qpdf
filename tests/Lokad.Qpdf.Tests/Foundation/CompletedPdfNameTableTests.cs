using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class CompletedPdfNameTableTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PdfNameTable<object> Table, WeakReference Scope, WeakReference Governor, object Borrowed) Published()
    {
        PdfName key = PdfNameTableTests.Key(1, 32); object borrowed = new(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        table.Assign(memory, governor, key, borrowed, CancellationToken.None); table.Freeze(memory, governor, CancellationToken.None);
        memory.Retain(table, table.StorageBytes, Array.Empty<QpdfObject>()); memory.Dispose();
        Assert.True(governor.Snapshot().LiveMemoryBytes >= table.StorageBytes);
        return (table, new WeakReference(memory), new WeakReference(governor), borrowed);
    }

    [Fact]
    public void AcceptedFrozenStorageAndKeyAliasesDoNotRetainCompletedExecutionState()
    {
        var state = Published(); IEnumerable<PdfName> keys = state.Table.Keys;
        for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(state.Scope.IsAlive); Assert.False(state.Governor.IsAlive);
        Assert.Same(state.Table, keys); Assert.Same(state.Borrowed, state.Table[PdfNameTableTests.Key(1, 32)]); Assert.Single(keys); Assert.Single(state.Table.Values);
        GC.KeepAlive(state); GC.KeepAlive(keys);
    }
}
