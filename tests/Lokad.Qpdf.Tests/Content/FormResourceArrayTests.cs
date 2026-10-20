using System.Reflection;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormResourceArrayTests
{
    private delegate void MergeCall(Dictionary<PdfName, QpdfObject> union, PdfName type, PdfArray having, PdfArray other, ResourceGovernor governor, CancellationToken token);
    private static readonly MergeCall Merge = (typeof(AcroFormCopy).GetMethod("MergeResourceArray", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException()).CreateDelegate<MergeCall>();
    private static readonly PdfName Type = PdfName.FromAscii("ProcSet");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeUnchangedArraysBorrowTheirOriginalStorage(bool nonempty)
    {
        PdfName pdf = PdfName.FromAscii("PDF"); PdfArray original = new(Enumerable.Repeat<QpdfObject>(pdf, 262144).ToArray());
        PdfArray incoming = new(nonempty ? new QpdfObject[] { pdf, new PdfReference(1, 0) } : []);
        Dictionary<PdfName, QpdfObject> union = new() { [Type] = original };
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1024 }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Merge(union, Type, original, incoming, governor, CancellationToken.None);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
        Assert.Same(original, union[Type]); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void LargeChangedArraysRefuseBeforeCopyingAndPreserveTheUnion()
    {
        PdfArray original = new(Enumerable.Repeat<QpdfObject>(PdfName.FromAscii("PDF"), 262144).ToArray());
        Dictionary<PdfName, QpdfObject> union = new() { [Type] = original };
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1024 }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => Merge(union, Type, original, new([PdfName.FromAscii("Text")]), governor, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
        Assert.Same(original, union[Type]); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ScalarEqualityOrderAndBorrowedElementsSurviveTheMergePhase()
    {
        PdfName a = PdfName.FromAscii("A"), b = PdfName.FromAscii("B"); PdfInteger one = new("1", 1);
        PdfInteger alternate = new("01", 1);
        PdfArray original = new([a, a, one]); Dictionary<PdfName, QpdfObject> union = new() { [Type] = original };
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { OwnsDocumentCounters = true };
        Merge(union, Type, original, new([PdfName.FromAscii("A"), one, alternate, b, b, PdfBoolean.True, new PdfReference(7, 0)]), governor, CancellationToken.None);
        PdfArray escaped = Assert.IsType<PdfArray>(union[Type]); Assert.Equal(new QpdfObject[] { a, a, one, alternate, b, PdfBoolean.True }, escaped.Items);
        governor.AcceptDocument(); governor.CompletePrivateStorage(); Assert.True(governor.CallerStorageBytes > 0L);
        Assert.Same(b, escaped[4]); Assert.Equal(3, original.Count);
    }

    private sealed class CancellingValue(CancellationTokenSource source) : QpdfObject
    {
        public override bool Equals(object? obj) { source.Cancel(); return false; }
        public override int GetHashCode() => 0;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringTheInnerSearchPreservesTheUnion(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        PdfArray original = new([new CancellingValue(cancellation), PdfName.FromAscii("PDF")]);
        Dictionary<PdfName, QpdfObject> union = new() { [Type] = original };
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => Merge(union, Type, original, new([PdfName.FromAscii("Text")]), governor,
            caller ? cancellation.Token : CancellationToken.None));
        Assert.Same(original, union[Type]); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
