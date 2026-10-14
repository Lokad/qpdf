using Xunit.Abstractions;
using Lokad.Qpdf.Tests.Foundation;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CompletedObjectStreamCaptureTests
{
    private readonly ITestOutputHelper _output;
    private OwnershipBatch.Observation? _collection;

    public CompletedObjectStreamCaptureTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void FindOwners(Task writing, List<ObjectStreamCapture> owners, int depth)
    {
        if (depth > 24) { return; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType == typeof(ObjectStreamCapture) && field.GetValue(state) is ObjectStreamCapture owner
                && !owners.Any(value => ReferenceEquals(value, owner))) { owners.Add(owner); }
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            {
                FindAwaitedOwners(awaiter, owners, depth + 1);
            }
        }
    }

    private static void FindAwaitedOwners(object value, List<ObjectStreamCapture> owners, int depth)
    {
        if (depth > 24) { return; }
        if (value is Task task) { FindOwners(task, owners, depth + 1); return; }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            {
                if (field.GetValue(value) is object nested) { FindAwaitedOwners(nested, owners, depth + 1); }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ObjectStreamCapture[] Owners, ParserMemoryScope[] Scopes, WeakReference[] Arrays) FundedStorage(Task writing, string route)
    {
        List<ObjectStreamCapture> owners = new();
        FindOwners(writing, owners, 0);
        Assert.Equal(route == "qdf" ? 2 : 1, owners.Count);
        List<ParserMemoryScope> scopes = new();
        List<WeakReference> arrays = new();
        long totalCaptured = 0L;
        foreach (ObjectStreamCapture owner in owners)
        {
            int[] ends = typeof(ObjectStreamCapture).GetField("_ends", Instance)?.GetValue(owner) as int[]
                ?? throw new InvalidOperationException("Expected member table.");
            ParserMemoryScope scope = typeof(ObjectStreamCapture).GetField("_memory", Instance)?.GetValue(owner) as ParserMemoryScope
                ?? throw new InvalidOperationException("Expected capture scope.");
            arrays.Add(new WeakReference(ends));
            for (int index = 0; index < owner.Count; index++)
            {
                totalCaptured += owner[index].Length;
            }
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(owner[0], out ArraySegment<byte> backing));
            Assert.NotNull(backing.Array);
            arrays.Add(new WeakReference(backing.Array));
            Assert.InRange(scope.ReservedBytes, ends.Length * (long)sizeof(int), QpdfLimits.Default.MaxMemoryBytes);
            scopes.Add(scope);
        }
        Assert.True(totalCaptured >= 4096L * 4L);
        return (owners.ToArray(), scopes.ToArray(), arrays.ToArray());
    }

    internal async Task CapturesStayFundedThroughOutputAndDieWithRetainedTasks(string route, string outcome)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        PausedContainerSink sink = new(outcome == "failure");
        Task<long> writing = ObjectStreamWriterCaptureTests.Start(route, ObjectStreamWriterCaptureTests.Model(false),
            QpdfLimits.Default, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (ObjectStreamCapture[] owners, ParserMemoryScope[] scopes, WeakReference[] arrays) = FundedStorage(writing, route);
            await ObserveAsync(governor, 1);
            Assert.All(arrays, value => Assert.True(value.IsAlive));
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { Assert.True(await writing > 0L); }
            else if (outcome == "failure")
            {
                QpdfException failure = await Assert.ThrowsAsync<QpdfException>(async () => await writing);
                Assert.Equal("Authored captured-member output failure.", failure.Message);
            }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.All(scopes, scope => Assert.Equal(0L, scope.ReservedBytes));
            Assert.All(owners, owner => Assert.Null(typeof(ObjectStreamCapture).GetField("_ends", Instance)?.GetValue(owner)));
            await ObserveAsync(governor, 3);
            Assert.All(arrays, value => Assert.False(value.IsAlive));
            GC.KeepAlive(writing); GC.KeepAlive(sink); GC.KeepAlive(owners); GC.KeepAlive(scopes);
        }
        finally
        {
            sink.Resume();
            try { await writing; }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class PausedContainerSink(bool fail) : IByteSink
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
                if (fail) { throw new QpdfException("Authored captured-member output failure."); }
            }
            _next |= chunk.Span.IndexOf("/Type /ObjStm"u8) >= 0;
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
            (nameof(CapturesStayFundedThroughOutputAndDieWithRetainedTasks), new object[][] { ["ordinary", "success"], ["ordinary", "failure"], ["ordinary", "cancellation"], ["qdf", "success"], ["qdf", "failure"], ["qdf", "cancellation"], ["linearized", "success"], ["linearized", "failure"], ["linearized", "cancellation"], ["counting", "success"], ["counting", "failure"], ["counting", "cancellation"] }),
        };

    public static IEnumerable<object[]> OwnershipBatches() => OwnershipMatrix.Batches(OwnershipCases());

    [Theory]
    [MemberData(nameof(OwnershipBatches))]
    [Trait("Layer", "Ownership")]
    public Task BatchedLifetimeObservationsPreserveEveryOriginalPhase(int batch)
        => OwnershipMatrix.RunAsync(_output, OwnershipCases(), batch,
            observation => new CompletedObjectStreamCaptureTests(_output) { _collection = observation });
}
