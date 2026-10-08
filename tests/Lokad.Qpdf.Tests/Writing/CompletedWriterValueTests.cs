using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class CompletedWriterValueTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed record Witness(WriterValue Owner, ParserMemoryScope Scope, WeakReference Graph, WeakReference Backing);

    private static void Find(Task task, List<WriterValue> owners, int depth)
    {
        if (depth > 24) { return; }
        object? state = task.GetType().GetField("StateMachine", Instance)?.GetValue(task); if (state is null) { return; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.GetValue(state) is object value)
            {
                if (value is WriterValue owner && !owners.Contains(owner)) { owners.Add(owner); }
                if (field.Name.StartsWith("<>u__", StringComparison.Ordinal)) { FindAwaited(value, owners, depth + 1); }
            }
        }
    }
    private static void FindAwaited(object value, List<WriterValue> owners, int depth)
    {
        if (depth > 24) { return; } if (value is Task task) { Find(task, owners, depth + 1); return; }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        { foreach (FieldInfo field in value.GetType().GetFields(Instance)) { if (field.GetValue(value) is object nested) { FindAwaited(nested, owners, depth + 1); } } }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Witness Capture(Task task)
    {
        List<WriterValue> owners = new(); Find(task, owners, 0); WriterValue owner = Assert.Single(owners);
        QpdfObject graph = owner.Value; object backing = graph switch
        {
            PdfArray array => QdfEmissionPlanTests.Field(array, "_items"),
            PdfDictionary dictionary => QdfEmissionPlanTests.Field(dictionary, "_entries"),
            _ => throw new InvalidOperationException("Expected rebuilt metadata."),
        };
        ParserMemoryScope scope = Assert.IsType<ParserMemoryScope>(QdfEmissionPlanTests.Field(owner, "_memory")); Assert.True(scope.ReservedBytes > 41000L);
        return new Witness(owner, scope, new WeakReference(graph), new WeakReference(backing));
    }

    private static void Detached(Witness witness)
    {
        Assert.Null(witness.Owner.GetType().GetField("_memory", Instance)?.GetValue(witness.Owner));
        Assert.Null(witness.Owner.GetType().GetField("_value", Instance)?.GetValue(witness.Owner)); Assert.Equal(0L, witness.Scope.ReservedBytes);
    }

    [Theory]
    [MemberData(nameof(WriterValueOwnershipTests.Outcomes), MemberType = typeof(WriterValueOwnershipTests))]
    public async Task RootedTasksAndValueOwnersDropDeadMetadataAcrossEveryOutputOutcome(string kind, bool retired, string outcome)
    {
        List<NumberedObject> model = WriterValueOwnershipTests.Model();
        using CancellationTokenSource cancellation = new(); bool governorCancellation = outcome == "governor-cancellation";
        ResourceGovernor governor = new(QpdfLimits.Default, governorCancellation ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        WriterValueOwnershipTests.PausedSink sink = new(kind, retired, outcome == "failure");
        Task writing = WriterValueOwnershipTests.Start(model, QpdfLimits.Default, governor, sink, null, governorCancellation ? CancellationToken.None : cancellation.Token);
        await sink.First.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Witness witness = Capture(writing); QdfEmissionPlanTests.Collected(); Assert.True(witness.Graph.IsAlive); Assert.True(witness.Backing.IsAlive);
            if (retired)
            {
                sink.ResumeFirst(); await sink.Second.WaitAsync(TimeSpan.FromSeconds(10)); Detached(witness);
                QdfEmissionPlanTests.Collected(); Assert.False(witness.Graph.IsAlive); Assert.False(witness.Backing.IsAlive);
            }
            if (outcome.EndsWith("cancellation", StringComparison.Ordinal)) { cancellation.Cancel(); }
            if (governorCancellation || !outcome.EndsWith("cancellation", StringComparison.Ordinal)) { sink.ResumeAll(); }
            await WriterIndexOwnershipTests.Finish(writing, outcome.EndsWith("cancellation", StringComparison.Ordinal) ? "cancellation" : outcome);
            Detached(witness); QdfEmissionPlanTests.Collected(); Assert.False(witness.Graph.IsAlive); Assert.False(witness.Backing.IsAlive);
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
            GC.KeepAlive(witness); GC.KeepAlive(writing); GC.KeepAlive(model); GC.KeepAlive(sink);
        }
        finally { await WriterValueOwnershipTests.Drain(writing, sink); }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("caller-cancellation")]
    [InlineData("governor-cancellation")]
    public async Task MemberMetadataRetiresAfterCaptureAndOnObservedWorkFailureOrCancellation(string outcome)
    {
        List<NumberedObject> model = WriterValueOwnershipTests.Model(); PdfArray source = Assert.IsType<PdfArray>(model[4].Value);
        model[3] = new NumberedObject(4, new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), "q\nQ\n"u8.ToArray()));
        model[4] = new NumberedObject(5, PdfNull.Instance); model[5] = new NumberedObject(6, source);
        using CancellationTokenSource cancellation = new(); bool governorCancellation = outcome == "governor-cancellation";
        ResourceGovernor governor = new(QpdfLimits.Default, governorCancellation ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        WriterValueOwnershipTests.PausedSink sink = new("header", true, false); Witness? witness = null; Task? writing = null;
        ObservedIds ids = new(() =>
        {
            witness = Capture(writing ?? throw new InvalidOperationException("Writer has not reached its first await."));
            Assert.True(governor.Snapshot().TotalWork > 1024L);
            if (outcome.EndsWith("cancellation", StringComparison.Ordinal)) { cancellation.Cancel(); }
            if (outcome == "failure") { throw new QpdfException("Authored member metadata failure."); }
        });
        writing = WriterValueOwnershipTests.Start(model, QpdfLimits.Default, governor, sink, ids, governorCancellation ? CancellationToken.None : cancellation.Token);
        await sink.First.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            sink.ResumeAll(); await WriterIndexOwnershipTests.Finish(writing, outcome.EndsWith("cancellation", StringComparison.Ordinal) ? "cancellation" : outcome);
            Witness observed = Assert.IsType<Witness>(witness); Detached(observed); QdfEmissionPlanTests.Collected();
            Assert.False(observed.Graph.IsAlive); Assert.False(observed.Backing.IsAlive); Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(97L, prior.ReservedBytes); Assert.Equal(8, Assert.IsType<PdfReference>(source[1023]).ObjectNumber);
            GC.KeepAlive(observed); GC.KeepAlive(writing); GC.KeepAlive(ids); GC.KeepAlive(model); GC.KeepAlive(source);
        }
        finally { await WriterValueOwnershipTests.Drain(writing, sink); }
    }

    private sealed class ObservedIds(Action observe) : IReadOnlyDictionary<int, int>
    {
        public bool TryGetValue(int key, out int value) { if (key == 6) { observe(); } value = 0; return false; }
        public int Count => 0; public IEnumerable<int> Keys => Array.Empty<int>(); public IEnumerable<int> Values => Array.Empty<int>();
        public int this[int key] => throw new KeyNotFoundException(); public bool ContainsKey(int key) => false;
        public IEnumerator<KeyValuePair<int, int>> GetEnumerator() => Enumerable.Empty<KeyValuePair<int, int>>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
