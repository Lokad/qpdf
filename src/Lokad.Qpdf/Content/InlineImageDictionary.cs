using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

/// <summary>Expands inline-image names while owning only fresh dictionary/name/array storage.</summary>
internal static class InlineImageDictionary
{
    internal static PdfDictionary Convert(PdfDictionary raw, PdfDictionary resources,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, AllocationCount(raw, governor, token), token);
        PdfDictionary result = Build(graph, raw, resources, diagnostics);
        graph.Retain(); return result;
    }

    internal static PdfDictionary Build(OwnedAllocationBatch graph, PdfDictionary raw, PdfDictionary resources, DiagnosticBag diagnostics)
        => Build(graph, raw, resources, diagnostics, null);

    internal static PdfDictionary Build(OwnedAllocationBatch graph, PdfDictionary raw, PdfDictionary? resources,
        DiagnosticBag diagnostics, IIndirectResolver? resolver)
        => BuildWithResources(graph, raw, resources, diagnostics, resolver);

    internal static PdfDictionary BuildWithResources(OwnedAllocationBatch graph, PdfDictionary raw, QpdfObject? resources,
        DiagnosticBag diagnostics, IIndirectResolver? resolver)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using PdfDictionary.EntryEnumerator source = raw.GetEntryEnumerator();
        while (source.MoveNext(governor, token))
        {
            PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
            if (value is null or PdfNull) { continue; }
            ReadOnlySpan<byte> expanded = KeyName(key, governor, token);
            if (IsShadowed(raw, expanded, governor, token)) { continue; }
            ReadOnlySpan<byte> name = expanded.IsEmpty ? key.RawSpan : expanded;
            if (Equal(name, "ColorSpace"u8, governor, token) && value is PdfName color)
            {
                ReadOnlySpan<byte> full = ColorName(color, governor, token);
                if (!full.IsEmpty) { value = PdfDictionaryRebuild.CreateName(graph, full); }
                else if (TryColorspace(resources as PdfDictionary, color, resolver, governor, token, out QpdfObject found))
                { value = found; }
                else
                {
                    if (resources is not PdfDictionary)
                    {
                        InlineImages.WarnDictionaryOperation(resources, false, diagnostics);
                        if (resources is PdfNull) { throw new QpdfParseException("Unable to resolve image colorspace through null Resources.", null, null); }
                    }
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Unable to resolve image colorspace; keeping the original name.");
                }
            }
            else if (Equal(name, "Filter"u8, governor, token)) { value = ExpandFilters(graph, value); }
            PdfName convertedKey = expanded.IsEmpty ? key : PdfDictionaryRebuild.CreateName(graph, expanded);
            entries.Assign(graph.Memory, governor, convertedKey, value, token);
        }
        PdfDictionary result = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        return result;
    }

    private static bool TryColorspace(PdfDictionary? resources, PdfName name, IIndirectResolver? resolver,
        ResourceGovernor governor, CancellationToken token, out QpdfObject value)
    {
        value = PdfNull.Instance;
        if (resources is null || !PdfDictionaryLookup.TryGet(resources, "ColorSpace"u8, governor, token, out QpdfObject raw)) { return false; }
        QpdfObject? target = raw;
        if (raw is PdfReference reference && resolver is not null)
        { PdfObjectLookup.TryResolve(reference, resolver, governor, token, out target); }
        return target is PdfDictionary spaces &&
            PdfDictionaryLookup.TryGet(spaces, name.RawSpan, governor, token, out value) && value is not PdfNull;
    }

    internal static int AllocationCount(PdfDictionary raw, ResourceGovernor governor, CancellationToken token)
    {
        int count = 2; // Fresh table and dictionary; the batch itself stays scoped.
        using PdfDictionary.EntryEnumerator source = raw.GetEntryEnumerator();
        while (source.MoveNext(governor, token))
        {
            PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
            if (value is null or PdfNull) { continue; }
            ReadOnlySpan<byte> expanded = KeyName(key, governor, token);
            if (IsShadowed(raw, expanded, governor, token)) { continue; }
            if (!expanded.IsEmpty) { count = checked(count + 2); }
            ReadOnlySpan<byte> name = expanded.IsEmpty ? key.RawSpan : expanded;
            if (Equal(name, "ColorSpace"u8, governor, token) && value is PdfName color && !ColorName(color, governor, token).IsEmpty)
            { count = checked(count + 2); }
            else if (Equal(name, "Filter"u8, governor, token))
            {
                int names = FilterAllocations(value, governor, token);
                count = checked(count + names + (names != 0 && value is PdfArray ? 2 : 0));
            }
        }
        return count;
    }

    private static bool IsShadowed(PdfDictionary raw, ReadOnlySpan<byte> expanded, ResourceGovernor governor, CancellationToken token)
        => !expanded.IsEmpty && PdfDictionaryLookup.TryGet(raw, expanded, governor, token, out QpdfObject canonical) && canonical is not PdfNull;

    private static int FilterAllocations(QpdfObject value, ResourceGovernor governor, CancellationToken token)
    {
        if (value is PdfName name) { return FilterName(name, governor, token).IsEmpty ? 0 : 2; }
        int count = 0;
        if (value is PdfArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                Step(governor, token);
                if (array[index] is PdfName item && !FilterName(item, governor, token).IsEmpty) { count = checked(count + 2); }
            }
        }
        return count;
    }

    private static QpdfObject ExpandFilters(OwnedAllocationBatch graph, QpdfObject value)
    {
        if (value is PdfName name)
        {
            ReadOnlySpan<byte> full = FilterName(name, graph.Governor, graph.Token);
            return full.IsEmpty ? value : PdfDictionaryRebuild.CreateName(graph, full);
        }
        if (value is not PdfArray array || FilterAllocations(value, graph.Governor, graph.Token) == 0) { return value; }
        long bytes = checked(32L + (long)IntPtr.Size * array.Count);
        graph.Memory.Reserve(bytes); graph.Governor.ChargeWork(array.Count); Step(graph.Governor, graph.Token);
        QpdfObject[] items = new QpdfObject[array.Count]; graph.Add(bytes);
        for (int index = 0; index < items.Length; index++)
        {
            Step(graph.Governor, graph.Token); QpdfObject item = array[index];
            if (item is PdfName filter)
            {
                ReadOnlySpan<byte> full = FilterName(filter, graph.Governor, graph.Token);
                if (!full.IsEmpty) { item = PdfDictionaryRebuild.CreateName(graph, full); }
            }
            items[index] = item;
        }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L); return result;
    }

    private static ReadOnlySpan<byte> KeyName(PdfName key, ResourceGovernor governor, CancellationToken token)
    {
        if (PdfDictionaryLookup.NameEquals(key, "BPC"u8, governor, token)) { return "BitsPerComponent"u8; }
        if (PdfDictionaryLookup.NameEquals(key, "CS"u8, governor, token)) { return "ColorSpace"u8; }
        if (PdfDictionaryLookup.NameEquals(key, "D"u8, governor, token)) { return "Decode"u8; }
        if (PdfDictionaryLookup.NameEquals(key, "DP"u8, governor, token)) { return "DecodeParms"u8; }
        if (PdfDictionaryLookup.NameEquals(key, "F"u8, governor, token)) { return "Filter"u8; }
        if (PdfDictionaryLookup.NameEquals(key, "H"u8, governor, token)) { return "Height"u8; }
        if (PdfDictionaryLookup.NameEquals(key, "IM"u8, governor, token)) { return "ImageMask"u8; }
        if (PdfDictionaryLookup.NameEquals(key, "I"u8, governor, token)) { return "Interpolate"u8; }
        if (PdfDictionaryLookup.NameEquals(key, "W"u8, governor, token)) { return "Width"u8; }
        return ReadOnlySpan<byte>.Empty;
    }

    private static ReadOnlySpan<byte> ColorName(PdfName name, ResourceGovernor governor, CancellationToken token)
    {
        if (PdfDictionaryLookup.NameEquals(name, "G"u8, governor, token)) { return "DeviceGray"u8; }
        if (PdfDictionaryLookup.NameEquals(name, "RGB"u8, governor, token)) { return "DeviceRGB"u8; }
        if (PdfDictionaryLookup.NameEquals(name, "CMYK"u8, governor, token)) { return "DeviceCMYK"u8; }
        if (PdfDictionaryLookup.NameEquals(name, "I"u8, governor, token)) { return "Indexed"u8; }
        return ReadOnlySpan<byte>.Empty;
    }

    private static ReadOnlySpan<byte> FilterName(PdfName name, ResourceGovernor governor, CancellationToken token)
    {
        if (PdfDictionaryLookup.NameEquals(name, "AHx"u8, governor, token)) { return "ASCIIHexDecode"u8; }
        if (PdfDictionaryLookup.NameEquals(name, "A85"u8, governor, token)) { return "ASCII85Decode"u8; }
        if (PdfDictionaryLookup.NameEquals(name, "LZW"u8, governor, token)) { return "LZWDecode"u8; }
        if (PdfDictionaryLookup.NameEquals(name, "Fl"u8, governor, token)) { return "FlateDecode"u8; }
        if (PdfDictionaryLookup.NameEquals(name, "RL"u8, governor, token)) { return "RunLengthDecode"u8; }
        if (PdfDictionaryLookup.NameEquals(name, "CCF"u8, governor, token)) { return "CCITTFaxDecode"u8; }
        if (PdfDictionaryLookup.NameEquals(name, "DCT"u8, governor, token)) { return "DCTDecode"u8; }
        return ReadOnlySpan<byte>.Empty;
    }

    private static bool Equal(ReadOnlySpan<byte> name, ReadOnlySpan<byte> expected, ResourceGovernor governor, CancellationToken token)
    {
        Step(governor, token); if (name.Length != expected.Length) { return false; }
        for (int index = 0; index < name.Length; index++) { Step(governor, token); if (name[index] != expected[index]) { return false; } }
        return true;
    }

    private static void Step(ResourceGovernor governor, CancellationToken token)
    { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L); }
}
