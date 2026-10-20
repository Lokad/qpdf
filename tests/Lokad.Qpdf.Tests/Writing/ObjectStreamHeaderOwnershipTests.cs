using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamHeaderOwnershipTests
{
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
    public async Task HeaderAdmissionSurvivesAwaitAndRetiresBeforePayload(string route, bool compressed, string outcome)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        HeaderSink sink = new(governor, outcome == "failure");
        Task<long> writing = ObjectStreamPayloadOwnershipTests.Start(route, compressed, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            if (outcome == "cancellation") { cancellation.Cancel(); } else { sink.Resume(); }
            await WriterIndexOwnershipTests.Finish(writing, outcome);
            if (outcome == "success")
            {
                Assert.True(await writing > 0L);
                Assert.Equal(160L + sink.HeaderLength, sink.HeaderAdmission - sink.PayloadAdmission);
            }
            Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
            GC.KeepAlive(writing); GC.KeepAlive(sink);
        }
        finally
        {
            sink.Resume();
            try { await writing; } catch (QpdfException) { } catch (OperationCanceledException) { }
        }
    }

    internal sealed class HeaderSink(ResourceGovernor governor, bool fail) : IByteSink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _next;
        private bool _paused;
        private bool _payload;
        internal Task Entered => _entered.Task;
        internal long HeaderAdmission { get; private set; }
        internal long PayloadAdmission { get; private set; }
        internal int HeaderLength { get; private set; }
        public long Length { get; private set; }
        internal void Resume() => _resume.TrySetResult();

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            if (!_paused && chunk.Span.IndexOf("/Type /ObjStm"u8) >= 0)
            {
                _paused = true;
                _next = true;
                HeaderLength = chunk.Length;
                HeaderAdmission = governor.Snapshot().LiveMemoryBytes;
                _entered.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
                if (fail) { throw new QpdfException("Authored header output failure."); }
            }
            else if (_next && !_payload)
            {
                _payload = true;
                PayloadAdmission = governor.Snapshot().LiveMemoryBytes;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }
}
