using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class CompletedWriterOrderTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void FindOwners(Task writing, List<object> owners, int depth)
    {
        if (depth > 24) { return; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(WriterOrder<>)
                && field.GetValue(state) is object owner && !owners.Any(value => ReferenceEquals(value, owner))) { owners.Add(owner); }
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            { FindAwaitedOwners(awaiter, owners, depth + 1); }
        }
    }

    private static void FindAwaitedOwners(object value, List<object> owners, int depth)
    {
        if (depth > 24) { return; }
        if (value is Task task) { FindOwners(task, owners, depth + 1); return; }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            { if (field.GetValue(value) is object nested) { FindAwaitedOwners(nested, owners, depth + 1); } }
        }
    }

    private sealed record Witness(object Owner, ParserMemoryScope Scope, WeakReference Backing);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Witness Funded(Task writing, int count, long itemBytes)
    {
        List<object> owners = new(); FindOwners(writing, owners, 0);
        object owner = Assert.Single(owners, value => value.GetType().GetField("_items", Instance)?.GetValue(value) is Array);
        ParserMemoryScope scope = owner.GetType().GetField("_memory", Instance)?.GetValue(owner) as ParserMemoryScope
            ?? throw new InvalidOperationException("Expected ordering scope.");
        Array items = owner.GetType().GetField("_items", Instance)?.GetValue(owner) as Array
            ?? throw new InvalidOperationException("Expected ordering backing.");
        Assert.Equal(count, items.Length); Assert.Equal(96L + count * itemBytes, scope.ReservedBytes);
        return new Witness(owner, scope, new WeakReference(items));
    }

    private static void Detached(object owner)
    {
        Assert.Null(owner.GetType().GetField("_items", Instance)?.GetValue(owner));
        Assert.Null(owner.GetType().GetField("_memory", Instance)?.GetValue(owner));
    }

    private static void Collect()
    { for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task DictionaryOrderBackingDiesWithCompletedTasksAndBorrowedKeys(string outcome)
    {
        PdfDictionary source = WriterOrderOwnershipTests.Names(6, 0);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        WriterOrderOwnershipTests.PausedSink sink = new("dictionary-first", outcome == "failure");
        Task writing = WriterOrderOwnershipTests.Start(source, QpdfLimits.Default, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Witness witness = Funded(writing, 6, IntPtr.Size);
            Collect(); Assert.True(witness.Backing.IsAlive);
            if (outcome == "cancellation") { cancellation.Cancel(); } else { sink.Resume(); }
            await Finish(writing, outcome);
            Assert.Equal(0L, witness.Scope.ReservedBytes); Detached(witness.Owner);
            Collect(); Assert.False(witness.Backing.IsAlive); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(6, source.Count);
            GC.KeepAlive(writing); GC.KeepAlive(sink); GC.KeepAlive(source); GC.KeepAlive(witness);
        }
        finally { await Drain(writing, sink); }
    }

    public static IEnumerable<object[]> LinearizedOutcomes()
    {
        foreach (string route in new[] { "classic", "fallback" })
        {
            foreach (bool firstPass in new[] { false, true })
            {
                foreach (string half in new[] { "first-half", "second-half" })
                {
                    foreach (string outcome in new[] { "success", "failure", "cancellation" }) { yield return [route, firstPass, half, outcome]; }
                }
            }
        }
    }

    private static List<NumberedObject> Model()
    {
        List<NumberedObject> source = LinearizedStreamedPartsTests.OnePageModel();
        for (int index = 0; index < 512; index++) { source.Add(new NumberedObject(index + 6, PdfNull.Instance)); }
        return source;
    }

    private static Task<long> StartLinearized(string route, List<NumberedObject> source, ResourceGovernor governor,
        IByteSink sink, IByteSink? firstPass, CancellationToken token)
    {
        LinearizedWriter writer = new(QpdfLimits.Default, governor); byte[] id = new byte[16];
        return route == "classic"
            ? writer.WriteLinearizedAsync(new PdfVersion(1, 5), source, null, id, id, false, false, null, sink, firstPass, token).AsTask()
            : writer.WriteLinearizedStreamedAsync(new PdfVersion(1, 5), source, Array.Empty<IReadOnlyList<int>>(), null,
                id, id, false, false, null, sink, firstPass, token).AsTask();
    }

    [Theory]
    [MemberData(nameof(LinearizedOutcomes))]
    public async Task BothClassicHalvesStayFundedAndDetachAcrossBothPasses(string route, bool firstPass, string half, string outcome)
    {
        List<NumberedObject> source = Model();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        WriterOrderOwnershipTests.PausedSink paused = new(half, outcome == "failure"); CountingSink other = new();
        Task<long> writing = StartLinearized(route, source, governor, firstPass ? other : paused, firstPass ? paused : null, cancellation.Token);
        await paused.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            int count = half == "first-half" ? 6 : 513;
            Witness witness = Funded(writing, count, sizeof(int));
            Assert.True(governor.Snapshot().LiveMemoryBytes >= 160L + count * sizeof(int));
            Collect(); Assert.True(witness.Backing.IsAlive);
            if (outcome == "cancellation") { cancellation.Cancel(); } else { paused.Resume(); }
            await Finish(writing, outcome);
            Assert.Equal(0L, witness.Scope.ReservedBytes); Detached(witness.Owner);
            Collect(); Assert.False(witness.Backing.IsAlive);
            Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
            if (firstPass && outcome != "success") { Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); }
            Assert.Equal(517, source.Count); Assert.Same(PdfNull.Instance, source[^1].Value);
            GC.KeepAlive(writing); GC.KeepAlive(paused); GC.KeepAlive(source); GC.KeepAlive(witness);
        }
        finally { await Drain(writing, paused); }
    }

    [Theory]
    [InlineData(false, "after-first-half")]
    [InlineData(true, "after-first-half")]
    [InlineData(false, "after-second-half")]
    [InlineData(true, "after-second-half")]
    public async Task ClassicOrderRetiresBeforeItsNextUnrelatedAwait(bool firstPass, string point)
    {
        List<NumberedObject> source = Model(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WriterOrderOwnershipTests.PausedSink paused = new(point, false); CountingSink other = new();
        Task<long> writing = StartLinearized("classic", source, governor, firstPass ? other : paused, firstPass ? paused : null, CancellationToken.None);
        await paused.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            List<object> owners = new(); FindOwners(writing, owners, 0);
            Assert.Equal(point == "after-first-half" ? 1 : 2, owners.Count); Assert.All(owners, Detached);
            paused.Resume(); await writing; Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
            GC.KeepAlive(owners); GC.KeepAlive(writing); GC.KeepAlive(source);
        }
        finally { await Drain(writing, paused); }
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(false, "failure")]
    [InlineData(false, "cancellation")]
    [InlineData(true, "success")]
    [InlineData(true, "failure")]
    [InlineData(true, "cancellation")]
    public async Task QdfModelOrderingIsDetachedBeforeFirstOutput(bool streamed, string outcome)
    {
        List<NumberedObject> source = LinearizedStreamedPartsTests.OnePageModel();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        WriterOrderOwnershipTests.PausedSink paused = new("qdf-version", outcome == "failure");
        PdfSerializer writer = new(QpdfLimits.Default, governor); byte[] id = new byte[16];
        IReadOnlyList<IReadOnlyList<int>>? groups = streamed ? new IReadOnlyList<int>[] { new[] { 5 } } : null;
        Task<long> writing = writer.SerializeQdfAsync(new PdfVersion(1, 5), source, groups, null, null, id, id,
            new QdfOptions(true, false), false, paused, cancellation.Token).AsTask();
        await paused.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            List<object> owners = new(); FindOwners(writing, owners, 0);
            Assert.Equal(streamed ? 3 : 1, owners.Count);
            object ordering = Assert.Single(owners, value => value.GetType() == (streamed ? typeof(WriterOrder<int>) : typeof(WriterOrder<NumberedObject>)));
            Detached(ordering);
            foreach (object rows in owners.Where(value => !ReferenceEquals(value, ordering)))
            { Assert.NotNull(rows.GetType().GetField("_memory", Instance)?.GetValue(rows)); }
            if (outcome == "cancellation") { cancellation.Cancel(); } else { paused.Resume(); }
            await Finish(writing, outcome); Assert.All(owners, Detached); Assert.Equal(5, source.Count);
            GC.KeepAlive(owners); GC.KeepAlive(writing); GC.KeepAlive(writer); GC.KeepAlive(source);
        }
        finally { await Drain(writing, paused); }
    }

    private static async Task Finish(Task writing, string outcome)
    {
        if (outcome == "success") { await writing; }
        else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(() => writing); }
        else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing); }
    }

    private static async Task Drain(Task writing, WriterOrderOwnershipTests.PausedSink sink)
    { sink.Resume(); try { await writing; } catch (QpdfException) { } catch (OperationCanceledException) { } }
}
