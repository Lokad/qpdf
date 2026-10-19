using System.Reflection;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterOrderOwnershipTests
{
    internal static PdfDictionary Names(int count, int commonLength)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int index = count - 1; index >= 0; index--)
        {
            byte[] raw = new byte[commonLength + 2];
            raw.AsSpan(0, commonLength).Fill(35);
            raw[commonLength] = (byte)(index >> 8); raw[commonLength + 1] = (byte)index;
            entries.Add(new PdfName(raw), PdfBoolean.True);
        }
        return new PdfDictionary(entries);
    }

    internal static Task Start(PdfDictionary source, QpdfLimits limits, ResourceGovernor governor, IByteSink sink, CancellationToken token)
    {
        PdfSerializer writer = new(limits, governor);
        MethodInfo method = typeof(PdfSerializer).GetMethod("AppendQdfValueAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected preceding-compatible QDF method.");
        object? result = method.Invoke(writer, [source, sink, 0, token]);
        return result is ValueTask pending ? pending.AsTask() : throw new InvalidOperationException("Expected QDF task.");
    }

    [Fact]
    public void LongNameSortingRejectsBeforeUnownedDisplayAllocation()
    {
        PdfDictionary source = Names(512, 512);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        CollectingSink sink = new();
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Start(source, limits, governor, sink, CancellationToken.None).GetAwaiter().GetResult());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32768L, $"Rejected name ordering allocated {allocated} bytes.");
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal("<<", Encoding.ASCII.GetString(sink.ToArray()));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, source.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterPrefixStopsSortingBeforeDisplayAllocation(bool governorToken)
    {
        PdfDictionary source = Names(512, 512);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        CancelingSink sink = new(cancellation);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<OperationCanceledException>(() => Start(source, QpdfLimits.Default, governor, sink,
            governorToken ? CancellationToken.None : cancellation.Token).GetAwaiter().GetResult());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32768L, $"Canceled name ordering allocated {allocated} bytes.");
        Assert.Equal(2L, sink.Length); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, source.Count);
    }

    public static IEnumerable<object[]> AwaitedOutcomes()
    {
        foreach (bool closing in new[] { false, true })
        {
            foreach (string outcome in new[] { "success", "failure", "cancellation" }) { yield return [closing, outcome]; }
        }
    }

    [Theory]
    [MemberData(nameof(AwaitedOutcomes))]
    public async Task KeyStorageSurvivesItsConsumerAndRetiresBeforeClosing(bool closing, string outcome)
    {
        PdfDictionary source = Names(6, 0);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using CancellationTokenSource cancellation = new();
        PausedSink sink = new(closing ? "dictionary-close" : "dictionary-first", outcome == "failure");
        Task writing = Start(source, QpdfLimits.Default, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            Assert.Equal(161L + 163L + (closing ? 0L : 160L + 6L * IntPtr.Size), governor.Snapshot().LiveMemoryBytes);
            if (outcome == "cancellation") { cancellation.Cancel(); } else { sink.Resume(); }
            if (outcome == "success") { await writing; }
            else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(() => writing); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing); }
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(6, source.Count);
            GC.KeepAlive(writing); GC.KeepAlive(sink); GC.KeepAlive(source);
        }
        finally { sink.Resume(); try { await writing; } catch (QpdfException) { } catch (OperationCanceledException) { } }
    }

    [Fact]
    public void ClassicQdfModelOrderingRejectsBeforeLargeBacking()
    {
        List<NumberedObject> source = Enumerable.Range(1, 8192).Reverse().Select(number => new NumberedObject(number, PdfNull.Instance)).ToList();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfSerializer writer = new(limits, governor); CountingSink sink = new(); byte[] id = [1];
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => writer.SerializeQdfAsync(new PdfVersion(1, 5), source, null,
            null, null, id, id, new QdfOptions(true, false), false, sink, CancellationToken.None).AsTask().GetAwaiter().GetResult());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32768L, $"Rejected model ordering allocated {allocated} bytes.");
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(0L, sink.Length); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(8192, source.Count); Assert.Equal(8192, source[0].Number);
    }

    [Fact]
    public async Task MixedBinaryNamesPreserveAuthoredQdfOrderAndNullElision()
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (byte[] raw in new byte[][] { [255], [65, 0], [47], [65], [35], [32], [] }) { entries.Add(new PdfName(raw), PdfBoolean.True); }
        entries.Add(new PdfName([0]), PdfNull.Instance);
        PdfDictionary source = new(entries); CollectingSink sink = new();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        await Start(source, QpdfLimits.Default, governor, sink, CancellationToken.None);
        Assert.Equal("<<\n  / true\n  /#20 true\n  /#23 true\n  /#FF true\n  /#2F true\n  /A true\n  /A# true\n>>", Encoding.ASCII.GetString(sink.ToArray()));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(8, source.Count);
    }

    internal sealed class PausedSink(string point, bool fail) : IByteSink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private bool _paused;
        internal Task Entered => _entered.Task;
        public long Length { get; private set; }
        internal void Resume() => _resume.TrySetResult();
        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            int call = _calls++;
            bool selected = point switch
            {
                "dictionary-first" => call == 1,
                "dictionary-close" => chunk.Span.SequenceEqual("\n>>"u8),
                "first-half" => chunk.Span.StartsWith("xref\n"u8) && !chunk.Span.StartsWith("xref\n0 "u8),
                "second-half" => chunk.Span.StartsWith("xref\n0 "u8),
                "after-first-half" => chunk.Span.StartsWith(" /Prev "u8),
                "after-second-half" => chunk.Span.StartsWith("startxref\n"u8) && !chunk.Span.SequenceEqual("startxref\n0\n%%EOF\n"u8),
                "qdf-version" => chunk.Span.StartsWith("%PDF-"u8),
                _ => throw new InvalidOperationException("Unknown pause point."),
            };
            if (!_paused && selected)
            {
                _paused = true; _entered.TrySetResult(); await _resume.Task.WaitAsync(token);
                if (fail) { throw new QpdfException("Authored ordering output failure."); }
            }
            token.ThrowIfCancellationRequested(); Length += chunk.Length;
        }
    }

    private sealed class CancelingSink(CancellationTokenSource cancellation) : IByteSink
    {
        public long Length { get; private set; }
        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            Length += chunk.Length; cancellation.Cancel(); return ValueTask.CompletedTask;
        }
    }

    private sealed class CollectingSink : IByteSink
    {
        private readonly MemoryStream _data = new();
        internal byte[] ToArray() => _data.ToArray();
        public long Length => _data.Length;
        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); _data.Write(chunk.Span); return ValueTask.CompletedTask;
        }
    }
}
