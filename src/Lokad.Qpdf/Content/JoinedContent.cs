using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Content;

/// <summary>Owns the exact joined content buffer through its analysis.</summary>
internal sealed class JoinedContent : IDisposable
{
    private ParserMemoryScope? _memory;
    private byte[]? _bytes;

    private JoinedContent(ParserMemoryScope memory, byte[] bytes)
    { _memory = memory; _bytes = bytes; }

    internal ReadOnlyMemory<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(JoinedContent));

    internal static JoinedContent Create(IReadOnlyList<byte[]> parts, ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(governor);
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(32L);
            long length = 0L;
            bool newline = false;
            for (int index = 0; index < parts.Count; index++)
            {
                Visit(governor, token);
                byte[] part = parts[index];
                length = checked(length + part.Length + (newline ? 1L : 0L));
                newline = part.Length == 0 || part[^1] != (byte)'\n';
            }
            if (length > Array.MaxLength)
            { throw new QpdfLimitException("Joined content bytes", length, Array.MaxLength); }
            byte[] bytes = Array.Empty<byte>();
            if (length != 0L)
            {
                memory.Reserve(checked(length + GovernedBufferStream.ArrayOverheadBytes));
                governor.ChargeWork(length);
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                bytes = new byte[(int)length];
            }
            int position = 0;
            newline = false;
            for (int index = 0; index < parts.Count; index++)
            {
                Visit(governor, token);
                byte[] part = parts[index];
                if (newline)
                {
                    governor.ChargeWork(1L);
                    bytes[position++] = (byte)'\n';
                }
                GovernedBufferStream.CopyBytes(part, bytes.AsSpan(position, part.Length), governor, token, CancellationToken.None);
                position += part.Length;
                newline = part.Length == 0 || part[^1] != (byte)'\n';
            }
            return new JoinedContent(memory, bytes);
        }
        catch { memory.Dispose(); throw; }
    }

    private static void Visit(ResourceGovernor governor, CancellationToken token)
    { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L); }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _bytes = null; _memory = null;
        memory?.Dispose();
    }
}
