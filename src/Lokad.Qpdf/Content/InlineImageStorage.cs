using System.Globalization;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal readonly record struct InlineImageEntry(PdfName Name, PdfStream Stream);

/// <summary>Constructs image candidates through the shared parser and allocation handoff.</summary>
internal static class InlineImageStorage
{
    internal static InlineImageEntry? ReadWithResources(ReadOnlyMemory<byte> dictionaryBytes, ContentTokenizer tokenizer,
        long minimum, long offset, QpdfObject? resources, bool canAttach, ref int suffix, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, PoolResolver resolver, CancellationToken token)
    {
        Step(governor, token);
        using ParserMemoryScope parsedMemory = ParserMemoryScope.Create(governor);
        PdfDictionary raw = Parse(dictionaryBytes, offset, limits, governor, diagnostics, resolver, parsedMemory, token);
        tokenizer.TryReadInlineImageData(SampleLength(raw, resources, resolver, governor, token), out ContentToken image);
        ReadOnlyMemory<byte> imageBytes = image.Data;
        if (imageBytes.Length < minimum) { return null; }
        if (!canAttach)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning,
                "Inline image kept because its XObject resources are unavailable.");
            return null;
        }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary? converted = InlineImageDictionary.BuildWithResources(graph, raw, resources, diagnostics, resolver);
        if (converted is null) { return null; }
        byte[] data = Copy(imageBytes.Span, graph.Memory, governor, token);
        if (data.Length != 0) { graph.Add(checked(32L + data.Length)); }
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        AddDefault(graph, entries, converted, "Type"u8, "XObject"u8);
        AddDefault(graph, entries, converted, "Subtype"u8, "Image"u8);
        using PdfDictionary.EntryEnumerator source = converted.GetEntryEnumerator();
        while (source.MoveNext(governor, token)) { entries.Assign(graph.Memory, governor, source.Current.Key, source.Current.Value, token); }
        PdfDictionary header = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Memory.Reserve(32L); PdfStream stream = PdfStream.FromOwnedData(header, data); graph.Add(32L);
        PdfName name = Name(graph, resources as PdfDictionary, resolver, ref suffix);
        graph.RetainParsed(parsedMemory);
        return new(name, stream);
    }

    private static QpdfObject? Value(PdfDictionary dictionary, ReadOnlySpan<byte> key, ReadOnlySpan<byte> alias,
        PoolResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, key, governor, token, out QpdfObject raw) || raw is PdfNull)
        { if (!PdfDictionaryLookup.TryGet(dictionary, alias, governor, token, out raw)) { return null; } }
        return CatalogCopy.ResolveValue(raw, resolver, governor, token);
    }

    // Only an unfiltered image has a sample extent equal to its encoded extent.
    // Row padding belongs to each row, rather than to the whole image.
    private static long? SampleLength(PdfDictionary raw, QpdfObject? resources, PoolResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        QpdfObject? filter = Value(raw, "Filter"u8, "F"u8, resolver, governor, token);
        if (filter is not null and not PdfNull && (filter is not PdfArray filters || filters.Count != 0)) { return null; }
        if (Value(raw, "Width"u8, "W"u8, resolver, governor, token) is not PdfInteger width || width.Value <= 0
            || Value(raw, "Height"u8, "H"u8, resolver, governor, token) is not PdfInteger height || height.Value <= 0) { return null; }
        bool mask = Value(raw, "ImageMask"u8, "IM"u8, resolver, governor, token) is PdfBoolean { Value: true };
        QpdfObject? depth = Value(raw, "BitsPerComponent"u8, "BPC"u8, resolver, governor, token);
        long bits = depth is PdfInteger integer ? integer.Value : mask && (depth is null or PdfNull) ? 1L : 0L;
        if (bits is not (1 or 2 or 4 or 8 or 16) || mask && bits != 1) { return null; }
        int components = mask ? 1 : Components(Value(raw, "ColorSpace"u8, "CS"u8, resolver, governor, token), resources, resolver, governor, token);
        if (components <= 0) { return null; }
        try { return checked(((checked(width.Value * components * bits) + 7L) / 8L) * height.Value); }
        catch (OverflowException) { return null; }
    }

    private static int Components(QpdfObject? color, QpdfObject? resources, PoolResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (color is PdfName name)
        {
            if (name.RawSpan.SequenceEqual("G"u8) || name.RawSpan.SequenceEqual("DeviceGray"u8)) { return 1; }
            if (name.RawSpan.SequenceEqual("RGB"u8) || name.RawSpan.SequenceEqual("DeviceRGB"u8)) { return 3; }
            if (name.RawSpan.SequenceEqual("CMYK"u8) || name.RawSpan.SequenceEqual("DeviceCMYK"u8)) { return 4; }
            if (resources is not PdfDictionary dictionary
                || !PdfDictionaryLookup.TryGet(dictionary, "ColorSpace"u8, governor, token, out QpdfObject spacesRaw)
                || CatalogCopy.ResolveDictionary(spacesRaw, resolver, governor, token) is not PdfDictionary spaces
                || !PdfDictionaryLookup.TryGet(spaces, name.RawSpan, governor, token, out QpdfObject raw)) { return 0; }
            color = CatalogCopy.ResolveValue(raw, resolver, governor, token);
            if (color is PdfName resolved)
            {
                if (resolved.RawSpan.SequenceEqual("DeviceGray"u8)) { return 1; }
                if (resolved.RawSpan.SequenceEqual("DeviceRGB"u8)) { return 3; }
                if (resolved.RawSpan.SequenceEqual("DeviceCMYK"u8)) { return 4; }
            }
        }
        if (color is not PdfArray array || array.Count == 0
            || CatalogCopy.ResolveValue(array[0], resolver, governor, token) is not PdfName family) { return 0; }
        ReadOnlySpan<byte> kind = family.RawSpan;
        if (kind.SequenceEqual("Indexed"u8) || kind.SequenceEqual("I"u8) || kind.SequenceEqual("Separation"u8) || kind.SequenceEqual("CalGray"u8)) { return 1; }
        if (kind.SequenceEqual("CalRGB"u8) || kind.SequenceEqual("Lab"u8)) { return 3; }
        if (array.Count < 2) { return 0; }
        QpdfObject? value = CatalogCopy.ResolveValue(array[1], resolver, governor, token);
        if (kind.SequenceEqual("DeviceN"u8) && value is PdfArray names) { return names.Count; }
        if (kind.SequenceEqual("ICCBased"u8) && value is PdfStream profile
            && PdfDictionaryLookup.TryGet(profile.Dictionary, "N"u8, governor, token, out QpdfObject count)
            && CatalogCopy.ResolveValue(count, resolver, governor, token) is PdfInteger channels && channels.Value is 1 or 3 or 4) { return (int)channels.Value; }
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PdfDictionary Parse(ReadOnlyMemory<byte> bytes, long offset, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, PoolResolver resolver, ParserMemoryScope parsedMemory, CancellationToken token)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        int length = checked(bytes.Length + 6); scratch.Reserve(checked(32L + length + 224L));
        governor.NoteOperation(); Step(governor, token);
        byte[] synthetic = new byte[length];
        "<< "u8.CopyTo(synthetic); " >>"u8.CopyTo(synthetic.AsSpan(length - 3));
        GovernedBufferStream.CopyBytes(bytes.Span, synthetic.AsSpan(3, bytes.Length), governor, token, CancellationToken.None);
        PdfParser parser = new(limits, governor, diagnostics, resolver, token) { RejectIndirectReferences = true };
        PdfTokenizer tokenizer = new(synthetic, token);
        QpdfObject parsed = parser.ParseValue(tokenizer, 0, parsedMemory);
        return parsed as PdfDictionary ?? throw new QpdfParseException("Inline image dictionary must be a dictionary.", offset, null);
    }

    private static bool HasValue(PdfDictionary dictionary, ReadOnlySpan<byte> key, ResourceGovernor governor, CancellationToken token)
        => PdfDictionaryLookup.TryGet(dictionary, key, governor, token, out QpdfObject value) && value is not PdfNull;

    private static void AddDefault(OwnedAllocationBatch graph, PdfNameTable<QpdfObject> entries, PdfDictionary converted,
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (HasValue(converted, key, graph.Governor, graph.Token)) { return; }
        PdfName name = PdfDictionaryRebuild.CreateName(graph, key), target = PdfDictionaryRebuild.CreateName(graph, value);
        entries.Assign(graph.Memory, graph.Governor, name, target, graph.Token);
    }

    private static PdfName Name(OwnedAllocationBatch graph, PdfDictionary? resources, PoolResolver resolver, ref int suffix)
    {
        Span<byte> bytes = stackalloc byte[13]; "IIm"u8.CopyTo(bytes);
        while (true)
        {
            Step(graph.Governor, graph.Token);
            if (suffix < 1 || suffix == int.MaxValue) { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), (long)suffix + 1L, int.MaxValue); }
            if (!suffix.TryFormat(bytes[3..], out int digits, provider: CultureInfo.InvariantCulture)) { throw new InvalidOperationException("Inline image suffix does not fit."); }
            suffix = checked(suffix + 1); ReadOnlySpan<byte> candidate = bytes[..(3 + digits)];
            if (!HasResourceName(resources, candidate, resolver, graph.Governor, graph.Token))
            { return PdfDictionaryRebuild.CreateName(graph, candidate); }
        }
    }

    private static bool HasResourceName(PdfDictionary? resources, ReadOnlySpan<byte> name, PoolResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (resources is null) { return false; }
        using PdfDictionary.EntryEnumerator categories = resources.GetEntryEnumerator();
        while (categories.MoveNext(governor, token))
        {
            QpdfObject? target = categories.Current.Value;
            if (target is PdfReference reference)
            { PdfObjectLookup.TryResolve(reference, resolver, governor, token, out target); }
            if (target is PdfDictionary entries && PdfDictionaryLookup.TryGet(entries, name, governor, token, out QpdfObject value) && value is not PdfNull)
            { return true; }
        }
        return false;
    }

    private static byte[] Copy(ReadOnlySpan<byte> value, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (value.IsEmpty) { return Array.Empty<byte>(); }
        memory.Reserve(checked(32L + value.Length)); governor.NoteOperation(); Step(governor, token);
        byte[] result = new byte[value.Length];
        GovernedBufferStream.CopyBytes(value, result, governor, token, CancellationToken.None); return result;
    }
    private static void Step(ResourceGovernor governor, CancellationToken token)
    { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
}

