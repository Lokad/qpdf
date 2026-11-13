using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Writing;

// Borrows the prepared model and format choices for counting, identifier replay
// and final output. Transactions, staging and security owners stay with callers.
internal readonly record struct OutputSerialization(PdfVersion Version, List<NumberedObject> Model,
    IReadOnlyList<IReadOnlyList<int>>? ObjectStreams, bool CompressStreams, int? CompressionLevel,
    int? Info, bool NewlineBeforeEndstream, QdfOptions Qdf, IReadOnlyDictionary<int, int>? OriginalIds)
{
    internal ValueTask<long> WriteAsync(PdfSerializer writer, byte[] id1, byte[] id2,
        IByteSink destination, CancellationToken token)
    {
        if (Qdf.Enabled)
        { return writer.SerializeQdfAsync(Version, Model, ObjectStreams, OriginalIds, Info, id1, id2,
            Qdf, NewlineBeforeEndstream, destination, token); }
        if (ObjectStreams is null || ObjectStreams.Count == 0)
        { return writer.SerializeAsync(Version, Model, Info, id1, id2, NewlineBeforeEndstream, destination, token); }
        return writer.SerializeStreamedAsync(Version, Model, ObjectStreams, CompressStreams, CompressionLevel,
            Info, id1, id2, NewlineBeforeEndstream, destination, token);
    }

    internal ValueTask<long> WriteLinearizedAsync(LinearizedWriter writer, byte[] id1, byte[] id2,
        IByteSink destination, IByteSink? firstPass, CancellationToken token)
    {
        if (ObjectStreams is not null && ObjectStreams.Count > 0)
        { return writer.WriteLinearizedStreamedAsync(Version, Model, ObjectStreams, Info, id1, id2,
            NewlineBeforeEndstream, CompressStreams, CompressionLevel, destination, firstPass, token); }
        return writer.WriteLinearizedAsync(Version, Model, Info, id1, id2, NewlineBeforeEndstream,
            CompressStreams, CompressionLevel, destination, firstPass, token);
    }
}
