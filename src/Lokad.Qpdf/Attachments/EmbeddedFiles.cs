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

    internal static List<EmbeddedAttachment> ReadAttachments(
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
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
        List<EmbeddedAttachment> result = new();
        PdfDictionary? catalog = ResolveDictionary(engine, engine.Trailer, "Root");
        if (catalog is null)
        {
            return result;
        }

        PdfDictionary? names = ResolveDictionary(engine, catalog, "Names");
        if (names is null)
        {
            return result;
        }

        PdfDictionary? tree = ResolveDictionary(engine, names, "EmbeddedFiles");
        if (tree is null)
        {
            return result;
        }

        List<(string Key, QpdfObject Value)> pairs = new();
        WalkNode(engine, tree, -1, 0, new HashSet<int>(), pairs, limits, governor, cancellationToken);
        foreach ((string key, QpdfObject value) in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            try
            {
                EmbeddedAttachment? attachment = ParseAttachment(engine, key, value, limits, governor, cancellationToken);
                if (attachment is not null)
                {
                    result.Add(attachment);
                }
            }
            catch (QpdfException ex) when (ex is not QpdfLimitException)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unlistable attachment.");
            }
        }

        result.Sort(static (left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
        return result;
    }

    internal static byte[] ExtractData(
        PdfStream stream,
        int number,
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics)
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
        byte[] raw = (byte[])stream.Data.Clone();
        governor.ReserveMemory(raw.Length);
        try
        {
            using FilterPlan stages = FilterChain.Resolve(stream.Dictionary, (IIndirectResolver)engine, governor, governor.CancellationToken);
            return FilterChain.Decode(raw, stages, limits, governor);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, $"Stream {number} extracted raw: decode unavailable.");
            return raw;
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

    private static void WalkNode(
        DocumentEngine engine,
        PdfDictionary node,
        int number,
        int depth,
        HashSet<int> visited,
        List<(string Key, QpdfObject Value)> pairs,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException("Embedded-files name tree exceeds nesting budget.", null, null);
        }

        governor.ChargeWork(1L);
        if (node.TryGet(PdfName.FromAscii("Names"), out QpdfObject? namesRaw) && namesRaw is not null && namesRaw is not PdfNull)
        {
            if (namesRaw is not PdfArray names || names.Count % 2 != 0)
            {
                throw new QpdfParseException("Embedded-files name array must hold key/value pairs.", null, null);
            }

            for (int i = 0; i < names.Count; i += 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (names[i] is not PdfByteString keyBytes)
                {
                    throw new QpdfParseException("Embedded-files name-tree keys must be strings.", null, null);
                }

                governor.ReserveMemory(keyBytes.Value.Length);
                pairs.Add((PdfText.Decode(keyBytes.Value), names[i + 1]));
            }
        }

        if (node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) && kidsRaw is not null && kidsRaw is not PdfNull)
        {
            if (kidsRaw is not PdfArray kids)
            {
                throw new QpdfParseException("Embedded-files kids must be an array.", null, null);
            }

            foreach (QpdfObject item in kids.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                int kidNumber = item is PdfReference kidReference ? kidReference.ObjectNumber : -1;
                QpdfObject target = item is PdfReference reference ? ResolveRequired(engine, reference, "name-tree kid") : item;
                if (target is not PdfDictionary kid)
                {
                    throw new QpdfParseException("Embedded-files kid must be a dictionary.", null, null);
                }

                bool track = kidNumber >= 0;
                if (track && !visited.Add(kidNumber))
                {
                    throw new QpdfParseException("Embedded-files name tree contains a cycle.", null, null);
                }

                try
                {
                    WalkNode(engine, kid, kidNumber, depth + 1, visited, pairs, limits, governor, cancellationToken);
                }
                finally
                {
                    if (track)
                    {
                        visited.Remove(kidNumber);
                    }
                }
            }
        }
    }

    private static EmbeddedAttachment? ParseAttachment(
        DocumentEngine engine,
        string key,
        QpdfObject value,
        QpdfLimits limits,
        ResourceGovernor governor,
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
                fileName = PdfText.Decode(text.Value);
                break;
            }
        }

        string description = string.Empty;
        if (spec.TryGet(PdfName.FromAscii("Desc"), out QpdfObject? descRaw) && descRaw is PdfByteString descText)
        {
            description = PdfText.Decode(descText.Value);
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
                    streamReference = streamLink.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + streamLink.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + " R";
                }

                break;
            }
        }

        if (data is null)
        {
            throw new QpdfParseException("Embedded file specification has no embedded stream.", null, null);
        }

        string mimeType = string.Empty;
        if (data.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtypeRaw) && subtypeRaw is PdfName subtype)
        {
            mimeType = subtype.DisplayText;
        }

        string creationDate = string.Empty;
        string modDate = string.Empty;
        string checksum = string.Empty;
        long size = -1L;
        PdfDictionary? parms = null;
        if (data.Dictionary.TryGet(PdfName.FromAscii("Params"), out QpdfObject? parmsRaw) && parmsRaw is not null && parmsRaw is not PdfNull)
        {
            QpdfObject parmsTarget = parmsRaw is PdfReference parmsReference ? ResolveRequired(engine, parmsReference, "embedded stream parameters") : parmsRaw;
            parms = parmsTarget as PdfDictionary;
        }

        if (parms is not null)
        {
            if (parms.TryGet(PdfName.FromAscii("CreationDate"), out QpdfObject? creationRaw) && creationRaw is PdfByteString creationText)
            {
                creationDate = PdfText.Decode(creationText.Value);
            }

            if (parms.TryGet(PdfName.FromAscii("ModDate"), out QpdfObject? modRaw) && modRaw is PdfByteString modText)
            {
                modDate = PdfText.Decode(modText.Value);
            }

            if (parms.TryGet(PdfName.FromAscii("CheckSum"), out QpdfObject? checksumRaw) && checksumRaw is PdfByteString checksumBytes)
            {
                checksum = Convert.ToHexStringLower(checksumBytes.Value);
            }

            if (parms.TryGet(PdfName.FromAscii("Size"), out QpdfObject? sizeRaw) && sizeRaw is PdfInteger sizeInteger && sizeInteger.Value >= 0L)
            {
                size = sizeInteger.Value;
            }
        }

        governor.ChargeWork(1L);
        string? specReference = value is PdfReference specLink
            ? specLink.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + specLink.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + " R"
            : null;
        return new EmbeddedAttachment(key, fileName, description, mimeType, creationDate, modDate, checksum, streamNumber, size, data, value, specReference, streamReference);
    }
}
