using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Attachments;

internal sealed record EmbeddedAttachment(
    string Key,
    string FileName,
    string Description,
    string MimeType,
    string CreationDate,
    string ModDate,
    string Checksum,
    long Size,
    PdfStream Stream,
    QpdfObject Spec,
    PdfReference? StreamReference)
{
    internal int StreamObjectNumber => StreamReference?.ObjectNumber ?? -1;
}

internal static class EmbeddedFiles
{
    private static readonly string[] NameKeys = ["UF", "F", "Unix", "DOS", "Mac"];

    internal static List<EmbeddedAttachment> ReadAttachments(DocumentEngine engine, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, ParserMemoryScope memory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor); ArgumentNullException.ThrowIfNull(diagnostics);
        memory.CheckOwner(governor); limits.Validate(); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        List<EmbeddedAttachment> result = ParserContainers.CreateList<EmbeddedAttachment>(memory);
        PdfDictionary? catalog = ResolveDictionary(engine, engine.Trailer, "Root", governor, token);
        PdfDictionary? names = catalog is null ? null : ResolveDictionary(engine, catalog, "Names", governor, token);
        PdfDictionary? tree = names is null ? null : ResolveDictionary(engine, names, "EmbeddedFiles", governor, token);
        if (tree is null) { return result; }

        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        WalkNode(engine, tree, 0, GovernedIntSet.Create(traversal, governor, token), result,
            limits, governor, diagnostics, memory, traversal, token, null);
        // Comparisons can examine large names even when there are few attachments.
        try { result.Sort((left, right) => CompareKeys(left.Key, right.Key, governor, token)); }
        catch (InvalidOperationException ex) when (ex.InnerException is OperationCanceledException or QpdfException)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); }
        return result;
    }

    private static int CompareKeys(string left, string right, ResourceGovernor governor, CancellationToken token)
    {
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if ((i & 255) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
            int order = left[i] - right[i]; if (order != 0) { return order; }
        }
        return left.Length.CompareTo(right.Length);
    }

    internal static ReadOnlyMemory<byte> ExtractAttachment(DocumentEngine engine, string key,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope memory, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        PdfDictionary? catalog = ResolveDictionary(engine, engine.Trailer, "Root", governor, token);
        PdfDictionary? names = catalog is null ? null : ResolveDictionary(engine, catalog, "Names", governor, token);
        PdfDictionary? tree = names is null ? null : ResolveDictionary(engine, names, "EmbeddedFiles", governor, token);
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        QpdfObject? raw = tree is null ? null : WalkNode(engine, tree, 0,
            GovernedIntSet.Create(traversal, governor, token), null,
            limits, governor, diagnostics, memory, traversal, token, key);
        if (raw is null) { throw new QpdfParseException("The requested attachment was not found.", 0L, null); }
        (PdfStream stream, PdfReference? reference) = ReadEmbeddedStream(engine, ReadFileSpec(engine, raw, governor, token), governor, token);
        return ExtractData(stream, reference?.ObjectNumber ?? -1, engine, limits, governor, diagnostics, memory, token);
    }

    internal static ReadOnlyMemory<byte> ExtractData(
        PdfStream stream,
        int number,
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        limits.Validate();
        memory.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        try
        {
            using FilterPlan stages = FilterChain.Resolve(stream.Dictionary, (IIndirectResolver)engine, governor, cancellationToken);
            // The open document funds raw storage until the caller consumes it.
            if (stages.Count == 0) { return stream.RawMemory; }
            byte[] raw = stream.ArrayData(memory, governor, cancellationToken);
            return FilterChain.Decode(raw, stages, limits, governor, memory, cancellationToken);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            diagnostics.AddParts(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, ["Stream ", number, " extracted raw: decode unavailable."]);
            return stream.RawMemory;
        }
    }

    private static PdfDictionary? ResolveDictionary(DocumentEngine engine, PdfDictionary parent, string key,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!parent.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        QpdfObject target = raw is PdfReference reference ? ResolveRequired(engine, reference, key, governor, token) : raw;
        if (target is PdfNull)
        {
            return null;
        }

        if (target is not PdfDictionary dictionary)
        {
            throw new QpdfParseException($"Embedded-files {key} must be a dictionary.", null, null);
        }

        return dictionary;
    }

    private static QpdfObject ResolveRequired(DocumentEngine engine, PdfReference reference, string role,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfObjectLookup.TryResolve(reference, engine, governor, token, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
        {
            throw new QpdfParseException($"Unresolvable embedded-files {role} reference.", null, reference.ObjectNumber);
        }

        return resolved;
    }

    private static QpdfObject? WalkNode(DocumentEngine engine, PdfDictionary node, int depth, GovernedIntSet visited,
        List<EmbeddedAttachment>? result, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope memory, ParserMemoryScope traversal, CancellationToken token, string? requestedKey)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (depth > limits.MaxNestingDepth)
        { throw new QpdfParseException("Embedded-files name tree exceeds nesting budget.", null, null); }
        if (node.TryGet(PdfName.FromAscii("Names"), out QpdfObject? namesRaw) && namesRaw is not null && namesRaw is not PdfNull)
        {
            if (ResolveOptionalValue(engine, namesRaw, governor, token) is not PdfArray names || names.Count % 2 != 0)
            { throw new QpdfParseException("Embedded-files name array must hold key/value pairs.", null, null); }
            for (int i = 0; i < names.Count; i += 2)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (ResolveOptionalValue(engine, names[i], governor, token) is not PdfByteString keyBytes)
                { throw new QpdfParseException("Embedded-files name-tree keys must be strings.", null, null); }
                long before = memory.ReservedBytes;
                string key = PdfText.Decode(keyBytes.ValueMemory, memory, governor, token);
                if (requestedKey is not null)
                {
                    bool matches = CompareKeys(key, requestedKey, governor, token) == 0;
                    memory.Release(memory.ReservedBytes - before);
                    if (matches) { return names[i + 1]; }
                    continue;
                }
                EmbeddedAttachment attachment;
                try { attachment = ParseAttachment(engine, key, names[i + 1], governor, memory, token); }
                catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
                {
                    memory.Release(memory.ReservedBytes - before);
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unlistable attachment.");
                    continue;
                }
                if (result is not null) { ParserContainers.AddItem(memory, result, attachment, IntPtr.Size); }
            }
        }
        if (node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) && kidsRaw is not null && kidsRaw is not PdfNull)
        {
            if (ResolveOptionalValue(engine, kidsRaw, governor, token) is not PdfArray kids)
            { throw new QpdfParseException("Embedded-files kids must be an array.", null, null); }
            for (int i = 0; i < kids.Count; i++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                QpdfObject item = kids[i];
                QpdfObject target = item is PdfReference reference ? ResolveRequired(engine, reference, "name-tree kid", governor, token) : item;
                if (target is not PdfDictionary kid)
                { throw new QpdfParseException("Embedded-files kid must be a dictionary.", null, null); }
                if (item is PdfReference link && !visited.Add(traversal, link.ObjectNumber))
                { throw new QpdfParseException("Embedded-files name tree contains a cycle.", null, link.ObjectNumber); }
                QpdfObject? found = WalkNode(engine, kid, depth + 1, visited, result,
                    limits, governor, diagnostics, memory, traversal, token, requestedKey);
                if (item is PdfReference completed) { visited.Remove(completed.ObjectNumber); }
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    private static EmbeddedAttachment ParseAttachment(
        DocumentEngine engine,
        string key,
        QpdfObject value,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfDictionary spec = ReadFileSpec(engine, value, governor, cancellationToken);

        string fileName = string.Empty;
        foreach (string name in NameKeys)
        {
            if (spec.TryGet(PdfName.FromAscii(name), out QpdfObject? raw)
                && ResolveOptionalValue(engine, raw, governor, cancellationToken) is PdfByteString text)
            {
                fileName = PdfText.Decode(text.ValueMemory, memory, governor, cancellationToken);
                break;
            }
        }

        string description = string.Empty;
        if (spec.TryGet(PdfName.FromAscii("Desc"), out QpdfObject? descRaw)
            && ResolveOptionalValue(engine, descRaw, governor, cancellationToken) is PdfByteString descText)
        {
            description = PdfText.Decode(descText.ValueMemory, memory, governor, cancellationToken);
        }

        (PdfStream data, PdfReference? streamReference) = ReadEmbeddedStream(engine, spec, governor, cancellationToken);
        PdfDictionary? parameters = null;
        if (data.Dictionary.TryGet(PdfName.FromAscii("Params"), out QpdfObject parametersRaw) && parametersRaw is not PdfNull)
        {
            parameters = (parametersRaw is PdfReference parametersReference
                ? ResolveRequired(engine, parametersReference, "embedded stream parameters", governor, cancellationToken) : parametersRaw) as PdfDictionary;
        }
        EmbeddedStreamMetadata metadata = ReadStreamMetadata(engine, data, parameters, memory, governor, cancellationToken);

        governor.NoteOperation();
        memory.Reserve(144L);
        return new EmbeddedAttachment(key, fileName, description, metadata.MimeType, metadata.CreationDate, metadata.ModDate,
            metadata.Checksum, metadata.Size, data, value, streamReference);
    }

    private static PdfDictionary ReadFileSpec(DocumentEngine engine, QpdfObject value, ResourceGovernor governor, CancellationToken token)
    {
        QpdfObject target = value is PdfReference reference ? ResolveRequired(engine, reference, "file specification", governor, token) : value;
        return target as PdfDictionary ?? throw new QpdfParseException("Embedded file specification must be a dictionary.", null, null);
    }

    private static (PdfStream Stream, PdfReference? Reference) ReadEmbeddedStream(DocumentEngine engine, PdfDictionary spec,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!spec.TryGet(PdfName.FromAscii("EF"), out QpdfObject? efRaw) || efRaw is null || efRaw is PdfNull)
        {
            throw new QpdfParseException("Embedded file specification has no embedded stream.", null, null);
        }

        QpdfObject efTarget = efRaw is PdfReference efReference ? ResolveRequired(engine, efReference, "embedded stream set", governor, token) : efRaw;
        if (efTarget is not PdfDictionary ef)
        {
            throw new QpdfParseException("Embedded file specification has no embedded stream.", null, null);
        }

        PdfReference? streamReference = null;
        PdfStream? data = null;
        foreach (string name in NameKeys)
        {
            if (!ef.TryGet(PdfName.FromAscii(name), out QpdfObject? candidate) || candidate is null || candidate is PdfNull)
            {
                continue;
            }

            QpdfObject resolved = candidate is PdfReference resolvedReference ? ResolveRequired(engine, resolvedReference, "embedded stream", governor, token) : candidate;
            if (resolved is PdfStream stream)
            {
                data = stream;
                streamReference = candidate as PdfReference;

                break;
            }
        }

        if (data is null)
        {
            throw new QpdfParseException("Embedded file specification has no embedded stream.", null, null);
        }

        return (data, streamReference);
    }

    internal readonly record struct EmbeddedStreamMetadata(string MimeType, string CreationDate, string ModDate, string Checksum, long Size);

    internal static QpdfObject? ResolveOptionalValue(IIndirectResolver resolver, QpdfObject raw,
        ResourceGovernor governor, CancellationToken token)
        => raw is PdfReference reference
            ? PdfObjectLookup.TryResolve(reference, resolver, governor, token, out QpdfObject? value) ? value : null
            : raw;

    internal static EmbeddedStreamMetadata ReadStreamMetadata(IIndirectResolver resolver, PdfStream data, PdfDictionary? parms, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor); cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        string mimeType = string.Empty;
        if (data.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtypeRaw)
            && ResolveOptionalValue(resolver, subtypeRaw, governor, cancellationToken) is PdfName subtype)
        {
            mimeType = PdfText.DecodeUtf8(subtype.RawMemory, memory, governor, cancellationToken);
        }

        string creationDate = string.Empty;
        string modDate = string.Empty;
        string checksum = string.Empty;
        long size = -1L;
        if (parms is not null)
        {
            if (parms.TryGet(PdfName.FromAscii("CreationDate"), out QpdfObject? creationRaw)
                && ResolveOptionalValue(resolver, creationRaw, governor, cancellationToken) is PdfByteString creationText)
            {
                creationDate = PdfText.Decode(creationText.ValueMemory, memory, governor, cancellationToken);
            }

            if (parms.TryGet(PdfName.FromAscii("ModDate"), out QpdfObject? modRaw)
                && ResolveOptionalValue(resolver, modRaw, governor, cancellationToken) is PdfByteString modText)
            {
                modDate = PdfText.Decode(modText.ValueMemory, memory, governor, cancellationToken);
            }

            if (parms.TryGet(PdfName.FromAscii("CheckSum"), out QpdfObject? checksumRaw)
                && ResolveOptionalValue(resolver, checksumRaw, governor, cancellationToken) is PdfByteString checksumBytes)
            {
                checksum = DecodeChecksum(checksumBytes.ValueMemory, memory, governor, cancellationToken);
            }

            if (parms.TryGet(PdfName.FromAscii("Size"), out QpdfObject? sizeRaw)
                && ResolveOptionalValue(resolver, sizeRaw, governor, cancellationToken) is PdfInteger sizeInteger && sizeInteger.Value >= 0L)
            {
                size = sizeInteger.Value;
            }
        }

        return new(mimeType, creationDate, modDate, checksum, size);
    }

    private static string DecodeChecksum(ReadOnlyMemory<byte> raw, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        int length = checked(raw.Length * 2); memory.Reserve(GovernedText.StringBytes(length));
        return string.Create(length, (raw, governor, token), static (target, state) =>
        {
            for (int i = 0; i < state.raw.Length; i++)
            {
                if ((i & 255) == 0) { state.token.ThrowIfCancellationRequested(); state.governor.CheckCancellation(); }
                byte value = state.raw.Span[i];
                target[2 * i] = "0123456789abcdef"[value >> 4]; target[2 * i + 1] = "0123456789abcdef"[value & 15];
            }
        });
    }
}
