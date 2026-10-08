using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class ParsedAllocationBatchTests
{
    private static PdfDictionary Parse(ResourceGovernor governor,ParserMemoryScope memory)
    {
        PdfParser parser=new(QpdfLimits.Default,governor,new(governor),null);
        return Assert.IsType<PdfDictionary>(parser.ParseValue(new(Encoding.ASCII.GetBytes("<< /Width 123 /Text (abc) /Array [/A /B] >>")),0,memory));
    }
    [Fact]
    public void FailedMixedHandoffRestoresSourceReceiptAndFreshGraphScope()
    {
        ResourceGovernor governor=new(QpdfLimits.Default with{MaxWork=1200L},CancellationToken.None);
        using ParserMemoryScope source=ParserMemoryScope.Create(governor);
        PdfDictionary parsed=Parse(governor,source);long sourceBytes=source.ReservedBytes;
        using OwnedAllocationBatch graph=OwnedAllocationBatch.Create(governor,2,CancellationToken.None);
        PdfNameTable<QpdfObject> entries=PdfDictionaryRebuild.CreateEntries(graph,out int allocation);
        using PdfDictionary.EntryEnumerator iterator=parsed.GetEntryEnumerator();
        while(iterator.MoveNext(governor,CancellationToken.None)){entries.Assign(graph.Memory,governor,iterator.Current.Key,iterator.Current.Value,CancellationToken.None);}
        PdfDictionary result=PdfDictionaryRebuild.Complete(graph,entries,allocation);
        long before=governor.Snapshot().LiveMemoryBytes;
        governor.ChargeWork(1200L-governor.Snapshot().TotalWork-8L);
        Assert.Throws<QpdfLimitException>(()=>graph.RetainParsed(parsed,source));
        Assert.Equal(sourceBytes,source.ReservedBytes);Assert.Equal(before,governor.Snapshot().LiveMemoryBytes);Assert.Equal(3,result.Count);Assert.Equal(3,parsed.Count);
    }
    [Fact]
    public void ConvertedBorrowedColorspaceDoesNotBecomeAnAcceptedParsedRoot()
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);PdfByteString borrowed=new(new byte[2*1024*1024],false);
        PdfDictionary spaces=new(new Dictionary<PdfName,QpdfObject>{[PdfName.FromAscii("Named")]=borrowed});
        PdfDictionary resources=new(new Dictionary<PdfName,QpdfObject>{[PdfName.FromAscii("ColorSpace")]=spaces});
        int suffix=1;
        Lokad.Qpdf.Content.InlineImageEntry image=Lokad.Qpdf.Content.InlineImageStorage.Create("/CS /Named /W 1"u8.ToArray(),"abc"u8.ToArray(),0L,0L,resources,ref suffix,QpdfLimits.Default,governor,new(governor),new(new Dictionary<int,QpdfObject>()),CancellationToken.None)??throw new InvalidOperationException();
        Assert.True(image.Stream.Dictionary.TryGet(PdfName.FromAscii("ColorSpace"),out QpdfObject? found));Assert.Same(borrowed,found);
        Assert.True(governor.Snapshot().PeakMemoryBytes<16384L);GC.KeepAlive(image);GC.KeepAlive(resources);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Capture(ResourceGovernor governor,bool text)
    {
        using ParserMemoryScope source=ParserMemoryScope.Create(governor);PdfDictionary parsed=Parse(governor,source);
        using OwnedAllocationBatch graph=OwnedAllocationBatch.Create(governor,2,CancellationToken.None);
        PdfNameTable<QpdfObject> entries=PdfDictionaryRebuild.CreateEntries(graph,out int allocation);
        using PdfDictionary.EntryEnumerator iterator=parsed.GetEntryEnumerator();
        while(iterator.MoveNext(governor,CancellationToken.None)){entries.Assign(graph.Memory,governor,iterator.Current.Key,iterator.Current.Value,CancellationToken.None);}
        PdfDictionary result=PdfDictionaryRebuild.Complete(graph,entries,allocation);graph.RetainParsed(parsed,source);
        if(!text){return result.Keys;}
        Assert.True(result.TryGet(PdfName.FromAscii("Width"),out QpdfObject? value));return Assert.IsType<PdfInteger>(value).Text;
    }
    private sealed class Holder(object value){internal object? Value=value;}
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsedNumericTextAndFreshKeyViewKeepTheirIndependentReceipts(bool text)
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);Holder holder=MakeHolder(governor,text);Collect(governor);
        Assert.True(governor.Snapshot().LiveMemoryBytes>=32L);holder.Value=null;Collect(governor);Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);GC.KeepAlive(holder);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Holder MakeHolder(ResourceGovernor governor,bool text)=>new(Capture(governor,text));
    private static void Collect(ResourceGovernor governor){for(int round=0;round<4;round++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();governor.ReserveMemory(0L);}}
}
