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
    internal static InlineImageEntry? Create(ReadOnlyMemory<byte> dictionaryBytes, ReadOnlyMemory<byte> imageBytes,
        long minimum, long offset, PdfDictionary resources, ref int suffix, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, PoolResolver resolver, CancellationToken token)
    {
        Step(governor, token);
        using ParserMemoryScope dataMemory = ParserMemoryScope.Create(governor);
        byte[] data = Copy(imageBytes.Span, dataMemory, governor, token);
        if (data.Length < minimum) { return null; }
        using ParserMemoryScope parsedMemory = ParserMemoryScope.Create(governor);
        PdfDictionary raw = Parse(dictionaryBytes, offset, limits, governor, diagnostics, resolver, parsedMemory, token);
        int defaults = (HasValue(raw, "Type"u8, governor, token) ? 0 : 4) + (HasValue(raw, "Subtype"u8, governor, token) ? 0 : 4);
        int capacity = checked(InlineImageDictionary.AllocationCount(raw, governor, token) + defaults + 5 + (data.Length == 0 ? 0 : 1));
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, capacity, token);
        dataMemory.TransferTo(graph.Memory);
        if (data.Length != 0) { graph.Add(data, checked(32L + data.Length)); }
        PdfDictionary converted = InlineImageDictionary.Build(graph, raw, resources, diagnostics);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out int allocation);
        AddDefault(graph, entries, converted, "Type"u8, "XObject"u8);
        AddDefault(graph, entries, converted, "Subtype"u8, "Image"u8);
        using PdfDictionary.EntryEnumerator source = converted.GetEntryEnumerator();
        while (source.MoveNext(governor, token)) { entries.Assign(graph.Memory, governor, source.Current.Key, source.Current.Value, token); }
        PdfDictionary header = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Memory.Reserve(32L); PdfStream stream = PdfStream.FromOwnedData(header, data); graph.Add(stream, 32L);
        PdfName name = Name(graph, resources, ref suffix);
        graph.RetainParsed(raw, parsedMemory);
        return new(name, stream);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PdfDictionary Parse(ReadOnlyMemory<byte> bytes, long offset, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, PoolResolver resolver, ParserMemoryScope parsedMemory, CancellationToken token)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        int length = checked(bytes.Length + 6); scratch.Reserve(checked(32L + length + 224L));
        governor.ChargeWork(length); Step(governor, token);
        byte[] synthetic = new byte[length];
        "<< "u8.CopyTo(synthetic); " >>"u8.CopyTo(synthetic.AsSpan(length - 3));
        GovernedBufferStream.CopyBytes(bytes.Span, synthetic.AsSpan(3, bytes.Length), governor, token, CancellationToken.None);
        PdfParser parser = new(limits, governor, diagnostics, resolver, token);
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

    private static PdfName Name(OwnedAllocationBatch graph, PdfDictionary resources, ref int suffix)
    {
        PdfDictionary? xobjects = PdfDictionaryLookup.TryGet(resources, "XObject"u8, graph.Governor, graph.Token, out QpdfObject raw)
            ? raw as PdfDictionary : null;
        Span<byte> bytes = stackalloc byte[13]; "IIm"u8.CopyTo(bytes);
        while (true)
        {
            Step(graph.Governor, graph.Token);
            if (suffix < 1 || suffix == int.MaxValue) { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), (long)suffix + 1L, int.MaxValue); }
            if (!suffix.TryFormat(bytes[3..], out int digits, provider: CultureInfo.InvariantCulture)) { throw new InvalidOperationException("Inline image suffix does not fit."); }
            suffix = checked(suffix + 1); ReadOnlySpan<byte> candidate = bytes[..(3 + digits)];
            if (xobjects is null || !PdfDictionaryLookup.Contains(xobjects, candidate, graph.Governor, graph.Token))
            { return PdfDictionaryRebuild.CreateName(graph, candidate); }
        }
    }

    private static byte[] Copy(ReadOnlySpan<byte> value, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (value.IsEmpty) { return Array.Empty<byte>(); }
        memory.Reserve(checked(32L + value.Length)); governor.ChargeWork(value.Length); Step(governor, token);
        byte[] result = new byte[value.Length];
        GovernedBufferStream.CopyBytes(value, result, governor, token, CancellationToken.None); return result;
    }
    private static void Step(ResourceGovernor governor, CancellationToken token)
    { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L); }
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
        if (Images.Count == Images.Capacity) { Governor.ChargeWork(checked(Images.Count + Math.Max(4L, Images.Capacity * 2L))); Step(); }
        ParserContainers.AddItem(Memory, Images, image, 16L);
    }
    internal void Write(ReadOnlySpan<byte> bytes) => Output.Write(bytes, _token);
    internal void WriteImage(PdfName name) { Write("/"u8); Write(name.RawSpan); Write(" Do\n"u8); }
    internal PdfStream Complete(bool compress, int? level, QpdfLimits limits)
    {
        ResourceGovernor governor = Governor; CancellationToken token = _token;
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 8, token);
        bool encoded = compress && Output.Length > 0;
        byte[] data = encoded ? Encode(graph, level, limits) : Output.ToArray(graph.Memory, token);
        if (data.Length != 0) { graph.Add(data, checked(32L + data.Length)); }
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out int allocation);
        if (encoded)
        {
            PdfName key = PdfDictionaryRebuild.CreateName(graph, "Filter"u8), value = PdfDictionaryRebuild.CreateName(graph, "FlateDecode"u8);
            entries.Assign(graph.Memory, governor, key, value, token);
        }
        PdfDictionary dictionary = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Memory.Reserve(32L); PdfStream stream = PdfStream.FromOwnedData(dictionary, data); graph.Add(stream, 32L);
        graph.Retain(); return stream;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[] Encode(OwnedAllocationBatch graph, int? level, QpdfLimits limits)
    {
        using ParserMemoryScope rawMemory = ParserMemoryScope.Create(Governor);
        byte[] raw = Output.ToArray(rawMemory, _token);
        return FlateFilter.Encode(raw, level, limits, Governor, graph.Memory, _token);
    }
    private void Step() { _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); Governor.ChargeWork(1L); }
    public void Dispose()
    {
        ParserMemoryScope? memory = _memory; GovernedBufferStream? output = _output; List<InlineImageEntry>? images = _images;
        _memory = null; _output = null; _images = null; _governor = null; _token = default;
        images?.Clear(); output?.Dispose(); memory?.Dispose();
    }
}
