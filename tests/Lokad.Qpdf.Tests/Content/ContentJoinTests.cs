using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ContentJoinTests
{
    // Bind the private joining path on both runtimes; inputs, ceilings and
    // assertions stay identical. The older returned array has no owner to close.
    private static readonly MethodInfo JoinMethod = typeof(ContentTransforms).GetMethod("JoinContentBytes", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing content join.");
    private static (ReadOnlyMemory<byte> Bytes, IDisposable? Owner) Join(List<byte[]> parts, ResourceGovernor governor, CancellationToken token)
    {
        object value;
        try
        {
            value = JoinMethod.Invoke(null, JoinMethod.GetParameters().Length == 2 ? [parts, governor] : [parts, governor, token])
                ?? throw new InvalidOperationException("Missing joined content.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is Exception inner)
        { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
        if (value is byte[] legacy) { return (legacy, null); }
        PropertyInfo bytes = value.GetType().GetProperty("Bytes", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing content bytes.");
        return ((ReadOnlyMemory<byte>)(bytes.GetValue(value) ?? throw new InvalidOperationException("Missing bytes.")), (IDisposable)value);
    }
    private static List<byte[]> Parts(params string[] parts) => parts.Select(Encoding.Latin1.GetBytes).ToList();

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("", new[] { "" })]
    [InlineData("\n", new[] { "", "" })]
    [InlineData("\n\n", new[] { "", "", "" })]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\nb", new[] { "a\n", "b" })]
    [InlineData("a\n\nb", new[] { "a\n", "", "b" })]
    [InlineData("a\n", new[] { "a", "" })]
    [InlineData("a\r\nb", new[] { "a\r", "b" })]
    [InlineData("a\n\nb", new[] { "a", "\nb" })]
    [InlineData("\u0000\u00ff\n\u00fe", new[] { "\u0000\u00ff", "\u00fe" })]
    public void ExistingNewlineAndBinaryRulesStayExact(string expected, string[] parts)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        var joined = Join(Parts(parts), governor, CancellationToken.None);
        try { Assert.Equal(Encoding.Latin1.GetBytes(expected), joined.Bytes.ToArray()); }
        finally { joined.Owner?.Dispose(); }
    }

    [Fact]
    public void CompletedAnalysisReleasesJoinedStorageWithoutTouchingBorrowedInputs()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long live = governor.Snapshot().LiveMemoryBytes; List<byte[]> parts = Parts("a", "b");
        var joined = Join(parts, governor, CancellationToken.None);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= live + 3L);
        Assert.Equal("a\nb"u8.ToArray(), joined.Bytes.ToArray());
        joined.Owner?.Dispose(); joined = default;
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal("a"u8.ToArray(), parts[0]); Assert.Equal("b"u8.ToArray(), parts[1]);
    }

    [Fact]
    public void RepeatedAnalysesFitOneFiniteBufferBudget()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, CancellationToken.None);
        List<byte[]> parts = [new byte[16384]];
        for (int cycle = 0; cycle < 32; cycle++)
        {
            var joined = Join(parts, governor, CancellationToken.None);
            Assert.Equal(16384, joined.Bytes.Length); joined.Owner?.Dispose(); joined = default;
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreCancellationCannotAllocateJoinedStorage(bool governorToken)
    {
        using CancellationTokenSource source = new(); source.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => Join(Parts("a", "b"), governor, governorToken ? CancellationToken.None : source.Token));
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);     }
}
