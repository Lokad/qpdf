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

    [Fact]
    public void SeparateCallerTokenStopsNumericCopyAfterObservedWork()
    {
        using CancellationTokenSource canceled = new(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null, canceled.Token);
        byte[] data = new byte[8 * 1024 * 1024]; Array.Fill(data, (byte)'0');
        using ManualResetEventSlim started = new(); using ManualResetEventSlim finished = new();
        bool observed = false;
        Thread controller = new(() =>
        {
            started.Set();
            while (!finished.IsSet)
            {
                if (governor.Snapshot().TotalWork >= 8L) { observed = true; canceled.Cancel(); return; }
                Thread.Yield();
            }
        });
        controller.Start(); started.Wait();
        try { Assert.ThrowsAny<OperationCanceledException>(() => Run(parser, data)); }
        finally { finished.Set(); Assert.True(controller.Join(TimeSpan.FromSeconds(10))); }
        Assert.True(observed); Assert.InRange(governor.Snapshot().TotalWork, 8L, 2048L);
    }

    [Fact]
    public void OrdinaryParserStillAcceptsIndirectReferences()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfReference value = Assert.IsType<PdfReference>(parser.ParseValue(new PdfTokenizer("17 0 R"u8.ToArray()), 0));
        Assert.Equal(17, value.ObjectNumber); Assert.Equal(0, value.Generation);
    }
}
