using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class CompletedObjectStreamPayloadTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static ObjectStreamPayload? FindOwner(Task writing, int depth)
    {
        if (depth > 24) { return null; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return null; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType == typeof(ObjectStreamPayload) && field.GetValue(state) is ObjectStreamPayload owner) { return owner; }
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            {
                ObjectStreamPayload? found = FindAwaitedOwner(awaiter, depth + 1);
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    private static ObjectStreamPayload? FindAwaitedOwner(object value, int depth)
    {
        if (depth > 24) { return null; }
        if (value is Task task) { return FindOwner(task, depth + 1); }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            {
                if (field.GetValue(value) is object nested)
                {
                    ObjectStreamPayload? found = FindAwaitedOwner(nested, depth + 1);
                    if (found is not null) { return found; }
                }
            }
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ObjectStreamPayload Owner, ParserMemoryScope Scope, WeakReference[] Arrays) Funded(Task writing, bool compress)
    {
        ObjectStreamPayload owner = FindOwner(writing, 0) ?? throw new InvalidOperationException("Expected awaited payload owner.");
        ParserMemoryScope scope = typeof(ObjectStreamPayload).GetField("_memory", Instance)?.GetValue(owner) as ParserMemoryScope
            ?? throw new InvalidOperationException("Expected payload scope.");
        byte[] raw = typeof(ObjectStreamPayload).GetField("_raw", Instance)?.GetValue(owner) as byte[]
            ?? throw new InvalidOperationException("Expected raw backing.");
        byte[] stored = typeof(ObjectStreamPayload).GetField("_stored", Instance)?.GetValue(owner) as byte[]
            ?? throw new InvalidOperationException("Expected stored backing.");
        Assert.True(raw.Length > 16384);
        Assert.Equal(compress, owner.Compressed);
        Assert.Equal(64L + 32L + raw.Length + (compress ? 32L + stored.Length : 0L), scope.ReservedBytes);
        if (compress) { Assert.NotSame(raw, stored); }
        else { Assert.Same(raw, stored); }
        return (owner, scope, new[] { new WeakReference(raw), new WeakReference(stored) });
    }

    [Theory]
    [InlineData("ordinary", false, "success")]
    [InlineData("ordinary", false, "failure")]
    [InlineData("ordinary", false, "cancellation")]
    [InlineData("ordinary", true, "success")]
    [InlineData("ordinary", true, "failure")]
    [InlineData("ordinary", true, "cancellation")]
    [InlineData("qdf", false, "success")]
    [InlineData("qdf", false, "failure")]
    [InlineData("qdf", false, "cancellation")]
    [InlineData("linearized", false, "success")]
    [InlineData("linearized", false, "failure")]
    [InlineData("linearized", false, "cancellation")]
    [InlineData("linearized", true, "success")]
    [InlineData("linearized", true, "failure")]
    [InlineData("linearized", true, "cancellation")]
    [InlineData("counting", false, "success")]
    [InlineData("counting", false, "failure")]
    [InlineData("counting", false, "cancellation")]
    [InlineData("counting", true, "success")]
    [InlineData("counting", true, "failure")]
    [InlineData("counting", true, "cancellation")]
    public async Task RawAndCompressedBuffersDieWithRetainedOwnersAndTasks(string route, bool compress, string outcome)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        ObjectStreamPayloadOwnershipTests.PausedSink sink = new(outcome == "failure");
        Task<long> writing = ObjectStreamPayloadOwnershipTests.Start(route, compress, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (ObjectStreamPayload owner, ParserMemoryScope scope, WeakReference[] arrays) = Funded(writing, compress);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.All(arrays, value => Assert.True(value.IsAlive));
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { Assert.True(await writing > 0L); }
            else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(async () => await writing); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.Equal(0L, scope.ReservedBytes);
            Assert.Throws<ObjectDisposedException>(() => owner.Data);
            for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
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
}
