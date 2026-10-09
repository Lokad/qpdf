using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    private sealed class PreparedSplitPublications : IDisposable
    {
        private ParserMemoryScope? _memory;
        private List<PreparedSplitPublication>? _items;

        private PreparedSplitPublications(ParserMemoryScope memory, List<PreparedSplitPublication> items)
        { _memory = memory; _items = items; }

        internal ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(PreparedSplitPublications));
        internal List<PreparedSplitPublication> Items => _items ?? throw new ObjectDisposedException(nameof(PreparedSplitPublications));

        internal static PreparedSplitPublications Create(ResourceGovernor governor)
        {
            ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            try
            {
                memory.Reserve(32L);
                List<PreparedSplitPublication> items = ParserContainers.CreateList<PreparedSplitPublication>(memory);
                return new PreparedSplitPublications(memory, items);
            }
            catch { memory.Dispose(); throw; }
        }

        public void Dispose()
        {
            List<PreparedSplitPublication>? items = _items;
            ParserMemoryScope? memory = _memory;
            _items = null;
            _memory = null;
            items?.Clear();
            memory?.Dispose();
        }
    }

    private sealed record PreparedSplitPublication(QpdfPath Path, List<NumberedObject> Model,
        IReadOnlyList<IReadOnlyList<int>>? ObjectStreams, int? InfoRef,
        IReadOnlyDictionary<int, int> OriginalIds, long OutputBytes);

    private static async ValueTask<long> CountPresentationOutputAsync(PdfVersion version,
        List<NumberedObject> model, IReadOnlyList<IReadOnlyList<int>>? objectStreams, bool compressStreams,
        int? compressionLevel, int? infoRef, byte[]? originalId, bool staticId, bool newlineBeforeEndstream,
        QdfOptions qdf, IReadOnlyDictionary<int, int> originalIds, bool linearize, bool pass1,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        // Two counters and the serializer/writer wrappers; the shared writers own
        // their dynamic scratch. The identifier arrays are borrowed, never changed.
        memory.Reserve(linearize ? 128L : 96L);
        CountingSink counter = new();
        CountingSink? firstPass = pass1 ? new() : null;
        byte[] id2 = staticId ? StaticIdBytes : PlaceholderIdBytes;
        byte[] id1 = originalId ?? id2;
        if (linearize)
        {
            LinearizedWriter writer = new(limits, governor);
            if (objectStreams is not null && objectStreams.Count > 0)
            {
                await writer.WriteLinearizedStreamedAsync(version, model, objectStreams, infoRef, id1, id2,
                    newlineBeforeEndstream, compressStreams, compressionLevel, counter, firstPass, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteLinearizedAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream,
                    compressStreams, compressionLevel, counter, firstPass, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            PdfSerializer serializer = new(limits, governor);
            if (qdf.Enabled)
            { await serializer.SerializeQdfAsync(version, model, objectStreams, originalIds, infoRef, id1, id2,
                qdf, newlineBeforeEndstream, counter, cancellationToken).ConfigureAwait(false); }
            else if (objectStreams is null || objectStreams.Count == 0)
            { await serializer.SerializeAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream,
                counter, cancellationToken).ConfigureAwait(false); }
            else
            { await serializer.SerializeStreamedAsync(version, model, objectStreams, compressStreams,
                compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, counter, cancellationToken).ConfigureAwait(false); }
        }
        return checked(counter.Length + (firstPass?.Length ?? 0L));
    }
}
