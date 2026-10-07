using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ContentTokenizerWorkTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("word")]
    [InlineData("integer")]
    [InlineData("fraction")]
    [InlineData("literal")]
    [InlineData("hex")]
    [InlineData("space")]
    [InlineData("comment")]
    [InlineData("included-space")]
    [InlineData("included-comment")]
    [InlineData("image")]
    [InlineData("probe-name")]
    [InlineData("probe-word")]
    [InlineData("probe-literal")]
    [InlineData("probe-hex")]
    [InlineData("probe-comment")]
    [InlineData("probe-space")]
    public void EveryLongTokenAndInlineProbeHonorsFiniteWork(string shape)
    {
        string run = new('a', 8192), digits = new('1', 8192), spaces = new(' ', 8192);
        string text = shape switch
        {
            "name" => "/" + run, "word" => run, "integer" => digits, "fraction" => "." + digits,
            "literal" => "(" + run + ")", "hex" => "<" + run + ">", "space" or "included-space" => spaces,
            "comment" or "included-comment" => "%" + run,
            "image" => "ID " + run + " EI q", "probe-name" => "ID x EI /" + run,
            "probe-word" => "ID x EI " + run, "probe-literal" => "ID x EI (" + run + ")",
            "probe-hex" => "ID x EI <" + run + ">", "probe-comment" => "ID x EI %" + run,
            "probe-space" => "ID x EI " + spaces, _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 128L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        ContentTokenizer tokenizer = new(Encoding.ASCII.GetBytes(text), limits, governor)
        { IncludeComments = shape == "included-comment", IncludeWhitespace = shape == "included-space" };
        if (shape == "image" || shape.StartsWith("probe-", StringComparison.Ordinal))
        {
            Assert.True(tokenizer.TryReadToken(out ContentToken id)); Assert.Equal("ID"u8.ToArray(), id.Data.ToArray());
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => tokenizer.TryReadInlineImageData(out _));
            Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        }
        else
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => tokenizer.TryReadToken(out _)); Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        }
        Assert.InRange(governor.Snapshot().TotalWork, 128L, 129L); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GovernorCancellationStopsEvenEmptyContent(bool inline)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        ContentTokenizer tokenizer = new(ReadOnlyMemory<byte>.Empty, QpdfLimits.Default, governor); source.Cancel();
        if (inline) { Assert.ThrowsAny<OperationCanceledException>(() => tokenizer.TryReadInlineImageData(out _)); }
        else { Assert.ThrowsAny<OperationCanceledException>(() => tokenizer.TryReadToken(out _)); }
        Assert.Equal(0L, governor.Snapshot().TotalWork);
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
        Assert.True(tokenizer.TryReadToken(out _)); long work = governor.Snapshot().TotalWork; source.Cancel();
        if (inline) { Assert.ThrowsAny<OperationCanceledException>(() => tokenizer.TryReadInlineImageData(out _)); }
        else { Assert.ThrowsAny<OperationCanceledException>(() => tokenizer.TryReadToken(out _)); }
        Assert.Equal(work, governor.Snapshot().TotalWork);
    }
}
