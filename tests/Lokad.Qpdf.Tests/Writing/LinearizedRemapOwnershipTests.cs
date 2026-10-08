using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedRemapOwnershipTests
{
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
    public async Task RebuiltGraphStaysFundedThroughEitherPassAndRetires(string route, string outcome, bool firstPass)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        PdfArray borrowed = new(Enumerable.Repeat<QpdfObject>(new PdfReference(5, 0), 1024).ToArray());
        model.Add(new NumberedObject(6, borrowed));
        QpdfLimits limits = QpdfLimits.Default;
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
            long metadata = 1024L * (32L + IntPtr.Size);
            Assert.True(live >= metadata, $"Only {live} bytes admitted for {metadata} bytes of rebuilt reference/array metadata.");
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
                Assert.Equal("Authored remap output failure.", failure.Message);
            }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.True(governor.Snapshot().LiveMemoryBytes <= live - metadata);
            if (firstPass && outcome != "success") { Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); }
            Assert.Same(borrowed, model[^1].Value);
            Assert.Equal(5, Assert.IsType<PdfReference>(borrowed[1023]).ObjectNumber);
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
    public async Task BothPassesEmitBorrowedImmutablePayloadWithoutCopies(string route)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        byte[] bytes = new byte[1024 * 1024];
        bytes.AsSpan().Fill((byte)' ');
        PdfStream original = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), bytes);
        model[3] = new NumberedObject(4, original);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 128L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PayloadSink first = new(original.AllocationCarrier);
        PayloadSink final = new(original.AllocationCarrier);
        long written = await Start(route, model, governor, limits, final, first, CancellationToken.None);
        Assert.Equal(written, final.Length);
        Assert.Equal(bytes.Length, first.BorrowedBytes);
        Assert.Equal(bytes.Length, final.BorrowedBytes);
        Assert.True(first.BorrowedChunks > 1);
        Assert.True(final.BorrowedChunks > 1);
        Assert.True(governor.Snapshot().PeakMemoryBytes < bytes.Length);
        Assert.Same(original, model[3].Value);
        Assert.Equal((byte)' ', original.RawMemory.Span[0]);
        Assert.Equal((byte)' ', original.RawMemory.Span[^1]);
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("streamed")]
    [InlineData("fallback")]
    public async Task RebuiltGraphAdmissionRejectsBeforeAnyPassAppend(string route)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        PdfArray borrowed = new(Enumerable.Repeat<QpdfObject>(new PdfReference(5, 0), 2048).ToArray());
        model.Add(new NumberedObject(6, borrowed));
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        CountingSink first = new();
        CountingSink final = new();
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            Start(route, model, governor, limits, final, first, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(0L, first.Length);
        Assert.Equal(0L, final.Length);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 2048L);
        Assert.Same(borrowed, model[^1].Value);
    }

    private sealed class PayloadSink(object payload) : IByteSink
    {
        public long Length { get; private set; }
        internal long BorrowedBytes { get; private set; }
        internal int BorrowedChunks { get; private set; }

        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MemoryMarshal.TryGetArray(chunk, out ArraySegment<byte> segment)
                && ReferenceEquals(segment.Array, payload))
            {
                BorrowedBytes += chunk.Length;
                BorrowedChunks++;
            }
            Length += chunk.Length;
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
                if (fail) { throw new QpdfException("Authored remap output failure."); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }
}
