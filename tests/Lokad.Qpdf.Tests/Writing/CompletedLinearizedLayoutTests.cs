using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class CompletedLinearizedLayoutTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void FindLayouts(Task writing, List<object> layouts, int depth)
    {
        if (depth > 8) { return; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType == typeof(LinearizedLayout) && field.GetValue(state) is object layout
                && !layouts.Any(value => ReferenceEquals(value, layout))) { layouts.Add(layout); }
        }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            {
                FindAwaitedLayouts(awaiter, layouts, depth + 1);
            }
        }
    }

    private static void FindAwaitedLayouts(object value, List<object> layouts, int depth)
    {
        if (depth > 8) { return; }
        if (value is Task task) { FindLayouts(task, layouts, depth + 1); return; }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            {
                if (field.GetValue(value) is object nested) { FindAwaitedLayouts(nested, layouts, depth + 1); }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] BackingArrays(Task writing)
    {
        List<object> layouts = new();
        FindLayouts(writing, layouts, 0);
        Assert.Equal(2, layouts.Count);
        List<WeakReference> arrays = new();
        foreach (object layout in layouts)
        {
            foreach (string name in new[] { "_starts", "_lengths", "_members" })
            {
                object? map = layout.GetType().GetField(name, Instance)?.GetValue(layout);
                if (map is null) { continue; }
                Array backing = map.GetType().GetField("_entries", Instance)?.GetValue(map) as Array
                    ?? throw new InvalidOperationException("Expected scoped index backing.");
                if (backing.Length > 0) { arrays.Add(new WeakReference(backing)); }
            }
        }
        Assert.True(arrays.Count >= 2);
        return arrays.ToArray();
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
    public async Task CountingAndReplayIndexesDieWhileCompletedTasksStayRooted(string route, string outcome)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        for (int index = 0; index < 512; index++) { model.Add(new NumberedObject(index + 6, PdfNull.Instance)); }
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        LinearizedWriter writer = new(limits, governor);
        byte[] id = new byte[16];
        using CancellationTokenSource cancellation = new();
        PausedSink sink = new(outcome == "failure");
        Task<long> writing = route == "classic"
            ? writer.WriteLinearizedAsync(new PdfVersion(1, 5), model, null, id, id, false, false, null,
                sink, null, cancellation.Token).AsTask()
            : writer.WriteLinearizedStreamedAsync(new PdfVersion(1, 5), model,
                route == "fallback" ? Array.Empty<IReadOnlyList<int>>() : new IReadOnlyList<int>[] { new[] { 5 } },
                null, id, id, false, false, null, sink, null, cancellation.Token).AsTask();
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            WeakReference[] arrays = BackingArrays(writing);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.All(arrays, value => Assert.True(value.IsAlive));
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success")
            {
                long written = await writing;
                Assert.Equal(sink.Length, written);
            }
            else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(async () => await writing); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(arrays, value => Assert.False(value.IsAlive));
            Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
            Assert.Same(PdfNull.Instance, model[^1].Value);
            GC.KeepAlive(writing); GC.KeepAlive(writer); GC.KeepAlive(sink); GC.KeepAlive(model);
        }
        finally
        {
            sink.Resume();
            try { await writing; }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
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
                if (fail) { throw new QpdfException("Authored completed-layout failure."); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }
}
