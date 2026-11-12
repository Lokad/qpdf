using System.Security.Cryptography;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using static Lokad.Qpdf.Security.EncryptionDictionary;

namespace Lokad.Qpdf.Security;

internal sealed partial class OutputSecurity
{
    // Called only with an authenticated document key. The output keeps both
    // password entries and normalizes crypt filters to AES, as qpdf does.
    internal static OutputSecurity CopyAuthenticated(PdfDictionary encryption, ReadOnlySpan<byte> fileKey,
        ReadOnlySpan<byte> identifier, EncryptionParameters parameters, ResourceGovernor governor)
    {
        int revision = parameters.Revision;
        if (revision is not (4 or 6) || fileKey.Length != (revision == 6 ? 32 : 16))
        { throw new ArgumentException("An authenticated AES file key is required."); }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        byte[]? key = null;
        Span<byte> values = stackalloc byte[176];
        try
        {
            governor.CheckCancellation();
            memory.Reserve(192L + identifier.Length); // Context, file key and identifier array.
            key = fileKey.ToArray();
            byte[] copiedIdentifier = new byte[identifier.Length];
            for (int offset = 0; offset < identifier.Length;)
            {
                governor.CheckCancellation();
                int count = Math.Min(65536, identifier.Length - offset);
                identifier.Slice(offset, count).CopyTo(copiedIdentifier.AsSpan(offset, count));
                offset += count;
            }
            int entryLength = revision == 6 ? 48 : 32;
            ReadBytes(encryption, "U"u8, entryLength, governor).CopyTo(values);
            ReadBytes(encryption, "O"u8, entryLength, governor).CopyTo(values[entryLength..]);
            if (revision == 6)
            {
                ReadBytes(encryption, "UE"u8, 32, governor).CopyTo(values[96..]);
                ReadBytes(encryption, "OE"u8, 32, governor).CopyTo(values[128..]);
                ReadBytes(encryption, "Perms"u8, 16, governor).CopyTo(values[160..]);
            }
            PdfDictionary dictionary = BuildDictionary(values, revision, parameters.Permissions, parameters.EncryptMetadata, memory, governor);
            governor.CheckCancellation();
            return new(memory, key, dictionary, revision, parameters.EncryptMetadata, copiedIdentifier);
        }
        catch { if (key is not null) { CryptographicOperations.ZeroMemory(key); } memory.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(values); }
    }
}
