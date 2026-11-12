using System.Globalization;
using System.Security.Cryptography;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Security;

internal sealed partial class DocumentSecurity
{
    // Called only for physical indirect objects, never object-stream members.
    internal QpdfObject Decrypt(QpdfObject value, int objectNumber, int generation, bool rootMetadata,
        IIndirectResolver resolver, ParserMemoryScope destination)
    {
        CheckOwner(_governor); destination.CheckOwner(_governor);
        _governor.CheckCancellation();
        if (value is not (PdfByteString or PdfArray or PdfDictionary or PdfStream)) { return value; }
        if (value is PdfStream xref && PdfStringCrypt.HasType(xref.Dictionary, "XRef"u8, _governor)) { return value; }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(_governor, _governor.CancellationToken);
        Span<byte> derived = stackalloc byte[16];
        try
        {
            ReadOnlySpan<byte> key = ObjectKey(_strings, objectNumber, generation, derived);
            return DecryptObject(value, objectNumber, generation, rootMetadata, resolver, destination, graph, key);
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    private ReadOnlySpan<byte> ObjectKey(PdfCipher cipher, int number, int generation, Span<byte> derived)
    {
        byte[] key = _key ?? throw new ObjectDisposedException(nameof(DocumentSecurity));
        if (cipher is not (PdfCipher.Rc4 or PdfCipher.Aes128)) { return key; }
        int length = LegacyEncryption.DeriveObjectKey(key, number, generation, derived, cipher == PdfCipher.Aes128, _governor);
        return derived[..length];
    }

    private QpdfObject DecryptObject(QpdfObject value, int number, int generation, bool rootMetadata, IIndirectResolver resolver,
        ParserMemoryScope destination, OwnedAllocationBatch graph, ReadOnlySpan<byte> key)
    {
        QpdfObject result;
        if (value is PdfStream stream)
        {
            bool embedded = TryGet(stream.Dictionary, "Type"u8, out QpdfObject rawType)
                && Resolve(rawType, resolver) is PdfName type && type.RawSpan.SequenceEqual("EmbeddedFile"u8);
            PdfCipher cipher = embedded ? _files : _streams;
            if (rootMetadata && !EncryptMetadata) { cipher = PdfCipher.Identity; }
            PdfDictionary header = DecryptDictionary(stream.Dictionary, graph, 0, key);
            if (Revision >= 4) { header = ReadStreamCipher(header, resolver, embedded, graph, ref cipher); }
            if (FilterChain.HasExternalData(stream, resolver, _governor, graph.Token)) { cipher = PdfCipher.Identity; }
            if (cipher == PdfCipher.Identity)
            {
                graph.Memory.Reserve(32L); graph.Add(32L);
                result = stream.WithDictionary(header);
            }
            else
            {
                Span<byte> derived = stackalloc byte[16];
                byte[] bytes;
                try
                {
                    ReadOnlySpan<byte> streamKey = cipher == _strings ? key : ObjectKey(cipher, number, generation, derived);
                    bytes = PdfStringCrypt.Bytes(stream.RawMemory.Span, graph, streamKey, cipher, false);
                }
                finally { CryptographicOperations.ZeroMemory(derived); }
                PdfInteger length;
                if (bytes.Length < PdfInteger.SharedValueCount) { length = PdfInteger.GetSharedValue(bytes.Length); }
                else
                {
                    graph.Memory.Reserve(88L); graph.Add(88L);
                    length = new(bytes.Length.ToString(CultureInfo.InvariantCulture), bytes.Length);
                }
                header = PdfDictionaryRebuild.ReplaceEntry(graph, header, "Length"u8, length);
                graph.Memory.Reserve(32L); graph.Add(32L);
                result = PdfStream.FromOwnedData(header, bytes);
            }
        }
        else { result = DecryptValue(value, graph, 0, key); }
        graph.Memory.TransferTo(destination, graph.Bytes);
        return result;
    }

    private QpdfObject DecryptValue(QpdfObject value, OwnedAllocationBatch graph, int depth, ReadOnlySpan<byte> key)
    {
        if (_strings == PdfCipher.Identity) { return value; }
        return PdfStringCrypt.Value(value, graph, depth, key, _strings, false);
    }

    private PdfDictionary DecryptDictionary(PdfDictionary dictionary, OwnedAllocationBatch graph, int depth, ReadOnlySpan<byte> key)
    {
        if (_strings == PdfCipher.Identity) { return dictionary; }
        return PdfStringCrypt.Dictionary(dictionary, graph, depth, key, _strings, false);
    }

    private bool TryGet(PdfDictionary dictionary, ReadOnlySpan<byte> name, out QpdfObject value)
        => PdfDictionaryLookup.TryGet(dictionary, name, _governor, _governor.CancellationToken, out value);
}
