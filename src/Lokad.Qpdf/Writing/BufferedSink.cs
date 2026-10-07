using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns captured byte capacities; returned copies transfer to a caller scope.</summary>
internal sealed class BufferedSink : IByteSink, IDisposable
{
    private ParserMemoryScope? _memory;
    private GovernedBufferStream? _buffer;

    private BufferedSink(ParserMemoryScope memory, GovernedBufferStream buffer)
    {
        _memory = memory;
        _buffer = buffer;
    }

    internal static BufferedSink Create(QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor);
        cancellationToken.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + 128L);
            GovernedBufferStream buffer = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes), cancellationToken);
            try { return new BufferedSink(memory, buffer); }
            catch { buffer.Dispose(); throw; }
        }
        catch { memory.Dispose(); throw; }
    }

    private GovernedBufferStream Buffer => _buffer ?? throw new ObjectDisposedException(nameof(BufferedSink));

    public long Length => Buffer.Length;

    public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        => Buffer.WriteAsync(chunk, cancellationToken);

    internal byte[] ToArray(ParserMemoryScope memory, CancellationToken cancellationToken)
        => Buffer.ToArray(memory, cancellationToken);

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
