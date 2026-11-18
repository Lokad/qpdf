using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;

namespace Lokad.Qpdf.Writing;

internal static class StreamPreparation
{
    internal static (bool Compress, string Decode, int? Level) ResolveCompression(CompressionRequest request, bool qdf)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.StreamData is not null && request.StreamData != "compress" && request.StreamData != "preserve" && request.StreamData != "uncompress")
        {
            throw new QpdfParseException("--stream-data must be compress, preserve, or uncompress.", 0L, null);
        }

        if (request.CompressStreams is not null && request.CompressStreams != "y" && request.CompressStreams != "n")
        {
            throw new QpdfParseException("--compress-streams must be y or n.", 0L, null);
        }

        if (request.DecodeLevel is not null && request.DecodeLevel != "none" && request.DecodeLevel != "generalized" && request.DecodeLevel != "specialized" && request.DecodeLevel != "all")
        {
            throw new QpdfParseException("--decode-level must be none, generalized, specialized, or all.", 0L, null);
        }

        int? level = request.CompressionLevel is null ? null : CompressionRequest.ParseLevel(request.CompressionLevel, "--compression-level");
        bool compress = request.CompressStreams is not null
            ? request.CompressStreams == "y"
            : request.StreamData == "compress";
        string decode = request.DecodeLevel
            ?? ((request.StreamData == "compress" || request.StreamData == "uncompress") ? "generalized" : (qdf ? "generalized" : "none"));
        // Compression asks the writer to filter generalized encodings even at decode level none.
        if (compress && decode == "none") { decode = "generalized"; }
        return (compress, decode, level);
    }

    internal static void TransformCopierStreams(
        GraphCopier copier,
        DocumentEngine engine,
        bool compress,
        string decode,
        bool recompressFlate,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        PdfReference? metadata)
    {
        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        Content.PoolResolver resolver = copier.CreateResolver(scratch);
        List<int> ids = ParserContainers.CreateList<int>(scratch);
        foreach (int id in copier.CopiedObjects.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserContainers.AddItem(scratch, ids, id, sizeof(int));
        }
        foreach (int id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!copier.CopiedObjects.TryGetValue(id, out QpdfObject? value) || value is not PdfStream stream)
            {
                continue;
            }

            QpdfObject? replacement = TransformForOutput(stream, id, resolver, metadata?.ObjectNumber ?? 0,
                compress, decode, recompressFlate, level, limits, governor, diagnostics, cancellationToken);
            if (replacement is not null)
            {
                copier.ReplaceCopiedObject(id, replacement);
            }
        }
    }

    internal static void TransformNumberedStreams(List<NumberedObject> model, IIndirectResolver resolver, int metadata,
        CompressionRequest request, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        (bool compress, string decode, int? level) = ResolveCompression(request, false);
        if (!compress && decode == "none" && metadata == 0) { return; }
        for (int i = 0; i < model.Count; i++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            NumberedObject item = model[i];
            if (item.Value is not PdfStream stream || (!compress && decode == "none" && item.Number != metadata)) { continue; }
            PdfStream? replacement = TransformForOutput(stream, item.Number, resolver, metadata,
                compress, decode, request.RecompressFlate, level, limits, governor, diagnostics, token);
            if (replacement is not null) { model[i] = new(item.Number, replacement); }
        }
    }

    private static PdfStream? TransformForOutput(PdfStream stream, int id, IIndirectResolver resolver, int metadata,
        bool compress, string decode, bool recompressFlate, int? level, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken token)
    {
        bool xmlMetadata = metadata == id
            && PdfDictionaryLookup.HasName(stream.Dictionary, "Type"u8, "Metadata"u8, resolver, governor, token)
            && PdfDictionaryLookup.HasName(stream.Dictionary, "Subtype"u8, "XML"u8, resolver, governor, token);
        return StreamTransformer.Transform(stream, id, resolver, !xmlMetadata && compress,
            xmlMetadata ? "all" : decode, recompressFlate, level, limits, governor, diagnostics, token);
    }

}
