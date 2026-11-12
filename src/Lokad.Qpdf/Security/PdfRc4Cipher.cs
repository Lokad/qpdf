using System.Security.Cryptography;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Security;

// PDF compatibility only: the system libraries supply AES/MD5/SHA, but not RC4.
// The caller owns the in-place payload, including clearing it after a failure.
internal static class PdfRc4Cipher
{
    internal static byte[] Copy(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ParserMemoryScope destination, ResourceGovernor governor)
    {
        destination.CheckOwner(governor); governor.CheckCancellation();
        if (key.Length is < 5 or > 16) { throw new ArgumentException("PDF RC4 keys must hold 5 to 16 bytes.", nameof(key)); }
        if (data.IsEmpty) { return []; }
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        long bytes = checked(data.Length + GovernedBufferStream.ArrayOverheadBytes);
        memory.Reserve(bytes);
        byte[] result = new byte[data.Length];
        bool accepted = false;
        try
        {
            for (int offset = 0; offset < data.Length;)
            {
                governor.CheckCancellation(); int count = Math.Min(data.Length - offset, 64 * 1024);
                data.Slice(offset, count).CopyTo(result.AsSpan(offset, count)); offset += count;
            }
            Transform(key, result, governor); memory.TransferTo(destination, bytes); accepted = true;
            return result;
        }
        finally { if (!accepted) { CryptographicOperations.ZeroMemory(result); } }
    }

    internal static void Transform(ReadOnlySpan<byte> key, Span<byte> data, ResourceGovernor governor)
    {
        governor.CheckCancellation();
        if (key.Length is < 5 or > 16) { throw new ArgumentException("PDF RC4 keys must hold 5 to 16 bytes.", nameof(key)); }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(256L);
        Span<byte> state = stackalloc byte[256];
        try
        {
            for (int index = 0; index < 256; index++) { state[index] = (byte)index; }
            int swap = 0;
            for (int index = 0; index < 256; index++)
            {
                swap = (swap + state[index] + key[index % key.Length]) & 255;
                (state[index], state[swap]) = (state[swap], state[index]);
            }
            int position = 0; swap = 0;
            for (int offset = 0; offset < data.Length; offset++)
            {
                if ((offset & 4095) == 0) { governor.CheckCancellation(); }
                position = (position + 1) & 255;
                swap = (swap + state[position]) & 255;
                (state[position], state[swap]) = (state[swap], state[position]);
                data[offset] ^= state[(state[position] + state[swap]) & 255];
            }
            governor.CheckCancellation();
        }
        finally { CryptographicOperations.ZeroMemory(state); }
    }
}
