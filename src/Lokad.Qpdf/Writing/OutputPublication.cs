using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using System.Security.Cryptography;
using static Lokad.Qpdf.Writing.OutputSecurityPreparation;

namespace Lokad.Qpdf.Writing;

internal static class OutputPublication
{
    internal static readonly byte[] StaticIdBytes = new byte[] { 0x31, 0x41, 0x59, 0x26, 0x53, 0x58, 0x97, 0x93, 0x23, 0x84, 0x62, 0x64, 0x33, 0x83, 0x27, 0x95 };

    internal static readonly byte[] PlaceholderIdBytes = new byte[16];

    internal sealed class IdentifierHashSink : ISynchronousByteSink, IDisposable
    {
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;
        private ParserMemoryScope? _memory;
        private IncrementalHash? _hash;
        private long _length;

        private IdentifierHashSink(QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, IncrementalHash hash)
        {
            _limits = limits; _governor = governor; _memory = memory; _hash = hash;
        }

        internal static IdentifierHashSink Create(QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(governor);
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            IncrementalHash? hash = null;
            try
            {
                // A fixed allowance for the hash context and its small result.
                memory.Reserve(192L);
                hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                return new(limits, governor, memory, hash);
            }
            catch { hash?.Dispose(); memory.Dispose(); throw; }
        }

        public long Length => _length;

        public bool CanAppendSynchronously => true;

        internal byte[] Complete(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            return (_hash ?? throw new ObjectDisposedException(nameof(IdentifierHashSink))).GetHashAndReset();
        }

        internal void AdmitOutputMirror()
            => (_memory ?? throw new ObjectDisposedException(nameof(IdentifierHashSink))).Reserve(64L);

        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            Append(chunk.Span, cancellationToken);
            return ValueTask.CompletedTask;
        }

