using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class WriterEncodingOwnershipTests
{
    internal static object Source(string route)
    {
        if (route.EndsWith("ascii", StringComparison.Ordinal)) { return new string('A', 16384); }
        byte[] raw = new byte[16384]; Array.Fill(raw, (byte)128);
        return route.EndsWith("name", StringComparison.Ordinal) ? new PdfName(raw)
            : new PdfByteString(raw, route.EndsWith("hex", StringComparison.Ordinal));
    }

    internal static int Length(string route) => route.EndsWith("ascii", StringComparison.Ordinal) ? 16384
        : route.EndsWith("name", StringComparison.Ordinal) ? 49153
        : route.EndsWith("hex", StringComparison.Ordinal) ? 32770 : 65538;

    internal static Task Start(string route, object source, QpdfLimits limits, ResourceGovernor governor, IByteSink sink, CancellationToken token)
    {
        object writer = route == "linear-ascii" ? new LinearizedWriter(limits, governor) : new PdfSerializer(limits, governor);
        bool ascii = source is string;
        if (!ascii && !route.StartsWith("qdf-", StringComparison.Ordinal))
        {
            return ((PdfSerializer)writer).AppendValueAsync((QpdfObject)source, sink, 0, false, token).AsTask();
        }
        MethodInfo method = writer.GetType().GetMethod(ascii ? "AppendAsciiAsync" : "AppendQdfValueAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected preceding-compatible formatter.");
        object? result = method.Invoke(writer, ascii ? new[] { source, sink, (object)token } : new[] { source, sink, 0, (object)token });
        return result is ValueTask pending ? pending.AsTask() : throw new InvalidOperationException("Expected formatting task.");
    }

    public static IEnumerable<object[]> Routes()
    {
        foreach (string route in new[] { "ordinary-name", "ordinary-literal", "ordinary-hex", "ordinary-ascii", "qdf-name", "qdf-literal", "qdf-hex", "linear-ascii" })
        {
            foreach (string outcome in new[] { "success", "failure", "cancellation" }) { yield return [route, outcome]; }
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EncodingStaysFundedAcrossEveryAwaitedOutcome(string route, string outcome)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        PausedSink sink = new(outcome == "failure");
        object source = Source(route);
        Task writing = Start(route, source, QpdfLimits.Default, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            Assert.Equal(160L + Length(route), governor.Snapshot().LiveMemoryBytes);
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { await writing; Assert.Equal(Length(route), sink.Length); }
            else if (outcome == "failure")
            {
                QpdfException failure = await Assert.ThrowsAsync<QpdfException>(() => writing);
                Assert.Equal("Authored encoding output failure.", failure.Message);
            }
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

    internal sealed class PausedSink(bool fail) : IByteSink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        internal Task Entered => _entered.Task;
        public long Length { get; private set; }
        internal void Resume() => _resume.TrySetResult();
        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            if (!_paused)
            {
                _paused = true; _entered.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
                if (fail) { throw new QpdfException("Authored encoding output failure."); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }

    [Theory]
    [InlineData("ordinary-name")]
    [InlineData("ordinary-literal")]
    [InlineData("ordinary-hex")]
    [InlineData("ordinary-ascii")]
    [InlineData("qdf-name")]
    [InlineData("qdf-literal")]
    [InlineData("qdf-hex")]
    [InlineData("linear-ascii")]
    public async Task InsufficientEncodingMemoryRejectsBeforeAnySinkAppend(string route)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = Length(route) - 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        object source = Source(route);
        CollectingSink sink = new();
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => Start(route, source, limits, governor, sink, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(0L, sink.Length);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
        Assert.Equal(16384,
            source is string text ? text.Length : source is PdfName name ? name.RawSpan.Length : ((PdfByteString)source).ValueSpan.Length);
    }

    [Theory]
    [InlineData("ordinary-name")]
    [InlineData("ordinary-literal")]
    [InlineData("ordinary-hex")]
    [InlineData("ordinary-ascii")]
    [InlineData("qdf-name")]
    [InlineData("qdf-literal")]
    [InlineData("qdf-hex")]
    [InlineData("linear-ascii")]
    public async Task OwnedEncodingPreservesPrecedingWriterSpelling(string route)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        object source = Source(route);
        CollectingSink sink = new();
        await Start(route, source, QpdfLimits.Default, governor, sink, CancellationToken.None);
        string expected = source is string text ? text : source is PdfName ? "/" + string.Concat(Enumerable.Repeat("#80", 16384))
            : ((PdfByteString)source).Hex ? "<" + string.Concat(Enumerable.Repeat("80", 16384)) + ">"
            : "(" + string.Concat(Enumerable.Repeat("\\200", 16384)) + ")";
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(expected), sink.Data);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CollectingSink : IByteSink
    {
        private readonly MemoryStream _data = new();
        internal byte[] Data => _data.ToArray();
        public long Length => _data.Length;
        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _data.Write(chunk.Span);
            return ValueTask.CompletedTask;
        }
    }
}
