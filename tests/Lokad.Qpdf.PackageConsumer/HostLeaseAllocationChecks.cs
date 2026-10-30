using System.Runtime.CompilerServices;
using Lokad.Qpdf;

// Runs only in a capped child process, so real allocation failure cannot affect
// the ordinary test runner. Each mode also starts with cold engine metadata.
internal static class HostLeaseAllocationChecks
{
    internal static async Task<int> RunAsync(byte[] bytes, string mode)
    {
        long allowance = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (allowance > 64L * 1024L * 1024L || allowance < 16L * 1024L * 1024L)
        { throw new InvalidOperationException("Expected an isolated 32 MiB managed heap."); }
        bool output = mode.EndsWith("output", StringComparison.Ordinal);
        bool raw = mode.StartsWith("raw-", StringComparison.Ordinal);
        bool pressure = !mode.StartsWith("quiet-", StringComparison.Ordinal);
        if (mode is not ("quiet-input" or "raw-input" or "engine-input" or "quiet-output" or "raw-output" or "engine-output"))
        { throw new ArgumentException("Unknown lease allocation mode.", nameof(mode)); }
        Host host = new(bytes, output, pressure);
        bool oom = false;
        try
        {
            if (raw && output)
            { await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None); }
            else if (raw)
            { await using IQpdfInput input = await host.OpenInputAsync(QpdfPath.From("input"), CancellationToken.None); }
            else if (output)
            {
                QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
                    new QpdfCommandInvocation(["input", "output"]), CancellationToken.None);
                if (result.Status != QpdfExecutionStatus.Completed) { throw new InvalidOperationException("Unexpected execution failure."); }
            }
            else
            { await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None); }
        }
        catch (OutOfMemoryException) { oom = true; }
        finally { Host.ReleasePressure(); }
        int disposed = output ? host.Output.Disposes : host.Input.Disposes;
        bool opened = output ? host.OutputOpened : host.InputOpened;
        if (!opened || disposed != 1 || host.PressureReached != pressure || oom && (raw || !pressure))
        { throw new InvalidOperationException($"{mode}: opened={opened}, disposals={disposed}, pressure={host.PressureReached}, OOM={oom}."); }
        Console.WriteLine($"PASS {mode}: disposals={disposed}, pressure={host.PressureReached}, OOM={oom}");
        return 0;
    }

    private sealed class Host(byte[] bytes, bool outputPressure, bool pressure) : IQpdfHost
    {
        private sealed class Node(Node? previous)
        {
            internal readonly Node? Previous = previous;
            internal readonly long Padding = 1L;
        }
        private static Node? _pressure;
        internal readonly Input Input = new(bytes);
        internal readonly Output Output = new();
        internal bool InputOpened, OutputOpened, PressureReached;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Fill()
        {
            try { while (true) { _pressure = new Node(_pressure); } }
            catch (OutOfMemoryException) { PressureReached = true; }
        }

        internal static void ReleasePressure() { _pressure = null; }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            if (pressure && !outputPressure) { Fill(); }
            InputOpened = true;
            return ValueTask.FromResult<IQpdfInput>(Input);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            if (pressure && outputPressure) { Fill(); }
            OutputOpened = true;
            return ValueTask.FromResult<IQpdfOutputTransaction>(Output);
        }
    }

    private sealed class Input(byte[] bytes) : IQpdfInput
    {
        internal int Disposes;
        public QpdfPath Path => QpdfPath.From("input");
        public string Identity => "input";
        public long Length => bytes.Length;
        public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
        {

            token.ThrowIfCancellationRequested();
            if (offset < 0 || offset > Length) { throw new ArgumentOutOfRangeException(nameof(offset)); }
            int count = Math.Min(destination.Length, bytes.Length - (int)offset);
            bytes.AsMemory((int)offset, count).CopyTo(destination);
            return new(count);
        }
        public ValueTask DisposeAsync() { Disposes++; Host.ReleasePressure(); return ValueTask.CompletedTask; }
    }

    private sealed class Output : IQpdfOutputTransaction
    {
        internal int Disposes;
        public QpdfPath Path => QpdfPath.From("output");
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask CommitAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DiscardAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposes++; Host.ReleasePressure(); return ValueTask.CompletedTask; }
    }
}
