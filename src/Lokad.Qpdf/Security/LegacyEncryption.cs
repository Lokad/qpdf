using System.Buffers.Binary;
using System.Security.Cryptography;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using static Lokad.Qpdf.Security.EncryptionDictionary;

namespace Lokad.Qpdf.Security;

// ISO 32000-1 algorithms 1 through 7 for revisions 2, 3 and 4.
// Password encoding is separate; this layer consumes at most 32 supplied bytes.
internal static class LegacyEncryption
{
    private static ReadOnlySpan<byte> Padding =>
    [
        0x28, 0xbf, 0x4e, 0x5e, 0x4e, 0x75, 0x8a, 0x41, 0x64, 0x00, 0x4e, 0x56, 0xff, 0xfa, 0x01, 0x08,
        0x2e, 0x2e, 0x00, 0xb6, 0xd0, 0x68, 0x3e, 0x80, 0x2f, 0x0c, 0xa9, 0xfe, 0x64, 0x53, 0x69, 0x7a,
    ];

    internal static PasswordAuthentication Authenticate(PdfDictionary encryption, ReadOnlySpan<byte> identifier,
        ReadOnlySpan<byte> password, Span<byte> fileKey, ResourceGovernor governor)
    {
        fileKey.Clear(); governor.CheckCancellation();
        int revision = ReadInteger(encryption, "R"u8, governor);
        int length = fileKey.Length;
        if (revision is not (2 or 3 or 4) || length is < 5 or > 16 || revision == 2 && length != 5 || revision == 4 && length != 16)
        { throw new ArgumentException("The destination must match the legacy revision's key length.", nameof(fileKey)); }
        ReadOnlySpan<byte> owner = ReadBytes(encryption, "O"u8, 32, governor);
        ReadOnlySpan<byte> user = ReadBytes(encryption, "U"u8, 32, governor);
        int permissions = ReadInteger(encryption, "P"u8, governor);
        bool metadata = revision < 4 || ReadMetadata(encryption, governor);
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
            // The published algorithm hashes all 16 digest bytes for the owner
            // key. qpdf and Poppler hash only the selected key length instead.
            // Check both bounded conventions for short R3 keys, before the user.
            int attempts = revision == 3 && length < 16 ? 2 : 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                _ = MD5.HashData(padded, ownerKey);
                if (revision >= 3) { Iterate(ownerKey, attempt == 0 ? 16 : length, governor); }
                owner.CopyTo(recovered);
                for (int round = revision >= 3 ? 19 : 0; round >= 0; round--)
                {
                    for (int index = 0; index < length; index++) { iterationKey[index] = (byte)(ownerKey[index] ^ round); }
                    PdfRc4Cipher.Transform(iterationKey[..length], recovered, governor);
                }
                if (AuthenticateUser(recovered, owner, user, identifier, permissions, metadata, revision, fileKey, governor))
                { accepted = true; return PasswordAuthentication.Owner; }
            }
            if (AuthenticateUser(padded, owner, user, identifier, permissions, metadata, revision, fileKey, governor))
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

    internal static int DeriveObjectKey(ReadOnlySpan<byte> fileKey, int number, int generation,
        Span<byte> objectKey, bool aes, ResourceGovernor governor)
    {
        if (fileKey.Length is < 5 or > 16 || objectKey.Length != 16 || aes && fileKey.Length != 16)
        { throw new ArgumentException("Legacy keys require 5..16 bytes and a 16-byte object-key destination; AES requires 16 key bytes."); }
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
            int length = fileKey.Length;
            material[length] = (byte)number; material[length + 1] = (byte)(number >> 8); material[length + 2] = (byte)(number >> 16);
            material[length + 3] = (byte)generation; material[length + 4] = (byte)(generation >> 8);
            if (aes) { "sAlT"u8.CopyTo(material[(length + 5)..]); }
            _ = MD5.HashData(material[..(length + (aes ? 9 : 5))], objectKey);
            governor.CheckCancellation(); accepted = true;
            return Math.Min(length + 5, 16);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            if (!accepted) { CryptographicOperations.ZeroMemory(objectKey); }
        }
    }

    private static bool AuthenticateUser(ReadOnlySpan<byte> padded, ReadOnlySpan<byte> owner, ReadOnlySpan<byte> user,
        ReadOnlySpan<byte> identifier, int permissions, bool metadata, int revision, Span<byte> fileKey, ResourceGovernor governor)
    {
        Span<byte> flags = stackalloc byte[4];
        Span<byte> check = stackalloc byte[32];
        Span<byte> digest = stackalloc byte[16];
        Span<byte> iterationKey = stackalloc byte[16];
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            hash.AppendData(padded); hash.AppendData(owner);
            BinaryPrimitives.WriteInt32LittleEndian(flags, permissions); hash.AppendData(flags);
            AppendIdentifier(hash, identifier, governor);
            if (!metadata) { flags.Fill(0xff); hash.AppendData(flags); }
            Finish(hash, digest);
            int length = fileKey.Length;
            if (revision >= 3) { Iterate(digest, length, governor); }
            digest[..length].CopyTo(fileKey);
            int checkLength = revision == 2 ? 32 : 16;
            if (revision == 2) { Padding.CopyTo(check); }
            else { hash.AppendData(Padding); AppendIdentifier(hash, identifier, governor); Finish(hash, check[..16]); }
            for (int round = 0; round < (revision >= 3 ? 20 : 1); round++)
            {
                for (int index = 0; index < length; index++) { iterationKey[index] = (byte)(fileKey[index] ^ round); }
                PdfRc4Cipher.Transform(iterationKey[..length], check[..checkLength], governor);
            }
            governor.CheckCancellation();
            return CryptographicOperations.FixedTimeEquals(check[..checkLength], user[..checkLength]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(flags); CryptographicOperations.ZeroMemory(check); CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(iterationKey);
        }
    }

    private static void Pad(ReadOnlySpan<byte> password, Span<byte> padded)
    {
        int count = Math.Min(password.Length, 32);
        password[..count].CopyTo(padded); Padding[..(32 - count)].CopyTo(padded[count..]);
    }

    private static void Iterate(Span<byte> digest, int inputLength, ResourceGovernor governor)
    {
        Span<byte> next = stackalloc byte[16];
        try
        {
            for (int round = 0; round < 50; round++)
            { governor.CheckCancellation(); _ = MD5.HashData(digest[..inputLength], next); next.CopyTo(digest); }
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