/// <summary>Owns rewrite bytes and candidate collection metadata until graph publication.</summary>
internal sealed class InlineImageBuffer : IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private GovernedBufferStream? _output;
    private List<InlineImageEntry>? _images;
    private CancellationToken _token;

    private InlineImageBuffer(ParserMemoryScope memory, ResourceGovernor governor, GovernedBufferStream output, CancellationToken token)
    { _memory = memory; _governor = governor; _output = output; _token = token; _images = ParserContainers.CreateList<InlineImageEntry>(memory); }
    internal static InlineImageBuffer Create(ResourceGovernor governor, QpdfLimits limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedBufferStream? output = null;
        try
        {
            memory.Reserve(200L);
            output = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes), token);
            return new(memory, governor, output, token);
        }
        catch { output?.Dispose(); memory.Dispose(); throw; }
    }
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(InlineImageBuffer));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(InlineImageBuffer));
    private GovernedBufferStream Output => _output ?? throw new ObjectDisposedException(nameof(InlineImageBuffer));
    private List<InlineImageEntry> Images => _images ?? throw new ObjectDisposedException(nameof(InlineImageBuffer));
    internal int Count => Images.Count;
    internal InlineImageEntry Get(int index) { Step(); return Images[index]; }
    internal void Add(InlineImageEntry image)
    {
        Step();
        if (Images.Count == Images.Capacity) { Governor.NoteOperation(); Step(); }
        ParserContainers.AddItem(Memory, Images, image, 16L);
    }
    internal void Write(ReadOnlySpan<byte> bytes) => Output.Write(bytes, _token);
    internal void WriteImage(PdfName name) { Write("/"u8); Write(name.RawSpan); Write(" Do\n"u8); }
    internal PdfStream Complete(bool compress, int? level, QpdfLimits limits)
    {
        ResourceGovernor governor = Governor; CancellationToken token = _token;
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        bool encoded = compress && Output.Length > 0;
        byte[] data = encoded ? Encode(graph, level, limits) : Output.ToArray(graph.Memory, token);
        if (data.Length != 0) { graph.Add(checked(32L + data.Length)); }
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        if (encoded)
        {
            PdfName key = PdfDictionaryRebuild.CreateName(graph, "Filter"u8), value = PdfDictionaryRebuild.CreateName(graph, "FlateDecode"u8);
            entries.Assign(graph.Memory, governor, key, value, token);
        }
        PdfDictionary dictionary = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Memory.Reserve(32L); PdfStream stream = PdfStream.FromOwnedData(dictionary, data); graph.Add(32L);
        graph.Retain(); return stream;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[] Encode(OwnedAllocationBatch graph, int? level, QpdfLimits limits)
    {
        using ParserMemoryScope rawMemory = ParserMemoryScope.Create(Governor);
        byte[] raw = Output.ToArray(rawMemory, _token);
        return FlateFilter.Encode(raw, level, limits, Governor, graph.Memory, _token);
    }
    private void Step() { _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); }
    public void Dispose()
    {
        ParserMemoryScope? memory = _memory; GovernedBufferStream? output = _output; List<InlineImageEntry>? images = _images;
        _memory = null; _output = null; _images = null; _governor = null; _token = default;
        images?.Clear(); output?.Dispose(); memory?.Dispose();
    }
}
