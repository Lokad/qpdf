using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FilterCodecOwnershipTests
{
    [Theory]
    [InlineData("FlateDecode")]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("RunLengthDecode")]
    public void EmptyDecodedResultRetiresScratchWithoutAdoptingAnArrayReceipt(string filter)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L);

        using FilterPlan stages = FilterPlan.Create(governor, CancellationToken.None);
        stages.Allocate(1);
        stages.Set(0, new(filter, null, null));
        long live = governor.Snapshot().LiveMemoryBytes;
        byte[] output = FilterChain.Decode(DecoderOwnershipFixtures.Encode(filter, Array.Empty<byte>()), stages, QpdfLimits.Default, governor, memory);
        Assert.Empty(output); Assert.Equal(97L, memory.ReservedBytes);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
        stages.Dispose(); memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FlateDecode")]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("RunLengthDecode")]
    public void ScopedResultHasItsExactReceiptAndLeavesBorrowedInputUsable(string filter)
    {
        byte[] raw = "\0\u00ff\n/Used 12 Tf"u8.ToArray(); byte[] encoded = DecoderOwnershipFixtures.Encode(filter, raw); byte[] original = encoded.ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L);
        using FilterPlan stages = FilterPlan.Create(governor, CancellationToken.None);
        stages.Allocate(filter == "" ? 0 : 1);
        if (filter != "") { stages.Set(0, new(filter, null, null)); }
        long prepared = governor.Snapshot().LiveMemoryBytes;
        byte[] output = FilterChain.Decode(encoded, stages, QpdfLimits.Default, governor, memory);
        Assert.Equal(raw, output); Assert.Equal(original, encoded); Assert.NotSame(encoded, output);
        Assert.Equal(97L + raw.Length + 32L, memory.ReservedBytes);
        Assert.Equal(prepared + raw.Length + 32L, governor.Snapshot().LiveMemoryBytes);
        stages.Dispose(); memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(raw, output);
    }

    [Theory]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("FlateDecode")]
    public void PartialCodecFailureRetiresScratchAndPreservesPriorFunding(string filter)
    {
        byte[] input = filter switch
        {
            "ASCIIHexDecode" => "ABCG>"u8.ToArray(),
            "ASCII85Decode" => "zzabz"u8.ToArray(),
            "LZWDecode" => DecoderOwnershipFixtures.Pack([256, 65, 66, 400]),
            _ => [0, 0, 0, 0, 0, 0, 0],
        };
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor);
        using FilterPlan stages = FilterPlan.Create(governor, CancellationToken.None);
        stages.Allocate(1);
        stages.Set(0, new(filter, null, null));
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfFilterException>(() => FilterChain.Decode(input, stages, QpdfLimits.Default, governor, decodedMemory));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Theory]
    [InlineData("ASCIIHexDecode")]
    [InlineData("FlateDecode")]
    public void LaterStageFailureReturnsOnlyItsChainArrays(string first)
    {
        byte[] input = DecoderOwnershipFixtures.Encode(first, [1, 2, 3]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L);
        using FilterPlan stages = FilterPlan.Create(governor, CancellationToken.None);
        stages.Allocate(2);
        stages.Set(0, new(first, null, null));
        stages.Set(1, new("FlateDecode", null, null));
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfFilterException>(() => FilterChain.Decode(input, stages, QpdfLimits.Default, governor, memory));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("RunLengthDecode")]
    [InlineData("FlateDecode")]
    public void SuccessfulChainsRetireIntermediateArraysBeforeReturning(string outer)
    {
        byte[] raw = "owned output"u8.ToArray(); byte[] inner = DecoderOwnershipFixtures.Encode("FlateDecode", raw);
        byte[] input = DecoderOwnershipFixtures.Encode(outer, inner); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using FilterPlan stages = FilterPlan.Create(governor, CancellationToken.None);
        stages.Allocate(2);
        stages.Set(0, new(outer, null, null));
        stages.Set(1, new("FlateDecode", null, null));
        long prepared = governor.Snapshot().LiveMemoryBytes;
        byte[] output = FilterChain.Decode(input, stages, QpdfLimits.Default, governor, memory);
        Assert.Equal(raw, output); Assert.Equal(raw.Length + 32L, memory.ReservedBytes);
        Assert.Equal(prepared + raw.Length + 32L, governor.Snapshot().LiveMemoryBytes);
        stages.Dispose(); memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class LoadingResolver : IIndirectResolver, IDisposable
    {
        private readonly ResourceGovernor _governor;
        private ParserMemoryScope? _nested;
        internal LoadingResolver(ResourceGovernor governor) { _governor = governor; }
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { _nested = ParserMemoryScope.Create(_governor); _nested.Reserve(101L); value = new PdfInteger("7", 7L); return true; }
        public void Dispose() => _nested?.Dispose();
    }
    [Fact]
    public void AFailedStagePreservesAnIndependentResolverLoadAcceptedAfterDecodingStarts()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using LoadingResolver resolver = new(governor);
        PdfDictionary parameters = DecoderOwnershipFixtures.Dictionary(("Predictor", new PdfReference(99, 0)));
        using ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor);
        using FilterPlan stages = FilterPlan.Create(governor, CancellationToken.None);
        stages.Allocate(2);
        stages.Set(0, new("ASCIIHexDecode", null, null));
        stages.Set(1, new("FlateDecode", parameters, resolver));
        long prepared = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfFilterException>(() => FilterChain.Decode("00>"u8.ToArray(), stages, QpdfLimits.Default, governor, decodedMemory));
        Assert.Equal(prepared + 165L, governor.Snapshot().LiveMemoryBytes);
        stages.Dispose(); decodedMemory.Dispose(); resolver.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
