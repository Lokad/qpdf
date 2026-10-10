using System.Globalization;
using System.Text;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Content;

/// <summary>Admits fresh label values and number trees while borrowing copied source values.</summary>
internal static class PageLabelStorage
{
    internal static PdfDictionary Explicit(PageLabelSpec specification, ResourceGovernor governor, CancellationToken token)
    {
        ReadOnlySpan<byte> style = specification.Style switch
        {
            null or PdfPageLabelStyle.None => default,
            PdfPageLabelStyle.Decimal => "D"u8,
            PdfPageLabelStyle.UpperRoman => "R"u8,
            PdfPageLabelStyle.LowerRoman => "r"u8,
            PdfPageLabelStyle.UpperAlpha => "A"u8,
            PdfPageLabelStyle.LowerAlpha => "a"u8,
            _ => throw new QpdfException($"Unknown page label style {(int)specification.Style.Value}."),
        };
        bool hasStart = specification.Start.HasValue && specification.Start.Value != 1;
        int capacity = 2 + (style.IsEmpty ? 0 : 4) + (hasStart ? 4 : 0) + (specification.Prefix is null ? 0 : 4);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, capacity, token);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out int allocation);
        if (!style.IsEmpty) { Assign(graph, entries, "S"u8, PdfDictionaryRebuild.CreateName(graph, style)); }
        if (hasStart) { Assign(graph, entries, "St"u8, Integer(graph, specification.Start.GetValueOrDefault())); }
        if (specification.Prefix is string prefix) { Assign(graph, entries, "P"u8, Prefix(graph, prefix)); }
        PdfDictionary result = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.RetainWithPrepaidRetirement(); return result;
    }

    internal static PdfDictionary Merged(QpdfObject? style, QpdfObject? prefix, long start,
        ResourceGovernor governor, CancellationToken token)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 6 + (style is null ? 0 : 2) + (prefix is null ? 0 : 2), token);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out int allocation);
        if (style is not null) { Assign(graph, entries, "S"u8, style); }
        if (prefix is not null) { Assign(graph, entries, "P"u8, prefix); }
        Assign(graph, entries, "St"u8, Integer(graph, start));
        PdfDictionary result = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.RetainWithPrepaidRetirement(); return result;
    }

    internal static PdfDictionary NumberTree(IReadOnlyList<(int Index, PdfDictionary Label)> items,
        ResourceGovernor governor, CancellationToken token)
    {
        int count = checked(items.Count * 2);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, checked(count + 6), token);
        long bytes = checked(32L + IntPtr.Size * (long)count);
        graph.Memory.Reserve(bytes); QpdfObject[] numbers = new QpdfObject[count]; graph.Add(numbers, bytes);
        for (int i = 0; i < items.Count; i++)
        {
            Step(graph, 1L); numbers[i * 2] = Integer(graph, items[i].Index); numbers[i * 2 + 1] = items[i].Label;
        }
        graph.Memory.Reserve(24L); PdfArray array = PdfArray.FromOwnedItems(numbers); graph.Add(array, 24L);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out int allocation);
        Assign(graph, entries, "Nums"u8, array);
        PdfDictionary result = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.RetainWithPrepaidRetirement(); return result;
    }

    private static void Assign(OwnedAllocationBatch graph, PdfNameTable<QpdfObject> entries, ReadOnlySpan<byte> key, QpdfObject value)
        => entries.Assign(graph.Memory, graph.Governor, PdfDictionaryRebuild.CreateName(graph, key), value, graph.Token);

    private static PdfInteger Integer(OwnedAllocationBatch graph, long value)
    {
        Step(graph, 20L); Span<char> text = stackalloc char[20];
        if (!value.TryFormat(text, out int length, provider: CultureInfo.InvariantCulture))
        { throw new InvalidOperationException("Label number exceeds its bounded token storage."); }
        long bytes = GovernedText.StringBytes(length);
        graph.Memory.Reserve(bytes); string token = text[..length].ToString(); graph.Add(token, bytes);
        graph.Memory.Reserve(32L); PdfInteger result = new(token, value); graph.Add(result, 32L); return result;
    }

    private static PdfByteString Prefix(OwnedAllocationBatch graph, string prefix)
    {
        bool ascii = true, pdfDoc = true;
        for (int offset = 0; offset < prefix.Length;)
        {
            int count = Math.Min(256, prefix.Length - offset); Step(graph, count);
            for (int i = offset; i < offset + count; i++)
            {
                ascii &= prefix[i] < 128;
            }
            offset += count;
        }
        if (!ascii)
        {
            for (int offset = 0; offset < prefix.Length && pdfDoc;)
            {
                int count = Math.Min(256, prefix.Length - offset); Step(graph, count);
                for (int i = offset; i < offset + count; i++)
                { if (!Attachments.PdfText.TryEncodePdfDocByte(prefix[i], out _)) { pdfDoc = false; break; } }
                offset += count;
            }
        }
        int length = ascii || pdfDoc ? prefix.Length : checked(2 + prefix.Length * 2);
        long bytes = checked(32L + length); graph.Memory.Reserve(bytes);
        byte[] encoded = new byte[length]; graph.Add(encoded, bytes);
        if (!ascii && !pdfDoc) { encoded[0] = 0xfe; encoded[1] = 0xff; }
        for (int offset = 0; offset < prefix.Length;)
        {
            int count = Math.Min(256, prefix.Length - offset);
            // Keep a surrogate pair in the same bounded BCL encoding call.
            if (offset + count < prefix.Length && char.IsHighSurrogate(prefix[offset + count - 1])
                && char.IsLowSurrogate(prefix[offset + count])) { count++; }
            Step(graph, count);
            if (ascii)
            { for (int i = offset; i < offset + count; i++) { encoded[i] = (byte)prefix[i]; } }
            else if (pdfDoc)
            {
                for (int i = offset; i < offset + count; i++)
                {
                    if (!Attachments.PdfText.TryEncodePdfDocByte(prefix[i], out encoded[i]))
                    { throw new InvalidOperationException("Admitted PDFDocEncoding prefix changed during construction."); }
                }
            }
            else { Encoding.BigEndianUnicode.GetBytes(prefix.AsSpan(offset, count), encoded.AsSpan(2 + offset * 2, count * 2)); }
            offset += count;
        }
        graph.Memory.Reserve(32L); PdfByteString result = PdfByteString.FromOwnedBytes(encoded, !ascii);
        graph.Add(result, 32L); return result;
    }

    private static void Step(OwnedAllocationBatch graph, long work)
    { graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation(); graph.Governor.ChargeWork(work); }
}
