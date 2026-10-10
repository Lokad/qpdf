using Xunit.Abstractions;
using Lokad.Qpdf.Tests.Foundation;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CompletedLinearizedRemapTests
{
    private readonly ITestOutputHelper _output;
    private OwnershipBatch.Observation? _collection;

    public CompletedLinearizedRemapTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static object? ActiveOwner(Task writing, int depth)
    {
        if (depth > 8) { return null; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return null; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType == typeof(RemappedModel) && field.GetValue(state) is object owner) { return owner; }
        }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            {
                object? owner = AwaitedOwner(awaiter, depth + 1);
                if (owner is not null) { return owner; }
            }
        }
        return null;
    }

    private static object? AwaitedOwner(object value, int depth)
    {
        if (depth > 8) { return null; }
        if (value is Task task) { return ActiveOwner(task, depth + 1); }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            {
                if (field.GetValue(value) is object nested)
                {
                    object? owner = AwaitedOwner(nested, depth + 1);
                    if (owner is not null) { return owner; }
                }
            }
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RebuiltItems(Task writing)
    {
        object owner = ActiveOwner(writing, 0)
            ?? throw new InvalidOperationException("Expected remapped owner.");
        GovernedIntDictionary<QpdfObject> objects = typeof(RemappedModel).GetField("_objects", Instance)?.GetValue(owner)
            as GovernedIntDictionary<QpdfObject> ?? throw new InvalidOperationException("Expected remapped objects.");
        foreach (KeyValuePair<int, QpdfObject> entry in objects)
        {
            if (entry.Value is PdfArray array && array.Count == 1024)
            {
                return new WeakReference(array.AllocationCarrier);
            }
        }
        throw new InvalidOperationException("Expected rebuilt tail array.");
    }

    internal async Task RetainedCompletedTasksReleaseRebuiltStorage(string route, string outcome)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        PdfArray borrowed = new(Enumerable.Repeat<QpdfObject>(new PdfReference(5, 0), 1024).ToArray());
        model.Add(new NumberedObject(6, borrowed));
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
            WeakReference items = RebuiltItems(writing);
            await ObserveAsync(governor, 1);
            Assert.True(items.IsAlive);
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success")
            {
                long written = await writing;
                Assert.Equal(sink.Length, written);
            }
            else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(async () => await writing); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            await ObserveAsync(governor, 3);
            Assert.False(items.IsAlive);
            Assert.Same(borrowed, model[^1].Value);
            Assert.Equal(5, Assert.IsType<PdfReference>(borrowed[1023]).ObjectNumber);
            GC.KeepAlive(writing);
            GC.KeepAlive(writer);
            GC.KeepAlive(sink);
            GC.KeepAlive(model);
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
                if (fail) { throw new QpdfException("Authored completed-writer failure."); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }

    private Task ObserveAsync(ResourceGovernor governor, int rounds)
        => (_collection ?? throw new InvalidOperationException("Missing ownership batch."))
            .CollectAsync(governor, 0, rounds, false, false);

    private static IEnumerable<(string Method, IEnumerable<object[]> Rows)> OwnershipCases()
        => new (string, IEnumerable<object[]>)[]
        {
            (nameof(RetainedCompletedTasksReleaseRebuiltStorage), new object[][] { ["classic", "success"], ["classic", "failure"], ["classic", "cancellation"], ["streamed", "success"], ["streamed", "failure"], ["streamed", "cancellation"], ["fallback", "success"], ["fallback", "failure"], ["fallback", "cancellation"] }),
        };

    public static IEnumerable<object[]> OwnershipBatches() => OwnershipMatrix.Batches(OwnershipCases());

    [Theory]
    [MemberData(nameof(OwnershipBatches))]
    [Trait("Layer", "Ownership")]
    public Task BatchedLifetimeObservationsPreserveEveryOriginalPhase(int batch)
        => OwnershipMatrix.RunAsync(_output, OwnershipCases(), batch,
            observation => new CompletedLinearizedRemapTests(_output) { _collection = observation });
}
