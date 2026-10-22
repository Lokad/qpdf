using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FilterCallerCancellationTests
{
    private sealed class CancellingResolver : IIndirectResolver
    {
        private readonly CancellationTokenSource _source;
        private readonly ResourceGovernor _governor;
        internal long DecodedAtCancellation;
        internal CancellingResolver(CancellationTokenSource source, ResourceGovernor governor) { _source = source; _governor = governor; }
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { DecodedAtCancellation = _governor.Snapshot().TotalDecodedBytes; _source.Cancel(); value = new PdfInteger("1", 1L); return true; }
    }
    [Theory]
    [InlineData("FlateDecode")]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("RunLengthDecode")]
    public void CallerCancellationAfterOneDecodedStageRetiresOnlyChainStorage(string outer)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L);
        byte[] inner = DecoderOwnershipFixtures.Encode("FlateDecode", "owned"u8.ToArray()); byte[] input = DecoderOwnershipFixtures.Encode(outer, inner);
        CancellingResolver resolver = new(source, governor);
        PdfDictionary parameters = DecoderOwnershipFixtures.Dictionary(("Predictor", new PdfReference(99, 0)));
        using FilterPlan stages = FilterPlan.Create(governor, CancellationToken.None);
        stages.Allocate(2);
        stages.Set(0, new(outer, null, null));
        stages.Set(1, new("FlateDecode", parameters, resolver));
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => FilterChain.Decode(input, stages, QpdfLimits.Default, governor, memory, source.Token));
        Assert.Equal(inner.Length, resolver.DecodedAtCancellation); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, memory.ReservedBytes);
    }
}
