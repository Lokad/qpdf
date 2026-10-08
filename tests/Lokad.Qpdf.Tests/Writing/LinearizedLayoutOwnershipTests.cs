using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedLayoutOwnershipTests
{
    private static List<NumberedObject> Model()
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        for (int index = 0; index < 512; index++) { model.Add(new NumberedObject(index + 6, PdfNull.Instance)); }
        return model;
    }

    private static long GraphAdmission(string route, List<NumberedObject> model, QpdfLimits limits)
    {
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        if (route == "streamed")
        {
            using StreamedParts parts = LinearizedParts.ClassifyStreamed(model,
                new IReadOnlyList<int>[] { new[] { 5 } }, memory, governor, CancellationToken.None);
            using RemappedModel remapped = RemappedModel.Create(parts.Lookup, parts.Renumber, governor, CancellationToken.None);
            return governor.Snapshot().LiveMemoryBytes;
        }
        using LinearizedParts classic = LinearizedParts.Classify(model, memory, governor, CancellationToken.None);
        using RemappedModel copied = RemappedModel.Create(classic.Lookup, classic.Renumber, governor, CancellationToken.None);
        return governor.Snapshot().LiveMemoryBytes;
    }

    private static Task<long> Start(string route, List<NumberedObject> model, ResourceGovernor governor,
        QpdfLimits limits, IByteSink sink, IByteSink? firstPass, CancellationToken cancellationToken)
    {
        LinearizedWriter writer = new(limits, governor);
        byte[] id = new byte[16];
        return route == "classic"
            ? writer.WriteLinearizedAsync(new PdfVersion(1, 5), model, null, id, id, false, false, null,
                sink, firstPass, cancellationToken).AsTask()
            : writer.WriteLinearizedStreamedAsync(new PdfVersion(1, 5), model,
                route == "fallback" ? Array.Empty<IReadOnlyList<int>>() : new IReadOnlyList<int>[] { new[] { 5 } },
                null, id, id, false, false, null, sink, firstPass, cancellationToken).AsTask();
    }

    [Theory]
    [InlineData("classic", "success", false)]
    [InlineData("classic", "failure", false)]
    [InlineData("classic", "cancellation", false)]
    [InlineData("streamed", "success", false)]
    [InlineData("streamed", "failure", false)]
    [InlineData("streamed", "cancellation", false)]
    [InlineData("fallback", "success", false)]
    [InlineData("fallback", "failure", false)]
    [InlineData("fallback", "cancellation", false)]
    [InlineData("classic", "success", true)]
    [InlineData("classic", "failure", true)]
    [InlineData("classic", "cancellation", true)]
    [InlineData("streamed", "success", true)]
    [InlineData("streamed", "failure", true)]
    [InlineData("streamed", "cancellation", true)]
    [InlineData("fallback", "success", true)]
    [InlineData("fallback", "failure", true)]
    [InlineData("fallback", "cancellation", true)]
    public async Task PassMetadataStaysFundedAcrossAwaitAndRetiresOnEveryOutcome(string route, string outcome, bool firstPass)
    {
        List<NumberedObject> model = Model();
        QpdfLimits limits = QpdfLimits.Default;
        long graphAdmission = GraphAdmission(route, model, limits);
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        PausedSink paused = new(outcome == "failure");
        CountingSink other = new();
        Task<long> writing = Start(route, model, governor, limits,
            firstPass ? other : paused, firstPass ? paused : null, cancellation.Token);
        await paused.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            long live = governor.Snapshot().LiveMemoryBytes;
            long minimum = firstPass ? 256L : 2L * model.Count * 16L;
            Assert.True(live - graphAdmission >= minimum,
                $"Only {live - graphAdmission} extra bytes admitted for live pass metadata; expected at least {minimum}.");
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { paused.Resume(); }
            if (outcome == "success")
            {
                long written = await writing;
                Assert.Equal(firstPass ? other.Length : paused.Length, written);
            }
            else if (outcome == "failure")
            {
                QpdfException failure = await Assert.ThrowsAsync<QpdfException>(async () => await writing);
                Assert.Equal("Authored layout output failure.", failure.Message);
            }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
            if (firstPass && outcome != "success") { Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); }
            Assert.Same(PdfNull.Instance, model[^1].Value);
            GC.KeepAlive(writing);
        }
        finally
        {
            paused.Resume();
            try { await writing; }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("streamed")]
    [InlineData("fallback")]
    public async Task FinitePassAdmissionRejectsAndReleasesMetadataAfterWorkBegins(string route)
    {
        List<NumberedObject> model = Model();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 128L * 1024L };
        long graphAdmission = GraphAdmission(route, model, limits);
        ResourceGovernor governor = new(limits, CancellationToken.None);
        CountingSink first = new();
        CountingSink final = new();
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            Start(route, model, governor, limits, final, first, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.True(first.Length > 0L);
        Assert.True(governor.Snapshot().PeakMemoryBytes > graphAdmission + 512L * 16L);
        Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
        Assert.Same(PdfNull.Instance, model[^1].Value);
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("streamed")]
    [InlineData("fallback")]
    public async Task DivergentReplayRejectsAndRetiresPassMetadata(string route)
    {
        List<NumberedObject> model = Model();
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DivergentSink sink = new();
        QpdfException failure = await Assert.ThrowsAsync<QpdfException>(() =>
            Start(route, model, governor, limits, sink, null, CancellationToken.None));
        Assert.Equal("Linearized replay diverged between counting and emitting passes.", failure.Message);
        Assert.True(sink.Length > 0L);
        Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
    }

    private sealed class DivergentSink : IByteSink
    {
        private bool _first = true;
        public long Length { get; private set; }

        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length + (_first ? 1L : 0L);
            _first = false;
            return ValueTask.CompletedTask;
        }
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
                if (fail) { throw new QpdfException("Authored layout output failure."); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }
}
