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
            if (items is not null) { foreach (PreparedSplitPublication item in items) { item.Dispose(); } }
            items?.Clear();
            memory?.Dispose();
        }
    }

    private sealed class PreparedSplitPublication : IDisposable
    {
        private ParserMemoryScope? _memory;
        private PreparedOutputSink? _main, _pass1;
        internal readonly QpdfPath Path;
        internal readonly QpdfPath? Pass1Path;

        private PreparedSplitPublication(ParserMemoryScope memory, PreparedOutputSink main, PreparedOutputSink? pass1,
            QpdfPath path, QpdfPath? pass1Path)
        { _memory = memory; _main = main; _pass1 = pass1; Path = path; Pass1Path = pass1Path; }

        internal static PreparedSplitPublication Create(QpdfLimits limits, ResourceGovernor governor,
            QpdfPath path, QpdfPath? pass1Path, CancellationToken token)
        {
            ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            PreparedOutputSink? main = null, pass1 = null;
            try
            {
                memory.Reserve(64L);
                main = PreparedOutputSink.Create(limits, governor, token);
                if (pass1Path.HasValue) { pass1 = PreparedOutputSink.Create(limits, governor, token); }
                return new(memory, main, pass1, path, pass1Path);
            }
            catch { pass1?.Dispose(); main?.Dispose(); memory.Dispose(); throw; }
        }

        internal PreparedOutputSink Main => _main ?? throw new ObjectDisposedException(nameof(PreparedSplitPublication));
        internal PreparedOutputSink? Pass1 => _pass1;
        internal long PublicationOperations => 1L + Main.SegmentCount
            + (_pass1 is null ? 0L : 1L + _pass1.SegmentCount);
        internal long PublicationCapacity => Math.Max(Main.Length, _pass1?.Length ?? 0L);

        internal async ValueTask PublishAsync(IQpdfHost host, ResourceGovernor governor, CancellationToken token)
        {
            await PublishPreparedOutputAsync(host, Path, Main, governor, token).ConfigureAwait(false);
            if (_pass1 is PreparedOutputSink pass1 && Pass1Path is QpdfPath path)
            { await PublishPreparedOutputAsync(host, path, pass1, governor, token).ConfigureAwait(false); }
        }

        public void Dispose()
        {
            PreparedOutputSink? main = _main, pass1 = _pass1;
            _main = null; _pass1 = null;
            pass1?.Dispose(); main?.Dispose();
            ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
        }
    }

    private static async ValueTask<PreparedSplitPublication> PreparePresentationOutputAsync(PdfVersion version,
        List<NumberedObject> model, IReadOnlyList<IReadOnlyList<int>>? objectStreams, bool compressStreams,
        int? compressionLevel, int? infoRef, byte[]? originalId, bool staticId, bool newlineBeforeEndstream,
        QdfOptions qdf, IReadOnlyDictionary<int, int>? originalIds, bool linearize, QpdfPath? pass1, QpdfPath path,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        PreparedSplitPublication publication = PreparedSplitPublication.Create(limits, governor, path, linearize ? pass1 : null, cancellationToken);
        try
        {
            using ParserMemoryScope ids = ParserMemoryScope.Create(governor);
            byte[] id2 = StaticIdBytes;
            if (!staticId)
            {
                using IdentifierHashSink placeholder = IdentifierHashSink.Create(limits, governor, cancellationToken);
                await SerializePresentationOutputAsync(version, model, objectStreams, compressStreams, compressionLevel,
                    infoRef, originalId ?? PlaceholderIdBytes, PlaceholderIdBytes, newlineBeforeEndstream, qdf,
                    originalIds, linearize, placeholder, null, limits, governor, cancellationToken).ConfigureAwait(false);
                ids.Reserve(40L);
                id2 = placeholder.Complete(cancellationToken);
            }
            await SerializePresentationOutputAsync(version, model, objectStreams, compressStreams, compressionLevel,
                infoRef, originalId ?? id2, id2, newlineBeforeEndstream, qdf, originalIds, linearize,
                publication.Main, publication.Pass1, limits, governor, cancellationToken).ConfigureAwait(false);
            governor.ChargeOutputBytes(checked(publication.Main.Length + (publication.Pass1?.Length ?? 0L)));
            return publication;
        }
        catch { publication.Dispose(); throw; }
    }

    private static async ValueTask SerializePresentationOutputAsync(PdfVersion version,
        List<NumberedObject> model, IReadOnlyList<IReadOnlyList<int>>? objectStreams, bool compressStreams,
        int? compressionLevel, int? infoRef, byte[] id1, byte[] id2, bool newlineBeforeEndstream,
        QdfOptions qdf, IReadOnlyDictionary<int, int>? originalIds, bool linearize, IByteSink counter, IByteSink? firstPass,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        // Two counters and the serializer/writer wrappers; the shared writers own
        // their dynamic scratch. The identifier arrays are borrowed, never changed.
        memory.Reserve(linearize ? 128L : 96L);
        using BatchingSink buffered = BatchingSink.Create(counter, limits, governor, cancellationToken);
        using BatchingSink? bufferedFirstPass = firstPass is null ? null
            : BatchingSink.Create(firstPass, limits, governor, cancellationToken);
        if (linearize)
        {
            LinearizedWriter writer = new(limits, governor);
            if (objectStreams is not null && objectStreams.Count > 0)
            {
                await writer.WriteLinearizedStreamedAsync(version, model, objectStreams, infoRef, id1, id2,
                    newlineBeforeEndstream, compressStreams, compressionLevel, buffered, bufferedFirstPass, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteLinearizedAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream,
                    compressStreams, compressionLevel, buffered, bufferedFirstPass, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            PdfSerializer serializer = new(limits, governor);
            if (qdf.Enabled)
            { await serializer.SerializeQdfAsync(version, model, objectStreams, originalIds, infoRef, id1, id2,
                qdf, newlineBeforeEndstream, buffered, cancellationToken).ConfigureAwait(false); }
            else if (objectStreams is null || objectStreams.Count == 0)
            { await serializer.SerializeAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream,
                buffered, cancellationToken).ConfigureAwait(false); }
            else
            { await serializer.SerializeStreamedAsync(version, model, objectStreams, compressStreams,
                compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, buffered, cancellationToken).ConfigureAwait(false); }
        }
        // Identifiers, publication lengths and segment counts are consumed by
        // the caller only after both captures contain their complete bytes.
        await buffered.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (bufferedFirstPass is not null)
        { await bufferedFirstPass.FlushAsync(cancellationToken).ConfigureAwait(false); }
    }

    private static async ValueTask PublishPreparedOutputAsync(IQpdfHost host, QpdfPath path, PreparedOutputSink output,
        ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, path, governor, token).ConfigureAwait(false);
        // The batch checked local staging policy; the host admits actual storage.
        GovernedHost.PrepareOutputCapacity(transaction, output.Length, token);
        for (PreparedOutputSink.Segment? segment = output.First; segment is not null; segment = segment.Next)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            await transaction.WriteAsync(segment.Written, token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static async ValueTask PublishPreparedBytesAsync(IQpdfHost host, QpdfPath path, Json.JsonPayload payload,
        ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, path, governor, token).ConfigureAwait(false);
        ReadOnlyMemory<byte> bytes = payload.Data;
        for (int offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int count = Math.Min(32768, bytes.Length - offset);
            await transaction.WriteAsync(bytes.Slice(offset, count), token).ConfigureAwait(false);
            offset += count;
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
}
