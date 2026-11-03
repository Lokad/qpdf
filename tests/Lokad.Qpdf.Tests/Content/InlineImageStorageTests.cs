using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class InlineImageStorageTests
{
    internal static ContentTokenizer DataTokenizer(ReadOnlySpan<byte> bytes, ResourceGovernor governor, CancellationToken token)
    {
        byte[] data = [.. " "u8, .. bytes, .. " EI"u8];
        return new(data, governor, token);
    }

    [Theory]
    [InlineData(16384, false)]
    [InlineData(262144, false)]
    [InlineData(16384, true)]
    [InlineData(262144, true)]
    public void BelowThresholdSkipsPayloadAdmission(int length, bool canceled)
    {
        byte[] bytes = new byte[length];
        using CancellationTokenSource cancellation = new();
        if (canceled) { cancellation.Cancel(); }
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32768L }, cancellation.Token);
        DiagnosticBag diagnostics = new(governor);
        PoolResolver resolver = new(new Dictionary<int, QpdfObject>());
        int suffix = 1;
        ContentTokenizer tokenizer = DataTokenizer(bytes, governor, cancellation.Token);
        byte[] header = Encoding.ASCII.GetBytes("/W 1 /H " + length + " /CS /G /BPC 8");
        long before = GC.GetAllocatedBytesForCurrentThread();
        InlineImageEntry? Convert() => InlineImageStorage.ReadWithResources(header, tokenizer,
            length + 2L, 0L, null, true, ref suffix, QpdfLimits.Default, governor, diagnostics, resolver, cancellation.Token);
        if (canceled) { Assert.ThrowsAny<OperationCanceledException>(() => Convert()); }
        else { Assert.Null(Convert()); }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 32768L);
        Assert.Equal(1, suffix); Assert.Empty(diagnostics.Entries);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void ThresholdUsesTheTokenizedPayloadLength(long minimum, bool converted)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PoolResolver resolver = new(new Dictionary<int, QpdfObject>());
        int suffix = 1;
        InlineImageEntry? entry = InlineImageStorage.ReadWithResources("/W 2 /H 1 /BPC 8 /CS /G"u8.ToArray(),
            DataTokenizer("ab"u8, governor, CancellationToken.None), minimum, 0L, null, true, ref suffix, QpdfLimits.Default, governor, diagnostics, resolver, CancellationToken.None);
        Assert.Equal(converted, entry.HasValue);
        if (entry is InlineImageEntry image) { Assert.Equal("ab "u8.ToArray(), image.Stream.Data.ToArray()); }
        Assert.Equal(converted ? 2 : 1, suffix);
        governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static PdfDictionary Empty()=>new(new Dictionary<PdfName,QpdfObject>());
    private static InlineImageEntry Create(ResourceGovernor governor, CancellationToken token)
    {
        int suffix=1; PoolResolver resolver=new(new Dictionary<int,QpdfObject>());
        return InlineImageStorage.ReadWithResources(Encoding.ASCII.GetBytes("/W 2 /H 1 /D [0 1] /F [/AHx /Fl] /Binary#FF#23 (text)"), DataTokenizer("abc"u8,governor,token),0L,0L,Empty(),true,ref suffix,QpdfLimits.Default,governor,new(governor),resolver,token)??throw new InvalidOperationException();
    }
    private sealed class Holder(object value,WeakReference governor)
    { internal object? Value=value; internal readonly WeakReference Governor=governor; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Holder Capture(ResourceGovernor governor,string alias)
    {
        InlineImageEntry entry=Create(governor,CancellationToken.None);
        object value=alias switch{"name"=>entry.Name,"bytes"=>entry.Stream.AllocationCarrier,"keys"=>entry.Stream.Dictionary.Keys,_=>entry.Stream};
        return new(value,new(governor));
    }
    [Theory]
    [InlineData("name")]
    [InlineData("bytes")]
    [InlineData("keys")]
    [InlineData("stream")]
    public void ImageAliasesRemainFundedUntilThePrivatePhaseEnds(string alias)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); Holder holder = Capture(governor, alias); Collect();
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 24L); holder.Value = null; Collect();
        governor.CompletePrivateStorage();
        Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(holder);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Holder Completed(string alias)=>Capture(new(QpdfLimits.Default,CancellationToken.None),alias);
    [Theory]
    [InlineData("name")]
    [InlineData("bytes")]
    [InlineData("keys")]
    [InlineData("stream")]
    public void EscapingImageAliasesDoNotAnchorCompletedExecutionState(string alias)
    { Holder holder = Completed(alias); Collect(); Assert.False(holder.Governor.IsAlive); GC.KeepAlive(holder.Value); }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawAndCompressedOutputRetainsOnlyEscapingStorage(bool compress)
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None); Holder holder=Output(governor,compress);
        Collect(); Assert.True(governor.Snapshot().LiveMemoryBytes > 0); holder.Value = null;
        governor.CompletePrivateStorage();
        Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);GC.KeepAlive(holder);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Holder Output(ResourceGovernor governor,bool compress)
    {using InlineImageBuffer output=InlineImageBuffer.Create(governor,QpdfLimits.Default,CancellationToken.None);output.Write("q Q\n"u8);PdfStream stream=output.Complete(compress,null,QpdfLimits.Default);return new(stream,new(governor));}
    [Fact]
    public void RootedDisposedBufferDropsControllerCollectionAndBacking()
    {
        (InlineImageBuffer owner,WeakReference governor,WeakReference buffer,WeakReference images)=Disposed();
        Collect(); Assert.False(governor.IsAlive); Assert.False(buffer.IsAlive); Assert.False(images.IsAlive); GC.KeepAlive(owner);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (InlineImageBuffer,WeakReference,WeakReference,WeakReference) Disposed()
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);InlineImageBuffer output=InlineImageBuffer.Create(governor,QpdfLimits.Default,CancellationToken.None);
        output.Write(new byte[32768]);
        object buffer=typeof(InlineImageBuffer).GetField("_output",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)?.GetValue(output)??throw new InvalidOperationException();
        object images=typeof(InlineImageBuffer).GetField("_images",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)?.GetValue(output)??throw new InvalidOperationException();
        output.Dispose();Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);return(output,new(governor),new(buffer),new(images));
    }
    private static void Collect()
    { { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } }
}
