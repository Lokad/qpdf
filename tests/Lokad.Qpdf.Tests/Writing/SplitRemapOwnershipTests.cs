using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SplitRemapOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeChangedContainerRefusesAndItsAttemptRefunds(bool dictionary)
    {
        PdfReference reference = new(7, 0);
        QpdfObject source = dictionary
            ? new PdfDictionary(Enumerable.Range(0, 262144).ToDictionary(i => PdfName.FromAscii("K" + i), _ => (QpdfObject)reference))
            : new PdfArray(Enumerable.Repeat<QpdfObject>(reference, 262144).ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope attempt = ParserMemoryScope.Create(governor))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<QpdfLimitException>(() => ObjectRemap.RemapIds(source, new Dictionary<int, int> { [7] = 8 }, attempt, governor, CancellationToken.None));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0L, 65536L);
        }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, prior.ReservedBytes);
        Assert.Equal(7, reference.ObjectNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherTokenCancelsThePlainMapPathBeforeConstruction(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        long before = governor.Snapshot().LiveMemoryBytes; cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ObjectRemap.RemapIds(new PdfReference(7, 1), new Dictionary<int, int> { [7] = 8 },
            memory, governor, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedOrDeepGraphsRefundTheirAttemptAndPreserveTheSource(bool depth)
    {
        QpdfObject source = depth ? new PdfArray([new PdfArray([new PdfArray([new PdfReference(7, 0)])])])
            : new PdfArray([new PdfReference(7, 0), new PdfReference(99, 0)]);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxNestingDepth = 2 }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope attempt = ParserMemoryScope.Create(governor))
        {
            QpdfException failure = Assert.ThrowsAny<QpdfException>(() => ObjectRemap.RemapIds(source, new Dictionary<int, int> { [7] = 8 }, attempt, governor, CancellationToken.None));
            if (depth) { Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), Assert.IsType<QpdfLimitException>(failure).LimitName); }
            else { Assert.Contains("unmapped object 99", failure.Message, StringComparison.Ordinal); }
        }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, prior.ReservedBytes);
        if (!depth) { Assert.Equal(7, Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(source)[0]).ObjectNumber); }
    }

    [Fact]
    public void RebuiltStreamRetainsBorrowedPayloadAndUnchangedValuesAfterScopeClosure()
    {
        PdfName key = new([0, 35, 255]); PdfByteString text = new([73, 0, 255], true);
        PdfArray unchanged = new([text, new PdfReference(8, 0)]);
        PdfStream source = new(new(new Dictionary<PdfName, QpdfObject> {
            [key] = unchanged, [PdfName.FromAscii("Changed")] = new PdfReference(7, 9) }), new byte[2 * 1024 * 1024]);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        PdfStream mapped;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            mapped = Assert.IsType<PdfStream>(ObjectRemap.RemapIds(source, new Dictionary<int, int> { [7] = 8, [8] = 8 }, memory, governor, CancellationToken.None));
            Assert.True(memory.ReservedBytes > 0L);
            Assert.Same(source.AllocationCarrier, mapped.AllocationCarrier);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Contains(mapped.Dictionary.Keys, candidate => ReferenceEquals(candidate, key));
        Assert.True(mapped.Dictionary.TryGet(key, out QpdfObject kept)); Assert.Same(unchanged, kept);
        Assert.Equal(new byte[] { 73, 0, 255 }, text.Value);
        Assert.True(mapped.Dictionary.TryGet(PdfName.FromAscii("Changed"), out QpdfObject changed));
        PdfReference reference = Assert.IsType<PdfReference>(changed);
        Assert.Equal(8, reference.ObjectNumber); Assert.Equal(0, reference.Generation);
        Assert.Equal(2 * 1024 * 1024, mapped.RawMemory.Length);
    }
}
