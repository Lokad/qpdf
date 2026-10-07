using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class PdfObjectComparisonTests
{
    private static PdfDictionary Dictionary(QpdfObject value) => new(new Dictionary<PdfName, QpdfObject> { [new PdfName([0, 255, 35])] = value });
    public static IEnumerable<object[]> Values()
    {
        yield return [PdfNull.Instance, PdfNull.Instance];
        yield return [PdfBoolean.True, new PdfBoolean(true)];
        yield return [PdfBoolean.True, PdfBoolean.False];
        yield return [new PdfInteger("+00", 0), new PdfInteger("0", 0)];
        yield return [new PdfInteger("4", 4), new PdfInteger("4", 5)];
        yield return [new PdfReal("NaN", double.NaN), new PdfReal("NaN", double.NaN)];
        yield return [new PdfReal("0.0", 0.0), new PdfReal("0.0", -0.0)];
        yield return [new PdfName([0, 255, 35]), new PdfName([0, 255, 35])];
        yield return [new PdfName([35, 52, 49]), new PdfName([65])];
        yield return [new PdfByteString([0, 255], false), new PdfByteString([0, 255], true)];
        yield return [new PdfByteString([0, 255], false), new PdfByteString([0, 255], false)];
        yield return [new PdfReference(12, 0), new PdfReference(12, 1)];
        yield return [new PdfArray([PdfBoolean.True, new PdfInteger("4", 4)]), new PdfArray([new PdfBoolean(true), new PdfInteger("4", 4)])];
        yield return [new PdfArray([PdfBoolean.True]), new PdfArray([])];
        yield return [new PdfStream(Dictionary(PdfBoolean.True), [0, 255]), new PdfStream(Dictionary(new PdfBoolean(true)), [0, 255])];
        yield return [new PdfStream(Dictionary(PdfBoolean.True), [0, 255]), new PdfStream(Dictionary(PdfBoolean.True), [0, 254])];
        yield return [new PdfStream(Dictionary(PdfBoolean.True), [0, 255]), new PdfStream(Dictionary(PdfBoolean.False), [0, 255])];
        yield return [new PdfName([65]), new PdfByteString([65], false)];
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void BorrowedComparisonMatchesExistingScalarAndCompoundValueSemantics(QpdfObject left, QpdfObject right)
    {
        PdfDictionary first = Dictionary(left), second = Dictionary(right);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using PdfObjectComparison comparison = PdfObjectComparison.Create(governor, CancellationToken.None);
        Assert.Equal(first.Equals(second), comparison.AreEqual(first, second)); comparison.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ReversedBinaryKeysAndEqualPrefixCollisionsPreserveDictionaryValueEquality()
    {
        PdfName firstKey = new([0, 255, 35]), secondKey = new([0, 255, 36]);
        PdfDictionary first = new(new Dictionary<PdfName, QpdfObject> { [firstKey] = PdfBoolean.True, [secondKey] = PdfBoolean.False });
        PdfDictionary second = new(new Dictionary<PdfName, QpdfObject> { [secondKey] = PdfBoolean.False, [firstKey] = PdfBoolean.True });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using PdfObjectComparison comparison = PdfObjectComparison.Create(governor, CancellationToken.None);
        Assert.True(comparison.AreEqual(first, second)); Assert.Equal(first.GetHashCode(), comparison.Hash(second));
    }

    [Fact]
    public void DeepValuesUseBoundedIterativeStorageWithoutRecursiveClrCalls()
    {
        QpdfObject left = PdfBoolean.True, right = new PdfBoolean(true);
        for (int depth = 0; depth < 4096; depth++) { left = new PdfArray([left]); right = new PdfArray([right]); }
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 2 * 1024 * 1024L }, CancellationToken.None);
        using PdfObjectComparison comparison = PdfObjectComparison.Create(governor, CancellationToken.None);
        Assert.True(comparison.AreEqual(Dictionary(left), Dictionary(right))); Assert.True(governor.Snapshot().PeakMemoryBytes < 2 * 1024 * 1024L);
        comparison.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedByteOrDeepComparisonClearsBorrowedFramesAndPreservesPriorReceipt(bool bytes)
    {
        QpdfObject left = bytes ? new PdfByteString(new byte[4096], false) : PdfBoolean.True;
        QpdfObject right = bytes ? new PdfByteString(new byte[4096], false) : new PdfBoolean(true);
        if (!bytes) { for (int depth = 0; depth < 128; depth++) { left = new PdfArray([left]); right = new PdfArray([right]); } }
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = 64L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using PdfObjectComparison comparison = PdfObjectComparison.Create(governor, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => comparison.AreEqual(Dictionary(left), Dictionary(right)));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); comparison.Dispose(); Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Fact]
    public void WarmedBorrowedComparisonsAllocateNoKeyDisplayPayloadOrStackCopies()
    {
        PdfDictionary left = Dictionary(new PdfStream(Dictionary(new PdfInteger("0004", 4)), new byte[256]));
        PdfDictionary right = Dictionary(new PdfStream(Dictionary(new PdfInteger("0004", 4)), new byte[256]));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using PdfObjectComparison comparison = PdfObjectComparison.Create(governor, CancellationToken.None);
        for (int index = 0; index < 32; index++) { Assert.True(comparison.AreEqual(left, right)); }
        long before = GC.GetAllocatedBytesForCurrentThread(); bool equal = true;
        for (int index = 0; index < 128; index++) { equal &= comparison.AreEqual(left, right); }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before); Assert.True(equal); comparison.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
