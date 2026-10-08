using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class CompletedWriterXrefTests
{
    private const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static WriterXref? Find(Task task,int depth)
    {
        if (depth>24) {return null;}
        object? state=task.GetType().GetField("StateMachine",Instance)?.GetValue(task);
        if (state is null) {return null;}
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType==typeof(WriterXref) && field.GetValue(state) is WriterXref owner) {return owner;}
            if (field.Name.StartsWith("<>u__",StringComparison.Ordinal) && field.GetValue(state) is object value)
            {WriterXref? nested=FindAwaited(value,depth+1); if (nested is not null) {return nested;}}
        }
        return null;
    }
    private static WriterXref? FindAwaited(object value,int depth)
    {
        if (depth>24) {return null;}
        if (value is Task task) {return Find(task,depth+1);}
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {foreach (FieldInfo field in value.GetType().GetFields(Instance))
         {if (field.GetValue(value) is object nested) {WriterXref? owner=FindAwaited(nested,depth+1); if (owner is not null) {return owner;}}}}
        return null;
    }
    private sealed record Witness(WriterXref Owner,ParserMemoryScope? Scope,WeakReference? Raw,WeakReference? Stored);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Witness Capture(Task writing)
    {
        WriterXref owner=Find(writing,0) ?? throw new InvalidOperationException("Expected writer xref owner.");
        ParserMemoryScope? memory=typeof(WriterXref).GetField("_memory",Instance)?.GetValue(owner) as ParserMemoryScope;
        object? raw=typeof(WriterXref).GetField("_raw",Instance)?.GetValue(owner);
        object? stored=typeof(WriterXref).GetField("_stored",Instance)?.GetValue(owner);
        return new Witness(owner,memory,raw is null ? null : new WeakReference(raw),stored is null ? null : new WeakReference(stored));
    }
    private static void Detached(WriterXref owner)
    {
        foreach (string name in new[] {"_memory","_governor","_raw","_stored"}) {Assert.Null(typeof(WriterXref).GetField(name,Instance)?.GetValue(owner));}
        CancellationToken token=Assert.IsType<CancellationToken>(typeof(WriterXref).GetField("_token",Instance)?.GetValue(owner));
        Assert.False(token.CanBeCanceled);
    }
    private static void Collected() {for (int round=0;round<3;round++) {GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}}

    [Theory]
    [MemberData(nameof(WriterXrefOwnershipTests.Outcomes),MemberType=typeof(WriterXrefOwnershipTests))]
    public async Task RootedCompletedTasksAndOwnersReleaseXrefBacking(string mode,string point,string outcome)
    {
        var model=WriterIndexOwnershipTests.Model(513);
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        using CancellationTokenSource cancellation=new(); WriterXrefOwnershipTests.PausedSink sink=new(point,outcome=="failure");
        Task<long> writing=WriterXrefOwnershipTests.Start(mode,model,QpdfLimits.Default,governor,sink,cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Witness witness=Capture(writing);
            if (point=="retired") {Detached(witness.Owner);Assert.Null(witness.Scope);}
            else
            {
                Assert.NotNull(witness.Scope); Assert.NotNull(witness.Stored);
                Assert.Equal(128L+sink.BodyLength,witness.Scope.ReservedBytes);
                if (mode=="compressed") {Assert.Null(witness.Raw);} else {Assert.NotNull(witness.Raw);}
            }
            Collected();
            if (point=="retired") {Assert.NotNull(sink.Buffer);Assert.False(sink.Buffer.IsAlive);}
            else {Assert.NotNull(witness.Stored);Assert.True(witness.Stored.IsAlive);}
            if (outcome=="cancellation") {cancellation.Cancel();} else {sink.Resume();}
            await WriterIndexOwnershipTests.Finish(writing,outcome); Detached(witness.Owner);
            if (witness.Scope is not null) {Assert.Equal(0L,witness.Scope.ReservedBytes);}
            Collected();
            if (witness.Stored is not null) {Assert.False(witness.Stored.IsAlive);}
            if (witness.Raw is not null) {Assert.False(witness.Raw.IsAlive);}
            if (sink.Buffer is not null) {Assert.False(sink.Buffer.IsAlive);}
            Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);
            GC.KeepAlive(writing);GC.KeepAlive(witness);GC.KeepAlive(sink);GC.KeepAlive(model);
        }
        finally {await WriterXrefOwnershipTests.Drain(writing,sink);}
    }
}
