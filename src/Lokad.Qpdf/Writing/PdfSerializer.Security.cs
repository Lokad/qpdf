using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

internal sealed partial class PdfSerializer
{
    private void ValidateEncryptionModel(IReadOnlyList<NumberedObject> objects, CancellationToken cancellationToken)
        => ValidateEncryptionModel(objects, _security, _encryptionObject, _governor, cancellationToken);

    internal static void ValidateEncryptionModel(IReadOnlyList<NumberedObject> objects, OutputSecurity? security,
        int encryptionObject, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (security is null) { return; }
        bool found = false;
        foreach (NumberedObject item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (item.Number != encryptionObject) { continue; }
            if (found || !ReferenceEquals(item.Value, security.Dictionary))
            { throw new QpdfException("Output encryption dictionary does not match its physical object."); }
            found = true;
        }
        if (!found) { throw new QpdfException("Output encryption dictionary is missing from the model."); }
    }

    internal async ValueTask AppendObjectStreamPayloadAsync(int number, int count, ObjectStreamPayload payload,
        IByteSink sink, CancellationToken cancellationToken)
    {
        int length = _security is null ? payload.Data.Length : PdfAesCipher.EncryptedLength(payload.Data.Length);
        if (length > _limits.MaxOutputBytes)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, _limits.MaxOutputBytes); }
        if (sink is CountingSink counter)
        {
            counter.AppendLength(ObjectStreamHeader.Measure(number, count, length, payload.First,
                payload.Compressed, false, _limits, _governor, cancellationToken), cancellationToken);
            counter.AppendLength(length, cancellationToken);
            return;
        }
        // Members are serialized in plaintext. Compress the complete container,
        // then encrypt it once and keep its owner alive through the sink await.
        using ParserMemoryScope? encrypted = _security is null ? null : ParserMemoryScope.Create(_governor);
        ReadOnlyMemory<byte> data = encrypted is null ? payload.Data
            : (_security ?? throw new InvalidOperationException("Missing output security.")).Encrypt(payload.Data.Span, encrypted, _governor);
        using (ObjectStreamHeader header = ObjectStreamHeader.Create(number, count, length, payload.First,
            payload.Compressed, false, _limits, _governor, cancellationToken))
        {
            await AppendBytesAsync(header.Data, sink, cancellationToken).ConfigureAwait(false);
        }
        await AppendBytesAsync(data, sink, cancellationToken).ConfigureAwait(false);
    }

    private QpdfObject EncryptQdfValue(QpdfObject value, bool rootMetadata, IIndirectResolver resolver, ParserMemoryScope memory)
    {
        QpdfObject result = (_security ?? throw new InvalidOperationException("Missing output security.")).EncryptObject(
            value, rootMetadata, resolver, memory, _governor);
        if (value is PdfStream original && result is PdfStream encrypted
            && PdfDictionaryLookup.TryGet(original.Dictionary, "Length"u8, _governor, _governor.CancellationToken, out QpdfObject length)
            && length is PdfReference)
        {
            // QDF writes the ciphertext length in the following physical object.
            // Keep that reference while borrowing the already-admitted ciphertext.
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(_governor, _governor.CancellationToken);
            PdfDictionary header = PdfDictionaryRebuild.ReplaceEntry(graph, encrypted.Dictionary, "Length"u8, length);
            graph.Memory.Reserve(32L); graph.Add(32L); result = encrypted.WithDictionary(header);
            graph.Memory.TransferTo(memory, graph.Bytes);
        }
        return result;
    }

    private static int MapSecurityObject(int number, WriterIndex<int> renumber)
    {
        if (number == 0) { return 0; }
        if (renumber.TryGetValue(number, out int mapped)) { return mapped; }
        throw new QpdfException("QDF output lost an encryption or metadata object.");
    }
}
