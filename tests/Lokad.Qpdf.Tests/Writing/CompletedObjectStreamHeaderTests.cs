using Xunit.Abstractions;
using Lokad.Qpdf.Tests.Foundation;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CompletedObjectStreamHeaderTests
{
    private readonly ITestOutputHelper _output;
    private OwnershipBatch.Observation? _collection;

    public CompletedObjectStreamHeaderTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static ObjectStreamHeader? FindOwner(Task writing, int depth)
    {
        if (depth > 24) { return null; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return null; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType == typeof(ObjectStreamHeader) && field.GetValue(state) is ObjectStreamHeader owner) { return owner; }
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            {
                ObjectStreamHeader? found = FindAwaitedOwner(awaiter, depth + 1);
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    private static ObjectStreamHeader? FindAwaitedOwner(object value, int depth)
    {
        if (depth > 24) { return null; }
        if (value is Task task) { return FindOwner(task, depth + 1); }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            {
                if (field.GetValue(value) is object nested)
                {
                    ObjectStreamHeader? found = FindAwaitedOwner(nested, depth + 1);
                    if (found is not null) { return found; }
                }
            }
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ObjectStreamHeader Owner, ParserMemoryScope Scope, WeakReference[] Arrays) Funded(Task writing, bool compress)
    {
        ObjectStreamHeader owner = FindOwner(writing, 0) ?? throw new InvalidOperationException("Expected awaited payload owner.");
        ParserMemoryScope scope = typeof(ObjectStreamHeader).GetField("_memory", Instance)?.GetValue(owner) as ParserMemoryScope
            ?? throw new InvalidOperationException("Expected payload scope.");
        byte[] bytes = typeof(ObjectStreamHeader).GetField("_bytes", Instance)?.GetValue(owner) as byte[]
            ?? throw new InvalidOperationException("Expected header backing.");
        Assert.InRange(bytes.Length, 1, 255);
        Assert.Equal(compress, bytes.AsSpan().IndexOf("/FlateDecode"u8) >= 0);
        Assert.Equal(64L + 32L + bytes.Length, scope.ReservedBytes);
        return (owner, scope, new[] { new WeakReference(bytes) });
    }

    internal async Task HeaderBufferDiesWithRetainedOwnersAndTasks(string route, bool compress, string outcome)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        ObjectStreamHeaderOwnershipTests.HeaderSink sink = new(governor, outcome == "failure");
        Task<long> writing = ObjectStreamPayloadOwnershipTests.Start(route, compress, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (ObjectStreamHeader owner, ParserMemoryScope scope, WeakReference[] arrays) = Funded(writing, compress);
            await ObserveAsync(governor, 1);
            Assert.All(arrays, value => Assert.True(value.IsAlive));
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { Assert.True(await writing > 0L); }
            else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(async () => await writing); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.Equal(0L, scope.ReservedBytes);
            Assert.Throws<ObjectDisposedException>(() => owner.Data);
            await ObserveAsync(governor, 3);
            Assert.All(arrays, value => Assert.False(value.IsAlive));
            Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
            GC.KeepAlive(writing); GC.KeepAlive(sink); GC.KeepAlive(owner); GC.KeepAlive(scope);
        }
        finally
        {
            sink.Resume();
            try { await writing; }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
        }
    }

    private Task ObserveAsync(ResourceGovernor governor, int rounds)
        => (_collection ?? throw new InvalidOperationException("Missing ownership batch."))
            .CollectAsync(governor, 0, rounds, false, false);

    private static IEnumerable<(string Method, IEnumerable<object[]> Rows)> OwnershipCases()
        => new (string, IEnumerable<object[]>)[]
        {
            (nameof(HeaderBufferDiesWithRetainedOwnersAndTasks), new object[][] { ["ordinary", false, "success"], ["ordinary", false, "failure"], ["ordinary", false, "cancellation"], ["ordinary", true, "success"], ["ordinary", true, "failure"], ["ordinary", true, "cancellation"], ["qdf", false, "success"], ["qdf", false, "failure"], ["qdf", false, "cancellation"], ["linearized", false, "success"], ["linearized", false, "failure"], ["linearized", false, "cancellation"], ["linearized", true, "success"], ["linearized", true, "failure"], ["linearized", true, "cancellation"], ["counting", false, "success"], ["counting", false, "failure"], ["counting", false, "cancellation"], ["counting", true, "success"], ["counting", true, "failure"], ["counting", true, "cancellation"] }),
        };

    public static IEnumerable<object[]> OwnershipBatches() => OwnershipMatrix.Batches(OwnershipCases());

    [Theory]
    [MemberData(nameof(OwnershipBatches))]
    [Trait("Layer", "Ownership")]
    public Task BatchedLifetimeObservationsPreserveEveryOriginalPhase(int batch)
        => OwnershipMatrix.RunAsync(_output, OwnershipCases(), batch,
            observation => new CompletedObjectStreamHeaderTests(_output) { _collection = observation });
}
