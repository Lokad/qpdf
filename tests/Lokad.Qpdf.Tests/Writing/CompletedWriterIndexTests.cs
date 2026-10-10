using Xunit.Abstractions;
using Lokad.Qpdf.Tests.Foundation;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class CompletedWriterIndexTests
{
    private readonly ITestOutputHelper _output;
    private OwnershipBatch.Observation? _collection;

    public CompletedWriterIndexTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void FindOwners(Task writing, List<object> owners, int depth)
    {
        if (depth > 24) { return; }
        object? state = writing.GetType().GetField("StateMachine",Instance)?.GetValue(writing);
        if (state is null) { return; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType.IsGenericType)
            {
                Type definition = field.FieldType.GetGenericTypeDefinition();
                if ((definition == typeof(WriterIndex<>) || definition == typeof(WriterOrder<>))
                    && field.GetValue(state) is object owner && !owners.Any(value => ReferenceEquals(value,owner))) { owners.Add(owner); }
            }
            if (field.Name.StartsWith("<>u__",StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            { FindAwaited(awaiter,owners,depth+1); }
        }
    }
    private static void FindAwaited(object value, List<object> owners, int depth)
    {
        if (depth > 24) { return; }
        if (value is Task task) { FindOwners(task,owners,depth+1); return; }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        { foreach (FieldInfo field in value.GetType().GetFields(Instance)) { if (field.GetValue(value) is object nested) { FindAwaited(nested,owners,depth+1); } } }
    }

    private sealed record Witness(object Owner, ParserMemoryScope? Scope, WeakReference? Buckets, WeakReference? Entries);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<Witness> Capture(Task writing)
    {
        List<object> owners = new(); FindOwners(writing,owners,0);
        List<Witness> witnesses = new();
        foreach (object owner in owners)
        {
            ParserMemoryScope? memory = owner.GetType().GetField("_memory",Instance)?.GetValue(owner) as ParserMemoryScope;
            object? index = owner.GetType().GetField("_index",Instance)?.GetValue(owner);
            Array? entries = index?.GetType().GetField("_entries",Instance)?.GetValue(index) as Array;
            Array? buckets = index?.GetType().GetField("_buckets",Instance)?.GetValue(index) as Array;
            Array? items = owner.GetType().GetField("_items",Instance)?.GetValue(owner) as Array;
            if (memory is not null && entries is not null && buckets is not null)
            {
                Assert.Equal(entries.Length,buckets.Length);
                Assert.Equal(208L + (entries.Length == 0 ? 0L : 64L + 20L * entries.Length),memory.ReservedBytes);
            }
            else if (memory is not null && items is not null)
            {
                Type element = items.GetType().GetElementType() ?? throw new InvalidOperationException("Expected writer row type.");
                long itemBytes = element == typeof(long) || element == typeof(int) ? element == typeof(long) ? sizeof(long) : sizeof(int)
                    : element == typeof((int,int,QpdfObject,int?)) ? 32L
                    : element == typeof((int,IReadOnlyList<int>)) ? 16L
                    : throw new InvalidOperationException("Unexpected writer row type.");
                Assert.Equal(96L + itemBytes * items.Length,memory.ReservedBytes);
            }
            else { Assert.Null(memory); }
            witnesses.Add(new Witness(owner,memory,buckets is {Length: >0} ? new WeakReference(buckets) : null,
                entries is {Length: >0} ? new WeakReference(entries) : items is {Length: >0} ? new WeakReference(items) : null));
        }
        return witnesses;
    }


    private static void Detached(object owner)
    {
        Assert.Null(owner.GetType().GetField("_memory",Instance)?.GetValue(owner));
        Assert.Null(owner.GetType().GetField("_index",Instance)?.GetValue(owner));
        Assert.Null(owner.GetType().GetField("_items",Instance)?.GetValue(owner));
    }

    public static IEnumerable<object[]> CompletionOutcomes()
    {
        foreach (object[] item in WriterIndexOwnershipTests.AwaitedOutcomes())
        { if ((string)item[0] != "ordinary" || (string)item[1] != "version") { yield return item; } }
    }

    internal async Task CompletedTasksAndRootedWrappersReleaseIndexBacking(string route, string point, string outcome)
    {
        var model = WriterIndexOwnershipTests.Model(513);
        ResourceGovernor governor = new(QpdfLimits.Default,CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        WriterIndexOwnershipTests.PausedSink sink = new(point,outcome == "failure");
        Task<long> writing = WriterIndexOwnershipTests.Start(route,model,QpdfLimits.Default,governor,sink,cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            List<Witness> witnesses = Capture(writing);
            Assert.NotEmpty(witnesses); Assert.Contains(witnesses,value => value.Scope is not null);
            await ObserveAsync(governor, 3);
            foreach (Witness witness in witnesses)
            { if (witness.Buckets is not null) { Assert.True(witness.Buckets.IsAlive); }; if (witness.Entries is not null) { Assert.True(witness.Entries.IsAlive); } }
            if (outcome == "cancellation") { cancellation.Cancel(); } else { sink.Resume(); }
            await WriterIndexOwnershipTests.Finish(writing,outcome);
            foreach (Witness witness in witnesses)
            { Detached(witness.Owner); if (witness.Scope is not null) { Assert.Equal(0L,witness.Scope.ReservedBytes); } }
            await ObserveAsync(governor, 3);
            foreach (Witness witness in witnesses)
            { if (witness.Buckets is not null) { Assert.False(witness.Buckets.IsAlive); }; if (witness.Entries is not null) { Assert.False(witness.Entries.IsAlive); } }
            Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes); Assert.Equal(513,model.Count);
            GC.KeepAlive(writing); GC.KeepAlive(model); GC.KeepAlive(sink); GC.KeepAlive(witnesses);
        }
        finally { await WriterIndexOwnershipTests.Drain(writing,sink); }
    }

    public static IEnumerable<object[]> EarlyRetirementOutcomes()
    {
        foreach (string route in new[] {"ordinary","ordinary-streamed","qdf","qdf-streamed"})
        { foreach (string outcome in new[] {"success","failure","cancellation"}) { yield return [route,outcome]; } }
    }

    internal async Task MetadataRetiresBeforeUnrelatedTrailerOrXrefOutput(string route, string outcome)
    {
        var model = WriterIndexOwnershipTests.Model(513);
        ResourceGovernor governor = new(QpdfLimits.Default,CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        WriterIndexOwnershipTests.PausedSink sink = new(route.EndsWith("streamed",StringComparison.Ordinal) ? "streamed-retired" : "classic-retired",outcome == "failure");
        Task<long> writing = WriterIndexOwnershipTests.Start(route,model,QpdfLimits.Default,governor,sink,cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            List<Witness> witnesses = Capture(writing); Assert.NotEmpty(witnesses);
            foreach (Witness witness in witnesses) { Detached(witness.Owner); Assert.Null(witness.Scope); }
            await ObserveAsync(governor, 3);
            if (outcome == "cancellation") { cancellation.Cancel(); } else { sink.Resume(); }
            await WriterIndexOwnershipTests.Finish(writing,outcome);
            Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);
            GC.KeepAlive(writing); GC.KeepAlive(model); GC.KeepAlive(sink); GC.KeepAlive(witnesses);
        }
        finally { await WriterIndexOwnershipTests.Drain(writing,sink); }
    }

    public static IEnumerable<object[]> StreamAndInfoOutcomes()
    {
        foreach ((string route,string point) in new[]
        {
            ("qdf","last-length-header"), ("qdf","last-length-value"), ("qdf","info-value"),
            ("qdf-streamed","xref-info-header"), ("qdf-streamed","info-value"),
        })
        { foreach (string outcome in new[] {"success","failure","cancellation"}) { yield return [route,point,outcome]; } }
    }

    internal async Task StreamLengthAndInfoKeepRenumberingOnlyThroughLastConsumer(string route, string point, string outcome)
    {
        var model = WriterIndexOwnershipTests.StreamModel();
        ResourceGovernor governor = new(QpdfLimits.Default,CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        WriterIndexOwnershipTests.PausedSink sink = new(point,outcome == "failure");
        Task<long> writing = WriterIndexOwnershipTests.StartQdf(route,model,governor,6,sink,cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            List<Witness> witnesses = Capture(writing); Assert.NotEmpty(witnesses);
            List<Witness> renumbers = witnesses.Where(value => value.Owner.GetType() == typeof(WriterIndex<int>)).ToList();
            Assert.Equal(route == "qdf" ? 1 : 3,renumbers.Count);
            bool stillUsed = point is "last-length-header" or "xref-info-header";
            if (stillUsed)
            {
                Witness renumber = Assert.Single(renumbers,value => value.Scope is not null);
                Assert.NotNull(renumber.Scope); Assert.Equal(513,((WriterIndex<int>)renumber.Owner).Count);
                Assert.NotNull(renumber.Buckets); Assert.NotNull(renumber.Entries);
                foreach (Witness retired in renumbers.Where(value => !ReferenceEquals(value,renumber))) { Detached(retired.Owner); Assert.Null(retired.Scope); }
            }
            else { foreach (Witness renumber in renumbers) { Detached(renumber.Owner); Assert.Null(renumber.Scope); } }
            if (point == "info-value")
            { foreach (Witness witness in witnesses) { Detached(witness.Owner); Assert.Null(witness.Scope); } }
            await ObserveAsync(governor, 3);
            foreach (Witness witness in witnesses)
            { if (witness.Buckets is not null) { Assert.True(witness.Buckets.IsAlive); } if (witness.Entries is not null) { Assert.True(witness.Entries.IsAlive); } }
            if (outcome == "cancellation") { cancellation.Cancel(); } else { sink.Resume(); }
            await WriterIndexOwnershipTests.Finish(writing,outcome);
            foreach (Witness witness in witnesses)
            { Detached(witness.Owner); if (witness.Scope is not null) { Assert.Equal(0L,witness.Scope.ReservedBytes); } }
            await ObserveAsync(governor, 3);
            foreach (Witness witness in witnesses)
            { if (witness.Buckets is not null) { Assert.False(witness.Buckets.IsAlive); } if (witness.Entries is not null) { Assert.False(witness.Entries.IsAlive); } }
            Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);
            Assert.Equal("q\nQ\n"u8.ToArray(),((Lokad.Qpdf.Objects.PdfStream)model[3].Value).Data);
            Assert.Equal("q\nQ\n"u8.ToArray(),((Lokad.Qpdf.Objects.PdfStream)model[4].Value).Data);
            GC.KeepAlive(writing); GC.KeepAlive(model); GC.KeepAlive(sink); GC.KeepAlive(witnesses);
        }
        finally { await WriterIndexOwnershipTests.Drain(writing,sink); }
    }

    private Task ObserveAsync(ResourceGovernor governor, int rounds)
        => (_collection ?? throw new InvalidOperationException("Missing ownership batch."))
            .CollectAsync(governor, 0, rounds, false, false);

    private static IEnumerable<(string Method, IEnumerable<object[]> Rows)> OwnershipCases()
        => new (string, IEnumerable<object[]>)[]
        {
            (nameof(CompletedTasksAndRootedWrappersReleaseIndexBacking), CompletionOutcomes()),
            (nameof(MetadataRetiresBeforeUnrelatedTrailerOrXrefOutput), EarlyRetirementOutcomes()),
            (nameof(StreamLengthAndInfoKeepRenumberingOnlyThroughLastConsumer), StreamAndInfoOutcomes()),
        };

    public static IEnumerable<object[]> OwnershipBatches() => OwnershipMatrix.Batches(OwnershipCases());

    [Theory]
    [MemberData(nameof(OwnershipBatches))]
    [Trait("Layer", "Ownership")]
    public Task BatchedLifetimeObservationsPreserveEveryOriginalPhase(int batch)
        => OwnershipMatrix.RunAsync(_output, OwnershipCases(), batch,
            observation => new CompletedWriterIndexTests(_output) { _collection = observation });
}
