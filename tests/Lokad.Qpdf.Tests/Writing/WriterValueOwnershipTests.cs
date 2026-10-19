using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterValueOwnershipTests
{
    internal static List<NumberedObject> Model()
    {
        List<NumberedObject> model = WriterIndexOwnershipTests.Model(8);
        PdfArray links = new(Enumerable.Repeat<QpdfObject>(new PdfReference(8, 0), 1024).ToArray());
        model[3] = new NumberedObject(4, new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Link")] = links, [PdfName.FromAscii("Length")] = new PdfInteger("4", 4) }), "q\nQ\n"u8.ToArray()));
        model[4] = new NumberedObject(5, links); model[5] = new NumberedObject(6, new PdfInteger("66", 66));
        model[7] = new NumberedObject(8, new PdfInteger("88", 88)); return model;
    }

    internal static Task<long> Start(List<NumberedObject> model, QpdfLimits limits, ResourceGovernor governor,
        IByteSink sink, IReadOnlyDictionary<int, int>? originalIds, CancellationToken token)
    {
        PdfSerializer serializer = new(limits, governor); byte[] id = new byte[16];
        return serializer.SerializeQdfAsync(new PdfVersion(1, 7), model, [new[] { 3, 6, 7 }], originalIds, null,
            id, id, new QdfOptions(true, true), false, sink, token).AsTask();
    }

    public static IEnumerable<object[]> Outcomes()
    {
        foreach (string kind in new[] { "header", "regular" })
        { foreach (bool retired in new[] { false, true })
          { foreach (string outcome in new[] { "success", "failure", "caller-cancellation", "governor-cancellation" })
            { yield return [kind, retired, outcome]; } } }
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task ValueMetadataStaysFundedThroughOutputAndRetiresBeforeClosingAwait(string kind, bool retired, string outcome)
    {
        List<NumberedObject> model = Model(); QpdfObject source = kind == "header" ? ((PdfStream)model[3].Value).Dictionary : model[4].Value;
        using CancellationTokenSource cancellation = new(); bool governorCancellation = outcome == "governor-cancellation";
        ResourceGovernor governor = new(QpdfLimits.Default, governorCancellation ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        PausedSink sink = new(kind, retired, outcome == "failure");
        Task writing = Start(model, QpdfLimits.Default, governor, sink, null, governorCancellation ? CancellationToken.None : cancellation.Token);
        await sink.First.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            long active = governor.Snapshot().LiveMemoryBytes;             long materialBacking = 1024L * IntPtr.Size;
            Assert.True(active >= baseline + materialBacking);
            if (retired)
            {
                sink.ResumeFirst(); await sink.Second.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(governor.Snapshot().LiveMemoryBytes <= active - materialBacking);
            }
            if (outcome.EndsWith("cancellation", StringComparison.Ordinal)) { cancellation.Cancel(); }
            if (governorCancellation || !outcome.EndsWith("cancellation", StringComparison.Ordinal)) { sink.ResumeAll(); }
            await WriterIndexOwnershipTests.Finish(writing, outcome.EndsWith("cancellation", StringComparison.Ordinal) ? "cancellation" : outcome);
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
            Assert.Same(source, kind == "header" ? ((PdfStream)model[3].Value).Dictionary : model[4].Value);
            Assert.Equal(8, model.Count); GC.KeepAlive(writing); GC.KeepAlive(sink); GC.KeepAlive(model);
        }
        finally { await Drain(writing, sink); }
    }

    [Theory]
    [InlineData("header")]
    [InlineData("regular")]
    [InlineData("member")]
    public async Task FiniteNestedRemapFailureRollsBackAndRetryPreservesBorrowedModel(string kind)
    {
        List<NumberedObject> model = Model(); PdfArray large = Assert.IsType<PdfArray>(model[4].Value);
        if (kind != "header") { model[3] = new NumberedObject(4, new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), "q\nQ\n"u8.ToArray())); }
        if (kind != "regular") { model[4] = new NumberedObject(5, PdfNull.Instance); }
        if (kind == "member") { model[5] = new NumberedObject(6, large); }
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L); CountingSink sink = new();
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => Start(model, limits, governor, sink, null, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.True(sink.Length > 0L);
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes); Assert.Equal(8, Assert.IsType<PdfReference>(large[1023]).ObjectNumber);
        model[3] = new NumberedObject(4, new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), "q\nQ\n"u8.ToArray()));
        model[4] = new NumberedObject(5, PdfNull.Instance); model[5] = new NumberedObject(6, PdfNull.Instance);
        await Start(model, limits, governor, new CountingSink(), null, CancellationToken.None);
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.True(governor.Snapshot().PeakMemoryBytes <= 32768L); GC.KeepAlive(large);
    }

    [Fact]
    public async Task StreamedQdfBorrowsLargePayloadUnderUnchangedMetadataAndAllocationCeilings()
    {
        List<NumberedObject> model = WriterIndexOwnershipTests.Model(8);
        PdfStream original = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), new byte[2 * 1024 * 1024]); model[3] = new NumberedObject(4, original);
        Assert.True(MemoryMarshal.TryGetArray(original.RawMemory, out ArraySegment<byte> segment)); byte[] backing = segment.Array ?? throw new InvalidOperationException("Expected stream backing.");
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        BorrowedSink sink = new(backing); long before = GC.GetAllocatedBytesForCurrentThread();
        await Start(model, limits, governor, sink, null, CancellationToken.None);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 131072L);
        Assert.Equal(backing.Length, sink.BorrowedBytes); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.True(governor.Snapshot().PeakMemoryBytes <= 32768L);
        Assert.Same(original, model[3].Value); byte[] copy = original.Data; Assert.NotSame(backing, copy); copy[0] = 255; Assert.Equal(0, original.RawMemory.Span[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamHeaderReferencesMapOnceAndIndirectLengthAndXrefRemainReadable(bool obsoleteLength)
    {
        List<NumberedObject> model = Model();
        model[3] = new NumberedObject(4, new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Link")] = new PdfReference(8, 0), [PdfName.FromAscii("Length")] = obsoleteLength ? new PdfReference(999, 0) : new PdfInteger("4", 4) }), "q\nQ\n"u8.ToArray()));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); RecordingSink sink = new();
        await Start(model, QpdfLimits.Default, governor, sink, null, CancellationToken.None);
        byte[] output = sink.Bytes; string text = Encoding.ASCII.GetString(output);
        Assert.Matches(@"3 0 obj\s+<<\s+/Length 4 0 R\s+/Link 6 0 R\s+>>", text);
        Assert.Contains("4 0 obj\n4\nendobj", text, StringComparison.Ordinal);
        Assert.Matches(@"6 0 obj\s+88\s+endobj", text); Assert.DoesNotContain("/Link 9 0 R", text, StringComparison.Ordinal);
        using PdfDocument independent = PdfDocument.Open(output); Assert.Equal(1, independent.NumberOfPages);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    internal static async Task Drain(Task writing, PausedSink sink)
    {
        sink.ResumeAll(); try { await writing.WaitAsync(TimeSpan.FromSeconds(10)); } catch (QpdfException) { } catch (OperationCanceledException) { }
    }

    internal sealed class PausedSink(string kind, bool second, bool fail) : IByteSink
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously), _second = new(TaskCreationOptions.RunContinuationsAsynchronously),
            _resumeFirst = new(TaskCreationOptions.RunContinuationsAsynchronously), _resumeSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _pausedFirst, _pausedSecond;
        internal Task First => _first.Task; internal Task Second => _second.Task;
        internal void ResumeFirst() => _resumeFirst.TrySetResult(); internal void ResumeAll() { _resumeFirst.TrySetResult(); _resumeSecond.TrySetResult(); }
        public long Length { get; private set; }
        public async ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            bool first = kind == "header" ? data.Span.SequenceEqual("3 0 obj\n"u8) : data.Span.SequenceEqual("5 0 obj\n"u8);
            if (!_pausedFirst && first)
            {
                _pausedFirst = true; _first.TrySetResult(); await _resumeFirst.Task.WaitAsync(token);
                if (!second && fail) { throw new QpdfException("Authored value output failure."); }
            }
            bool after = _pausedFirst && (kind == "header" ? data.Span.SequenceEqual("\nstream\n"u8) : data.Span.SequenceEqual("\nendobj\n\n"u8));
            if (second && !_pausedSecond && after)
            {
                _pausedSecond = true; _second.TrySetResult(); await _resumeSecond.Task.WaitAsync(token);
                if (fail) { throw new QpdfException("Authored value output failure."); }
            }
            token.ThrowIfCancellationRequested(); Length += data.Length;
        }
    }

    private sealed class BorrowedSink(byte[] expected) : IByteSink
    {
        internal int BorrowedBytes { get; private set; } public long Length { get; private set; }
        public ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) && ReferenceEquals(expected, segment.Array))
            { Assert.Equal(BorrowedBytes, segment.Offset); BorrowedBytes += data.Length; }
            Length += data.Length; return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSink : IByteSink
    {
        private readonly MemoryStream _stream = new(); public long Length => _stream.Length; internal byte[] Bytes => _stream.ToArray();
        public ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken token) { token.ThrowIfCancellationRequested(); _stream.Write(data.Span); return ValueTask.CompletedTask; }
    }
}
