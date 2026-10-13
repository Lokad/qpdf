using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterIndexOwnershipTests
{
    internal static List<NumberedObject> Model(int count)
    {
        List<NumberedObject> model = new(count)
        {
            new(1, new PdfDictionary(new Dictionary<PdfName,QpdfObject>
                { [PdfName.FromAscii("Type")] = PdfName.FromAscii("Catalog"), [PdfName.FromAscii("Pages")] = new PdfReference(2,0) })),
            new(2, new PdfDictionary(new Dictionary<PdfName,QpdfObject>
                { [PdfName.FromAscii("Type")] = PdfName.FromAscii("Pages"), [PdfName.FromAscii("Count")] = new PdfInteger("1",1),
                  [PdfName.FromAscii("Kids")] = new PdfArray([new PdfReference(3,0)]) })),
            new(3, new PdfDictionary(new Dictionary<PdfName,QpdfObject>
                { [PdfName.FromAscii("Type")] = PdfName.FromAscii("Page"), [PdfName.FromAscii("Parent")] = new PdfReference(2,0),
                  [PdfName.FromAscii("MediaBox")] = new PdfArray([new PdfInteger("0",0),new PdfInteger("0",0),new PdfInteger("100",100),new PdfInteger("100",100)]),
                  [PdfName.FromAscii("Resources")] = new PdfDictionary(new Dictionary<PdfName,QpdfObject>()) })),
        };
        for (int number = 4; number <= count; number++) { model.Add(new NumberedObject(number,PdfNull.Instance)); }
        return model;
    }

    internal static Task<long> Start(string route, List<NumberedObject> model, QpdfLimits limits,
        ResourceGovernor governor, IByteSink sink, CancellationToken token)
    {
        PdfSerializer writer = new(limits,governor);
        PdfVersion version = new(1,7); byte[] id = new byte[16];
        IReadOnlyList<IReadOnlyList<int>> groups = [new[] {3,4,5}];
        return route switch
        {
            "ordinary" => writer.SerializeAsync(version,model,null,id,id,false,sink,token).AsTask(),
            "ordinary-streamed" => writer.SerializeStreamedAsync(version,model,groups,false,null,null,id,id,false,sink,token).AsTask(),
            "qdf" => writer.SerializeQdfAsync(version,model,null,null,null,id,id,new QdfOptions(true,true),false,sink,token).AsTask(),
            "qdf-streamed" => writer.SerializeQdfAsync(version,model,groups,null,null,id,id,new QdfOptions(true,true),false,sink,token).AsTask(),
            _ => throw new InvalidOperationException("Unknown writer route."),
        };
    }

    public static IEnumerable<object[]> AwaitedOutcomes()
    {
        foreach (string route in new[] {"ordinary","ordinary-streamed","qdf","qdf-streamed"})
        {
            foreach (string point in new[] {"version","object"})
            {
                foreach (string outcome in new[] {"success","failure","cancellation"}) { yield return [route,point,outcome]; }
            }
        }
    }

    [Theory]
    [MemberData(nameof(AwaitedOutcomes))]
    public async Task IntegerMetadataStaysFundedThroughOutputAndRetiresOnEveryOutcome(string route, string point, string outcome)
    {
        List<NumberedObject> model = Model(513); QpdfObject borrowed = model[2].Value;
        ResourceGovernor governor = new(QpdfLimits.Default,CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        using CancellationTokenSource cancellation = new();
        PausedSink sink = new(point,outcome == "failure");
        Task<long> writing = Start(route,model,QpdfLimits.Default,governor,sink,cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            Assert.InRange(governor.Snapshot().LiveMemoryBytes, baseline + 1L, QpdfLimits.Default.MaxMemoryBytes);
            if (outcome == "cancellation") { cancellation.Cancel(); } else { sink.Resume(); }
            await Finish(writing,outcome);
            Assert.Equal(baseline,governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(513,model.Count); Assert.Same(borrowed,model[2].Value);
            GC.KeepAlive(writing); GC.KeepAlive(model); GC.KeepAlive(borrowed); GC.KeepAlive(sink);
        }
        finally { await Drain(writing,sink); }
    }

    [Theory]
    [InlineData("ordinary-streamed","memory")]
    [InlineData("ordinary-streamed","work")]
    [InlineData("qdf-streamed","memory")]
    [InlineData("qdf-streamed","work")]
    public void LargeLookupRejectsBeforeUnownedBackingAllocation(string route, string ceiling)
    {
        List<NumberedObject> model = Model(4096);
        QpdfLimits limits = ceiling == "memory" ? QpdfLimits.Default with {MaxMemoryBytes=4096L}
            : QpdfLimits.Default with {MaxWork=5000L};
        ResourceGovernor governor = new(limits,CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        PausedSink sink = new("none",false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Start(route,model,limits,governor,sink,CancellationToken.None).GetAwaiter().GetResult());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32768L,$"Rejected writer lookup allocated {allocated} bytes.");
        Assert.Equal(ceiling == "memory" ? nameof(QpdfLimits.MaxMemoryBytes) : nameof(QpdfLimits.MaxWork),failure.LimitName);
        Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L,prior.ReservedBytes);
        Assert.Equal(0L,sink.Length); Assert.Equal(4096,model.Count);
    }

    internal static async Task Finish(Task writing, string outcome)
    {
        if (outcome == "success") { await writing.WaitAsync(TimeSpan.FromSeconds(10)); }
        else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(() => writing.WaitAsync(TimeSpan.FromSeconds(10))); }
        else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(TimeSpan.FromSeconds(10))); }
    }

    internal static List<NumberedObject> StreamModel()
    {
        List<NumberedObject> model = Model(513);
        PdfDictionary page = (PdfDictionary)model[2].Value;
        Dictionary<PdfName,QpdfObject> pageEntries = new();
        using (PdfDictionary.EntryEnumerator entries = page.GetEntryEnumerator())
        { while (entries.MoveNext()) { pageEntries.Add(entries.Current.Key,entries.Current.Value); } }
        pageEntries[PdfName.FromAscii("Contents")] = new PdfArray([new PdfReference(4,0),new PdfReference(5,0)]);
        model[2] = new NumberedObject(3,new PdfDictionary(pageEntries));
        model[3] = new NumberedObject(4,new PdfStream(new PdfDictionary(new Dictionary<PdfName,QpdfObject>()),"q\nQ\n"u8.ToArray()));
        model[4] = new NumberedObject(5,new PdfStream(new PdfDictionary(new Dictionary<PdfName,QpdfObject>()),"q\nQ\n"u8.ToArray()));
        model[5] = new NumberedObject(6,new PdfDictionary(new Dictionary<PdfName,QpdfObject>
        { [PdfName.FromAscii("Author")] = new PdfByteString([65,255],false) }));
        return model;
    }

    internal static Task<long> StartQdf(string route, List<NumberedObject> model, ResourceGovernor governor,
        int infoNumber, IByteSink sink, CancellationToken token)
    {
        PdfSerializer writer = new(QpdfLimits.Default,governor); byte[] id = new byte[16];
        IReadOnlyList<IReadOnlyList<int>>? groups = route == "qdf-streamed" ? [new[] {3,6,7}] : null;
        return writer.SerializeQdfAsync(new PdfVersion(1,7),model,groups,null,infoNumber,id,id,
            new QdfOptions(true,true),false,sink,token).AsTask();
    }

    [Theory]
    [InlineData("qdf")]
    [InlineData("qdf-streamed")]
    public async Task MissingInfoPreservesFailureBoundary(string route)
    {
        List<NumberedObject> model = StreamModel();
        ResourceGovernor governor = new(QpdfLimits.Default,CancellationToken.None);
        PausedSink sink = new("none",false);
        QpdfException failure = await Assert.ThrowsAsync<QpdfException>(() => StartQdf(route,model,governor,
            999999,sink,CancellationToken.None));
        Assert.Contains("unmapped info object 999999",failure.Message,StringComparison.Ordinal);
        if (route == "qdf") { Assert.Equal(0L,sink.Length); Assert.Equal(0L,sink.XrefBytes); }
        else { Assert.True(sink.Length > 0L); Assert.True(sink.XrefBytes > 0L); }
        Assert.Equal(513,model.Count); Assert.Equal("q\nQ\n"u8.ToArray(),((PdfStream)model[3].Value).Data);
    }

    internal static async Task Drain(Task writing, PausedSink sink)
    {
        sink.Resume();
        try { await writing.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (QpdfException) { } catch (OperationCanceledException) { }
    }

    internal sealed class PausedSink(string point, bool fail) : IByteSink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        private bool _lastLengthHeader;
        internal Task Entered => _entered.Task;
        internal long XrefBytes { get; private set; }
        public long Length { get; private set; }
        internal void Resume() => _resume.TrySetResult();
        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            bool xref = chunk.Span.IndexOf("/Type /XRef"u8) >= 0;
            if (chunk.Span.SequenceEqual("7 0 obj\n"u8)) { _lastLengthHeader = true; }
            if (xref)
            {
                int offset = chunk.Span.IndexOf("/Length "u8) + 8;
                long count = 0;
                while (offset < chunk.Length && chunk.Span[offset] is >= 48 and <= 57)
                { count = checked(10L * count + chunk.Span[offset++] - 48); }
                XrefBytes = count;
            }
            bool selected = point switch
            {
                "version" => chunk.Span.StartsWith("%PDF-"u8),
                "object" => chunk.Span.SequenceEqual("1 0 obj\n"u8),
                "classic-retired" => chunk.Span.StartsWith("trailer"u8),
                "streamed-retired" => xref,
                "xref-info-header" => xref,
                "last-length-header" => chunk.Span.SequenceEqual("7 0 obj\n"u8),
                "last-length-value" => _lastLengthHeader && chunk.Span.SequenceEqual("4"u8),
                "info-value" => chunk.Span.StartsWith("  /Info "u8),
                "none" => false,
                _ => throw new InvalidOperationException("Unknown metadata pause point."),
            };
            if (!_paused && selected)
            {
                _paused = true; _entered.TrySetResult(); await _resume.Task.WaitAsync(token);
                if (fail) { throw new QpdfException("Authored metadata output failure."); }
            }
            token.ThrowIfCancellationRequested(); Length += chunk.Length;
        }
    }
}
