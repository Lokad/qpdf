using Lokad.Qpdf.Tests.Foundation;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CompletedWriterFormattingTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static WriterFormatting? FindOwner(Task writing, int depth)
    {
        if (depth > 24) { return null; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return null; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType == typeof(WriterFormatting) && field.GetValue(state) is WriterFormatting owner) { return owner; }
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            {
                WriterFormatting? found = FindAwaitedOwner(awaiter, depth + 1);
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    private static WriterFormatting? FindAwaitedOwner(object value, int depth)
    {
        if (depth > 24) { return null; }
        if (value is Task task) { return FindOwner(task, depth + 1); }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            {
                if (field.GetValue(value) is object nested)
                {
                    WriterFormatting? found = FindAwaitedOwner(nested, depth + 1);
                    if (found is not null) { return found; }
                }
            }
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WriterFormatting Owner, ParserMemoryScope Scope, WeakReference[] Arrays) Funded(Task writing, string route)
    {
        WriterFormatting owner = FindOwner(writing, 0) ?? throw new InvalidOperationException("Expected awaited encoding owner.");
        ParserMemoryScope scope = typeof(WriterFormatting).GetField("_memory", Instance)?.GetValue(owner) as ParserMemoryScope
            ?? throw new InvalidOperationException("Expected encoding scope.");
        byte[] bytes = typeof(WriterFormatting).GetField("_bytes", Instance)?.GetValue(owner) as byte[]
            ?? throw new InvalidOperationException("Expected encoding backing.");
        Assert.Equal(WriterFormattingOwnershipTests.Spelling(route).Length, bytes.Length);
        Assert.Equal(64L + 32L + bytes.Length, scope.ReservedBytes);
        return (owner, scope, new[] { new WeakReference(bytes) });
    }

    [Theory]
    [MemberData(nameof(WriterFormattingOwnershipTests.Routes), MemberType = typeof(WriterFormattingOwnershipTests))]
    public async Task EncodedBackingDiesWithRetainedOwnersAndTasks(string route, string outcome)
    {
        ResourceGovernor governor = new(WriterFormattingOwnershipTests.Limits, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        WriterFormattingOwnershipTests.PausedSink sink = new(route, outcome == "failure");
        Lokad.Qpdf.Objects.QpdfObject source = WriterFormattingOwnershipTests.Source(route);
        Task writing = WriterFormattingOwnershipTests.Start(route, source, WriterFormattingOwnershipTests.Limits, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (WriterFormatting owner, ParserMemoryScope scope, WeakReference[] arrays) = Funded(writing, route);
            Collect();
            Assert.All(arrays, value => Assert.True(value.IsAlive));
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { await writing; }
            else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(async () => await writing); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.Equal(0L, scope.ReservedBytes);
            Assert.Throws<ObjectDisposedException>(() => owner.Data);
            Collect();
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

    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
