using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>
/// Inline image externalization: images at or above a byte threshold become
/// regular XObjects referenced by generated Do operators. Mirrors the
/// upstream tracker including abbreviation expansion, colorspace lookup,
/// unique naming, and warn-and-keep failure policy.
/// </summary>
internal static class InlineImages
{
    private static List<int> ReplacedEntryIds(QpdfObject? value)
    {
        List<int> ids = new();
        if (value is PdfReference reference)
        {
            ids.Add(reference.ObjectNumber);
        }
        else if (value is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is PdfReference item)
                {
                    ids.Add(item.ObjectNumber);
                }
            }
        }

        return ids;
    }

    internal static PdfDictionary ExternalizePage(
        GraphCopier copier,
        int pageId,
        PdfDictionary page,
        long minBytes,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using AnalysisChunks? chunks = CollectDecodedContents(copier, page, limits, governor, cancellationToken);
        if (chunks is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Unable to filter content stream; not attempting to externalize inline images.");
            return page;
        }

        using JoinedContent joined = JoinedContent.Create(chunks.Parts, governor, cancellationToken);
        chunks.Dispose();
        RewriteResult rewritten = RewriteContent(copier, page, joined.RawBytes, minBytes, compress, level, limits, governor, diagnostics, cancellationToken);
        if (!rewritten.Changed || rewritten.Stream is null || rewritten.Resources is null)
        {
            return page;
        }

        PdfName contentsKey = PdfName.FromAscii("Contents");
        PdfName resourcesKey = PdfName.FromAscii("Resources");
        page.TryGet(contentsKey, out QpdfObject? contentsRaw);
        page.TryGet(resourcesKey, out QpdfObject? resourcesRaw);
        PdfDictionary replaced = page;
        List<int> orphaned = new();
        if (contentsRaw is PdfReference contentsRef)
        {
            copier.ReplaceCopiedObject(contentsRef.ObjectNumber, rewritten.Stream);
        }
        else
        {
            PdfReference? link = copier.AddIndirectObject(rewritten.Stream);
            if (link is null)
            {
                return page;
            }

            orphaned.AddRange(ReplacedEntryIds(contentsRaw));
            replaced = ReplaceEntry(replaced, contentsKey, link, governor);
        }

        if (resourcesRaw is PdfReference resourcesRef)
        {
            copier.ReplaceCopiedObject(resourcesRef.ObjectNumber, rewritten.Resources);
        }
        else
        {
            replaced = ReplaceEntry(replaced, resourcesKey, rewritten.Resources, governor);
        }

        copier.ReplaceCopiedObject(pageId, replaced);
        if (orphaned.Count > 0)
        {
            copier.DropPoolObjectsIfUnreferenced(orphaned, cancellationToken);
        }

        return replaced;
    }

    internal static void ExternalizeForm(
        GraphCopier copier,
        int formId,
        PdfStream form,
        long minBytes,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, cancellationToken);
        if (!chunks.TryAddForInlineImages(form, resolver, limits))
        {
            return;
        }

        RewriteResult rewritten = RewriteContent(copier, null, chunks.Parts[0], minBytes, compress, level, limits, governor, diagnostics, cancellationToken);
        chunks.Dispose();
        resolverMemory.Dispose();
        if (!rewritten.Changed || rewritten.Stream is null || rewritten.Resources is null)
        {
            return;
        }

        PdfDictionary formDict = ReplaceEntry(form.Dictionary, PdfName.FromAscii("Resources"), rewritten.Resources, governor);
        PdfDictionary encodedDict = rewritten.Stream.Dictionary;
        if (encodedDict.TryGet(PdfName.FromAscii("Filter"), out QpdfObject? filter) && filter is not null)
        {
            formDict = ReplaceEntry(formDict, PdfName.FromAscii("Filter"), filter, governor);
        }
        else
        {
            Dictionary<PdfName, QpdfObject> stripped = new();
            foreach (PdfName key in formDict.Keys)
            {
                if (key.DisplayText == "Filter" || key.DisplayText == "DecodeParms")
                {
                    continue;
                }

                if (formDict.TryGet(key, out QpdfObject? kept) && kept is not null)
                {
                    stripped[key] = kept;
                }
            }

            formDict = new PdfDictionary(stripped);
        }

        copier.ReplaceCopiedObject(formId, new PdfStream(formDict, rewritten.Stream.Data));
    }

    private sealed record RewriteResult(bool Changed, PdfStream? Stream, PdfDictionary? Resources, byte[] Data);

    private static AnalysisChunks? CollectDecodedContents(
        GraphCopier copier,
        PdfDictionary page,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contents) ||
            contents is PdfNull || contents is not (PdfArray or PdfReference or PdfStream))
        {
            return AnalysisChunks.Create(governor, cancellationToken);
        }

        using ContentEntries streams = ContentEntries.Create(governor, cancellationToken);
        PdfArray? array = contents as PdfArray;
        int count = array?.Count ?? 1;
        // Validate every entry before decoding any stream, preserving failure order.
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
            QpdfObject? item = array is null ? contents : array[index];
            int? objectId = null;
            if (item is PdfReference reference)
            {
                objectId = reference.ObjectNumber;
                PdfObjectLookup.TryResolve(reference, copier.CopiedObjects, governor, cancellationToken, out item);
            }
            if (item is not PdfStream stream) { return null; }
            streams.Add(new(objectId, index, stream));
        }
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        AnalysisChunks chunks = AnalysisChunks.Create(governor, cancellationToken);
        try
        {
            for (int index = 0; index < streams.Count; index++)
            {
                if (!chunks.TryAddForInlineImages(streams.Get(index).Stream, resolver, limits))
                { chunks.Dispose(); return null; }
            }
            return chunks;
        }
        catch { chunks.Dispose(); throw; }
    }

    private static PdfDictionary ReplaceEntry(PdfDictionary dictionary, PdfName key, QpdfObject value, ResourceGovernor governor)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName existing in dictionary.Keys)
        {
            if (dictionary.TryGet(existing, out QpdfObject? found) && found is not null)
            {
                entries[existing] = found;
            }
        }

        governor.ReserveMemory(64L);
        entries[key] = value;
        return new PdfDictionary(entries);
    }

    private static QpdfObject? ResolveOrNull(PoolResolver resolver, PdfReference reference)
    {
        if (((IIndirectResolver)resolver).TryResolve(reference, out QpdfObject? found) && found is not null && found is not PdfNull)
        {
            return found;
        }

        return null;
    }

    private static RewriteResult RewriteContent(
        GraphCopier copier,
        PdfDictionary? page,
        byte[] joined,
        long minBytes,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        PoolResolver resolver = new(copier.CopiedObjects);
        PdfDictionary resources = EnsureResources(copier, page, resolver, governor);
        ContentTokenizer tokenizer = new(joined, limits, governor)
        {
            IncludeComments = true,
            IncludeWhitespace = true,
        };
        List<byte> output = new(joined.Length + 64);
        Dictionary<string, PdfStream> images = new(StringComparer.Ordinal);
        bool inImage = false;
        long imageStart = 0;
        long dictStart = 0;
        bool anyImages = false;
        int suffix = 1;
        try
        {
            while (tokenizer.TryReadToken(out ContentToken token))
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (!inImage)
                {
                    if (token.Kind == ContentTokenKind.Operator && TokenText(token) == "BI")
                    {
                        inImage = true;
                        imageStart = token.Offset;
                        dictStart = token.Offset + token.Data.Length;
                        continue;
                    }

                    AppendRaw(output, token);
                    continue;
                }

                if (token.Kind == ContentTokenKind.Operator && TokenText(token) == "ID")
                {
                    if (!tokenizer.TryReadInlineImageData(out ContentToken image))
                    {
                        inImage = false;
                        continue;
                    }

                    string? name = ExternalizeOne(copier, joined, imageStart, dictStart, token, image, resources, minBytes, images, ref suffix, limits, governor, diagnostics, resolver, cancellationToken);
                    if (name is not null)
                    {
                        anyImages = true;
                        AppendAscii(output, "/" + name + " Do\n");
                    }
                    else
                    {
                        for (long at = imageStart; at < tokenizer.Position; at++)
                        {
                            output.Add(joined[(int)at]);
                        }
                    }

                    inImage = false;
                    continue;
                }

                if (token.Kind == ContentTokenKind.Operator && TokenText(token) == "EI")
                {
                    inImage = false;
                    continue;
                }
            }
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Unable to filter content stream; not attempting to externalize inline images.");
            return new RewriteResult(false, null!, null, joined);
        }

        if (!anyImages)
        {
            return new RewriteResult(false, null!, null, joined);
        }

        foreach (KeyValuePair<string, PdfStream> image in images)
        {
            PdfReference? link = copier.AddIndirectObject(image.Value);
            if (link is not null)
            {
                resources = AddXObjectEntry(resources, image.Key, link, governor);
            }
        }

        byte[] bytes = [.. output];
        PdfStream stream;
        if (compress && bytes.Length > 0)
        {
            byte[] encoded = FlateFilter.Encode(bytes, level, limits, governor);
            governor.ReserveMemory(64L);
            stream = new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"),
            }), encoded);
        }
        else
        {
            stream = new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), bytes);
        }

        return new RewriteResult(true, stream, resources, bytes);
    }
    private static string TokenText(ContentToken token)
    {
        return Encoding.ASCII.GetString(token.Data.Span);
    }

    private static void AppendRaw(List<byte> output, ContentToken token)
    {
        foreach (byte b in token.Data.Span)
        {
            output.Add(b);
        }
    }

    private static void AppendAscii(List<byte> output, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        foreach (byte b in bytes)
        {
            output.Add(b);
        }
    }

    private static PdfDictionary EnsureResources(
        GraphCopier copier,
        PdfDictionary? page,
        PoolResolver resolver,
        ResourceGovernor governor)
    {
        PdfDictionary? existing = null;
        if (page is not null
            && page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject? resourcesRaw)
            && resourcesRaw is not null
            && resourcesRaw is not PdfNull)
        {
            QpdfObject target = resourcesRaw is PdfReference resourcesReference
                ? ResolveOrNull(resolver, resourcesReference) ?? resourcesRaw
                : resourcesRaw;
            existing = target as PdfDictionary;
        }

        Dictionary<PdfName, QpdfObject> entries = new();
        if (existing is not null)
        {
            foreach (PdfName key in existing.Keys)
            {
                if (existing.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    entries[key] = found;
                }
            }
        }

        if (!entries.TryGetValue(PdfName.FromAscii("XObject"), out QpdfObject? xobjects)
            || xobjects is not PdfDictionary)
        {
            governor.ReserveMemory(64L);
            entries[PdfName.FromAscii("XObject")] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>());
        }

        governor.ReserveMemory(128L);
        return new PdfDictionary(entries);
    }

    private static PdfDictionary AddXObjectEntry(
        PdfDictionary resources,
        string name,
        PdfReference link,
        ResourceGovernor governor)
    {
        if (!resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject? xobjectsRaw) || xobjectsRaw is not PdfDictionary xobjects)
        {
            return resources;
        }

        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in xobjects.Keys)
        {
            if (xobjects.TryGet(key, out QpdfObject? found) && found is not null)
            {
                entries[key] = found;
            }
        }

        governor.ReserveMemory(64L);
        entries[new PdfName(Encoding.ASCII.GetBytes(name))] = link;
        PdfDictionary rebuilt = new(entries);
        Dictionary<PdfName, QpdfObject> resourcesEntries = new();
        foreach (PdfName key in resources.Keys)
        {
            if (resources.TryGet(key, out QpdfObject? found) && found is not null)
            {
                resourcesEntries[key] = found;
            }
        }

        governor.ReserveMemory(64L);
        resourcesEntries[PdfName.FromAscii("XObject")] = rebuilt;
        return new PdfDictionary(resourcesEntries);
    }
    private static string? ExternalizeOne(
        GraphCopier copier,
        byte[] joined,
        long imageStart,
        long dictStart,
        ContentToken idToken,
        ContentToken image,
        PdfDictionary resources,
        long minBytes,
        Dictionary<string, PdfStream> images,
        ref int suffix,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        PoolResolver resolver,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.ChargeWork(1L);
        byte[] data = image.Data.ToArray();
        governor.ReserveMemory(data.Length);
        if (data.Length < minBytes)
        {
            return null;
        }

        byte[] prefix = Encoding.ASCII.GetBytes("<< ");
        byte[] suffixBytes = Encoding.ASCII.GetBytes(" >>");
        int dictLength = (int)(idToken.Offset - dictStart);
        byte[] synthetic = new byte[prefix.Length + dictLength + suffixBytes.Length];
        Buffer.BlockCopy(prefix, 0, synthetic, 0, prefix.Length);
        Buffer.BlockCopy(joined, (int)dictStart, synthetic, prefix.Length, dictLength);
        Buffer.BlockCopy(suffixBytes, 0, synthetic, prefix.Length + dictLength, suffixBytes.Length);
        PdfParser parser = new(limits, governor, diagnostics, resolver);
        PdfTokenizer tokenizer = new(synthetic);
        QpdfObject parsed = parser.ParseValue(tokenizer, 0);
        if (parsed is not PdfDictionary raw)
        {
            throw new QpdfParseException("Inline image dictionary must be a dictionary.", idToken.Offset, null);
        }

        PdfDictionary converted = ConvertImageDict(raw, resources, governor, diagnostics, cancellationToken);
        governor.ReserveMemory(64L);
        Dictionary<PdfName, QpdfObject> entries = new();
        entries[PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject");
        entries[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Image");
        foreach (PdfName key in converted.Keys)
        {
            if (converted.TryGet(key, out QpdfObject? found) && found is not null)
            {
                entries[key] = found;
            }
        }

        PdfStream stream = new(new PdfDictionary(entries), data);
        string name = UniqueImageName(resources, ref suffix, governor);
        images[name] = stream;
        return name;
    }
    private static string UniqueImageName(PdfDictionary resources, ref int suffix, ResourceGovernor governor)
    {
        HashSet<string> taken = new(StringComparer.Ordinal);
        if (resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject? xobjectsRaw) && xobjectsRaw is PdfDictionary xobjects)
        {
            foreach (PdfName key in xobjects.Keys)
            {
                taken.Add(key.DisplayText);
            }
        }

        while (true)
        {
            string candidate = "IIm" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            suffix += 1;
            governor.ChargeWork(1L);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static PdfDictionary ConvertImageDict(
        PdfDictionary raw,
        PdfDictionary resources,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
        => InlineImageDictionary.Convert(raw, resources, governor, diagnostics, cancellationToken);
}