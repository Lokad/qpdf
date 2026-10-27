using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

/// <summary>Filters borrowed stream bytes and retains only fresh output storage.</summary>
internal static class StreamTransformer
{
    internal static byte Options(string decode, bool recompressFlate)
        => (byte)((decode switch { "none" => 0, "generalized" => 1, "specialized" => 2, "all" => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(decode)) }) | (recompressFlate ? 4 : 0));

    internal static string DecodeLevel(byte options)
        => (options & 3) switch { 1 => "generalized", 2 => "specialized", 3 => "all", _ => "none" };

    internal static PdfStream? Transform(PdfStream stream, int number, IIndirectResolver resolver,
        bool compress, string decode, bool recompressFlate, int? level, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        FilterPlan stages;
        try { stages = FilterChain.ResolveScoped(stream.Dictionary, resolver, governor, token); }
        catch (QpdfParseException)
        {
            Warn(governor, diagnostics, number, " kept opaque: unsupported filter."); return null;
        }
        using FilterPlan stageOwner = stages;
        if (stages.Count == 0 && (!compress || stream.RawMemory.Length == 0)) { return null; }
        if (stages.Count != 0 && stages[0].Name == FlateFilter.FilterName && !recompressFlate && compress)
        { return null; }
        if (compress && decode == "none") { decode = "generalized"; }
        foreach (FilterStage stage in stages)
        {
            if (FilterChain.StageInLevel(stage, decode)) { continue; }
            if (decode == "all") { Warn(governor, diagnostics, number, " kept opaque: unsupported filter."); }
            return null;
        }

        using ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor);
        byte[] input = stream.ArrayData(decodedMemory, governor, token);
        byte[] decoded = input;
        if (stages.Count != 0)
        {
            try { decoded = FilterChain.Decode(input, stages, limits, governor, decodedMemory, token); }
            catch (QpdfFilterException)
            {
                Warn(governor, diagnostics, number, " kept opaque: decode failed."); return null;
            }
        }
        stages.Dispose();
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        bool encoded = compress && decoded.Length != 0;
        byte[] data;
        if (encoded) { data = FlateFilter.Encode(decoded, level, limits, governor, graph.Memory, token); }
        else { data = decoded; decodedMemory.TransferTo(graph.Memory); }
        decodedMemory.Dispose();
        if (data.Length != 0) { graph.Add(checked(32L + data.Length)); }
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using PdfDictionary.EntryEnumerator source = stream.Dictionary.GetEntryEnumerator();
        while (source.MoveNext(governor, token))
        {
            PdfName key = source.Current.Key;
            if (PdfDictionaryLookup.NameEquals(key, "Filter"u8, governor, token)
                || PdfDictionaryLookup.NameEquals(key, "DecodeParms"u8, governor, token)
                || PdfDictionaryLookup.NameEquals(key, "Length"u8, governor, token)) { continue; }
            entries.Assign(graph.Memory, governor, key, source.Current.Value, token);
        }
        if (encoded)
        {
            PdfName key = PdfDictionaryRebuild.CreateName(graph, "Filter"u8);
            PdfName value = PdfDictionaryRebuild.CreateName(graph, "FlateDecode"u8);
            entries.Assign(graph.Memory, governor, key, value, token);
        }
        PdfDictionary header = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Memory.Reserve(32L);
        PdfStream result = PdfStream.FromOwnedData(header, data); graph.Add(32L);
        graph.Retain(); return result;
    }

    private static void Warn(ResourceGovernor governor, DiagnosticBag diagnostics, int number, string suffix)
    {
        using DisplayBuffer output = DisplayBuffer.Create(governor, true);
        output.AppendText("Stream "); output.AppendInteger(number); output.AppendText(suffix);
        using GovernedText message = output.Finish();
        diagnostics.AddOwned(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, message);
    }
}
