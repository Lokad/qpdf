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
        long live = governor.Snapshot().LiveMemoryBytes;
        byte[] output = FilterChain.Decode(DecoderOwnershipFixtures.Encode(filter, Array.Empty<byte>()),
            [new(filter, null, null)], QpdfLimits.Default, governor, memory);
        Assert.Empty(output); Assert.Equal(97L, memory.ReservedBytes);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
        memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
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
        FilterStage[] stages = filter == "" ? [] : [new(filter, null, null)];
        byte[] output = FilterChain.Decode(encoded, stages, QpdfLimits.Default, governor, memory);
        Assert.Equal(raw, output); Assert.Equal(original, encoded); Assert.NotSame(encoded, output);
        Assert.Equal(97L + raw.Length + 32L, memory.ReservedBytes);
        Assert.Equal(64L + memory.ReservedBytes, governor.Snapshot().LiveMemoryBytes);
        memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(raw, output);
    }

    [Theory]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("RunLengthDecode")]
    public void EncodedWhitespaceAndIgnoredCodesObserveFiniteWork(string filter)
    {
        byte[] input = filter switch
        {
            "ASCIIHexDecode" => Encoding.ASCII.GetBytes(new string(' ', 20000) + ">"),
            "ASCII85Decode" => Encoding.ASCII.GetBytes(new string(' ', 20000) + "~>"),
            "LZWDecode" => [.. DecoderOwnershipFixtures.Pack([256, 257]), .. new byte[20000]],
            _ => Enumerable.Repeat((byte)128, 20000).ToArray(),
        };
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 64L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L); long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => FilterChain.Decode(input, [new(filter, null, null)], limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 0L);
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
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L); long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfFilterException>(() => FilterChain.Decode(input, [new(filter, null, null)], QpdfLimits.Default, governor));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Theory]
    [InlineData("ASCIIHexDecode")]
    [InlineData("FlateDecode")]
    public void LaterStageFailureReturnsOnlyItsChainArrays(string first)
    {
        byte[] input = DecoderOwnershipFixtures.Encode(first, [1, 2, 3]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L); long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfFilterException>(() => FilterChain.Decode(input, [new(first, null, null), new("FlateDecode", null, null)], QpdfLimits.Default, governor, memory));
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
        byte[] output = FilterChain.Decode(input, [new(outer, null, null), new("FlateDecode", null, null)], QpdfLimits.Default, governor, memory);
        Assert.Equal(raw, output); Assert.Equal(raw.Length + 32L, memory.ReservedBytes);
        Assert.Equal(raw.Length + 32L + 64L, governor.Snapshot().LiveMemoryBytes);
        memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
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
        Assert.Throws<QpdfFilterException>(() => FilterChain.Decode("00>"u8.ToArray(),
            [new("ASCIIHexDecode", null, null), new("FlateDecode", parameters, resolver)], QpdfLimits.Default, governor));
        Assert.Equal(165L, governor.Snapshot().LiveMemoryBytes); resolver.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
