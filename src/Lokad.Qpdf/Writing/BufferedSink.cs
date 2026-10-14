using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns captured byte capacities; returned copies transfer to a caller scope.</summary>
internal sealed class BufferedSink : ISynchronousByteSink, IDisposable
{
    private ParserMemoryScope? _memory;
    private GovernedBufferStream? _buffer;

    private BufferedSink(ParserMemoryScope memory, GovernedBufferStream buffer)
    {
        _memory = memory;
        _buffer = buffer;
    }

    internal static BufferedSink Create(QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
        => Create(limits, governor, cancellationToken, 8192);

    internal static BufferedSink Create(QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken,
        int initialCapacity)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor);
        cancellationToken.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + 128L);
            GovernedBufferStream buffer = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes), cancellationToken, initialCapacity);
            try { return new BufferedSink(memory, buffer); }
            catch { buffer.Dispose(); throw; }
        }
        catch { memory.Dispose(); throw; }
    }

    private GovernedBufferStream Buffer => _buffer ?? throw new ObjectDisposedException(nameof(BufferedSink));

    public long Length => Buffer.Length;

    // The capture owner keeps this backing alive through synchronous assembly.
    internal ReadOnlyMemory<byte> WrittenMemory => Buffer.WrittenMemory;

    internal void CheckOwner(ResourceGovernor governor)
        => (_memory ?? throw new ObjectDisposedException(nameof(BufferedSink))).CheckOwner(governor);

    internal void Append(ReadOnlySpan<byte> chunk, CancellationToken cancellationToken)
        => Buffer.Write(chunk, cancellationToken);

    bool ISynchronousByteSink.CanAppendSynchronously => true;
    void ISynchronousByteSink.Append(ReadOnlySpan<byte> chunk, CancellationToken cancellationToken)
        => Append(chunk, cancellationToken);

    public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        => Buffer.WriteAsync(chunk, cancellationToken);

    internal byte[] ToArray(ParserMemoryScope memory, CancellationToken cancellationToken)
        => Buffer.ToArray(memory, cancellationToken);

    internal void ResetWrittenBytes() => Buffer.ResetWrittenBytes();

    internal void TruncateWrittenBytes(int length) => Buffer.TruncateWrittenBytes(length);

    public void Dispose()
    {
        GovernedBufferStream? buffer = _buffer;
        ParserMemoryScope? memory = _memory;
        _buffer = null;
        _memory = null;
        buffer?.Dispose();
        memory?.Dispose();
    }
}
