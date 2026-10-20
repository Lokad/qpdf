using Lokad.Qpdf.Tests.Foundation;
using System.Reflection;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CompletedObjectStreamCaptureTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void FindOwners(Task writing, List<ObjectStreamCapture> owners, int depth)
    {
        if (depth > 24) { return; }
        object? state = writing.GetType().GetField("StateMachine", Instance)?.GetValue(writing);
        if (state is null) { return; }
        foreach (FieldInfo field in state.GetType().GetFields(Instance))
        {
            if (field.FieldType == typeof(ObjectStreamCapture) && field.GetValue(state) is ObjectStreamCapture owner
                && !owners.Any(value => ReferenceEquals(value, owner))) { owners.Add(owner); }
            if (field.Name.StartsWith("<>u__", StringComparison.Ordinal) && field.GetValue(state) is object awaiter)
            {
                FindAwaitedOwners(awaiter, owners, depth + 1);
            }
        }
    }

    private static void FindAwaitedOwners(object value, List<ObjectStreamCapture> owners, int depth)
    {
        if (depth > 24) { return; }
        if (value is Task task) { FindOwners(task, owners, depth + 1); return; }
        if (value.GetType().IsValueType && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
        {
            foreach (FieldInfo field in value.GetType().GetFields(Instance))
            {
                if (field.GetValue(value) is object nested) { FindAwaitedOwners(nested, owners, depth + 1); }
            }
        }
    }

    private static (ObjectStreamCapture[] Owners, ParserMemoryScope[] Scopes) FundedStorage(Task writing, string route)
    {
        List<ObjectStreamCapture> owners = new();
        FindOwners(writing, owners, 0);
        Assert.Equal(route == "qdf" ? 2 : 1, owners.Count);
        List<ParserMemoryScope> scopes = new();
        long totalCaptured = 0L;
        foreach (ObjectStreamCapture owner in owners)
        {
            int[] ends = typeof(ObjectStreamCapture).GetField("_ends", Instance)?.GetValue(owner) as int[]
                ?? throw new InvalidOperationException("Expected member table.");
            ParserMemoryScope scope = typeof(ObjectStreamCapture).GetField("_memory", Instance)?.GetValue(owner) as ParserMemoryScope
                ?? throw new InvalidOperationException("Expected capture scope.");
            for (int index = 0; index < owner.Count; index++)
            {
                totalCaptured += owner[index].Length;
            }
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(owner[0], out ArraySegment<byte> backing));
            Assert.NotNull(backing.Array);
            Assert.InRange(scope.ReservedBytes, ends.Length * (long)sizeof(int), QpdfLimits.Default.MaxMemoryBytes);
            scopes.Add(scope);
        }
        Assert.True(totalCaptured >= 4096L * 4L);
        return (owners.ToArray(), scopes.ToArray());
    }

    [Theory]
    [InlineData("ordinary", "success")]
    [InlineData("ordinary", "failure")]
    [InlineData("ordinary", "cancellation")]
    [InlineData("qdf", "success")]
    [InlineData("qdf", "failure")]
    [InlineData("qdf", "cancellation")]
    [InlineData("linearized", "success")]
    [InlineData("linearized", "failure")]
    [InlineData("linearized", "cancellation")]
    [InlineData("counting", "success")]
    [InlineData("counting", "failure")]
    [InlineData("counting", "cancellation")]
    public async Task CompletedCapturesDisposeStorageAcrossOutputOutcomes(string route, string outcome)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        PausedContainerSink sink = new(outcome == "failure");
        Task<long> writing = ObjectStreamWriterCaptureTests.Start(route, ObjectStreamWriterCaptureTests.Model(false),
            QpdfLimits.Default, governor, sink, cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (ObjectStreamCapture[] owners, ParserMemoryScope[] scopes) = FundedStorage(writing, route);
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { sink.Resume(); }
            if (outcome == "success") { Assert.True(await writing > 0L); }
            else if (outcome == "failure")
            {
                QpdfException failure = await Assert.ThrowsAsync<QpdfException>(async () => await writing);
                Assert.Equal("Authored captured-member output failure.", failure.Message);
            }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing); }
            Assert.All(scopes, scope => Assert.Equal(0L, scope.ReservedBytes));
            foreach (ObjectStreamCapture owner in owners)
            {
                foreach (string field in new[] { "_ends", "_scratch", "_batch", "_memory" })
                { Assert.Null(typeof(ObjectStreamCapture).GetField(field, Instance)?.GetValue(owner)); }
            }
            GC.KeepAlive(writing); GC.KeepAlive(sink); GC.KeepAlive(owners); GC.KeepAlive(scopes);
        }
        finally
        {
            sink.Resume();
            try { await writing; }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class PausedContainerSink(bool fail) : IByteSink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _next;
        private bool _paused;
        internal Task Entered => _entered.Task;
        public long Length { get; private set; }
        internal void Resume() => _resume.TrySetResult();

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            if (_next && !_paused)
            {
                _paused = true;
                _entered.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
                if (fail) { throw new QpdfException("Authored captured-member output failure."); }
            }
            _next |= chunk.Span.IndexOf("/Type /ObjStm"u8) >= 0;
            cancellationToken.ThrowIfCancellationRequested();
            Length += chunk.Length;
        }
    }
}
