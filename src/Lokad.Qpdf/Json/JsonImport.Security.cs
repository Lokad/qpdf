using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Json;

internal static partial class JsonImport
{
    // JSON carries plaintext objects. Output protection comes from an authenticated
    // PDF or an explicit policy, never from unauthenticated JSON password entries.
    internal static ImportedModel RemoveEncryptionDeclaration(ImportedModel model, ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(model.Trailer, "Encrypt"u8, governor, token, out QpdfObject value) || value is PdfNull) { return model; }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary trailer = PdfDictionaryRebuild.RemoveEntry(graph, model.Trailer, "Encrypt"u8);
        graph.Memory.Reserve(48L); graph.Add(48L);
        ImportedModel result = model with { Trailer = trailer };
        graph.Retain(); return result;
    }

    private static ImportedModel PrepareUpdateEncryption(ImportedModel model, PdfDictionary originalTrailer,
        QpdfObject originalEncryption, bool authenticated, bool preserve, ResourceGovernor governor, CancellationToken token)
    {
        if (!preserve) { return RemoveEncryptionDeclaration(model, governor, token); }
        if (!authenticated || !PdfDictionaryLookup.TryGet(model.Trailer, "Encrypt"u8, governor, token, out QpdfObject current) || current is PdfNull)
        { return model; }
        // Unchanged full exports may repeat the original handler. Reject edits to
        // that handler under Preserve instead of silently assigning different keys.
        if (!PdfDictionaryLookup.TryGet(originalTrailer, "Encrypt"u8, governor, token, out QpdfObject original)
            || !SameEncryptionValue(original, current, governor, token, 0L)) { throw new QpdfCryptoException("/Encrypt"); }
        QpdfObject currentEncryption = current is PdfReference reference
            ? model.Objects.GetValueOrDefault(reference.ObjectNumber, PdfNull.Instance) : current;
        if (originalEncryption is not PdfDictionary || currentEncryption is not PdfDictionary
            || !SameEncryptionValue(originalEncryption, currentEncryption, governor, token, 0L))
        { throw new QpdfCryptoException("/Encrypt"); }
        return RemoveEncryptionDeclaration(model, governor, token);
    }

    private static bool SameEncryptionValue(QpdfObject left, QpdfObject right, ResourceGovernor governor, CancellationToken token, long depth)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteOperation();
        if (depth > governor.MaxNestingDepth) { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, governor.MaxNestingDepth); }
        if (ReferenceEquals(left, right)) { return true; }
        if (left is PdfArray array && right is PdfArray otherArray)
        {
            if (array.Count != otherArray.Count) { return false; }
            for (int index = 0; index < array.Count; index++)
            { if (!SameEncryptionValue(array[index], otherArray[index], governor, token, checked(depth + 1L))) { return false; } }
            return true;
        }
        if (left is PdfDictionary dictionary && right is PdfDictionary otherDictionary)
        {
            if (dictionary.Count != otherDictionary.Count) { return false; }
            using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
            while (entries.MoveNext(governor, token))
            {
                if (!PdfDictionaryLookup.TryGet(otherDictionary, entries.Current.Key.RawSpan, governor, token, out QpdfObject candidate)
                    || !SameEncryptionValue(entries.Current.Value, candidate, governor, token, checked(depth + 1L))) { return false; }
            }
            return true;
        }
        return (left, right) switch
        {
            (PdfNull, PdfNull) => true,
            (PdfBoolean a, PdfBoolean b) => a.Value == b.Value,
            (PdfInteger a, PdfInteger b) => a.Value == b.Value,
            (PdfReal a, PdfReal b) => a.Value.Equals(b.Value),
            (PdfName a, PdfName b) => PdfNameTable<QpdfObject>.NamesEqual(a, b, governor, token),
            (PdfByteString a, PdfByteString b) => SameEncryptionBytes(a.ValueSpan, b.ValueSpan, governor, token),
            (PdfReference a, PdfReference b) => a.ObjectNumber == b.ObjectNumber && a.Generation == b.Generation,
            _ => false,
        };
    }

    private static bool SameEncryptionBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, ResourceGovernor governor, CancellationToken token)
    {
        if (left.Length != right.Length) { return false; }
        for (int offset = 0; offset < left.Length;)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteOperation();
            int count = Math.Min(65536, left.Length - offset);
            if (!left.Slice(offset, count).SequenceEqual(right.Slice(offset, count))) { return false; }
            offset += count;
        }
        return true;
    }
}
