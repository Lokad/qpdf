using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class NumericCopyCancellationTests
{
    private static readonly MethodInfo Copy = typeof(PdfParser).GetMethod("CopyNumericText", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new InvalidOperationException();

    private static string Run(PdfParser parser, byte[] data)
    {
        try { return Assert.IsType<string>(Copy.Invoke(parser, [(ReadOnlyMemory<byte>)data])); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumericCopyHonorsBothTokensWithoutTouchingPriorAdmission(bool caller)
    {
        using CancellationTokenSource canceled = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : canceled.Token);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null, caller ? canceled.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long baseline = governor.Snapshot().LiveMemoryBytes; byte[] data = "0007"u8.ToArray(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Run(parser, data));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeparateCallerTokenStopsNumericCopyAfterObservedWork(bool governorToken)
    {
        using CancellationTokenSource canceled = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? canceled.Token : CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null,
            governorToken ? CancellationToken.None : canceled.Token);
        byte[] data = new byte[8 * 1024 * 1024]; Array.Fill(data, (byte)'0');
        int blocks = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => parser.CopyNumericTextObserved(data, () =>
        {
            if (++blocks == 8) { canceled.Cancel(); }
        }));
        Assert.Equal(8, blocks);
    }

    [Fact]
    public void OrdinaryParserStillAcceptsIndirectReferences()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        using ParserMemoryScope parsedMemory0 = ParserMemoryScope.Create(governor);
        PdfReference value = Assert.IsType<PdfReference>(parser.ParseValue(new PdfTokenizer("17 0 R"u8.ToArray()), 0, parsedMemory0));
        parsedMemory0.RetainGraph(); parsedMemory0.Dispose();
        Assert.Equal(17, value.ObjectNumber); Assert.Equal(0, value.Generation);
    }
}
