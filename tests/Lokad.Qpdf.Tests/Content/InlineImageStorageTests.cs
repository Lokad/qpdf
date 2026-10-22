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
    private static PdfDictionary Empty()=>new(new Dictionary<PdfName,QpdfObject>());
    private static InlineImageEntry Create(ResourceGovernor governor, CancellationToken token)
    {
        int suffix=1; PoolResolver resolver=new(new Dictionary<int,QpdfObject>());
        return InlineImageStorage.CreateWithResources(Encoding.ASCII.GetBytes("/W 2 /H 1 /D [0 1] /F [/AHx /Fl] /Binary#FF#23 (text)"), "abc"u8.ToArray(),0L,0L,Empty(),ref suffix,QpdfLimits.Default,governor,new(governor),resolver,token)??throw new InvalidOperationException();
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
