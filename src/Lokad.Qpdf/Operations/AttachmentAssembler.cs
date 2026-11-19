using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Operations.PageAssembly;
using static Lokad.Qpdf.Runtime.ExecutionFailures;
using static Lokad.Qpdf.Writing.ObjectStreamPreparation;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputVersionPolicy;

namespace Lokad.Qpdf.Operations;

internal static class AttachmentAssembler
{
    internal sealed record AttachmentAssembly(PdfReference? NamesRef, bool Inserted, int FinalCount, bool Replaced,
        QpdfDocument.VersionParts? Version, int ExtensionLevel);

    internal static async ValueTask<(PdfDictionary Catalog, PdfVersion Version, bool UpdatedVersion)> EditCatalogAsync(
        IQpdfHost host, IIndirectResolver resolver, PdfDictionary trailer, PdfDictionary catalog, GraphCopier pool,
        PdfVersion version, AttachmentEdits edits, QpdfRecoveryMode recovery, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, ParserMemoryScope memory, CancellationToken token)
    {
        memory.Reserve(128L);
        SortedDictionary<string, AttachmentReference> existing = new(StringComparer.Ordinal);
        using (ParserMemoryScope entriesMemory = ParserMemoryScope.Create(governor))
        {
            foreach (EmbeddedFileEntry entry in EmbeddedFiles.ReadEntries(resolver, trailer, edits.Removes,
                limits, governor, memory, entriesMemory, token))
            {
                token.ThrowIfCancellationRequested(); governor.NoteOperation();
                memory.Reserve(64L);
                existing.Add(entry.Key, new(entry.Name, entry.Spec));
            }
        }
        Dictionary<int, QpdfObject> additions = ParserContainers.CreateDictionary<int, QpdfObject>(memory);
        AttachmentAssembly attached = await AssembleAttachmentsAsync(host, existing, additions, pool.NextId, edits,
            false, null, false, ParserContainers.CreateList<(IReadOnlyDictionary<int, int>, HashSet<int>)>(memory),
            recovery, limits, governor, diagnostics, null, memory, token).ConfigureAwait(false);
        foreach ((int number, QpdfObject value) in additions)
        {
            token.ThrowIfCancellationRequested(); governor.NoteOperation();
            _ = pool.AddIndirectObjectWithId(value, number);
        }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionaryLookup.TryGet(catalog, "Names"u8, governor, token, out QpdfObject names);
        PdfDictionary? oldNames = Content.CatalogCopy.ResolveDictionary(names, resolver, governor, token);
        QpdfObject? newNames = attached.NamesRef;
        if (oldNames is not null)
        {
            if (attached.NamesRef is PdfReference attachedNames)
            {
                PdfDictionary wrapper = (PdfDictionary)additions[attachedNames.ObjectNumber];
                if (!PdfDictionaryLookup.TryGet(wrapper, "EmbeddedFiles"u8, governor, token, out QpdfObject tree))
                { throw new InvalidOperationException("An assembled attachment dictionary has no tree."); }
                pool.ReplaceCopiedObject(attachedNames.ObjectNumber, PdfDictionaryRebuild.ReplaceEntry(graph, oldNames, "EmbeddedFiles"u8, tree));
            }
            else
            {
                PdfDictionary remaining = PdfDictionaryRebuild.RemoveEntry(graph, oldNames, "EmbeddedFiles"u8);
                newNames = remaining.Count == 0 ? null : remaining;
            }
        }
        PdfDictionary updated = newNames is null ? PdfDictionaryRebuild.RemoveEntry(graph, catalog, "Names"u8)
            : PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "Names"u8, newNames);
        if (attached.Inserted && Content.CatalogCopy.ReadPageMode(updated, resolver, governor, token, out _) is null)
        { updated = PdfDictionaryRebuild.ReplaceEntry(graph, updated, "PageMode"u8, UseAttachmentsPageMode); }
        PdfVersion baseline = version;
        int extension = Content.CatalogCopy.ReadExtensionLevel(catalog, resolver, governor, token);
        IncludeVersionRequirement(ref version, ref extension, attached.Version, attached.ExtensionLevel, governor);
        IncludeVersionRequirement(ref version, ref extension,
            AuthoredVersionFloor(edits.Adds.Count > 0, attached.NamesRef is not null, false), 0, governor);
        bool updatedVersion = version.Major != baseline.Major || version.Minor != baseline.Minor || attached.ExtensionLevel > 0;
        if (updatedVersion)
        {
            PdfDictionaryLookup.TryGet(updated, "Extensions"u8, governor, token, out QpdfObject original);
            QpdfObject? extensions = PrepareExtensions(original, resolver, version, extension, graph);
            if (!ReferenceEquals(extensions, original))
            {
                updated = extensions is null ? PdfDictionaryRebuild.RemoveEntry(graph, updated, "Extensions"u8)
                    : PdfDictionaryRebuild.ReplaceEntry(graph, updated, "Extensions"u8, extensions);
            }
        }
        graph.Memory.TransferTo(memory, graph.Bytes);
        return (updated, version, updatedVersion);
    }

    internal static SortedDictionary<string, AttachmentReference> PreserveExistingAttachments(
        GraphCopier copier,
        DocumentEngine engine,
        AttachmentEdits edits,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
    {
        ownership.CheckOwner(governor);
        ownership.Reserve(96L);
        SortedDictionary<string, AttachmentReference> kept = new(StringComparer.Ordinal);
        using ParserMemoryScope entriesMemory = ParserMemoryScope.Create(governor);
        List<EmbeddedFileEntry> found = EmbeddedFiles.ReadEntries(engine, edits.Removes, limits, governor, ownership, entriesMemory, cancellationToken);
        HashSet<string> replacements = ParserContainers.CreateSet<string>(entriesMemory);
        foreach (AttachmentAdd add in edits.Adds)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (add.Replace)
            {
                string key = add.Key ?? AttachmentBuilder.Basename(add.File, governor, entriesMemory, cancellationToken);
                ParserContainers.AddSetItem(entriesMemory, replacements, key, IntPtr.Size);
            }
        }
        foreach (EmbeddedFileEntry attachment in found)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ownership.Reserve(64L);
            // Presence determines conflicts even when this graph cannot be kept.
            kept[attachment.Key] = new(attachment.Name, null);
            if (replacements.Contains(attachment.Key))
            {
                continue;
            }
            int firstId = copier.BeginCopyAttempt();
            try
            {
                PdfReference? link = copier.AddIndirectObject(attachment.Spec);
                if (link is not null)
                {
                    kept[attachment.Key] = new(attachment.Name, link);
                }
            }
            catch (QpdfParseException)
            {
                copier.RollbackCopyAttempt(firstId, cancellationToken);
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable attachment.");
            }
            finally { copier.EndCopyAttempt(); }
        }

        return kept;
    }

    internal static async ValueTask<AttachmentAssembly> AssembleAttachmentsAsync(
        IQpdfHost host,
        SortedDictionary<string, AttachmentReference> existing,
        Dictionary<int, QpdfObject> pool,
        int firstNewObject,
        AttachmentEdits edits,
        bool compress,
        int? level,
        bool preserveObjectStreams,
        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> compressibleSources,
        QpdfRecoveryMode recovery,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        HashSet<int>? preservationCandidates,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
    {
        ownership.CheckOwner(governor);
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (existing is null)
        {
            throw new ArgumentNullException(nameof(existing));
        }

        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        if (edits is null)
        {
            throw new ArgumentNullException(nameof(edits));
        }

        if (compressibleSources is null)
        {
            throw new ArgumentNullException(nameof(compressibleSources));
        }

        if (!edits.HasEdits && existing.Count == 0)
        {
            return new AttachmentAssembly(null, false, 0, false, null, 0);
        }

        int nextId = firstNewObject;
        foreach (int number in pool.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (number >= nextId)
            {
                nextId = checked(number + 1);
            }
        }

        bool duplicateKeys = false;
        bool replaced = false;
        foreach (AttachmentAdd add in edits.Adds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            string key = add.Key ?? AttachmentBuilder.Basename(add.File, governor, ownership, cancellationToken);
            string fileName = add.FileName ?? AttachmentBuilder.Basename(add.File, governor, ownership, cancellationToken);
            if (key.Length == 0)
            {
                throw new QpdfParseException("Attachment key must not be empty.", 0L, null);
            }

            if (fileName.Length == 0)
            {
                throw new QpdfParseException("Attachment filename must not be empty.", 0L, null);
            }

            if (!add.Replace && existing.ContainsKey(key))
            {
                duplicateKeys = true;
                continue;
            }

            byte[] data = await ReadAttachmentFileAsync(host, add.File, limits, governor, ownership, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (add.MimeType is not null && !IsAscii(add.MimeType, governor, cancellationToken))
            {
                throw new QpdfParseException("Attachment MIME type must be ASCII.", 0L, null);
            }

            byte[] checksum = AttachmentBuilder.Checksum(data, governor, ownership, cancellationToken);
            byte[] stored = data;
            if (compress && data.Length > 0)
            {
                stored = FlateFilter.Encode(data, level, limits, governor, ownership, cancellationToken);
            }

            PdfDictionary streamDict = AttachmentBuilder.BuildStreamDictionary(data.Length, checksum, add.MimeType, add.CreationDate, add.ModDate, compress && data.Length > 0, governor, ownership, cancellationToken);
            int streamId = nextId;
            checked
            {
                nextId += 1;
            }

            ownership.Reserve(64L);
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size);
            pool[streamId] = PdfStream.FromOwnedData(streamDict, stored);
            PdfDictionary spec = AttachmentBuilder.BuildFilespecDictionary(fileName, add.Description, streamId, governor, ownership, cancellationToken);
            int specId = nextId;
            checked
            {
                nextId += 1;
            }

            ownership.Reserve(128L);
            ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size);
            pool[specId] = spec;
            PdfByteString name;
            if (existing.TryGetValue(key, out AttachmentReference original))
            {
                replaced = true;
                name = original.Name;
                // A replaced addition has no source object to preserve.
                if (preservationCandidates is not null && original.Spec is PdfReference prior)
                { ParserContainers.AddSetItem(ownership, preservationCandidates, prior.ObjectNumber, sizeof(int)); }
            }
            else
            {
                ownership.Reserve(64L);
                name = AttachmentBuilder.EncodeText(key, governor, ownership, cancellationToken);
            }
            existing[key] = new(name, new PdfReference(specId, 0));
        }

        if (duplicateKeys)
        {
            throw new QpdfParseException("Attachments with conflicting keys already exist; use --replace to replace or --key to specify a different key.", 0L, null);
        }

        bool copiedAny = false;
        QpdfDocument.VersionParts? copyVersion = null; int copyExtension = 0;
        string? firstCopyConflict = null;
        bool moreCopyConflicts = false;
        using AttachmentImportCache? imports = edits.Copies.Count > 1 ? AttachmentImportCache.Create(governor, cancellationToken) : null;
        foreach (AttachmentCopy copy in edits.Copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (imports?.Find(copy) is IReadOnlyList<EmbeddedFileEntry> cached)
            {
                (bool reusedAny, _) = CopyImportedAttachments(copy, cached, null, null, existing,
                    governor, diagnostics, ownership, cancellationToken, ref firstCopyConflict, ref moreCopyConflicts);
                copiedAny |= reusedAny;
                continue;
            }
            long diagnosticsBefore = diagnostics.EmissionVersion;
            using ParserMemoryScope entriesMemory = ParserMemoryScope.Create(governor);
            EmbeddedFileEntry[]? captured;
            bool reusable;
            QpdfPath sourcePath = SelectionPlan.CheckedPath(copy.File);
            governor.PublicationInputs?.BorrowPath(sourcePath.Value);
            await using (QpdfDocument source = await QpdfDocument.OpenInspectionCore(host, sourcePath, copy.Password,
                limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false))
            {
                Content.CatalogCopy.IncludeSourceVersion(ref copyVersion, ref copyExtension, source, governor, cancellationToken);
                List<EmbeddedFileEntry> found = EmbeddedFiles.ReadEntries(source.Engine, [], limits, governor, ownership, entriesMemory, cancellationToken);
                captured = imports?.CreateEntries(found.Count, entriesMemory);
                GraphCopier copyCopier = GraphCopier.CreateOwned(source.Engine, governor, limits, nextId, ownership);
                (bool importedAny, bool canReuse) = CopyImportedAttachments(copy, found, copyCopier, captured, existing,
                    governor, diagnostics, ownership, cancellationToken, ref firstCopyConflict, ref moreCopyConflicts);
                copiedAny |= importedAny; reusable = canReuse;
                nextId = copyCopier.NextId;
                // Final writer models order object numbers; no sorted key snapshot is needed here.
                foreach ((int id, QpdfObject value) in copyCopier.CopiedObjects)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.NoteOperation();
                    ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size);
                    pool[id] = value;
                }
                ParserContainers.AddItem(ownership, compressibleSources,
                    (copyCopier.SourceToDestination, CollectCompressedOriginals(source.Engine, preserveObjectStreams, ownership, governor, cancellationToken)), 2L * IntPtr.Size);
                source.Engine.TransferExclusiveValuesTo(ownership);
            }
            if (reusable && captured is not null && diagnostics.EmissionVersion == diagnosticsBefore)
            { imports?.Remember(copy, captured, entriesMemory); }
            captured = null;
        }

        if (firstCopyConflict is not null)
        {
            throw AttachmentCopyConflict(firstCopyConflict, moreCopyConflicts, governor, ownership, cancellationToken);
        }

        // Discard failed specifications only after every edit has used their keys.
        using (ParserMemoryScope skippedMemory = ParserMemoryScope.Create(governor))
        {
            List<string>? skipped = null;
            foreach (KeyValuePair<string, AttachmentReference> entry in existing)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (entry.Value.Spec is not null) { continue; }
                skipped ??= ParserContainers.CreateList<string>(skippedMemory);
                ParserContainers.AddItem(skippedMemory, skipped, entry.Key, IntPtr.Size);
            }
            if (skipped is not null)
            {
                foreach (string key in skipped)
                { cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); existing.Remove(key); }
            }
        }
        if (existing.Count == 0)
        {
            return new AttachmentAssembly(null, false, 0, false, copyVersion, copyExtension);
        }

        PdfDictionary tree = AttachmentBuilder.BuildNameTree(existing.Values, governor, ownership, cancellationToken);
        ownership.Reserve(64L);
        PdfDictionary wrapper = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("EmbeddedFiles")] = tree,
        });
        int rootId = nextId;
        checked
        {
            nextId += 1;
        }

        ownership.Reserve(128L);
        ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size);
        pool[rootId] = wrapper;
        return new AttachmentAssembly(new PdfReference(rootId, 0), edits.Adds.Count > 0 || copiedAny, existing.Count, replaced, copyVersion, copyExtension);
    }

    private static (bool CopiedAny, bool Reusable) CopyImportedAttachments(
        AttachmentCopy copy, IReadOnlyList<EmbeddedFileEntry> entries, GraphCopier? copier, EmbeddedFileEntry[]? captured,
        SortedDictionary<string, AttachmentReference> existing, ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope ownership, CancellationToken token, ref string? firstConflict, ref bool moreConflicts)
    {
        bool copiedAny = false, reusable = captured is not null;
        for (int index = 0; index < entries.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.NoteOperation();
            EmbeddedFileEntry attachment = entries[index];
            string newKey = AttachmentCopyKey(copy.Prefix, attachment.Key, governor, ownership, token);
            if (existing.ContainsKey(newKey))
            {
                reusable = false;
                firstConflict ??= copy.File;
                moreConflicts |= !string.Equals(firstConflict, copy.File, StringComparison.Ordinal);
                continue;
            }
            // Placeholders retain failed keys until every request checks conflicts.
            ownership.Reserve(64L);
            existing.Add(newKey, new(attachment.Name, null));
            PdfReference? link;
            if (copier is null)
            { link = attachment.Spec as PdfReference ?? throw new InvalidOperationException("A cached attachment must reference the output pool."); }
            else
            {
                int firstId = copier.BeginCopyAttempt();
                try { link = copier.AddIndirectObject(attachment.Spec); }
                catch (QpdfParseException)
                {
                    copier.RollbackCopyAttempt(firstId, token);
                    link = null;
                }
                finally { copier.EndCopyAttempt(); }
            }
            if (link is null)
            {
                reusable = false;
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable attachment.");
                continue;
            }
            PdfByteString name = string.IsNullOrEmpty(copy.Prefix) ? attachment.Name
                : AttachmentBuilder.EncodeText(newKey, governor, ownership, token);
            existing[newKey] = new(name, link);
            if (captured is not null) { captured[index] = new(attachment.Key, attachment.Name, link); }
            copiedAny = true;
        }
        return (copiedAny, reusable);
    }

    internal static string AttachmentCopyKey(string? prefix, string key, ResourceGovernor governor,
        ParserMemoryScope memory, CancellationToken token)
    {
        if (string.IsNullOrEmpty(prefix)) { return key; }
        if (key.Length == 0) { return prefix; }
        int length = checked(prefix.Length + key.Length);
        memory.Reserve(GovernedText.StringBytes(length));
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return string.Create(length, (prefix, key, governor, token), static (target, state) =>
        {
            int offset = 0;
            while (offset < target.Length)
            {
                state.token.ThrowIfCancellationRequested(); state.governor.CheckCancellation();
                ReadOnlySpan<char> source = offset < state.prefix.Length
                    ? state.prefix.AsSpan(offset) : state.key.AsSpan(offset - state.prefix.Length);
                int count = Math.Min(256, source.Length);
                source[..count].CopyTo(target.Slice(offset, count));
                offset += count;
            }
        });
    }

    internal static QpdfParseException AttachmentCopyConflict(string firstFile, bool moreFiles,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        // One source, at most 256 UTF-16 units, plus an explicit overflow marker.
        // Fund both the message and QpdfParseException's bounded offset suffix copy.
        memory.Reserve(2L * GovernedText.StringBytes(512));
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        Span<char> message = stackalloc char[512];
        const string start = "Attachments copied from '";
        start.AsSpan().CopyTo(message);
        int length = start.Length;
        int take = Math.Min(firstFile.Length, 256);
        if (take < firstFile.Length && char.IsHighSurrogate(firstFile[take - 1])) { take--; }
        firstFile.AsSpan(0, take).CopyTo(message[length..]); length += take;
        if (take < firstFile.Length) { "...".AsSpan().CopyTo(message[length..]); length += 3; }
        string end = moreFiles
            ? "' (additional source paths omitted) conflict with existing keys; use --prefix to specify a key prefix."
            : "' conflict with existing keys; use --prefix to specify a key prefix.";
        end.AsSpan().CopyTo(message[length..]); length += end.Length;
        return new QpdfParseException(new string(message[..length]), 0L, null);
    }

    internal static bool IsAscii(string text, ResourceGovernor governor, CancellationToken token)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 255) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
            if (text[index] > 127)
            {
                return false;
            }
        }

        return true;
    }

    internal static async ValueTask<byte[]> ReadAttachmentFileAsync(
        IQpdfHost host,
        string file,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
    {
        ownership.CheckOwner(governor);
        QpdfPath path = SelectionPlan.CheckedPath(file);
        governor.PublicationInputs?.BorrowPath(path.Value);
        IQpdfInput input = await GovernedHost.OpenInputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
        try
        {
            return await QpdfInputReader.ReadWholeAdmittedAsync(input, limits, ownership, governor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

}
