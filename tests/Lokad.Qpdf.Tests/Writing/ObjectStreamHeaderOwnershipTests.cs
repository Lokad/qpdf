using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamHeaderOwnershipTests
{
    [Theory]
    [InlineData("ordinary", false)]
    [InlineData("ordinary", true)]
    [InlineData("qdf", false)]
    [InlineData("linearized", false)]
    [InlineData("linearized", true)]
    [InlineData("counting", false)]
    [InlineData("counting", true)]
    public async Task HeaderAdmissionSurvivesAwaitAndRetiresBeforePayload(string route, bool compressed)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        HeaderSink sink = new(governor, false);
        Task<long> writing = ObjectStreamPayloadOwnershipTests.Start(route, compressed, governor, sink, CancellationToken.None);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            sink.Resume();
            Assert.True(await writing > 0L);
            Assert.Equal(160L + sink.HeaderLength, sink.HeaderAdmission - sink.PayloadAdmission);
            Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
            GC.KeepAlive(writing); GC.KeepAlive(sink);
        }
        finally
        {
            sink.Resume();
            await writing;
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
