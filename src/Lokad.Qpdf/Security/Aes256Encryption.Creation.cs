using System.Buffers.Binary;
using System.Security.Cryptography;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Security;

internal static partial class Aes256Encryption
{
    // Entries are U (48), O (48), UE (32), OE (32), Perms (16). The caller
    // admits and owns both destinations; no password or scratch escapes.
    internal static void Generate(ReadOnlySpan<byte> userPassword, ReadOnlySpan<byte> ownerPassword,
        int permissions, bool metadata, Span<byte> fileKey, Span<byte> entries, ResourceGovernor governor)
    {
        CheckCreationDestinations(fileKey, entries);
        fileKey.Clear(); entries.Clear();
        Span<byte> randomness = stackalloc byte[68];
        try
        {
            governor.CheckCancellation();
            RandomNumberGenerator.Fill(randomness);
            CreateEntries(userPassword, ownerPassword, permissions, metadata, randomness, fileKey, entries, governor);
        }
        finally { CryptographicOperations.ZeroMemory(randomness); }
    }

    // Deterministic core for recorded independent vectors. Production creation
    // goes through Generate, supplying fresh system randomness on every call.
    internal static void CreateEntries(ReadOnlySpan<byte> userPassword, ReadOnlySpan<byte> ownerPassword,
        int permissions, bool metadata, ReadOnlySpan<byte> randomness, Span<byte> fileKey, Span<byte> entries,
        ResourceGovernor governor)
    {
        CheckCreationDestinations(fileKey, entries);
        if (randomness.Length != 68) { throw new ArgumentException("Revision 6 creation requires 68 random bytes.", nameof(randomness)); }
        fileKey.Clear(); entries.Clear();
        Span<byte> hash = stackalloc byte[32];
        Span<byte> clearPermissions = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16]; iv.Clear();
        bool accepted = false;
        try
        {
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            memory.Reserve(4096L); // System AES state and fixed creation scratch.
            randomness[..32].CopyTo(fileKey);
            Span<byte> user = entries[..48], owner = entries.Slice(48, 48);
            randomness.Slice(32, 16).CopyTo(user[32..]);
            randomness.Slice(48, 16).CopyTo(owner[32..]);
            HashRevision6(userPassword, user.Slice(32, 8), [], user[..32], governor);
            HashRevision6(userPassword, user.Slice(40, 8), [], hash, governor);
            using Aes aes = Aes.Create(); aes.SetKey(hash);
            _ = aes.EncryptCbc(fileKey, iv, entries.Slice(96, 32), PaddingMode.None);
            HashRevision6(ownerPassword, owner.Slice(32, 8), user, owner[..32], governor);
            HashRevision6(ownerPassword, owner.Slice(40, 8), user, hash, governor);
            aes.SetKey(hash);
            _ = aes.EncryptCbc(fileKey, iv, entries.Slice(128, 32), PaddingMode.None);
            BinaryPrimitives.WriteInt32LittleEndian(clearPermissions, permissions);
            clearPermissions.Slice(4, 4).Fill(255);
            clearPermissions[8] = metadata ? (byte)'T' : (byte)'F';
            "adb"u8.CopyTo(clearPermissions[9..]); randomness[64..].CopyTo(clearPermissions[12..]);
            aes.SetKey(fileKey);
            _ = aes.EncryptEcb(clearPermissions, entries.Slice(160, 16), PaddingMode.None);
            governor.CheckCancellation(); accepted = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash); CryptographicOperations.ZeroMemory(clearPermissions);
            CryptographicOperations.ZeroMemory(iv);
            if (!accepted) { CryptographicOperations.ZeroMemory(fileKey); CryptographicOperations.ZeroMemory(entries); }
        }
    }

    private static void CheckCreationDestinations(Span<byte> fileKey, Span<byte> entries)
    {
        if (fileKey.Length != 32 || entries.Length != 176)
        { throw new ArgumentException("Revision 6 creation requires a 32-byte key and 176-byte entries destination."); }
    }
}
