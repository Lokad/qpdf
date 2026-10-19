using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedWriterOwnershipTests
{
    private static List<NumberedObject> Model()
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        for (int index = 0; index < 512; index++) { model.Add(new NumberedObject(index + 6, PdfNull.Instance)); }
        return model;
    }

    private static Task<long> Start(string route, List<NumberedObject> model, ResourceGovernor governor,
        QpdfLimits limits, IByteSink sink, CancellationToken cancellationToken)
    {
        LinearizedWriter writer = new(limits, governor);
        byte[] id = new byte[16];
        return route == "classic"
            ? writer.WriteLinearizedAsync(new PdfVersion(1, 5), model, null, id, id, false, false, null,
                sink, null, cancellationToken).AsTask()
            : writer.WriteLinearizedStreamedAsync(new PdfVersion(1, 5), model,
                route == "fallback" ? Array.Empty<IReadOnlyList<int>>() : new IReadOnlyList<int>[] { new[] { 5 } },
                null, id, id, false, false, null, sink, null, cancellationToken).AsTask();
    }

    [Theory]
    [InlineData("classic", "success")]
    [InlineData("classic", "failure")]
    [InlineData("classic", "cancellation")]
    [InlineData("streamed", "success")]
    [InlineData("streamed", "failure")]
    [InlineData("streamed", "cancellation")]
    [InlineData("fallback", "success")]
    [InlineData("fallback", "failure")]
    [InlineData("fallback", "cancellation")]
    public async Task ClassificationStaysFundedAcrossAwaitAndRetiresOnEveryOutcome(string route, string outcome)
    {
        List<NumberedObject> model = Model();
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        PausedSink sink = new(outcome == "failure");
        Task<long> writing = Start(route, model, governor, limits, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            long live = governor.Snapshot().LiveMemoryBytes;
            Assert.True(live >= 512L * 16L, $"Only {live} bytes admitted while the writer holds its classified graph.");
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success")
            {
                long written = await writing;
                Assert.Equal(sink.Length, written);
            }
            else if (outcome == "failure")
            {
                QpdfException failure = await Assert.ThrowsAsync<QpdfException>(async () => await writing);
                Assert.Equal("Authored output failure.", failure.Message);
            }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.True(governor.Snapshot().LiveMemoryBytes < live - 4096L);
            Assert.Same(PdfNull.Instance, model[^1].Value);
        }
        finally
        {
            sink.Resume();
            try { await writing; }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("streamed")]
    [InlineData("fallback")]
    public async Task FiniteClassificationAdmissionFailsBeforeAnyOutputAppend(string route)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        CountingSink sink = new();
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            Start(route, Model(), governor, limits, sink, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(0L, sink.Length);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
    }

    private sealed class PausedSink(bool fail) : IByteSink
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
                _paused = true;
                _entered.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
                if (fail) { throw new QpdfException("Authored output failure."); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }
}
