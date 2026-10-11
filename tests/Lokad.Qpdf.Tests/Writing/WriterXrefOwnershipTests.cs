using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterXrefOwnershipTests
{
    internal static Task<long> Start(string mode, List<NumberedObject> model, QpdfLimits limits,
        ResourceGovernor governor, IByteSink sink, CancellationToken token)
    {
        PdfSerializer writer=new(limits,governor); byte[] id=new byte[16];
        IReadOnlyList<IReadOnlyList<int>> groups=[new[] {3,4,5}];
        if (mode=="qdf")
        { return writer.SerializeQdfAsync(new PdfVersion(1,7),model,groups,null,null,id,id,new QdfOptions(true,true),false,sink,token).AsTask(); }
        return writer.SerializeStreamedAsync(new PdfVersion(1,7),model,groups,mode=="compressed",null,null,id,id,false,sink,token).AsTask();
    }

    public static IEnumerable<object[]> Outcomes()
    {
        foreach (string mode in new[] {"raw","compressed","qdf"})
        { foreach (string point in new[] {"header","body","retired"})
          { foreach (string outcome in new[] {"success","failure","cancellation"}) { yield return [mode,point,outcome]; } } }
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task XrefResultStaysFundedOnlyThroughItsConsumers(string mode,string point,string outcome)
    {
        List<NumberedObject> model=WriterIndexOwnershipTests.Model(513); QpdfObject borrowed=model[2].Value;
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long baseline=governor.Snapshot().LiveMemoryBytes;
        using CancellationTokenSource cancellation=new(); PausedSink sink=new(point,outcome=="failure");
        Task<long> writing=Start(mode,model,QpdfLimits.Default,governor,sink,cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted); Assert.True(sink.BodyLength>0);
            if (point!="retired")
            { Assert.True(governor.Snapshot().LiveMemoryBytes>=baseline+sink.BodyLength); }
            if (outcome=="cancellation") { cancellation.Cancel(); } else {sink.Resume();}
            await WriterIndexOwnershipTests.Finish(writing,outcome);
            Assert.Equal(baseline,governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L,prior.ReservedBytes);
            Assert.Equal(513,model.Count); Assert.Same(borrowed,model[2].Value);
            GC.KeepAlive(writing); GC.KeepAlive(model); GC.KeepAlive(borrowed); GC.KeepAlive(sink);
        }
        finally {await Drain(writing,sink);}
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("qdf")]
    public async Task RepeatedFiniteWritersRetireXrefAdmission(string mode)
    {
        List<NumberedObject> model=WriterIndexOwnershipTests.Model(129);
        QpdfLimits limits=QpdfLimits.Default with {MaxMemoryBytes=32768L};
        ResourceGovernor governor=new(limits,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor); prior.Reserve(97L);
        for (int cycle=0;cycle<32;cycle++)
        {
            PausedSink sink=new("none",false);
            await Start(mode,model,limits,governor,sink,CancellationToken.None);
            Assert.True(sink.BodyLength>0); Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(97L,prior.ReservedBytes); Assert.Equal(129,model.Count);
        }
        Assert.True(governor.Snapshot().PeakMemoryBytes<=limits.MaxMemoryBytes);
    }

    internal static async Task Drain(Task writing,PausedSink sink)
    {
        sink.Resume(); try {await writing.WaitAsync(TimeSpan.FromSeconds(10));}
        catch (QpdfException) {} catch (OperationCanceledException) {}
    }

    internal sealed class PausedSink(string point,bool fail):IByteSink
    {
        private readonly TaskCompletionSource _entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _xref;
        private bool _nextBody;
        private bool _paused;
        internal Task Entered=>_entered.Task;
        internal int BodyLength {get;private set;}
        internal int PauseChunkLength {get;private set;}
        internal WeakReference? Buffer {get;private set;}
        public long Length {get;private set;}
        internal void Resume()=>_resume.TrySetResult();
        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk,CancellationToken token)
        {
            bool header=chunk.Span.IndexOf("/Type /XRef"u8)>=0;
            bool body=false,retired=false;
            if (header)
            {
                _xref=true; int offset=chunk.Span.IndexOf("/Length "u8)+8; int length=0;
                while (offset<chunk.Length && chunk.Span[offset] is >=48 and <=57)
                {length=checked(10*length+chunk.Span[offset++]-48);}
                BodyLength=length;
            }
            else if (_xref && _nextBody)
            {
                _nextBody=false; body=true;
                Assert.True(MemoryMarshal.TryGetArray(chunk,out ArraySegment<byte> segment));
                byte[] storage=segment.Array ?? throw new InvalidOperationException("Expected owned xref backing.");
                Buffer=new WeakReference(storage); Assert.Equal(BodyLength,storage.Length);
            }
            if (_xref && chunk.Span.EndsWith("stream\n"u8) && !chunk.Span.Contains((byte)0)) {_nextBody=true;}
            if (_xref && chunk.Span.StartsWith("\nendstream\nendobj\n"u8)) {retired=true;}
            bool selected=point switch {"header"=>header,"body"=>body,"retired"=>retired,"none"=>false,_=>throw new InvalidOperationException("Unknown xref pause point.")};
            if (!_paused && selected)
            {
                _paused=true; PauseChunkLength=chunk.Length; _entered.TrySetResult(); await _resume.Task.WaitAsync(token);
                if (fail) {throw new QpdfException("Authored xref output failure.");}
            }
            token.ThrowIfCancellationRequested(); Length+=chunk.Length;
        }
    }
}
