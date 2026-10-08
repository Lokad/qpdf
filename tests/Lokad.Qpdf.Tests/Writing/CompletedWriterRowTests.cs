using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class CompletedWriterRowTests
{
    private const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private sealed record Witness(object Owner,ParserMemoryScope Scope,WeakReference Storage,bool Graph);

    private static void Find(Task task,List<object> owners,int depth)
    {
        if(depth>24){return;}
        object? state=task.GetType().GetField("StateMachine",Instance)?.GetValue(task);
        if(state is null){return;}
        foreach(FieldInfo field in state.GetType().GetFields(Instance))
        {
            if(field.GetValue(state) is object value)
            {
                bool row=field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition()==typeof(WriterOrder<>) &&
                    (field.Name.Contains("regular",StringComparison.Ordinal) || field.Name.Contains("containers",StringComparison.Ordinal));
                if((row || field.FieldType==typeof(QdfEmissionPlan)) && !owners.Any(owner=>ReferenceEquals(owner,value))){owners.Add(value);}
                if(field.Name.StartsWith("<>u__",StringComparison.Ordinal)){FindAwaited(value,owners,depth+1);}
            }
        }
    }
    private static void FindAwaited(object value,List<object> owners,int depth)
    {
        if(depth>24){return;}
        if(value is Task task){Find(task,owners,depth+1);return;}
        if(value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {foreach(FieldInfo field in value.GetType().GetFields(Instance)){if(field.GetValue(value) is object nested){FindAwaited(nested,owners,depth+1);}}}
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<Witness> Capture(Task task)
    {
        List<object> owners=new();Find(task,owners,0);Assert.NotEmpty(owners);
        List<Witness> witnesses=new();
        foreach(object owner in owners)
        {
            object rows=owner;
            if(owner is QdfEmissionPlan plan)
            {
                rows=QdfEmissionPlanTests.Field(plan,"_rows");
                PdfArray graph=Assert.IsType<PdfArray>(plan[3].Value);
                ParserMemoryScope scope=Assert.IsType<ParserMemoryScope>(QdfEmissionPlanTests.Field(plan,"_memory"));
                Assert.True(scope.ReservedBytes>=41000L);
                witnesses.Add(new Witness(plan,scope,new WeakReference(QdfEmissionPlanTests.Field(graph,"_items")),true));
            }
            ParserMemoryScope memory=Assert.IsType<ParserMemoryScope>(QdfEmissionPlanTests.Field(rows,"_memory"));
            Array items=Assert.IsAssignableFrom<Array>(QdfEmissionPlanTests.Field(rows,"_items"));
            Assert.NotEmpty(items);Assert.True(memory.ReservedBytes>=96L+items.Length*sizeof(int));
            witnesses.Add(new Witness(rows,memory,new WeakReference(items),false));
        }
        return witnesses;
    }

    private static void Detached(Witness witness)
    {
        string[] fields=witness.Graph ? new[] {"_memory","_rows","_renumber"} : new[] {"_memory","_items","_governor"};
        foreach(string name in fields){Assert.Null(witness.Owner.GetType().GetField(name,Instance)?.GetValue(witness.Owner));}
        Assert.Equal(0L,witness.Scope.ReservedBytes);
        if(!witness.Graph){Assert.False(Assert.IsType<CancellationToken>(witness.Owner.GetType().GetField("_token",Instance)?.GetValue(witness.Owner)).CanBeCanceled);}
    }

    public static IEnumerable<object[]> Outcomes()
    {
        foreach(string route in new[] {"ordinary-streamed","qdf","qdf-streamed"})
        {foreach(bool retired in new[] {false,true})
         {foreach(string outcome in new[] {"success","failure","cancellation"}){yield return [route,retired,outcome];}}}
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task RootedTasksAndOwnersDetachRowsAndGraphBeforeUnrelatedXrefAwait(string route,bool retired,string outcome)
    {
        List<NumberedObject> model=WriterIndexOwnershipTests.Model(513);
        PdfArray original=new(Enumerable.Repeat<QpdfObject>(new PdfReference(3,0),1024).ToArray());
        if(route=="qdf"){model[3]=new NumberedObject(4,original);}
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor);prior.Reserve(97L);
        using CancellationTokenSource cancellation=new();PausedSink sink=new(retired,outcome=="failure");
        Task writing=WriterIndexOwnershipTests.Start(route,model,QpdfLimits.Default,governor,sink,cancellation.Token);
        await sink.First.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            List<Witness> witnesses=Capture(writing);
            Assert.Equal(route=="ordinary-streamed" ? 1 : 2,witnesses.Count);
            QdfEmissionPlanTests.Collected();Assert.All(witnesses,witness=>Assert.True(witness.Storage.IsAlive));
            if(retired)
            {
                sink.ResumeFirst();await sink.Second.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.All(witnesses,Detached);QdfEmissionPlanTests.Collected();Assert.All(witnesses,witness=>Assert.False(witness.Storage.IsAlive));
            }
            if(outcome=="cancellation"){cancellation.Cancel();}else{sink.ResumeAll();}
            await WriterIndexOwnershipTests.Finish(writing,outcome);
            Assert.All(witnesses,Detached);QdfEmissionPlanTests.Collected();Assert.All(witnesses,witness=>Assert.False(witness.Storage.IsAlive));
            Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes);Assert.Equal(97L,prior.ReservedBytes);Assert.Equal(513,model.Count);
            if(route=="qdf"){Assert.Same(original,model[3].Value);Assert.Equal(3,Assert.IsType<PdfReference>(original[1023]).ObjectNumber);}
            GC.KeepAlive(writing);GC.KeepAlive(witnesses);GC.KeepAlive(sink);GC.KeepAlive(model);GC.KeepAlive(original);
        }
        finally
        {
            sink.ResumeAll();try{await writing.WaitAsync(TimeSpan.FromSeconds(10));}catch(QpdfException){}catch(OperationCanceledException){}
        }
    }

    private sealed class PausedSink(bool second,bool fail):IByteSink
    {
        private readonly TaskCompletionSource _first=new(TaskCreationOptions.RunContinuationsAsynchronously),_second=new(TaskCreationOptions.RunContinuationsAsynchronously),
            _resumeFirst=new(TaskCreationOptions.RunContinuationsAsynchronously),_resumeSecond=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _pausedFirst,_pausedSecond;
        internal Task First=>_first.Task;internal Task Second=>_second.Task;
        internal void ResumeFirst()=>_resumeFirst.TrySetResult();internal void ResumeAll(){_resumeFirst.TrySetResult();_resumeSecond.TrySetResult();}
        public long Length{get;private set;}
        public async ValueTask AppendAsync(ReadOnlyMemory<byte> data,CancellationToken token)
        {
            if(!_pausedFirst && data.Span.SequenceEqual("1 0 obj\n"u8))
            {
                _pausedFirst=true;_first.TrySetResult();await _resumeFirst.Task.WaitAsync(token);
                if(!second && fail){throw new QpdfException("Authored row output failure.");}
            }
            if(second && !_pausedSecond && (data.Span.StartsWith("xref\n0 "u8) || data.Span.IndexOf("/Type /XRef"u8)>=0))
            {
                _pausedSecond=true;_second.TrySetResult();await _resumeSecond.Task.WaitAsync(token);
                if(fail){throw new QpdfException("Authored row output failure.");}
            }
            token.ThrowIfCancellationRequested();Length+=data.Length;
        }
    }
}
