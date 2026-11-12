using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Security;

internal static class EncryptionDictionary
{
    internal static int ReadSupportedRevision(PdfDictionary dictionary, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "Filter"u8, governor, governor.CancellationToken, out QpdfObject raw) || raw is not PdfName handler)
        { throw new QpdfParseException("An encryption dictionary requires a security-handler name.", null, null); }
        if (!handler.RawSpan.SequenceEqual("Standard"u8)) { throw new QpdfSecurityException(QpdfSecurityFailure.UnsupportedHandler); }
        int version = ReadInteger(dictionary, "V"u8, governor);
        if (version is not (1 or 2 or 4 or 5)) { throw new QpdfSecurityException(QpdfSecurityFailure.UnsupportedHandler); }
        int revision = ReadInteger(dictionary, "R"u8, governor);
        if ((version == 1 && revision is 2 or 3) || (version == 2 && revision == 3)
            || (version == 4 && revision == 4) || (version == 5 && revision is 5 or 6)) { return revision; }
        throw new QpdfSecurityException(QpdfSecurityFailure.UnsupportedHandler);
    }

    internal static int ReadKeyLength(PdfDictionary dictionary, ResourceGovernor governor)
    {
        int version = ReadInteger(dictionary, "V"u8, governor);
        if (version == 1) { return 40; }
        int expected = version == 2 ? 40 : version == 4 ? 128 : 256;
        if (!PdfDictionaryLookup.TryGet(dictionary, "Length"u8, governor, governor.CancellationToken, out QpdfObject raw) || raw is PdfNull)
        { return expected; }
        if (raw is not PdfInteger bits || (version == 2 ? bits.Value is < 40 or > 128 || bits.Value % 8 != 0 : bits.Value != expected))
        { throw new QpdfParseException("Invalid encryption key length for this security version.", null, null); }
        return (int)bits.Value;
    }

    internal static ReadOnlySpan<byte> ReadIdentifier(PdfDictionary trailer, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(trailer, "ID"u8, governor, governor.CancellationToken, out QpdfObject raw)
            || raw is not PdfArray { Count: 2 } array || array[0] is not PdfByteString identifier || array[1] is not PdfByteString)
        { throw new QpdfParseException("Encrypted inputs require a direct two-string identifier array.", null, null); }
        return identifier.ValueSpan;
    }

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
