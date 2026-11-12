using System.Security.Cryptography;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Security;

internal static class PdfAesCipher
{
    internal static int EncryptedLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        long encoded = 16L + (length / 16L + 1L) * 16L;
        if (encoded > Array.MaxLength)
        { throw new QpdfLimitException("AES payload capacity", encoded, Array.MaxLength); }
        return (int)encoded;
    }

    // Each physical PDF payload gets a fresh IV. Chunks continue CBC using the
    // preceding ciphertext block; only the final block adds PKCS#7 padding.
    internal static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key,
        ParserMemoryScope owner, ResourceGovernor governor)
    {
        owner.CheckOwner(governor); governor.CheckCancellation();
        if (key.Length is not (16 or 32)) { throw new ArgumentException("PDF AES keys must be 128 or 256 bits.", nameof(key)); }
        int length = EncryptedLength(plain.Length);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(4096L); // System cipher state.
        long bytes = 32L + length;
        memory.Reserve(bytes);
        byte[] encoded = new byte[length];
        bool accepted = false;
        try
        {
            using Aes aes = Aes.Create(); aes.SetKey(key);
            RandomNumberGenerator.Fill(encoded.AsSpan(0, 16));
            int prefix = plain.Length & ~15;
            int offset = 0;
            while (offset < prefix)
            {
                governor.CheckCancellation();
                int count = Math.Min(64 * 1024, prefix - offset);
                _ = aes.EncryptCbc(plain.Slice(offset, count), encoded.AsSpan(offset, 16),
                    encoded.AsSpan(16 + offset, count), PaddingMode.None);
                offset += count;
            }
            governor.CheckCancellation();
            _ = aes.EncryptCbc(plain[offset..], encoded.AsSpan(offset, 16), encoded.AsSpan(16 + offset), PaddingMode.PKCS7);
            governor.CheckCancellation();
            memory.TransferTo(owner, bytes); accepted = true;
            return encoded;
        }
        finally { if (!accepted) { CryptographicOperations.ZeroMemory(encoded); } }
    }

    // PDF stream/string payloads carry a 16-byte IV, then CBC ciphertext with
    // PKCS#7 padding. Inspect the last block first to allocate the exact result;
    // preceding blocks use bounded system calls with cancellation between them.
    internal static byte[] Decrypt(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> key,
        ParserMemoryScope owner, ResourceGovernor governor)
    {
        owner.CheckOwner(governor); governor.CheckCancellation();
        if (key.Length is not (16 or 32)) { throw new ArgumentException("PDF AES keys must be 128 or 256 bits.", nameof(key)); }
        if (encoded.Length < 32 || (encoded.Length & 15) != 0)
        { throw new QpdfParseException("Invalid AES-encrypted PDF payload length.", null, null); }
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(4096L); // System cipher and fixed block scratch.
        Span<byte> last = stackalloc byte[16];
        byte[]? plain = null;
        bool accepted = false;
        try
        {
            using Aes aes = Aes.Create(); aes.SetKey(key);
            int tail = aes.DecryptCbc(encoded[^16..], encoded.Slice(encoded.Length - 32, 16), last, PaddingMode.PKCS7);
            int prefix = encoded.Length - 32;
            int length = checked(prefix + tail);
            long bytes = length == 0 ? 0L : checked(32L + length);
            memory.Reserve(bytes);
            plain = length == 0 ? [] : new byte[length];
            int offset = 0;
            while (offset < prefix)
            {
                governor.CheckCancellation();
                int count = Math.Min(64 * 1024, prefix - offset);
                _ = aes.DecryptCbc(encoded.Slice(16 + offset, count), encoded.Slice(offset, 16), plain.AsSpan(offset, count), PaddingMode.None);
                offset += count;
            }
            last[..tail].CopyTo(plain.AsSpan(prefix));
            governor.CheckCancellation();
            memory.TransferTo(owner, bytes);
            accepted = true;
            return plain;
        }
        catch (CryptographicException)
        { throw new QpdfParseException("Invalid AES-encrypted PDF payload padding.", null, null); }
        finally
        {
            CryptographicOperations.ZeroMemory(last);
            if (!accepted && plain is not null) { CryptographicOperations.ZeroMemory(plain); }
        }
    }
}
