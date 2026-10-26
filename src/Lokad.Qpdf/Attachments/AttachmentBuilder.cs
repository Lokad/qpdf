using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Attachments;

internal static class AttachmentBuilder
{
    private static PdfByteString EncodeText(string text, ResourceGovernor governor,
        ParserMemoryScope memory, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool ascii = true;
        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 255) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
            if (!PdfText.IsAsciiTextCharacter(text[index])) { ascii = false; break; }
        }
        long length = ascii ? text.Length : 2L + 2L * text.Length;
        if (length > Array.MaxLength) { throw new QpdfParseException("Attachment text exceeds supported capacity.", null, null); }
        memory.Reserve(64L + length);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        byte[] bytes = length == 0 ? Array.Empty<byte>() : new byte[(int)length];
        int written = ascii ? 0 : 2;
        if (!ascii) { bytes[0] = 0xfe; bytes[1] = 0xff; }
        Encoding encoding = ascii ? Encoding.ASCII : Encoding.BigEndianUnicode;
        ReadOnlySpan<char> remaining = text;
        while (!remaining.IsEmpty)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int count = Math.Min(256, remaining.Length);
            if (count < remaining.Length && char.IsHighSurrogate(remaining[count - 1]) && char.IsLowSurrogate(remaining[count])) { count--; }
            written += encoding.GetBytes(remaining[..count], bytes.AsSpan(written));
            remaining = remaining[count..];
        }
        return PdfByteString.FromOwnedBytes(bytes, !ascii);
    }

    private static PdfName Name(string text, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        memory.Reserve(64L + text.Length); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        byte[] bytes = new byte[text.Length];
        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 255) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
            if (text[index] > 127) { throw new ArgumentException("Name text must be ASCII.", nameof(text)); }
            bytes[index] = (byte)text[index];
        }
        return PdfName.FromOwnedBytes(bytes);
    }

    // Each header has a fixed small vocabulary; the existing table admits its
    // backing once and adopts it without a defensive dictionary copy.
    private static PdfDictionary Dictionary(ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token,
        ReadOnlySpan<(string Key, QpdfObject? Value)> values)
    {
        memory.CheckOwner(governor);
        PdfNameTable<QpdfObject> entries = PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, token);
        entries.EnsureCapacity(memory, governor, values.Length, token);
        foreach ((string key, QpdfObject? value) in values)
        {
            if (value is not null) { entries.Assign(memory, governor, Name(key, governor, memory, token), value, token); }
        }
        entries.Freeze(memory, governor, token); memory.Reserve(32L);
        return PdfDictionary.FromOwnedEntries(entries);
    }

    internal static string Basename(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf((char)92));
        return cut < 0 ? path : path.Substring(cut + 1);
    }

    internal static PdfDictionary BuildStreamDictionary(long length, byte[] checksum,
        string? mimeType, string? creationDate, string? modDate, bool flate,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ArgumentNullException.ThrowIfNull(checksum);
        memory.Reserve(192L + checksum.LongLength);
        PdfInteger size = new(length.ToString(System.Globalization.CultureInfo.InvariantCulture), length);
        PdfByteString digest = PdfByteString.FromOwnedBytes(checksum, true);
        PdfDictionary parameters = Dictionary(governor, memory, token,
        [
            ("Size", size), ("CheckSum", digest),
            ("CreationDate", creationDate is null ? null : EncodeText(creationDate, governor, memory, token)),
            ("ModDate", modDate is null ? null : EncodeText(modDate, governor, memory, token)),
        ]);
        return Dictionary(governor, memory, token,
        [
            ("Type", Name("EmbeddedFile", governor, memory, token)), ("Length", size), ("Params", parameters),
            ("Subtype", mimeType is null ? null : Name(mimeType, governor, memory, token)),
            ("Filter", flate ? Name("FlateDecode", governor, memory, token) : null),
        ]);
    }

    internal static PdfDictionary BuildFilespecDictionary(string fileName, string? description, int streamId,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        PdfByteString name = EncodeText(fileName, governor, memory, token);
        memory.Reserve(32L); PdfReference stream = new(streamId, 0);
        PdfDictionary embedded = Dictionary(governor, memory, token, [("F", stream), ("UF", stream)]);
        return Dictionary(governor, memory, token,
        [
            ("Type", Name("Filespec", governor, memory, token)), ("F", name), ("UF", name), ("EF", embedded),
            ("Desc", description is null ? null : EncodeText(description, governor, memory, token)),
        ]);
    }

    internal static PdfDictionary BuildNameTree(IReadOnlyCollection<KeyValuePair<string, PdfReference>> pairs,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0) { throw new QpdfException("Attachment name tree needs at least one entry."); }
        if (pairs.Count > Array.MaxLength / 2)
        { throw new QpdfParseException("Attachment name tree exceeds supported array capacity.", null, null); }
        int count = pairs.Count * 2;
        memory.Reserve(64L + IntPtr.Size * (long)count);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        QpdfObject[] items;
        using (ParserMemoryScope ordering = ParserMemoryScope.Create(governor))
        {
            ordering.Reserve(32L + 2L * IntPtr.Size * pairs.Count);
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            KeyValuePair<PdfByteString, PdfReference>[] encoded = new KeyValuePair<PdfByteString, PdfReference>[pairs.Count];
            int index = 0;
            bool sorted = true;
            EncodedKeyOrder order = new(governor, token);
            foreach (KeyValuePair<string, PdfReference> pair in pairs)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                encoded[index] = new(EncodeText(pair.Key, governor, memory, token), pair.Value);
                if (sorted && index > 0 && order.Compare(encoded[index - 1], encoded[index]) > 0) { sorted = false; }
                index++;
            }
            if (!sorted) { GovernedSort.Sort(encoded.AsSpan(), order, governor, token); }
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            items = new QpdfObject[count];
            for (index = 0; index < encoded.Length; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                items[2 * index] = encoded[index].Key;
                items[2 * index + 1] = encoded[index].Value;
            }
        }
        return Dictionary(governor, memory, token, [("Names", PdfArray.FromOwnedItems(items))]);
    }

    private readonly struct EncodedKeyOrder(ResourceGovernor governor, CancellationToken token) : IComparer<KeyValuePair<PdfByteString, PdfReference>>
    {
        public int Compare(KeyValuePair<PdfByteString, PdfReference> left, KeyValuePair<PdfByteString, PdfReference> right)
        {
            ReadOnlySpan<byte> first = left.Key.ValueMemory.Span, second = right.Key.ValueMemory.Span;
            int length = Math.Min(first.Length, second.Length);
            for (int offset = 0; offset < length; offset += Math.Min(8192, length - offset))
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                int count = Math.Min(8192, length - offset);
                int order = first.Slice(offset, count).SequenceCompareTo(second.Slice(offset, count));
                if (order != 0) { return order; }
            }
            return first.Length.CompareTo(second.Length);
        }
    }

    internal static byte[] Checksum(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return MD5.HashData(data);
    }
}
