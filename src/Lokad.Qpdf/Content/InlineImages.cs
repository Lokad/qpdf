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
        PoolResolver resolver = new(copier.CopiedObjects);
        List<byte[]>? chunks = CollectDecodedContents(copier, page, resolver, limits, governor, cancellationToken);
        if (chunks is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Unable to filter content stream; not attempting to externalize inline images.");
            return page;
        }

        byte[] joined = JoinBytes(chunks, governor);
        RewriteResult rewritten = RewriteContent(copier, page, joined, minBytes, compress, level, limits, governor, diagnostics, cancellationToken);
        if (!rewritten.Changed || rewritten.Stream is null || rewritten.Resources is null)
        {
            return page;
        }

        PdfReference? link = copier.AddIndirectObject(rewritten.Stream);
        if (link is null)
        {
            return page;
        }

        PdfDictionary replaced = ReplaceEntry(page, PdfName.FromAscii("Contents"), link, governor);
        replaced = ReplaceEntry(replaced, PdfName.FromAscii("Resources"), rewritten.Resources, governor);
        copier.ReplaceCopiedObject(pageId, replaced);
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
        PoolResolver resolver = new(copier.CopiedObjects);
        byte[]? decoded = DecodeStream(form, resolver, limits, governor);
        if (decoded is null)
        {
            return;
        }

        RewriteResult rewritten = RewriteContent(copier, null, decoded, minBytes, compress, level, limits, governor, diagnostics, cancellationToken);
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

    private static List<byte[]>? CollectDecodedContents(
        GraphCopier copier,
        PdfDictionary page,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null || contents is PdfNull)
        {
            return new List<byte[]>();
        }

        List<PdfStream> streams = new();
        if (contents is PdfArray array)
        {
            foreach (QpdfObject item in array.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QpdfObject target = item is PdfReference reference
                    ? ResolveOrNull(resolver, reference) ?? item
                    : item;
                if (target is not PdfStream stream)
                {
                    return null;
                }

                streams.Add(stream);
            }
        }
        else if (contents is PdfReference singleReference)
        {
            QpdfObject? target = ResolveOrNull(resolver, singleReference);
            if (target is not PdfStream stream)
            {
                return null;
            }

            streams.Add(stream);
        }
        else if (contents is PdfStream single)
        {
            streams.Add(single);
        }
        else
        {
            return new List<byte[]>();
        }

        List<byte[]> chunks = new(streams.Count);
        foreach (PdfStream stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            byte[]? decoded = DecodeStream(stream, resolver, limits, governor);
            if (decoded is null)
            {
                return null;
            }

            chunks.Add(decoded);
        }

        return chunks;
    }

    private static byte[]? DecodeStream(
        PdfStream stream,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        IReadOnlyList<FilterStage> stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, resolver);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            return stream.Data;
        }

        if (stages.Count == 0 || !FilterChain.AllStagesInLevel(stages, "specialized"))
        {
            return stream.Data;
        }

        try
        {
            return FilterChain.Decode(stream.Data, stages, limits, governor);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            return null;
        }
    }
    private static byte[] JoinBytes(List<byte[]> parts, ResourceGovernor governor)
    {
        List<byte[]> joined = new(parts.Count * 2);
        bool needNewline = false;
        foreach (byte[] part in parts)
        {
            governor.ChargeWork(1L);
            if (needNewline)
            {
                joined.Add([(byte)'\n']);
            }

            joined.Add(part);
            needNewline = part.Length == 0 || part[part.Length - 1] != (byte)'\n';
        }

        int total = 0;
        foreach (byte[] part in joined)
        {
            checked
            {
                total += part.Length;
            }
        }

        governor.ReserveMemory(total);
        byte[] output = new byte[total];
        int at = 0;
        foreach (byte[] part in joined)
        {
            Buffer.BlockCopy(part, 0, output, at, part.Length);
            at += part.Length;
        }

        return output;
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

        PdfDictionary converted = ConvertImageDict(raw, resources, governor, diagnostics);
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
        DiagnosticBag diagnostics)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in raw.Keys)
        {
            if (!raw.TryGet(key, out QpdfObject? value) || value is null || value is PdfNull)
            {
                continue;
            }

            string name = key.DisplayText;
            string mapped = name;
            if (mapped == "BPC")
            {
                mapped = "BitsPerComponent";
            }
            else if (mapped == "CS")
            {
                mapped = "ColorSpace";
            }
            else if (mapped == "D")
            {
                mapped = "Decode";
            }
            else if (mapped == "DP")
            {
                mapped = "DecodeParms";
            }
            else if (mapped == "F")
            {
                mapped = "Filter";
            }
            else if (mapped == "H")
            {
                mapped = "Height";
            }
            else if (mapped == "IM")
            {
                mapped = "ImageMask";
            }
            else if (mapped == "I")
            {
                mapped = "Interpolate";
            }
            else if (mapped == "W")
            {
                mapped = "Width";
            }

            if (mapped == "ColorSpace" && value is PdfName colorName)
            {
                string color = colorName.DisplayText;
                if (color == "G")
                {
                    color = "DeviceGray";
                }
                else if (color == "RGB")
                {
                    color = "DeviceRGB";
                }
                else if (color == "CMYK")
                {
                    color = "DeviceCMYK";
                }
                else if (color == "I")
                {
                    color = "Indexed";
                }
                else
                {
                    QpdfObject? lookedUp = null;
                    if (resources.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject? spacesRaw) && spacesRaw is PdfDictionary spaces)
                    {
                        foreach (PdfName spaceKey in spaces.Keys)
                        {
                            if (spaceKey.DisplayText == color && spaces.TryGet(spaceKey, out QpdfObject? spaceValue) && spaceValue is not null)
                            {
                                lookedUp = spaceValue;
                                break;
                            }
                        }
                    }

                    if (lookedUp is not null)
                    {
                        value = lookedUp;
                    }
                    else
                    {
                        diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Unable to resolve image colorspace; keeping the original name.");
                    }

                    color = string.Empty;
                }

                if (color.Length > 0)
                {
                    value = new PdfName(Encoding.ASCII.GetBytes(color));
                }
            }
            else if (mapped == "Filter")
            {
                value = ExpandImageFilters(value);
            }

            governor.ReserveMemory(64L);
            entries[PdfName.FromAscii(mapped)] = value;
        }

        return new PdfDictionary(entries);
    }
    private static QpdfObject ExpandImageFilters(QpdfObject value)
    {
        if (value is PdfName single)
        {
            string expanded = ExpandImageFilterName(single.DisplayText);
            if (expanded.Length > 0)
            {
                return new PdfName(Encoding.ASCII.GetBytes(expanded));
            }

            return value;
        }

        if (value is PdfArray array)
        {
            QpdfObject[] items = array.Items;
            List<QpdfObject> expanded = new(items.Length);
            foreach (QpdfObject item in items)
            {
                if (item is PdfName name)
                {
                    string full = ExpandImageFilterName(name.DisplayText);
                    expanded.Add(full.Length > 0 ? new PdfName(Encoding.ASCII.GetBytes(full)) : item);
                    continue;
                }

                expanded.Add(item);
            }

            return new PdfArray(expanded);
        }

        return value;
    }

    private static string ExpandImageFilterName(string abbreviated)
    {
        if (abbreviated == "AHx")
        {
            return "ASCIIHexDecode";
        }

        if (abbreviated == "A85")
        {
            return "ASCII85Decode";
        }

        if (abbreviated == "LZW")
        {
            return "LZWDecode";
        }

        if (abbreviated == "Fl")
        {
            return "FlateDecode";
        }

        if (abbreviated == "RL")
        {
            return "RunLengthDecode";
        }

        if (abbreviated == "CCF")
        {
            return "CCITTFaxDecode";
        }

        if (abbreviated == "DCT")
        {
            return "DCTDecode";
        }

        return string.Empty;
    }
}
