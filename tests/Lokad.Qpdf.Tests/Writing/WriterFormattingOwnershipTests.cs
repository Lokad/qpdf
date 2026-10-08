using System.Reflection;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class WriterFormattingOwnershipTests
{
    internal const int DeepLevel = 16384;
    internal static QpdfLimits Limits => QpdfLimits.Default with { MaxNestingDepth = DeepLevel + 2L };

    internal static QpdfObject Source(string route) => route switch
    {
        "qdf-array" => new PdfArray([PdfNull.Instance]),
        "qdf-dictionary" => new PdfDictionary(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("K")] = PdfBoolean.True }),
        "ordinary-object" or "ordinary-trailer" => PdfNull.Instance,
        _ => new PdfReference(int.MaxValue, 0),
    };

    internal static string Spelling(string route) => route switch
    {
        "ordinary-object" => "2147483647 0 obj\n",
        "ordinary-reference" or "qdf-reference" => "2147483647 0 R",
        "ordinary-trailer" => "trailer\n<< /Size 2 /Root 1 0 R /ID [<00017FFF><00017FFF>] >>\n",
        _ => "\n" + new string(' ', 2 * (DeepLevel + 1)),
    };

    internal static Task Start(string route, QpdfObject source, QpdfLimits limits, ResourceGovernor governor, IByteSink sink, CancellationToken token)
    {
        PdfSerializer writer = new(limits, governor);
        if (route == "ordinary-object") { return writer.AppendNumberedObjectAsync(int.MaxValue, source, sink, false, token).AsTask(); }
        if (route == "ordinary-reference") { return writer.AppendValueAsync(source, sink, 0, false, token).AsTask(); }
        if (route == "ordinary-trailer")
        {
            byte[] id = [0, 1, 127, 255];
            return writer.SerializeAsync(new PdfVersion(1, 7), [new NumberedObject(1, source)], null, id, id, false, sink, token).AsTask();
        }
        MethodInfo method = typeof(PdfSerializer).GetMethod("AppendQdfValueAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected preceding-compatible QDF method.");
        object? result = method.Invoke(writer, [source, sink, route == "qdf-reference" ? 0 : DeepLevel, token]);
        return result is ValueTask pending ? pending.AsTask() : throw new InvalidOperationException("Expected QDF task.");
    }

    public static IEnumerable<object[]> Routes()
    {
        foreach (string route in new[] { "ordinary-object", "ordinary-reference", "qdf-reference", "qdf-array", "qdf-dictionary", "ordinary-trailer" })
        {
            foreach (string outcome in new[] { "success", "failure", "cancellation" }) { yield return [route, outcome]; }
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task FormattedResultStaysFundedAcrossEveryAwaitedOutcome(string route, string outcome)
    {
        ResourceGovernor governor = new(Limits, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        PausedSink sink = new(route, outcome == "failure");
        QpdfObject source = Source(route);
        Task writing = Start(route, source, Limits, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            Assert.Equal(160L + Spelling(route).Length, governor.Snapshot().LiveMemoryBytes);
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { await writing; }
            else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(() => writing); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing); }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            GC.KeepAlive(writing); GC.KeepAlive(sink); GC.KeepAlive(source);
        }
        finally
        {
            sink.Resume();
            try { await writing; }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
        }
    }

    internal sealed class PausedSink(string route, bool fail) : IByteSink
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
            bool selected = route == "ordinary-trailer" ? chunk.Span.StartsWith("trailer\n"u8)
                : route is "qdf-array" or "qdf-dictionary" ? call == 1 : call == 0;
            if (!_paused && selected)
            {
                _paused = true; _entered.TrySetResult();
                await _resume.Task.WaitAsync(token);
                if (fail) { throw new QpdfException("Authored formatted output failure."); }
            }
            token.ThrowIfCancellationRequested(); Length += chunk.Length;
        }
    }

    [Theory]
    [InlineData("qdf-array", "memory")]
    [InlineData("qdf-dictionary", "memory")]
    [InlineData("qdf-array", "work")]
    [InlineData("qdf-dictionary", "work")]
    public void HugeIndentRejectsBeforeConstructingStrings(string route, string ceiling)
    {
        QpdfLimits limits = ceiling == "memory" ? Limits with { MaxMemoryBytes = 32770L } : Limits with { MaxWork = 500L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfObject source = Source(route);
        CollectingSink sink = new();
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => { Start(route, source, limits, governor, sink, CancellationToken.None).GetAwaiter().GetResult(); });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32768L, $"Rejected indentation allocated {allocated} bytes.");
        Assert.Equal(ceiling == "memory" ? nameof(QpdfLimits.MaxMemoryBytes) : nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(route == "qdf-array" ? "[" : "<<", Encoding.ASCII.GetString(sink.ToArray()));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("qdf-array", false)]
    [InlineData("qdf-array", true)]
    [InlineData("qdf-dictionary", false)]
    [InlineData("qdf-dictionary", true)]
    public void CancellationBetweenPrefixAndIndentAllocatesNoSourceString(string route, bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(Limits, governorToken ? cancellation.Token : CancellationToken.None);
        QpdfObject source = Source(route);
        CancelingSink sink = new(cancellation);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<OperationCanceledException>(() => { Start(route, source, Limits, governor, sink,
            governorToken ? CancellationToken.None : cancellation.Token).GetAwaiter().GetResult(); });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32768L, $"Canceled indentation allocated {allocated} bytes.");
        Assert.Equal(route == "qdf-array" ? 1L : 2L, sink.Length);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
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

    [Theory]
    [InlineData("ordinary-object")]
    [InlineData("ordinary-reference")]
    [InlineData("qdf-reference")]
    [InlineData("qdf-array")]
    [InlineData("qdf-dictionary")]
    [InlineData("ordinary-trailer")]
    public async Task WriterFormattingPreservesAuthoredBytes(string route)
    {
        ResourceGovernor governor = new(Limits, CancellationToken.None);
        CollectingSink sink = new();
        await Start(route, Source(route), Limits, governor, sink, CancellationToken.None);
        string output = Encoding.ASCII.GetString(sink.ToArray());
        Assert.Contains(Spelling(route), output, StringComparison.Ordinal);
        if (route is "ordinary-reference" or "qdf-reference") { Assert.Equal(Spelling(route), output); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
