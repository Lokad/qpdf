using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamPayloadOwnershipTests
{
    internal static Task<long> Start(string route, bool compress, ResourceGovernor governor, IByteSink sink, CancellationToken token)
    {
        byte[] id = new byte[16];
        IReadOnlyList<IReadOnlyList<int>> groups = new IReadOnlyList<int>[] { new[] { 5 } };
        PdfVersion version = new(1, 5);
        List<NumberedObject> model = ObjectStreamWriterCaptureTests.Model(false);
        if (route == "linearized" || route == "counting")
        {
            LinearizedWriter writer = new(QpdfLimits.Default, governor);
            return writer.WriteLinearizedStreamedAsync(version, model, groups, null, id, id, false, compress, null,
                route == "counting" ? new CountingSink() : sink, route == "counting" ? sink : null, token).AsTask();
        }
        PdfSerializer serializer = new(QpdfLimits.Default, governor);
        return route == "qdf"
            ? serializer.SerializeQdfAsync(version, model, groups, null, null, id, id, new QdfOptions(true, false), false, sink, token).AsTask()
            : serializer.SerializeStreamedAsync(version, model, groups, compress, null, null, id, id, false, sink, token).AsTask();
    }

    [Theory]
    [InlineData("ordinary", false, "success")]
    [InlineData("ordinary", false, "failure")]
    [InlineData("ordinary", false, "cancellation")]
    [InlineData("ordinary", true, "success")]
    [InlineData("ordinary", true, "failure")]
    [InlineData("ordinary", true, "cancellation")]
    [InlineData("qdf", false, "success")]
    [InlineData("qdf", false, "failure")]
    [InlineData("qdf", false, "cancellation")]
    [InlineData("linearized", false, "success")]
    [InlineData("linearized", false, "failure")]
    [InlineData("linearized", false, "cancellation")]
    [InlineData("linearized", true, "success")]
    [InlineData("linearized", true, "failure")]
    [InlineData("linearized", true, "cancellation")]
    [InlineData("counting", false, "success")]
    [InlineData("counting", false, "failure")]
    [InlineData("counting", false, "cancellation")]
    [InlineData("counting", true, "success")]
    [InlineData("counting", true, "failure")]
    [InlineData("counting", true, "cancellation")]
    public async Task EveryPayloadOutcomeRetiresTemporaryAssemblyAndCodecBytes(string route, bool compress, string outcome)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        PausedSink sink = new(outcome == "failure");
        Task<long> writing = Start(route, compress, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { Assert.True(await writing > 0L); }
            else if (outcome == "failure")
            {
                QpdfException failure = await Assert.ThrowsAsync<QpdfException>(async () => await writing);
                Assert.Equal("Authored payload output failure.", failure.Message);
            }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
            GC.KeepAlive(writing); GC.KeepAlive(sink);
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
        private bool _next;
        private bool _paused;
        internal Task Entered => _entered.Task;
        public long Length { get; private set; }
        internal void Resume() => _resume.TrySetResult();

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            if (_next && !_paused)
            {
                _paused = true;
                _entered.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
                if (fail) { throw new QpdfException("Authored payload output failure."); }
            }
            _next |= chunk.Span.IndexOf("/Type /ObjStm"u8) >= 0;
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }
}
