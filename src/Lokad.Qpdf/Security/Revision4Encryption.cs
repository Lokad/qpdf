using System.Buffers.Binary;
using System.Security.Cryptography;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using static Lokad.Qpdf.Security.EncryptionDictionary;

namespace Lokad.Qpdf.Security;

// ISO 32000-1 algorithms 2, 3, 5, 6 and 7 for the fixed 128-bit V4 key.
// Password encoding is separate; this layer consumes at most 32 supplied bytes.
internal static class Revision4Encryption
{
    private static ReadOnlySpan<byte> Padding =>
    [
        0x28, 0xbf, 0x4e, 0x5e, 0x4e, 0x75, 0x8a, 0x41, 0x64, 0x00, 0x4e, 0x56, 0xff, 0xfa, 0x01, 0x08,
        0x2e, 0x2e, 0x00, 0xb6, 0xd0, 0x68, 0x3e, 0x80, 0x2f, 0x0c, 0xa9, 0xfe, 0x64, 0x53, 0x69, 0x7a,
    ];

    internal static PasswordAuthentication Authenticate(PdfDictionary encryption, ReadOnlySpan<byte> identifier,
        ReadOnlySpan<byte> password, Span<byte> fileKey, ResourceGovernor governor)
    {
        if (fileKey.Length != 16) { throw new ArgumentException("A 128-bit key destination is required.", nameof(fileKey)); }
        fileKey.Clear(); governor.CheckCancellation();
        ReadOnlySpan<byte> owner = ReadBytes(encryption, "O"u8, 32, governor);
        ReadOnlySpan<byte> user = ReadBytes(encryption, "U"u8, 32, governor);
        int permissions = ReadInteger(encryption, "P"u8, governor);
        bool metadata = ReadMetadata(encryption, governor);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(4096L); // System MD5 state and fixed credential scratch.
        Span<byte> padded = stackalloc byte[32];
        Span<byte> ownerKey = stackalloc byte[16];
        Span<byte> recovered = stackalloc byte[32];
        Span<byte> iterationKey = stackalloc byte[16];
        bool accepted = false;
        try
        {
            Pad(password, padded);
            _ = MD5.HashData(padded, ownerKey); Iterate(ownerKey, governor);
            owner.CopyTo(recovered);
            for (int round = 19; round >= 0; round--)
            {
                for (int index = 0; index < 16; index++) { iterationKey[index] = (byte)(ownerKey[index] ^ round); }
                PdfRc4Cipher.Transform(iterationKey, recovered, governor);
            }
            if (AuthenticateUser(recovered, owner, user, identifier, permissions, metadata, fileKey, governor))
            { accepted = true; return PasswordAuthentication.Owner; }
            if (AuthenticateUser(padded, owner, user, identifier, permissions, metadata, fileKey, governor))
            { accepted = true; return PasswordAuthentication.User; }
            return PasswordAuthentication.None;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded); CryptographicOperations.ZeroMemory(ownerKey);
            CryptographicOperations.ZeroMemory(recovered); CryptographicOperations.ZeroMemory(iterationKey);
            if (!accepted) { CryptographicOperations.ZeroMemory(fileKey); }
        }
    }

    internal static void DeriveObjectKey(ReadOnlySpan<byte> fileKey, int number, int generation,
        Span<byte> objectKey, bool aes, ResourceGovernor governor)
    {
        if (fileKey.Length != 16 || objectKey.Length != 16) { throw new ArgumentException("Revision 4 object keys require 16 bytes."); }
        objectKey.Clear(); governor.CheckCancellation();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, 65535);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(4096L);
        Span<byte> material = stackalloc byte[25];
        bool accepted = false;
        try
        {
            fileKey.CopyTo(material);
            material[16] = (byte)number; material[17] = (byte)(number >> 8); material[18] = (byte)(number >> 16);
            material[19] = (byte)generation; material[20] = (byte)(generation >> 8);
            "sAlT"u8.CopyTo(material[21..]);
            _ = MD5.HashData(material[..(aes ? 25 : 21)], objectKey);
            governor.CheckCancellation(); accepted = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            if (!accepted) { CryptographicOperations.ZeroMemory(objectKey); }
        }
    }

    private static bool AuthenticateUser(ReadOnlySpan<byte> padded, ReadOnlySpan<byte> owner, ReadOnlySpan<byte> user,
        ReadOnlySpan<byte> identifier, int permissions, bool metadata, Span<byte> fileKey, ResourceGovernor governor)
    {
        Span<byte> flags = stackalloc byte[4];
        Span<byte> check = stackalloc byte[16];
        Span<byte> iterationKey = stackalloc byte[16];
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            hash.AppendData(padded); hash.AppendData(owner);
            BinaryPrimitives.WriteInt32LittleEndian(flags, permissions); hash.AppendData(flags);
            AppendIdentifier(hash, identifier, governor);
            if (!metadata) { flags.Fill(0xff); hash.AppendData(flags); }
            Finish(hash, fileKey); Iterate(fileKey, governor);
            hash.AppendData(Padding); AppendIdentifier(hash, identifier, governor); Finish(hash, check);
            for (int round = 0; round < 20; round++)
            {
                for (int index = 0; index < 16; index++) { iterationKey[index] = (byte)(fileKey[index] ^ round); }
                PdfRc4Cipher.Transform(iterationKey, check, governor);
            }
            governor.CheckCancellation();
            return CryptographicOperations.FixedTimeEquals(check, user[..16]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(flags); CryptographicOperations.ZeroMemory(check);
            CryptographicOperations.ZeroMemory(iterationKey);
        }
    }

    private static void Pad(ReadOnlySpan<byte> password, Span<byte> padded)
    {
        int count = Math.Min(password.Length, 32);
        password[..count].CopyTo(padded); Padding[..(32 - count)].CopyTo(padded[count..]);
    }

    private static void Iterate(Span<byte> digest, ResourceGovernor governor)
    {
        Span<byte> next = stackalloc byte[16];
        try
        {
            for (int round = 0; round < 50; round++)
            { governor.CheckCancellation(); _ = MD5.HashData(digest, next); next.CopyTo(digest); }
        }
        finally { CryptographicOperations.ZeroMemory(next); }
    }

    private static void AppendIdentifier(IncrementalHash hash, ReadOnlySpan<byte> identifier, ResourceGovernor governor)
    {
        while (!identifier.IsEmpty)
        {
            governor.CheckCancellation(); int count = Math.Min(identifier.Length, 64 * 1024);
            hash.AppendData(identifier[..count]); identifier = identifier[count..];
        }
    }

    private static void Finish(IncrementalHash hash, Span<byte> output)
    {
        if (!hash.TryGetHashAndReset(output, out int written) || written != 16)
        { throw new InvalidOperationException("MD5 did not produce a 128-bit digest."); }
    }
}
