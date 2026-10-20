using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Image optimization request: eligibility thresholds and JPEG quality.</summary>
internal sealed record ImageOptimizeRequest(
    bool Optimize,
    long MinWidth,
    long MinHeight,
    long MinArea,
    bool KeepInline,
    int? Quality)
{
    internal static ImageOptimizeRequest Disabled { get; } = new(false, 128L, 128L, 16384L, false, null);
}

/// <summary>
/// Image optimization over copied pages: eligible image XObjects (8 bits per
/// component, DeviceGray/DeviceRGB, above the size thresholds) are
/// JPEG-compressed and replaced only when the result is strictly smaller.
/// Mirrors the upstream size and colorspace rules: already-DCT images are
/// left alone unless a quality forces recompression, and every skip is
/// silent. Inline images are never touched here; four-component CMYK has no
/// raw-sample encode path and stays as is. Recursion follows form XObjects
/// with a cycle guard; shared images optimize once and stay consistent.
/// </summary>
internal static class ImageOptimizer
{
    internal static void Apply(
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        ImageOptimizeRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        if (!request.Optimize)
        {
            return;
        }

        HashSet<int> visitedForms = new();
        foreach ((GraphCopier copier, int pageId) in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary page)
            {
                continue;
            }

            PoolResolver resolver = new(copier.CopiedObjects);
            PdfDictionary? rebuilt = ProcessPage(copier, resolver, page, pageId, request, limits, governor, visitedForms, cancellationToken);
            if (rebuilt is not null)
            {
                copier.ReplaceCopiedObject(pageId, rebuilt);
            }
        }
    }

    private static PdfDictionary? ProcessPage(
        GraphCopier copier,
        PoolResolver resolver,
        PdfDictionary page,
        int pageId,
        ImageOptimizeRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        HashSet<int> visitedForms,
        CancellationToken cancellationToken)
    {
        if (!page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject? resourcesRaw) || resourcesRaw is null || resourcesRaw is PdfNull)
        {
            return null;
        }

        int? resourcesId = (resourcesRaw as PdfReference)?.ObjectNumber;
        QpdfObject resourcesTarget = resourcesRaw is PdfReference resourcesReference
            ? ResolveOrNull(resolver, resourcesReference) ?? resourcesRaw
            : resourcesRaw;
        if (resourcesTarget is not PdfDictionary resources)
        {
            return null;
        }

        PdfDictionary? rebuilt = ProcessResources(copier, resolver, resources, resourcesId, request, limits, governor, visitedForms, cancellationToken);
        if (rebuilt is null)
        {
            return null;
        }

        if (resourcesId.HasValue)
        {
            copier.ReplaceCopiedObject(resourcesId.Value, rebuilt);
            return null;
        }

        return WithEntry(page, PdfName.FromAscii("Resources"), rebuilt, governor);
    }

    private static PdfDictionary? ProcessResources(
        GraphCopier copier,
        PoolResolver resolver,
        PdfDictionary resources,
        int? resourcesId,
        ImageOptimizeRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        HashSet<int> visitedForms,
        CancellationToken cancellationToken)
    {
        if (!resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject? xobjectRaw) || xobjectRaw is null || xobjectRaw is PdfNull)
        {
            return null;
        }

        int? xobjectId = (xobjectRaw as PdfReference)?.ObjectNumber;
        QpdfObject xobjectTarget = xobjectRaw is PdfReference xobjectReference
            ? ResolveOrNull(resolver, xobjectReference) ?? xobjectRaw
            : xobjectRaw;
        if (xobjectTarget is not PdfDictionary xobjects)
        {
            return null;
        }

        PdfDictionary? rebuilt = ProcessXObjects(copier, resolver, xobjects, request, limits, governor, visitedForms, cancellationToken);
        if (rebuilt is null)
        {
            return null;
        }

        if (xobjectId.HasValue)
        {
            copier.ReplaceCopiedObject(xobjectId.Value, rebuilt);
            return null;
        }

        return WithEntry(resources, PdfName.FromAscii("XObject"), rebuilt, governor);
    }

    private static PdfDictionary? ProcessXObjects(
        GraphCopier copier,
        PoolResolver resolver,
        PdfDictionary xobjects,
        ImageOptimizeRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        HashSet<int> visitedForms,
        CancellationToken cancellationToken)
    {
        Dictionary<PdfName, QpdfObject>? entries = null;
        foreach (PdfName key in xobjects.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!xobjects.TryGet(key, out QpdfObject? entry) || entry is null)
            {
                continue;
            }

            int? entryId = (entry as PdfReference)?.ObjectNumber;
            QpdfObject target = entry is PdfReference entryReference
                ? ResolveOrNull(resolver, entryReference) ?? entry
                : entry;
            if (target is not PdfStream stream)
            {
                continue;
            }

            if (!IsSubtype(stream.Dictionary, "Image"))
            {
                if (IsSubtype(stream.Dictionary, "Form"))
                {
                    PdfStream? rebuiltForm = ProcessForm(copier, resolver, stream, entryId, request, limits, governor, visitedForms, cancellationToken);
                    if (rebuiltForm is not null)
                    {
                        if (entryId.HasValue)
                        {
                            copier.ReplaceCopiedObject(entryId.Value, rebuiltForm);
                        }
                        else
                        {
                            entries ??= CopyEntries(xobjects, governor);
                            entries[key] = rebuiltForm;
                        }
                    }
                }

                continue;
            }

            PdfStream? optimized = TryOptimize(stream, request, resolver, limits, governor, cancellationToken);
            if (optimized is null)
            {
                continue;
            }

            governor.NoteOperation();
            if (entryId.HasValue)
            {
                if (copier.CopiedObjects.ContainsKey(entryId.Value))
                {
                    copier.ReplaceCopiedObject(entryId.Value, optimized);
                }
            }
            else
            {
                entries ??= CopyEntries(xobjects, governor);
                entries[key] = optimized;
            }
        }

        if (entries is null)
        {
            return null;
        }

        return new PdfDictionary(entries);
    }

    private static PdfStream? ProcessForm(
        GraphCopier copier,
        PoolResolver resolver,
        PdfStream form,
        int? formId,
        ImageOptimizeRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        HashSet<int> visitedForms,
        CancellationToken cancellationToken)
    {
        if (formId.HasValue && !visitedForms.Add(formId.Value))
        {
            return null;
        }

        if (!form.Dictionary.TryGet(PdfName.FromAscii("Resources"), out QpdfObject? resourcesRaw) || resourcesRaw is null || resourcesRaw is PdfNull)
        {
            return null;
        }

        int? resourcesId = (resourcesRaw as PdfReference)?.ObjectNumber;
        QpdfObject resourcesTarget = resourcesRaw is PdfReference resourcesReference
            ? ResolveOrNull(resolver, resourcesReference) ?? resourcesRaw
            : resourcesRaw;
        if (resourcesTarget is not PdfDictionary resources)
        {
            return null;
        }

        PdfDictionary? rebuilt = ProcessResources(copier, resolver, resources, resourcesId, request, limits, governor, visitedForms, cancellationToken);
        if (rebuilt is null)
        {
            return null;
        }

        if (resourcesId.HasValue)
        {
            copier.ReplaceCopiedObject(resourcesId.Value, rebuilt);
            return null;
        }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
        PdfStream result = ResourceRebuild.ReplaceResources(graph, form, rebuilt);
        graph.Retain(); return result;
    }

    internal static PdfStream? TryOptimize(
        PdfStream image,
        ImageOptimizeRequest request,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (!ReadDimensions(image.Dictionary, resolver, out long width, out long height) || width <= 0L || height <= 0L)
        {
            return null;
        }

        if (!ReadBitsPerComponent(image.Dictionary, resolver))
        {
            return null;
        }

        int components = ReadComponents(image.Dictionary, resolver);
        if (components != 1 && components != 3)
        {
            return null;
        }

        if ((request.MinWidth > 0L && width <= request.MinWidth)
            || (request.MinHeight > 0L && height <= request.MinHeight)
            || (request.MinArea > 0L && checked(width * height) <= request.MinArea))
        {
            return null;
        }

        FilterPlan stages;
        try
        {
            stages = FilterChain.Resolve(image.Dictionary, resolver, governor, cancellationToken);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            return null;
        }
        using FilterPlan stageOwner = stages;

        bool isDct = false;
        foreach (FilterStage stage in stages)
        {
            if (stage.Name == DctFilter.FilterName)
            {
                isDct = true;
                break;
            }
        }

        if (isDct && !request.Quality.HasValue)
        {
            return null;
        }

        string level = request.Quality.HasValue ? "all" : "specialized";
        if (!FilterChain.AllStagesInLevel(stages, level))
        {
            return null;
        }

        if (governor.JpegCodec is null)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter, DctFilter.FilterName,
                "JPEG image optimization requires a codec supplied by the host.");
        }

        using ParserMemoryScope samples = ParserMemoryScope.Create(governor);
        byte[] raw;
        try
        {
            raw = image.ArrayData(samples, governor, cancellationToken);
            if (stages.Count == 0)
            {
                if (raw.LongLength > limits.MaxDecodedBytes)
                { throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), raw.LongLength, limits.MaxDecodedBytes); }
                governor.ChargeDecodedBytes(raw.Length);
            }
            else { raw = FilterChain.Decode(raw, stages, limits, governor, samples, cancellationToken); }
        }
        catch (QpdfFilterException)
        {
            return null;
        }

        stages.Dispose();
        long expected = checked(checked(width * height) * components);
        if ((long)raw.Length != expected)
        {
            // Inline-image extraction conventionally retains the separator whitespace before EI;
            // trim trailing PDF whitespace so such samples still validate, and skip anything else.
            byte[]? trimmed = TrimSeparators(raw, expected, samples, governor, cancellationToken);
            if (trimmed is null)
            {
                return null;
            }

            raw = trimmed;
        }

        if (width > int.MaxValue || height > int.MaxValue)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        using OwnedAllocationBatch candidate = OwnedAllocationBatch.Create(governor, cancellationToken);
        int quality = request.Quality ?? JpegCodec.DefaultQuality;
        byte[] encoded;
        try
        {
            encoded = components == 1
                ? JpegCodec.EncodeGray(raw, (int)width, (int)height, quality, limits, governor)
                : JpegCodec.EncodeRgb(raw, (int)width, (int)height, quality, limits, governor);
        }
        catch (QpdfFilterException)
        {
            return null;
        }

        long encodedBytes = encoded.Length == 0 ? 0L : encoded.LongLength + GovernedBufferStream.ArrayOverheadBytes;
        candidate.Memory.AdoptReserved(encodedBytes); candidate.Add(encodedBytes);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (encoded.Length >= image.RawMemory.Length)
        {
            return null;
        }

        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(candidate, out long allocation);
        using (PdfDictionary.EntryEnumerator source = image.Dictionary.GetEntryEnumerator())
        {
            while (source.MoveNext(governor, cancellationToken))
            {
                PdfName key = source.Current.Key;
                if (PdfDictionaryLookup.NameEquals(key, "Filter"u8, governor, cancellationToken)
                    || PdfDictionaryLookup.NameEquals(key, "DecodeParms"u8, governor, cancellationToken)
                    || PdfDictionaryLookup.NameEquals(key, "Length"u8, governor, cancellationToken)) { continue; }
                entries.Assign(candidate.Memory, governor, key, source.Current.Value, cancellationToken);
            }
        }
        PdfName dct = PdfDictionaryRebuild.CreateName(candidate, "DCTDecode"u8);
        PdfName filter = PdfDictionaryRebuild.CreateName(candidate, "Filter"u8);
        entries.Assign(candidate.Memory, governor, filter, dct, cancellationToken);
        PdfDictionary header = PdfDictionaryRebuild.Complete(candidate, entries, allocation);
        candidate.Memory.Reserve(32L);
        PdfStream optimized = PdfStream.FromOwnedData(header, encoded);
        candidate.Add(32L); candidate.Retain(); return optimized;
    }

    private static byte[]? TrimSeparators(byte[] raw, long expected, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        int length = raw.Length;
        while ((long)length > expected && length > 0 && IsPdfWhitespace(raw[length - 1]))
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            length -= 1;
        }

        if ((long)length != expected)
        {
            return null;
        }

        if (length == raw.Length)
        {
            return raw;
        }

        memory.Reserve(checked(32L + length));
        byte[] trimmed = new byte[length];
        GovernedBufferStream.CopyBytes(raw.AsSpan(0, length), trimmed, governor, cancellationToken, CancellationToken.None);
        return trimmed;
    }

    private static bool IsPdfWhitespace(byte value)
    {
        return value == 0 || value == 9 || value == 10 || value == 12 || value == 13 || value == 32;
    }

    private static bool IsSubtype(PdfDictionary dictionary, string subtype)
    {
        return dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? raw)
            && raw is PdfName name
            && name.DisplayText == subtype;
    }

    private static bool ReadDimensions(PdfDictionary dictionary, PoolResolver resolver, out long width, out long height)
    {
        width = 0L;
        height = 0L;
        return ReadLong(dictionary, resolver, "Width", out width) && ReadLong(dictionary, resolver, "Height", out height);
    }

    private static bool ReadLong(PdfDictionary dictionary, PoolResolver resolver, string key, out long value)
    {
        value = 0L;
        if (!dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return false;
        }

        QpdfObject target = raw is PdfReference reference
            ? ResolveOrNull(resolver, reference) ?? raw
            : raw;
        if (target is PdfInteger integer)
        {
            value = integer.Value;
            return true;
        }

        if (target is PdfReal real)
        {
            if (!double.IsFinite(real.Value))
            {
                return false;
            }

            value = (long)real.Value;
            return true;
        }

        return false;
    }

    private static bool ReadBitsPerComponent(PdfDictionary dictionary, PoolResolver resolver)
    {
        if (!dictionary.TryGet(PdfName.FromAscii("BitsPerComponent"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return false;
        }

        QpdfObject target = raw is PdfReference reference
            ? ResolveOrNull(resolver, reference) ?? raw
            : raw;
        return target is PdfInteger integer && integer.Value == 8L;
    }

    private static int ReadComponents(PdfDictionary dictionary, PoolResolver resolver)
    {
        if (!dictionary.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return 0;
        }

        QpdfObject target = raw is PdfReference reference
            ? ResolveOrNull(resolver, reference) ?? raw
            : raw;
        if (target is not PdfName name)
        {
            return 0;
        }

        if (name.DisplayText == "DeviceGray")
        {
            return 1;
        }

        if (name.DisplayText == "DeviceRGB")
        {
            return 3;
        }

        return 0;
    }

    private static Dictionary<PdfName, QpdfObject> CopyEntries(PdfDictionary dictionary, ResourceGovernor governor)
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
        return entries;
    }

    private static PdfDictionary WithEntry(PdfDictionary dictionary, PdfName key, QpdfObject value, ResourceGovernor governor)
    {
        Dictionary<PdfName, QpdfObject> entries = CopyEntries(dictionary, governor);
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
}
