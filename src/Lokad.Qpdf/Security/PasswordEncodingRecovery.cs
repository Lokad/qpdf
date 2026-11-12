using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Security;

// A fixed candidate set over bounded credential prefixes, never a code-page registry.
internal static class PasswordEncodingRecovery
{
    internal const int MaximumCandidates = 9;

    internal static bool TryConvert(ReadOnlySpan<byte> prefix, Span<byte> destination, int candidate,
        ResourceGovernor governor, out int count)
    {
        count = 0;
        if (prefix.Length >= 2 && (prefix[0] == 254 && prefix[1] == 255 || prefix[0] == 255 && prefix[1] == 254))
        {
            if (candidate >= 3) { return false; }
            bool littleEndian = prefix[0] == 255;
            int position = 2;
            for (; position + 1 < prefix.Length && count < destination.Length; position += 2)
            {
                governor.CheckCancellation();
                int code = littleEndian ? prefix[position] | prefix[position + 1] << 8 : prefix[position] << 8 | prefix[position + 1];
                if (!PdfText.TryEncodeSingleByte(code, (PdfSingleByteEncoding)candidate, out destination[count])) { return false; }
                count++;
            }
            // Do not repair malformed UTF-16 or a dangling byte into a password.
            return position == prefix.Length || count == destination.Length;
        }
        if (HasUtf8Prefix(prefix, destination.Length, governor))
        {
            if (candidate >= 3) { return false; }
            while (!prefix.IsEmpty && count < destination.Length)
            {
                governor.CheckCancellation();
                if (Rune.DecodeFromUtf8(prefix, out Rune rune, out int consumed) != OperationStatus.Done
                    || !PdfText.TryEncodeSingleByte(rune.Value, (PdfSingleByteEncoding)candidate, out destination[count])) { return false; }
                prefix = prefix[consumed..]; count++;
            }
            return true;
        }
        if (candidate < 3)
        {
            Span<byte> encoded = stackalloc byte[3];
            try
            {
                foreach (byte value in prefix)
                {
                    governor.CheckCancellation();
                    if (count == destination.Length) { break; }
                    Rune rune = new(PdfText.DecodeSingleByte(value, (PdfSingleByteEncoding)candidate));
                    int length = rune.EncodeToUtf8(encoded);
                    int copy = Math.Min(length, destination.Length - count);
                    encoded[..copy].CopyTo(destination[count..]); count += copy;
                }
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
        int source = (candidate - 3) / 2;
        int target = (candidate - 3) % 2;
        if (target >= source) { target++; }
        foreach (byte value in prefix)
        {
            governor.CheckCancellation();
            if (count == destination.Length) { break; }
            char decoded = PdfText.DecodeSingleByte(value, (PdfSingleByteEncoding)source);
            if (!PdfText.TryEncodeSingleByte(decoded, (PdfSingleByteEncoding)target, out destination[count])) { return false; }
            count++;
        }
        return true;
    }

    private static bool HasUtf8Prefix(ReadOnlySpan<byte> prefix, int characters, ResourceGovernor governor)
    {
        while (!prefix.IsEmpty && characters-- > 0)
        {
            governor.CheckCancellation();
            if (Rune.DecodeFromUtf8(prefix, out _, out int consumed) != OperationStatus.Done) { return false; }
            prefix = prefix[consumed..];
        }
        return true;
    }
}
