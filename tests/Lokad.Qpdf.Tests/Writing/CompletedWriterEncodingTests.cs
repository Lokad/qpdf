using Lokad.Qpdf.Tests.Foundation;
using System.Reflection;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CompletedWriterEncodingTests
{
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

    private static (WriterEncoding Owner, ParserMemoryScope Scope) Funded(Task writing, string route)
    {
        WriterEncoding owner = FindOwner(writing, 0) ?? throw new InvalidOperationException("Expected awaited encoding owner.");
        ParserMemoryScope scope = typeof(WriterEncoding).GetField("_memory", Instance)?.GetValue(owner) as ParserMemoryScope
            ?? throw new InvalidOperationException("Expected encoding scope.");
        byte[] bytes = typeof(WriterEncoding).GetField("_bytes", Instance)?.GetValue(owner) as byte[]
            ?? throw new InvalidOperationException("Expected encoding backing.");
        Assert.Equal(WriterEncodingOwnershipTests.Length(route), bytes.Length);
        Assert.Equal(64L + 32L + bytes.Length, scope.ReservedBytes);
        return (owner, scope);
    }

    [Theory]
    [MemberData(nameof(WriterEncodingOwnershipTests.Routes), MemberType = typeof(WriterEncodingOwnershipTests))]
    public async Task CompletedEncodingDisposesStorageOnEveryOutcome(string route, string outcome)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        WriterEncodingOwnershipTests.PausedSink sink = new(outcome == "failure");
        object source = WriterEncodingOwnershipTests.Source(route);
        Task writing = WriterEncodingOwnershipTests.Start(route, source, QpdfLimits.Default, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (WriterEncoding owner, ParserMemoryScope scope) = Funded(writing, route);
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { await writing; }
            else if (outcome == "failure") { await Assert.ThrowsAsync<QpdfException>(async () => await writing); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.Equal(0L, scope.ReservedBytes);
            Assert.Throws<ObjectDisposedException>(() => owner.Data);
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
}
