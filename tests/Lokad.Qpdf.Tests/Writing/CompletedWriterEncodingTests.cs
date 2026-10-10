using Xunit.Abstractions;
using Lokad.Qpdf.Tests.Foundation;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CompletedWriterEncodingTests
{
    private readonly ITestOutputHelper _output;
    private OwnershipBatch.Observation? _collection;

    public CompletedWriterEncodingTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static WriterEncoding? FindOwner(Task writing, int depth)
    {
        if (depth > 24) { return null; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return null; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType == typeof(WriterEncoding) && field.GetValue(state) is WriterEncoding owner) { return owner; }
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            {
                WriterEncoding? found = FindAwaitedOwner(awaiter, depth + 1);
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    private static WriterEncoding? FindAwaitedOwner(object value, int depth)
    {
        if (depth > 24) { return null; }
        if (value is Task task) { return FindOwner(task, depth + 1); }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            {
                if (field.GetValue(value) is object nested)
                {
                    WriterEncoding? found = FindAwaitedOwner(nested, depth + 1);
                    if (found is not null) { return found; }
                }
            }
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WriterEncoding Owner, ParserMemoryScope Scope, WeakReference[] Arrays) Funded(Task writing, string route)
    {
        WriterEncoding owner = FindOwner(writing, 0) ?? throw new InvalidOperationException("Expected awaited encoding owner.");
        ParserMemoryScope scope = typeof(WriterEncoding).GetField("_memory", Instance)?.GetValue(owner) as ParserMemoryScope
            ?? throw new InvalidOperationException("Expected encoding scope.");
        byte[] bytes = typeof(WriterEncoding).GetField("_bytes", Instance)?.GetValue(owner) as byte[]
            ?? throw new InvalidOperationException("Expected encoding backing.");
        Assert.Equal(WriterEncodingOwnershipTests.Length(route), bytes.Length);
        Assert.Equal(64L + 32L + bytes.Length, scope.ReservedBytes);
        return (owner, scope, new[] { new WeakReference(bytes) });
    }

    internal async Task EncodedBackingDiesWithRetainedOwnersAndTasks(string route, string outcome)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        WriterEncodingOwnershipTests.PausedSink sink = new(outcome == "failure");
        object source = WriterEncodingOwnershipTests.Source(route);
        Task writing = WriterEncodingOwnershipTests.Start(route, source, QpdfLimits.Default, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (WriterEncoding owner, ParserMemoryScope scope, WeakReference[] arrays) = Funded(writing, route);
            await ObserveAsync(governor, 1);
            Assert.All(arrays, value => Assert.True(value.IsAlive));
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { await writing; }
            else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(async () => await writing); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.Equal(0L, scope.ReservedBytes);
            Assert.Throws<ObjectDisposedException>(() => owner.Data);
            await ObserveAsync(governor, 3);
            Assert.All(arrays, value => Assert.False(value.IsAlive));
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            GC.KeepAlive(writing); GC.KeepAlive(sink); GC.KeepAlive(owner); GC.KeepAlive(scope); GC.KeepAlive(source);
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
            (nameof(EncodedBackingDiesWithRetainedOwnersAndTasks), WriterEncodingOwnershipTests.Routes()),
        };

    public static IEnumerable<object[]> OwnershipBatches() => OwnershipMatrix.Batches(OwnershipCases());

    [Theory]
    [MemberData(nameof(OwnershipBatches))]
    [Trait("Layer", "Ownership")]
    public Task BatchedLifetimeObservationsPreserveEveryOriginalPhase(int batch)
        => OwnershipMatrix.RunAsync(_output, OwnershipCases(), batch,
            observation => new CompletedWriterEncodingTests(_output) { _collection = observation });
}
