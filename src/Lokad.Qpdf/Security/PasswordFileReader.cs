using System.Security.Cryptography;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Security;

internal readonly record struct PasswordFileLine(bool HasLine, int Count, bool HasMoreLines);

// Reads only the first line through the host. Scratch stays admitted through
// asynchronous close and is cleared on every exit; the caller owns the prefix.
internal static class PasswordFileReader
{
    internal static async ValueTask<PasswordFileLine> ReadAsync(IQpdfHost host, QpdfPath path,
        Memory<byte> destination, bool hex, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        if (destination.Length is not (127 or QpdfPassword.RecoveryPrefixBytes))
        { throw new ArgumentException("Password prefix storage has an unsupported size.", nameof(destination)); }
        CryptographicOperations.ZeroMemory(destination.Span);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        byte[]? scratch = null;
        try
        {
            await using IQpdfInput input = await GovernedHost.OpenInputAsync(host, path, governor, token).ConfigureAwait(false);
            long length = input.Length;
            if (length > limits.MaxInputBytes) { throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), length, limits.MaxInputBytes); }
            if (length == 0) { return default; }
            int capacity = (int)Math.Min(512L, length);
            memory.Reserve(capacity + 32L);
            scratch = new byte[capacity];
            PasswordPrefixDecoder decoder = new(hex);
            long lineBytes = 0;
            for (long offset = 0; offset < length;)
            {
                governor.CheckCancellation();
                int count = (int)Math.Min(capacity, length - offset);
                await QpdfInputReader.ReadExactlyAsync(input, offset, scratch.AsMemory(0, count), token).ConfigureAwait(false);
                for (int index = 0; index < count; index++)
                {
                    if (scratch[index] == 10)
                    {
                        int decoded = decoder.Finish(destination.Span, lineBytes, true);
                        return new(true, decoded, offset + index + 1 < length);
                    }
                    decoder.Push((char)scratch[index], destination.Span);
                    lineBytes++;
                }
                offset += count;
            }
            return new(true, decoder.Finish(destination.Span, lineBytes, false), false);
        }
        catch { CryptographicOperations.ZeroMemory(destination.Span); throw; }
        finally { if (scratch is not null) { CryptographicOperations.ZeroMemory(scratch); } }
    }
}
