using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class QdfMarkStorageTests
{
    private static readonly MethodInfo Prepare = typeof(PdfSerializer).GetMethod("QdfPageMarksSorted", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing live QDF mark preparation.");

    private static PdfDictionary Dict(string key, QpdfObject value) => new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii(key)] = value });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedPageAndContentReferencesBorrowArrayStorage(bool repeatedPages)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<QpdfObject> lookup = WriterIndex<QpdfObject>.Create(IntPtr.Size, construction, CancellationToken.None);
        using WriterIndex<int> combined = WriterIndex<int>.Create(sizeof(int), construction, CancellationToken.None);
        lookup.Add(2, Dict("Kids", new PdfArray(Enumerable.Repeat<QpdfObject>(new PdfReference(3, 0), repeatedPages ? 262144 : 1).ToArray())));
        lookup.Add(3, Dict("Contents", new PdfArray(Enumerable.Repeat<QpdfObject>(new PdfReference(4, 0), repeatedPages ? 1 : 262144).ToArray())));
        combined.Add(3, 3); combined.Add(4, 4);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        PdfSerializer serializer = new(QpdfLimits.Default, governor);
        object[] parameters = [lookup, combined, CancellationToken.None];
        long before = GC.GetAllocatedBytesForCurrentThread();
        IDisposable marks = Assert.IsAssignableFrom<IDisposable>(Prepare.Invoke(serializer, parameters));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536L);
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, 1L, 4096L);
        marks.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanceledPreparationRefundsItsOwnStorage(bool caller)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<QpdfObject> lookup = WriterIndex<QpdfObject>.Create(IntPtr.Size, construction, CancellationToken.None);
        using WriterIndex<int> combined = WriterIndex<int>.Create(sizeof(int), construction, CancellationToken.None);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        PdfSerializer serializer = new(QpdfLimits.Default, governor);
        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() => Prepare.Invoke(serializer,
            [lookup, combined, caller ? cancellation.Token : CancellationToken.None]));
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
