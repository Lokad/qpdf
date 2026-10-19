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
    public void CanceledMixedHandoffPreservesSourceReceiptAndFreshGraphScope()
    {
        using CancellationTokenSource cancellation=new();
        ResourceGovernor governor=new(QpdfLimits.Default,cancellation.Token);
        using ParserMemoryScope source=ParserMemoryScope.Create(governor);
        PdfDictionary parsed=Parse(governor,source);long sourceBytes=source.ReservedBytes;
        using OwnedAllocationBatch graph=OwnedAllocationBatch.Create(governor,2,CancellationToken.None);
        PdfNameTable<QpdfObject> entries=PdfDictionaryRebuild.CreateEntries(graph,out long allocation);
        using PdfDictionary.EntryEnumerator iterator=parsed.GetEntryEnumerator();
        while(iterator.MoveNext(governor,CancellationToken.None)){entries.Assign(graph.Memory,governor,iterator.Current.Key,iterator.Current.Value,CancellationToken.None);}
        PdfDictionary result=PdfDictionaryRebuild.Complete(graph,entries,allocation);
        long before=governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>graph.RetainParsed(source));
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
    private static object Capture(ResourceGovernor governor,bool text)
    {
        using ParserMemoryScope source=ParserMemoryScope.Create(governor);PdfDictionary parsed=Parse(governor,source);
        using OwnedAllocationBatch graph=OwnedAllocationBatch.Create(governor,2,CancellationToken.None);
        PdfNameTable<QpdfObject> entries=PdfDictionaryRebuild.CreateEntries(graph,out long allocation);
        using PdfDictionary.EntryEnumerator iterator=parsed.GetEntryEnumerator();
        while(iterator.MoveNext(governor,CancellationToken.None)){entries.Assign(graph.Memory,governor,iterator.Current.Key,iterator.Current.Value,CancellationToken.None);}
        PdfDictionary result=PdfDictionaryRebuild.Complete(graph,entries,allocation);graph.RetainParsed(source);
        if(!text){return result.Keys;}
        Assert.True(result.TryGet(PdfName.FromAscii("Width"),out QpdfObject? value));return Assert.IsType<PdfInteger>(value).Text;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsedNumericTextAndKeyViewRemainUsableAfterOwnerCleanup(bool text)
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        object alias=Capture(governor,text);
        if(text){Assert.Equal("123",Assert.IsType<string>(alias));}
        else
        {
            Assert.Equal(new[]{"Width","Text","Array"},
                Assert.IsAssignableFrom<IEnumerable<PdfName>>(alias).Select(name=>name.DisplayText));
        }
    }
}
