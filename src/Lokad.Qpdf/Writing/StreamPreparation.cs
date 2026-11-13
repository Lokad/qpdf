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
        Content.PoolResolver resolver = Content.PoolResolver.Create(scratch, copier.CopiedObjects);
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

            bool xmlMetadata = metadata?.ObjectNumber == id
                && PdfDictionaryLookup.HasName(stream.Dictionary, "Type"u8, "Metadata"u8, resolver, governor, cancellationToken)
                && PdfDictionaryLookup.HasName(stream.Dictionary, "Subtype"u8, "XML"u8, resolver, governor, cancellationToken);
            QpdfObject? replacement = StreamTransformer.Transform(stream, id, resolver, xmlMetadata ? false : compress,
                xmlMetadata ? "all" : decode, recompressFlate, level, limits, governor, diagnostics, cancellationToken);
            if (replacement is not null)
            {
                copier.ReplaceCopiedObject(id, replacement);
            }
        }
    }

}