        public void Append(ReadOnlySpan<byte> chunk, CancellationToken cancellationToken)
        {
            IncrementalHash hash = _hash ?? throw new ObjectDisposedException(nameof(IdentifierHashSink));
            cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            long next = checked(_length + chunk.Length);
            if (next > _limits.MaxOutputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), next, _limits.MaxOutputBytes);
            }
            for (int offset = 0; offset < chunk.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
                int count = Math.Min(32768, chunk.Length - offset);
                _governor.NoteOperation();
                hash.AppendData(chunk.Slice(offset, count));
                offset += count;
            }
            _length = next;
        }

        public void Dispose()
        {
            IncrementalHash? hash = _hash; ParserMemoryScope? memory = _memory;
            _hash = null; _memory = null;
            try { hash?.Dispose(); } finally { memory?.Dispose(); }
        }
    }

    internal sealed class IdentifierOutputSink(TransactionSink destination, IdentifierHashSink hash)
        : ISynchronousByteSink, IOutputCapacityHint
    {
        private bool _hashing = true;
        public long Length => destination.Length;
        public bool CanAppendSynchronously => destination.CanAppendSynchronously;
        internal void FinishPrefix() => _hashing = false;

        void IOutputCapacityHint.EnsureCapacity(long length, CancellationToken token)
            => ((IOutputCapacityHint)destination).EnsureCapacity(length, token);

        public void Append(ReadOnlySpan<byte> chunk, CancellationToken token)
        {
            if (_hashing) { hash.Append(chunk, token); }
            destination.Append(chunk, token);
        }

        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            if (_hashing) { hash.Append(chunk.Span, token); }
            return destination.AppendAsync(chunk, token);
        }
    }

    internal static async ValueTask PublishLinearizedAsync(
        IQpdfHost host,
        PdfVersion version,
        List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>>? objectStreamGroups,
        int? infoRef,
        byte[]? originalId1,
        bool staticId,
        bool newlineBeforeEndstream,
        bool compressHints,
        int? compressionLevel,
        QpdfPath? linearizePass1,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken, OutputSecurity? security, int encryptionObject, int metadataObject)
    {
        if (linearizePass1 == path)
        {
            throw new QpdfParseException("The first-pass output must differ from every main PDF output.", null, null);
        }

        OutputSerialization serialization = new(version, model, objectStreamGroups, compressHints,
            compressionLevel, infoRef, newlineBeforeEndstream, QdfOptions.Disabled, null);
        Writing.LinearizedWriter writer = security is null ? new(limits, governor)
            : new(limits, governor, security, encryptionObject, metadataObject);
        byte[] id1;
        byte[] id2;
        if (staticId)
        {
            id2 = (byte[])StaticIdBytes.Clone();
            id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
        }
        else
        {
            byte[] placeholderId1 = originalId1 is null ? (byte[])PlaceholderIdBytes.Clone() : (byte[])originalId1.Clone();
            byte[] placeholderId2 = (byte[])PlaceholderIdBytes.Clone();
            Writing.LinearizedWriter probe = writer;
            using IdentifierHashSink memory = IdentifierHashSink.Create(limits, governor, cancellationToken);
            using BatchingSink hashed = BatchingSink.Create(memory, limits, governor, cancellationToken);
            await serialization.WriteLinearizedAsync(probe, placeholderId1, placeholderId2, hashed, null, cancellationToken).ConfigureAwait(false);
            await hashed.FlushAsync(cancellationToken).ConfigureAwait(false);
            byte[] digest = memory.Complete(cancellationToken);
            id2 = digest;
            id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
        }

        using ParserMemoryScope staging = ParserMemoryScope.Create(governor);
        await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
        TransactionSink destination = new(transaction, governor, staging, limits);
        using BatchingSink sink = BatchingSink.Create(destination, limits, governor, cancellationToken);
        if (linearizePass1 is null)
        {
            await serialization.WriteLinearizedAsync(writer, id1, id2, sink, null, cancellationToken).ConfigureAwait(false);
            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using IQpdfOutputTransaction pass1Transaction = await GovernedHost.CreateOutputAsync(host, linearizePass1.Value, governor, cancellationToken).ConfigureAwait(false);
        TransactionSink pass1Destination = new(pass1Transaction, governor);
        using BatchingSink pass1Sink = BatchingSink.Create(pass1Destination, limits, governor, cancellationToken);
        await serialization.WriteLinearizedAsync(writer, id1, id2, sink, pass1Sink, cancellationToken).ConfigureAwait(false);
        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        await pass1Sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await pass1Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask PublishWithIdsAsync(
        IQpdfHost host,
        PdfVersion version,
        List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>>? objectStreamGroups,
        bool compressStreams,
        int? compressionLevel,
        int? infoRef,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        Writing.QdfOptions qdf,
        IReadOnlyDictionary<int, int>? originalIds,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        IdentifierHashSink? identifiers,
        bool deriveFirstId, PdfSerializer serializer)
    {
        OutputSerialization serialization = new(version, model, objectStreamGroups, compressStreams,
            compressionLevel, infoRef, newlineBeforeEndstream, qdf, originalIds);
        CountingSink counter = new();
        long counted = await serialization.WriteAsync(serializer, id1, id2, counter, cancellationToken).ConfigureAwait(false);
        if (counted > limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), counted, limits.MaxOutputBytes);
        }

        governor.ReserveHostMemory(counted);
        try
        {
            await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
            TransactionSink destination = new(transaction, governor);
            IByteSink target = identifiers is null ? destination : new IdentifierOutputSink(destination, identifiers);
            using BatchingSink sink = BatchingSink.Create(target, limits, governor, cancellationToken);
            ((IOutputCapacityHint)sink).EnsureCapacity(counted, cancellationToken);
            long written;
            if (identifiers is not null)
            {
                (int size, long xrefOffset) = await serializer.SerializeClassicBodyAsync(version, model,
                    newlineBeforeEndstream, sink, cancellationToken).ConfigureAwait(false);
                // Include the bounded pending prefix without forcing an extra
                // host write. The real footer keeps the same lengths/offsets.
                sink.AppendPendingTo(identifiers, cancellationToken);
                ((IdentifierOutputSink)target).FinishPrefix();
                await serializer.AppendClassicTrailerAsync(size, infoRef, id1, id2, xrefOffset,
                    identifiers, cancellationToken).ConfigureAwait(false);
                if (identifiers.Length != counted)
                { throw new QpdfException($"Identifier replay mismatch: counted {counted} bytes but hashed {identifiers.Length}."); }
                byte[] digest = identifiers.Complete(cancellationToken);
                digest.CopyTo(id2, 0);
                if (deriveFirstId) { digest.CopyTo(id1, 0); }
                await serializer.AppendClassicTrailerAsync(size, infoRef, id1, id2, xrefOffset,
                    sink, cancellationToken).ConfigureAwait(false);
                written = sink.Length;
            }
            else
            { written = await serialization.WriteAsync(serializer, id1, id2, sink, cancellationToken).ConfigureAwait(false); }
            if (written != counted)
            {
                throw new QpdfException($"Writer replay mismatch: counted {counted} bytes but wrote {written}.");
            }

            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { governor.ReleaseHostMemory(counted); }
    }

    internal static async ValueTask PublishModelAsync(
        IQpdfHost host,
        PdfVersion version,
        List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>>? objectStreamGroups,
        bool compressStreams,
        int? compressionLevel,
        int? infoRef,
        byte[]? originalId1,
        bool staticId,
        bool newlineBeforeEndstream,
        Writing.QdfOptions qdf,
        IReadOnlyDictionary<int, int>? originalIds,
        bool linearize,
        QpdfPath? linearizePass1,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken, OutputSecurity? security, int encryptionObject, int metadataObject)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (linearize)
        {
            await PublishLinearizedAsync(host, version, model, objectStreamGroups, infoRef, originalId1, staticId, newlineBeforeEndstream, compressStreams, compressionLevel, linearizePass1, path, limits, governor, cancellationToken, security, encryptionObject, metadataObject).ConfigureAwait(false);
            return;
        }

        using ParserMemoryScope? bindingMemory = security is null ? null : ParserMemoryScope.Create(governor);
        PdfSerializer serializer = CreatePublicationSerializer(limits, governor, model, security, encryptionObject, metadataObject, bindingMemory);
        byte[] id1;
        byte[] id2;
        if (staticId)
        {
            id2 = (byte[])StaticIdBytes.Clone();
            id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
            await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, qdf, originalIds, path, limits, governor, cancellationToken, null, false, serializer).ConfigureAwait(false);
            return;
        }

        byte[] placeholderId1 = originalId1 is null ? (byte[])PlaceholderIdBytes.Clone() : (byte[])originalId1.Clone();
        byte[] placeholderId2 = (byte[])PlaceholderIdBytes.Clone();
        if (!qdf.Enabled && (objectStreamGroups is null || objectStreamGroups.Count == 0))
        {
            using IdentifierHashSink identifiers = IdentifierHashSink.Create(limits, governor, cancellationToken);
            identifiers.AdmitOutputMirror();
            await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel,
                infoRef, placeholderId1, placeholderId2, newlineBeforeEndstream, qdf, originalIds, path, limits,
                governor, cancellationToken, identifiers, originalId1 is null, serializer).ConfigureAwait(false);
            return;
        }
        PdfSerializer probe = serializer;
        using IdentifierHashSink memory = IdentifierHashSink.Create(limits, governor, cancellationToken);
        using BatchingSink hashed = BatchingSink.Create(memory, limits, governor, cancellationToken);
        OutputSerialization serialization = new(version, model, objectStreamGroups, compressStreams,
            compressionLevel, infoRef, newlineBeforeEndstream, qdf, originalIds);
        await serialization.WriteAsync(probe, placeholderId1, placeholderId2, hashed, cancellationToken).ConfigureAwait(false);
        await hashed.FlushAsync(cancellationToken).ConfigureAwait(false);
        hashed.Dispose();
        byte[] digest = memory.Complete(cancellationToken);
        memory.Dispose();
        id2 = digest;
        id1 = originalId1 is null ? (byte[])id2.Clone() : (byte[])originalId1.Clone();
        await PublishWithIdsAsync(host, version, model, objectStreamGroups, compressStreams, compressionLevel, infoRef, id1, id2, newlineBeforeEndstream, qdf, originalIds, path, limits, governor, cancellationToken, null, false, serializer).ConfigureAwait(false);
    }

    internal static PdfSerializer CreatePublicationSerializer(QpdfLimits limits, ResourceGovernor governor,
        List<NumberedObject> model, OutputSecurity? security, int encryptionObject, int metadataObject,
        ParserMemoryScope? memory)
    {
        if (security is null) { return new(limits, governor); }
        ParserMemoryScope owner = memory ?? throw new InvalidOperationException("Missing publication owner.");
        Dictionary<int, QpdfObject> values = ParserContainers.CreateDictionary<int, QpdfObject>(owner);
        foreach (NumberedObject item in model)
        {
            governor.CheckCancellation(); governor.NoteOperation();
            ParserContainers.EnsureEntryCapacity(owner, values, IntPtr.Size);
            values.Add(item.Number, item.Value);
        }
        return new(limits, governor, security, encryptionObject, metadataObject, Content.PoolResolver.Create(owner, values));
    }

    internal sealed class PreparedSplitPublications : IDisposable
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

    internal sealed class PreparedSplitPublication : IDisposable
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

    internal static async ValueTask<PreparedSplitPublication> PreparePresentationOutputAsync(PdfVersion version,
        List<NumberedObject> model, IReadOnlyList<IReadOnlyList<int>>? objectStreams, bool compressStreams,
        int? compressionLevel, int? infoRef, byte[]? originalId, bool staticId, bool newlineBeforeEndstream,
        QdfOptions qdf, IReadOnlyDictionary<int, int>? originalIds, bool linearize, QpdfPath? pass1, QpdfPath path,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken,
        OutputSecurity? security, int encryptionObject, int metadataObject)
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
                    originalIds, linearize, placeholder, null, limits, governor, cancellationToken,
                    security, encryptionObject, metadataObject).ConfigureAwait(false);
                ids.Reserve(40L);
                id2 = placeholder.Complete(cancellationToken);
            }
            await SerializePresentationOutputAsync(version, model, objectStreams, compressStreams, compressionLevel,
                infoRef, originalId ?? id2, id2, newlineBeforeEndstream, qdf, originalIds, linearize,
                publication.Main, publication.Pass1, limits, governor, cancellationToken,
                security, encryptionObject, metadataObject).ConfigureAwait(false);
            governor.ChargeOutputBytes(checked(publication.Main.Length + (publication.Pass1?.Length ?? 0L)));
            return publication;
        }
        catch { publication.Dispose(); throw; }
    }

    internal static async ValueTask SerializePresentationOutputAsync(PdfVersion version,
        List<NumberedObject> model, IReadOnlyList<IReadOnlyList<int>>? objectStreams, bool compressStreams,
        int? compressionLevel, int? infoRef, byte[] id1, byte[] id2, bool newlineBeforeEndstream,
        QdfOptions qdf, IReadOnlyDictionary<int, int>? originalIds, bool linearize, IByteSink counter, IByteSink? firstPass,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken,
        OutputSecurity? security, int encryptionObject, int metadataObject)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        // Two counters and the serializer/writer wrappers; the shared writers own
        // their dynamic scratch. The identifier arrays are borrowed, never changed.
        memory.Reserve(linearize ? 128L : 96L);
        using BatchingSink buffered = BatchingSink.Create(counter, limits, governor, cancellationToken);
        using BatchingSink? bufferedFirstPass = firstPass is null ? null
            : BatchingSink.Create(firstPass, limits, governor, cancellationToken);
        OutputSerialization serialization = new(version, model, objectStreams, compressStreams,
            compressionLevel, infoRef, newlineBeforeEndstream, qdf, originalIds);
        if (linearize)
        {
            LinearizedWriter writer = security is null ? new(limits, governor)
                : new(limits, governor, security, encryptionObject, metadataObject);
            await serialization.WriteLinearizedAsync(writer, id1, id2, buffered, bufferedFirstPass, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            PdfSerializer serializer = CreatePublicationSerializer(limits, governor, model, security, encryptionObject, metadataObject, memory);
            await serialization.WriteAsync(serializer, id1, id2, buffered, cancellationToken).ConfigureAwait(false);
        }
        // Identifiers and publication lengths are consumed by
        // the caller only after both captures contain their complete bytes.
        await buffered.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (bufferedFirstPass is not null)
        { await bufferedFirstPass.FlushAsync(cancellationToken).ConfigureAwait(false); }
    }

    internal static async ValueTask PublishPreparedOutputAsync(IQpdfHost host, QpdfPath path, PreparedOutputSink output,
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

    internal static async ValueTask PublishPreparedBytesAsync(IQpdfHost host, QpdfPath path, Json.JsonPayload payload,
        ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        await using IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, path, governor, token).ConfigureAwait(false);
        ReadOnlyMemory<byte> bytes = payload.Data;
        GovernedHost.PrepareOutputCapacity(transaction, bytes.Length, token);
        for (int offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int count = Math.Min(32768, bytes.Length - offset);
            await transaction.WriteAsync(bytes.Slice(offset, count), token).ConfigureAwait(false);
            offset += count;
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }}
