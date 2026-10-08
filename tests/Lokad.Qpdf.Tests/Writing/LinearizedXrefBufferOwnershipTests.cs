using System.Reflection;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedXrefBufferOwnershipTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static ParserMemoryScope? FindScope(Task writing, int depth)
    {
        if (depth > 8) { return null; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return null; }
        if (state.GetType().Name.StartsWith("<AppendXrefStreamAsync>", StringComparison.Ordinal))
        {
            foreach (FieldInfo field in state.GetType().GetFields(Instance))
            {
                if (field.FieldType == typeof(ParserMemoryScope) && field.GetValue(state) is ParserMemoryScope memory)
                {
                    return memory;
                }
            }
        }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            {
                ParserMemoryScope? found = FindAwaitedScope(awaiter, depth + 1);
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    private static ParserMemoryScope? FindAwaitedScope(object value, int depth)
    {
        if (depth > 8) { return null; }
        if (value is Task task) { return FindScope(task, depth + 1); }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            {
                if (field.GetValue(value) is object nested)
                {
                    ParserMemoryScope? found = FindAwaitedScope(nested, depth + 1);
                    if (found is not null) { return found; }
                }
            }
        }
        return null;
    }

    [Theory]
    [InlineData(false, 1, "success")]
    [InlineData(false, 1, "failure")]
    [InlineData(false, 1, "cancellation")]
    [InlineData(false, 2, "success")]
    [InlineData(false, 2, "failure")]
    [InlineData(false, 2, "cancellation")]
    [InlineData(true, 1, "success")]
    [InlineData(true, 1, "failure")]
    [InlineData(true, 1, "cancellation")]
    [InlineData(true, 2, "success")]
    [InlineData(true, 2, "failure")]
    [InlineData(true, 2, "cancellation")]
    public async Task XrefBytesStayFundedAcrossAwaitAndDieWithCompletedTasks(bool firstPass, int xref, string outcome)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        for (int index = 0; index < 512; index++) { model.Add(new NumberedObject(index + 6, PdfNull.Instance)); }
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        LinearizedWriter writer = new(limits, governor);
        using CancellationTokenSource cancellation = new();
        PausedXrefSink paused = new(xref, outcome == "failure");
        CountingSink other = new();
        byte[] id = new byte[16];
        Task<long> writing = writer.WriteLinearizedStreamedAsync(new PdfVersion(1, 5), model,
            new IReadOnlyList<int>[] { new[] { 5 } }, null, id, id, false, false, null,
            firstPass ? other : paused, firstPass ? paused : null, cancellation.Token).AsTask();
        await paused.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            ParserMemoryScope memory = Assert.IsType<ParserMemoryScope>(FindScope(writing, 0));
            Assert.Equal(32L + paused.BufferLength, memory.ReservedBytes);
            WeakReference buffer = paused.Buffer ?? throw new InvalidOperationException("Expected xref buffer.");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.True(buffer.IsAlive);
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
                Assert.Equal("Authored xref buffer failure.", failure.Message);
            }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.Equal(0L, memory.ReservedBytes);
            for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.False(buffer.IsAlive);
            Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
            GC.KeepAlive(writing); GC.KeepAlive(writer); GC.KeepAlive(paused); GC.KeepAlive(memory);
        }
        finally
        {
            paused.Resume();
            try { await writing; }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class PausedXrefSink(int target, bool fail) : IByteSink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _xref;
        private bool _next;
        internal Task Entered => _entered.Task;
        internal WeakReference? Buffer { get; private set; }
        internal int BufferLength { get; private set; }
        public long Length { get; private set; }
        internal void Resume() => _resume.TrySetResult();

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            if (_next)
            {
                _next = false;
                Assert.True(MemoryMarshal.TryGetArray(chunk, out ArraySegment<byte> segment));
                Buffer = new WeakReference(segment.Array ?? throw new InvalidOperationException("Expected xref array."));
                BufferLength = chunk.Length;
                _entered.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
                if (fail) { throw new QpdfException("Authored xref buffer failure."); }
            }
            else if (chunk.Span.IndexOf("/Type /XRef /Length "u8) >= 0)
            {
                _xref++;
                _next = _xref == target;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }
}
