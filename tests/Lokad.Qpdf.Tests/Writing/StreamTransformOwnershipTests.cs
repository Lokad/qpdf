using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(NameTableCollection))]
public sealed class StreamTransformOwnershipTests
{
    private static readonly MethodInfo Transform = typeof(Qpdf).GetMethod("TransformStream", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException();
    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
        return new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]), QpdfLimits.Default, governor, new(governor), QpdfRecoveryMode.Strict);
    }
    private static PdfStream Source() => new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("ASCIIHexDecode"), [PdfName.FromAscii("Keep")] = new PdfInteger("1", 1L) }), "61626364>"u8.ToArray());
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PdfStream Run(ResourceGovernor governor, QpdfLimits limits, bool compress)
    {
        using DocumentEngine engine = Engine();
        try { return Assert.IsType<PdfStream>(Transform.Invoke(null, [Source(), 4, engine, compress, "generalized", false, null, limits, governor, new DiagnosticBag(governor)])); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private sealed class Holder(object value, WeakReference governor)
    { internal object? Value = value; internal readonly WeakReference Governor = governor; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Holder Capture(ResourceGovernor governor, bool compress, string alias)
    {
        PdfStream stream = Run(governor, QpdfLimits.Default, compress);
        object value = alias switch
        {
            "bytes" => stream.AllocationCarrier, "keys" => stream.Dictionary.Keys,
            "header" => stream.Dictionary, "filter" => Get(stream.Dictionary, "Filter"), _ => stream
        };
        return new(value, new(governor));
    }
    private static QpdfObject Get(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : throw new InvalidOperationException();
    [Theory]
    [InlineData(false, "bytes")]
    [InlineData(false, "keys")]
    [InlineData(false, "header")]
    [InlineData(false, "stream")]
    [InlineData(true, "bytes")]
    [InlineData(true, "keys")]
    [InlineData(true, "header")]
    [InlineData(true, "stream")]
    [InlineData(true, "filter")]
    public void EscapingStorageRemainsFundedUntilItsLastAliasDies(bool compress, string alias)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Holder holder = Capture(governor, compress, alias); Collect(governor);
        Assert.True(governor.Snapshot().LiveMemoryBytes > 0);
        holder.Value = null; Collect(governor); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(holder);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Holder Completed(bool compress) => Capture(new(QpdfLimits.Default, CancellationToken.None), compress, "stream");
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputDoesNotAnchorCompletedExecutionState(bool compress)
    { Holder holder = Completed(compress); Collect(null); Assert.False(holder.Governor.IsAlive); GC.KeepAlive(holder.Value); }
    [Fact]
    public void InterruptedFilteringRetiresOnlyUnpublishedStorage()
    {
        int failures = 0, completions = 0;
        for (long maximum = 1; maximum < 3000; maximum += 7)
        {
            QpdfLimits limits = QpdfLimits.Default with { MaxWork = maximum };
            ResourceGovernor governor = new(limits, CancellationToken.None);
            using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
            long baseline = governor.Snapshot().LiveMemoryBytes;
            try { Assert.Equal("abcd"u8.ToArray(), Run(governor, limits, false).Data); completions++; }
            catch (QpdfLimitException error)
            { Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); failures++; }
            Assert.Equal(61L, prior.ReservedBytes);
        }
        Assert.True(failures > 0); Assert.True(completions > 0);
    }
    [Theory]
    [InlineData("memory")]
    [InlineData("output")]
    [InlineData("cancel")]
    public void EncodingFailurePreservesPriorAdmission(string kind)
    {
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = kind == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 4096L }
            : kind == "output" ? QpdfLimits.Default with { MaxOutputBytes = 1L } : QpdfLimits.Default;
        ResourceGovernor governor = new(limits, cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        if (kind == "cancel") { cancellation.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => Run(governor, limits, true)); }
        else { Assert.Throws<QpdfLimitException>(() => Run(governor, limits, true)); }
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes);
    }
    private static void Collect(ResourceGovernor? governor)
    { for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); governor?.ReserveMemory(0L); } }
}
