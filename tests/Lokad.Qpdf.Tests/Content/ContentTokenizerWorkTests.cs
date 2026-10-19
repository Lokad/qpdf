using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ContentTokenizerWorkTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GovernorCancellationStopsEvenEmptyContent(bool inline)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        ContentTokenizer tokenizer = new(ReadOnlyMemory<byte>.Empty, QpdfLimits.Default, governor); source.Cancel();
        if (inline) { Assert.ThrowsAny<OperationCanceledException>(() => tokenizer.TryReadInlineImageData(out _)); }
        else { Assert.ThrowsAny<OperationCanceledException>(() => tokenizer.TryReadToken(out _)); }
    }
}

public sealed class ContentTokenizerCallerCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeparateCallerCancellationStopsBeforeTheNextScan(bool inline)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ContentTokenizer tokenizer = new("ID x EI q"u8.ToArray(), QpdfLimits.Default, governor, source.Token);
        Assert.True(tokenizer.TryReadToken(out _)); source.Cancel();
        if (inline) { Assert.ThrowsAny<OperationCanceledException>(() => tokenizer.TryReadInlineImageData(out _)); }
        else { Assert.ThrowsAny<OperationCanceledException>(() => tokenizer.TryReadToken(out _)); }
    }
}
