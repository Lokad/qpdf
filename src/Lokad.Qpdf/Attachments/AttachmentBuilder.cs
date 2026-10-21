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
            if (text[index] > 127) { ascii = false; break; }
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

    internal static PdfDictionary BuildNameTree(IReadOnlyList<KeyValuePair<string, PdfReference>> pairs,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0) { throw new QpdfException("Attachment name tree needs at least one entry."); }
        int leafCount = (pairs.Count - 1) / 64 + 1;
        QpdfObject[] leaves = Array.Empty<QpdfObject>();
        if (leafCount > 1) { memory.Reserve(64L + IntPtr.Size * (long)leafCount); leaves = new QpdfObject[leafCount]; }
        PdfByteString? first = null, last = null;
        for (int leaf = 0; leaf < leafCount; leaf++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int start = checked(leaf * 64), take = Math.Min(64, pairs.Count - start);
            memory.Reserve(64L + 2L * IntPtr.Size * take);
            QpdfObject[] items = new QpdfObject[2 * take];
            for (int index = 0; index < take; index++)
            {
                PdfByteString key = EncodeText(pairs[start + index].Key, governor, memory, token);
                first ??= key; last = key;
                items[2 * index] = key; items[2 * index + 1] = pairs[start + index].Value;
            }
            PdfDictionary node = Dictionary(governor, memory, token, [("Names", PdfArray.FromOwnedItems(items))]);
            if (leafCount == 1) { return node; }
            leaves[leaf] = node;
        }
        memory.Reserve(64L + 2L * IntPtr.Size);
        QpdfObject[] limits = [first ?? throw new InvalidOperationException("Missing first attachment key."),
            last ?? throw new InvalidOperationException("Missing last attachment key.")];
        return Dictionary(governor, memory, token,
            [("Kids", PdfArray.FromOwnedItems(leaves)), ("Limits", PdfArray.FromOwnedItems(limits))]);
    }

    internal static byte[] Checksum(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return MD5.HashData(data);
    }
}
