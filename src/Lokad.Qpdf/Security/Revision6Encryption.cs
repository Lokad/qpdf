using System.Buffers.Binary;
using System.Security.Cryptography;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using static Lokad.Qpdf.Security.EncryptionDictionary;

namespace Lokad.Qpdf.Security;

internal enum PasswordAuthentication : byte { None, User, Owner }

// ISO 32000-2 algorithms 2.A, 2.B and 13, including the published 2.B/ECB errata.
// Password encoding belongs to the entry point. These routines consume bytes,
// truncate to 127 bytes, and never normalize or retain the caller's password.
internal static partial class Revision6Encryption
{
    internal static PasswordAuthentication Authenticate(PdfDictionary encryption,
        ReadOnlySpan<byte> password, Span<byte> fileKey, ResourceGovernor governor)
    {
        if (fileKey.Length != 32) { throw new ArgumentException("A 256-bit key destination is required.", nameof(fileKey)); }
        fileKey.Clear();
        governor.CheckCancellation();
        ReadOnlySpan<byte> user = ReadBytes(encryption, "U"u8, 48, governor);
        ReadOnlySpan<byte> owner = ReadBytes(encryption, "O"u8, 48, governor);
        ReadOnlySpan<byte> userKey = ReadBytes(encryption, "UE"u8, 32, governor);
        ReadOnlySpan<byte> ownerKey = ReadBytes(encryption, "OE"u8, 32, governor);
        ReadOnlySpan<byte> permissions = ReadBytes(encryption, "Perms"u8, 16, governor);
        int permissionsValue = ReadInteger(encryption, "P"u8, governor);
        bool metadata = ReadMetadata(encryption, governor);

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(4096L); // System AES state and fixed key/permissions scratch.
        Span<byte> hash = stackalloc byte[32];
        Span<byte> clearPermissions = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16]; iv.Clear();
        bool accepted = false;
        try
        {
            Hash(password, owner.Slice(32, 8), user, hash, governor);
            PasswordAuthentication match;
            ReadOnlySpan<byte> encryptedKey;
            if (CryptographicOperations.FixedTimeEquals(hash, owner[..32]))
            {
                match = PasswordAuthentication.Owner; encryptedKey = ownerKey;
                Hash(password, owner.Slice(40, 8), user, hash, governor);
            }
            else
            {
                Hash(password, user.Slice(32, 8), [], hash, governor);
                if (!CryptographicOperations.FixedTimeEquals(hash, user[..32])) { return PasswordAuthentication.None; }
                match = PasswordAuthentication.User; encryptedKey = userKey;
                Hash(password, user.Slice(40, 8), [], hash, governor);
            }
            governor.CheckCancellation();
            using Aes aes = Aes.Create();
            aes.SetKey(hash);
            _ = aes.DecryptCbc(encryptedKey, iv, fileKey, PaddingMode.None);
            aes.SetKey(fileKey);
            _ = aes.DecryptEcb(permissions, clearPermissions, PaddingMode.None);
            if (BinaryPrimitives.ReadInt32LittleEndian(clearPermissions) != permissionsValue
                || BinaryPrimitives.ReadUInt32LittleEndian(clearPermissions[4..]) != uint.MaxValue
                || clearPermissions[8] != (metadata ? (byte)'T' : (byte)'F')
                || !clearPermissions.Slice(9, 3).SequenceEqual("adb"u8))
            { throw new QpdfParseException("Encrypted permissions do not match the encryption dictionary.", null, null); }
            governor.CheckCancellation();
            accepted = true;
            return match;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(clearPermissions);
            CryptographicOperations.ZeroMemory(iv);
            if (!accepted) { CryptographicOperations.ZeroMemory(fileKey); }
        }
    }

    internal static void Hash(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> user, Span<byte> destination, ResourceGovernor governor)
    {
        if (salt.Length != 8 || (user.Length != 0 && user.Length != 48) || destination.Length != 32)
        { throw new ArgumentException("Invalid revision 6 hash input lengths."); }
        destination.Clear();
        governor.CheckCancellation();
        password = password[..Math.Min(password.Length, 127)];
        const int repeatedCapacity = (127 + 64 + 48) * 64;
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(checked(2L * repeatedCapacity + 4096L));
        byte[] buffers = new byte[2 * repeatedCapacity];
        Span<byte> repeated = buffers.AsSpan(0, repeatedCapacity);
        Span<byte> encrypted = buffers.AsSpan(repeatedCapacity);
        Span<byte> seed = stackalloc byte[127 + 8 + 48];
        Span<byte> digest = stackalloc byte[64];
        try
        {
            password.CopyTo(seed); salt.CopyTo(seed[password.Length..]); user.CopyTo(seed[(password.Length + 8)..]);
            int digestLength = SHA256.HashData(seed[..(password.Length + 8 + user.Length)], digest);
            using Aes aes = Aes.Create();
            // The stopping byte is at most 255, so the specification always
            // terminates by round 287. Check cancellation between bounded rounds.
            for (int round = 1; round <= 287; round++)
            {
                governor.CheckCancellation();
                int blockLength = password.Length + digestLength + user.Length;
                password.CopyTo(repeated); digest[..digestLength].CopyTo(repeated[password.Length..]);
                user.CopyTo(repeated[(password.Length + digestLength)..]);
                for (int copy = 1; copy < 64; copy++)
                { repeated[..blockLength].CopyTo(repeated[(copy * blockLength)..]); }
                int length = 64 * blockLength;
                aes.SetKey(digest[..16]);
                _ = aes.EncryptCbc(repeated[..length], digest.Slice(16, 16), encrypted[..length], PaddingMode.None);
                int selector = 0;
                for (int index = 0; index < 16; index++) { selector += encrypted[index]; }
                digestLength = (selector % 3) switch
                {
                    0 => SHA256.HashData(encrypted[..length], digest),
                    1 => SHA384.HashData(encrypted[..length], digest),
                    _ => SHA512.HashData(encrypted[..length], digest)
                };
                if (round >= 64 && encrypted[length - 1] <= round - 32)
                { governor.CheckCancellation(); digest[..32].CopyTo(destination); return; }
            }
            throw new InvalidOperationException("Revision 6 hash exceeded its mathematical bound.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffers);
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

}
