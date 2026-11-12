using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Security;

internal static class EncryptionDictionary
{
    internal static ReadOnlySpan<byte> ReadBytes(PdfDictionary dictionary, ReadOnlySpan<byte> key, int length, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, key, governor, governor.CancellationToken, out QpdfObject raw)
            || raw is not PdfByteString bytes || bytes.ValueMemory.Length != length)
        { throw new QpdfParseException("Invalid encryption byte string.", null, null); }
        return bytes.ValueMemory.Span;
    }

    internal static int ReadInteger(PdfDictionary dictionary, ReadOnlySpan<byte> key, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, key, governor, governor.CancellationToken, out QpdfObject raw)
            || raw is not PdfInteger integer || integer.Value < int.MinValue || integer.Value > int.MaxValue)
        { throw new QpdfParseException("An encryption integer must be a direct 32-bit integer.", null, null); }
        return (int)integer.Value;
    }

    internal static PdfName ReadName(PdfDictionary dictionary, ReadOnlySpan<byte> key, PdfName fallback, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, key, governor, governor.CancellationToken, out QpdfObject raw) || raw is PdfNull) { return fallback; }
        return raw as PdfName ?? throw new QpdfParseException("An encryption name must be a direct name.", null, null);
    }

    internal static bool ReadMetadata(PdfDictionary dictionary, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "EncryptMetadata"u8, governor, governor.CancellationToken, out QpdfObject raw) || raw is PdfNull) { return true; }
        return raw is PdfBoolean boolean ? boolean.Value : throw new QpdfParseException("Invalid encryption metadata flag.", null, null);
    }
}
