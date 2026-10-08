using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterRowOwnershipTests
{
    public static IEnumerable<object[]> Outcomes()
    {
        foreach (string route in new[] {"ordinary-streamed","qdf","qdf-streamed"})
        {foreach (string point in new[] {"version","object","retired"})
         {foreach (string outcome in new[] {"success","failure","cancellation"}) {yield return [route,point,outcome];}}}
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task RowsAndClassicRebuiltGraphStayFundedAndRetireOnEveryOutcome(string route,string point,string outcome)
    {
        List<NumberedObject> model=WriterIndexOwnershipTests.Model(513);QpdfObject borrowed=model[2].Value;
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor);prior.Reserve(97L);
        using CancellationTokenSource cancellation=new();
        string pause=point=="retired" ? route.EndsWith("streamed",StringComparison.Ordinal) ? "streamed-retired" : "classic-retired" : point;
        WriterIndexOwnershipTests.PausedSink sink=new(pause,outcome=="failure");
        Task writing=WriterIndexOwnershipTests.Start(route,model,QpdfLimits.Default,governor,sink,cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (point!="retired")
            {
                long index=route switch
                {
                    "ordinary-streamed"=>WriterIndexOwnershipTests.IndexBytes(513)+WriterIndexOwnershipTests.IndexBytes(0)+
                        (point=="version" ? 0L : WriterIndexOwnershipTests.IndexBytes(1)),
                    "qdf"=>WriterIndexOwnershipTests.IndexBytes(1)+(point=="version" ? 0L : 160L+513L*sizeof(long)),
                    "qdf-streamed"=>2L*WriterIndexOwnershipTests.IndexBytes(513)+3L*WriterIndexOwnershipTests.IndexBytes(3)+
                        (point=="version" ? 0L : WriterIndexOwnershipTests.IndexBytes(1)),
                    _=>throw new InvalidOperationException("Unknown row route."),
                };
                long rows=route switch
                {
                    "ordinary-streamed"=>point=="version" ? 0L : 160L+513L*sizeof(int),
                    // Three authored catalog/page dictionaries, their references and arrays: 1332 bytes.
                    "qdf"=>128L+1332L+160L+513L*32L,
                    "qdf-streamed"=>160L+513L*32L+176L+(point=="version" ? 0L : 436L),
                    _=>throw new InvalidOperationException("Unknown row route."),
                };
                Assert.Equal(161L+(point=="version" ? 169L : 168L)+index+rows,governor.Snapshot().LiveMemoryBytes);
            }
            if (outcome=="cancellation") {cancellation.Cancel();}else {sink.Resume();}
            await WriterIndexOwnershipTests.Finish(writing,outcome);
            Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes);Assert.Equal(97L,prior.ReservedBytes);
            Assert.Equal(513,model.Count);Assert.Same(borrowed,model[2].Value);
            GC.KeepAlive(writing);GC.KeepAlive(sink);GC.KeepAlive(model);GC.KeepAlive(borrowed);
        }
        finally {await WriterIndexOwnershipTests.Drain(writing,sink);}
    }

    [Theory]
    [InlineData("ordinary-streamed")]
    [InlineData("qdf")]
    [InlineData("qdf-streamed")]
    public async Task RepeatedFiniteRowsReleaseTheirOwnAdmission(string route)
    {
        List<NumberedObject> model=WriterIndexOwnershipTests.Model(129);
        QpdfLimits limits=QpdfLimits.Default with {MaxMemoryBytes=32768L};ResourceGovernor governor=new(limits,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor);prior.Reserve(97L);
        for(int cycle=0;cycle<32;cycle++)
        {
            CountingSink sink=new();await WriterIndexOwnershipTests.Start(route,model,limits,governor,sink,CancellationToken.None);
            Assert.True(sink.Length>0);Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(97L,prior.ReservedBytes);Assert.Equal(129,model.Count);
        }
        Assert.True(governor.Snapshot().PeakMemoryBytes<=32768L);
    }

    [Fact]
    public async Task ClassicQdfBorrowsLargePayloadUnderFiniteMetadataCeiling()
    {
        List<NumberedObject> model=WriterIndexOwnershipTests.Model(4);
        PdfStream original=new(new PdfDictionary(new Dictionary<PdfName,QpdfObject>()),new byte[2*1024*1024]);
        model[3]=new NumberedObject(4,original);
        Assert.True(MemoryMarshal.TryGetArray(original.RawMemory,out ArraySegment<byte> segment));
        byte[] backing=segment.Array ?? throw new InvalidOperationException("Expected borrowed stream backing.");
        QpdfLimits limits=QpdfLimits.Default with {MaxMemoryBytes=32768L};ResourceGovernor governor=new(limits,CancellationToken.None);
        BorrowedSink sink=new(backing);
        long before=GC.GetAllocatedBytesForCurrentThread();
        await WriterIndexOwnershipTests.Start("qdf",model,limits,governor,sink,CancellationToken.None);
        Assert.True(GC.GetAllocatedBytesForCurrentThread()-before<131072L);
        Assert.Equal(backing.Length,sink.BorrowedBytes);Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);Assert.True(governor.Snapshot().PeakMemoryBytes<=32768L);
        Assert.Equal(2*1024*1024,original.RawMemory.Length);Assert.Same(original,model[3].Value);
        byte[] publicCopy=original.Data;Assert.NotSame(backing,publicCopy);publicCopy[0]=255;Assert.Equal(0,original.RawMemory.Span[0]);
    }

    private sealed class BorrowedSink(byte[] expected):IByteSink
    {
        internal int BorrowedBytes{get;private set;}public long Length{get;private set;}
        public ValueTask AppendAsync(ReadOnlyMemory<byte> data,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(MemoryMarshal.TryGetArray(data,out ArraySegment<byte> segment) && ReferenceEquals(expected,segment.Array))
            {Assert.Equal(BorrowedBytes,segment.Offset);BorrowedBytes+=data.Length;}
            Length+=data.Length;return ValueTask.CompletedTask;
        }
    }
}
