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
    int StreamObjectNumber,
    long Size,
    PdfStream Stream,
    QpdfObject Spec,
    string? SpecReference,
    string? StreamReference);

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
        PdfDictionary? catalog = ResolveDictionary(engine, engine.Trailer, "Root");
        PdfDictionary? names = catalog is null ? null : ResolveDictionary(engine, catalog, "Names");
        PdfDictionary? tree = names is null ? null : ResolveDictionary(engine, names, "EmbeddedFiles");
        if (tree is null) { return result; }

        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        WalkNode(engine, tree, 0, GovernedIntSet.Create(traversal, governor, token), result,
            limits, governor, diagnostics, memory, traversal, token);
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

    private static PdfDictionary? ResolveDictionary(DocumentEngine engine, PdfDictionary parent, string key)
    {
        if (!parent.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        QpdfObject target = raw is PdfReference reference ? ResolveRequired(engine, reference, key) : raw;
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

    private static QpdfObject ResolveRequired(DocumentEngine engine, PdfReference reference, string role)
    {
        if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
        {
            throw new QpdfParseException($"Unresolvable embedded-files {role} reference.", null, reference.ObjectNumber);
        }

        return resolved;
    }

    private static void WalkNode(DocumentEngine engine, PdfDictionary node, int depth, GovernedIntSet visited,
        List<EmbeddedAttachment> result, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope memory, ParserMemoryScope traversal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (depth > limits.MaxNestingDepth)
        { throw new QpdfParseException("Embedded-files name tree exceeds nesting budget.", null, null); }
        if (node.TryGet(PdfName.FromAscii("Names"), out QpdfObject? namesRaw) && namesRaw is not null && namesRaw is not PdfNull)
        {
            if (namesRaw is not PdfArray names || names.Count % 2 != 0)
            { throw new QpdfParseException("Embedded-files name array must hold key/value pairs.", null, null); }
            for (int i = 0; i < names.Count; i += 2)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (names[i] is not PdfByteString keyBytes)
                { throw new QpdfParseException("Embedded-files name-tree keys must be strings.", null, null); }
                long before = memory.ReservedBytes;
                string key = PdfText.Decode(keyBytes.ValueMemory, memory, governor, token);
                EmbeddedAttachment attachment;
                try { attachment = ParseAttachment(engine, key, names[i + 1], governor, memory, token); }
                catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
                {
                    memory.Release(memory.ReservedBytes - before);
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unlistable attachment.");
                    continue;
                }
                ParserContainers.AddItem(memory, result, attachment, IntPtr.Size);
            }
        }
        if (node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) && kidsRaw is not null && kidsRaw is not PdfNull)
        {
            if (kidsRaw is not PdfArray kids)
            { throw new QpdfParseException("Embedded-files kids must be an array.", null, null); }
            for (int i = 0; i < kids.Count; i++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                QpdfObject item = kids[i];
                QpdfObject target = item is PdfReference reference ? ResolveRequired(engine, reference, "name-tree kid") : item;
                if (target is not PdfDictionary kid)
                { throw new QpdfParseException("Embedded-files kid must be a dictionary.", null, null); }
                if (item is PdfReference link && !visited.Add(traversal, link.ObjectNumber))
                { throw new QpdfParseException("Embedded-files name tree contains a cycle.", null, link.ObjectNumber); }
                WalkNode(engine, kid, depth + 1, visited, result, limits, governor, diagnostics, memory, traversal, token);
                if (item is PdfReference completed) { visited.Remove(completed.ObjectNumber); }
            }
        }
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
        QpdfObject target = value is PdfReference reference ? ResolveRequired(engine, reference, "file specification") : value;
        if (target is not PdfDictionary spec)
        {
            throw new QpdfParseException("Embedded file specification must be a dictionary.", null, null);
        }

        string fileName = string.Empty;
        foreach (string name in NameKeys)
        {
            if (spec.TryGet(PdfName.FromAscii(name), out QpdfObject? raw) && raw is PdfByteString text)
            {
                fileName = PdfText.Decode(text.ValueMemory, memory, governor, cancellationToken);
                break;
            }
        }

        string description = string.Empty;
        if (spec.TryGet(PdfName.FromAscii("Desc"), out QpdfObject? descRaw) && descRaw is PdfByteString descText)
        {
            description = PdfText.Decode(descText.ValueMemory, memory, governor, cancellationToken);
        }

        if (!spec.TryGet(PdfName.FromAscii("EF"), out QpdfObject? efRaw) || efRaw is null || efRaw is PdfNull)
        {
            throw new QpdfParseException("Embedded file specification has no embedded stream.", null, null);
        }

        QpdfObject efTarget = efRaw is PdfReference efReference ? ResolveRequired(engine, efReference, "embedded stream set") : efRaw;
        if (efTarget is not PdfDictionary ef)
        {
            throw new QpdfParseException("Embedded file specification has no embedded stream.", null, null);
        }

        int streamNumber = -1;
        string? streamReference = null;
        PdfStream? data = null;
        foreach (string name in NameKeys)
        {
            if (!ef.TryGet(PdfName.FromAscii(name), out QpdfObject? candidate) || candidate is null || candidate is PdfNull)
            {
                continue;
            }

            int candidateNumber = candidate is PdfReference candidateReference ? candidateReference.ObjectNumber : -1;
            QpdfObject resolved = candidate is PdfReference resolvedReference ? ResolveRequired(engine, resolvedReference, "embedded stream") : candidate;
            if (resolved is PdfStream stream)
            {
                data = stream;
                streamNumber = candidateNumber;
                if (candidate is PdfReference streamLink)
                {
                    memory.Reserve(72L);
                    streamReference = streamLink.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + streamLink.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + " R";
                }

                break;
            }
        }

        if (data is null)
        {
            throw new QpdfParseException("Embedded file specification has no embedded stream.", null, null);
        }

        PdfDictionary? parameters = null;
        if (data.Dictionary.TryGet(PdfName.FromAscii("Params"), out QpdfObject parametersRaw) && parametersRaw is not PdfNull)
        {
            parameters = (parametersRaw is PdfReference parametersReference
                ? ResolveRequired(engine, parametersReference, "embedded stream parameters") : parametersRaw) as PdfDictionary;
        }
        EmbeddedStreamMetadata metadata = ReadStreamMetadata(data, parameters, memory, governor, cancellationToken);

        governor.NoteOperation();
        if (value is PdfReference) { memory.Reserve(72L); }
        string? specReference = value is PdfReference specLink
            ? specLink.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + specLink.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + " R"
            : null;
        memory.Reserve(160L);
        return new EmbeddedAttachment(key, fileName, description, metadata.MimeType, metadata.CreationDate, metadata.ModDate,
            metadata.Checksum, streamNumber, metadata.Size, data, value, specReference, streamReference);
    }

    internal readonly record struct EmbeddedStreamMetadata(string MimeType, string CreationDate, string ModDate, string Checksum, long Size);

    internal static EmbeddedStreamMetadata ReadStreamMetadata(PdfStream data, PdfDictionary? parms, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor); cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        string mimeType = string.Empty;
        if (data.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtypeRaw) && subtypeRaw is PdfName subtype)
        {
            mimeType = PdfNameDisplay.Decode(subtype, memory, governor, cancellationToken);
        }

        string creationDate = string.Empty;
        string modDate = string.Empty;
        string checksum = string.Empty;
        long size = -1L;
        if (parms is not null)
        {
            if (parms.TryGet(PdfName.FromAscii("CreationDate"), out QpdfObject? creationRaw) && creationRaw is PdfByteString creationText)
            {
                creationDate = PdfText.Decode(creationText.ValueMemory, memory, governor, cancellationToken);
            }

            if (parms.TryGet(PdfName.FromAscii("ModDate"), out QpdfObject? modRaw) && modRaw is PdfByteString modText)
            {
                modDate = PdfText.Decode(modText.ValueMemory, memory, governor, cancellationToken);
            }

            if (parms.TryGet(PdfName.FromAscii("CheckSum"), out QpdfObject? checksumRaw) && checksumRaw is PdfByteString checksumBytes)
            {
                checksum = DecodeChecksum(checksumBytes.ValueMemory, memory, governor, cancellationToken);
            }

            if (parms.TryGet(PdfName.FromAscii("Size"), out QpdfObject? sizeRaw) && sizeRaw is PdfInteger sizeInteger && sizeInteger.Value >= 0L)
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
