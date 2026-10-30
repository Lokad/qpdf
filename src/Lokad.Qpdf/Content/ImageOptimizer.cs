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

        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        HashSet<int> visitedObjects = ParserContainers.CreateSet<int>(traversal);
        traversal.Reserve(32L); // One short-lived pool resolver at a time.
        foreach ((GraphCopier copier, int pageId) in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) && raw is PdfDictionary page)
            {
                ProcessResources(copier, new PoolResolver(copier.CopiedObjects), page, request,
                    limits, governor, traversal, visitedObjects, 0, cancellationToken);
            }
        }
    }

    private static void ProcessResources(
        GraphCopier copier,
        PoolResolver resolver,
        PdfDictionary owner,
        ImageOptimizeRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope traversal,
        HashSet<int> visitedObjects,
        int depth,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (depth > limits.MaxNestingDepth)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, limits.MaxNestingDepth); }
        if (!PdfDictionaryLookup.TryGet(owner, "Resources"u8, governor, token, out QpdfObject raw)) { return; }
        QpdfObject? resolved = raw is PdfReference resourcesLink ? ResolveOrNull(resolver, resourcesLink) : raw;
        if (resolved is not PdfDictionary resources ||
            !PdfDictionaryLookup.TryGet(resources, "XObject"u8, governor, token, out raw)) { return; }
        resolved = raw is PdfReference objectsLink ? ResolveOrNull(resolver, objectsLink) : raw;
        if (resolved is not PdfDictionary xobjects) { return; }

        // PDF streams are indirect objects, including images externalized from
        // inline content. Replacing a pool entry preserves every resource alias;
        // page/resource dictionaries do not need to be copied or rebuilt.
        using PdfDictionary.EntryEnumerator entries = xobjects.GetEntryEnumerator();
        while (entries.MoveNext(governor, token))
        {
            if (entries.Current.Value is not PdfReference link ||
                ResolveOrNull(resolver, link) is not PdfStream stream) { continue; }
            if (IsSubtype(stream.Dictionary, "Form"u8, governor, token))
            {
                if (ParserContainers.AddSetItem(traversal, visitedObjects, link.ObjectNumber, sizeof(int)))
                {
                    ProcessResources(copier, resolver, stream.Dictionary, request, limits, governor,
                        traversal, visitedObjects, checked(depth + 1), token);
                }
            }
            else if (IsSubtype(stream.Dictionary, "Image"u8, governor, token)
                && ParserContainers.AddSetItem(traversal, visitedObjects, link.ObjectNumber, sizeof(int)))
            {
                PdfStream? optimized = TryOptimize(stream, request, resolver, limits, governor, token);
                if (optimized is not null) { copier.ReplaceCopiedObject(link.ObjectNumber, optimized); }
            }
        }
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
        if (!ReadDimensions(image.Dictionary, resolver, governor, cancellationToken, out long width, out long height)
            || width <= 0L || height <= 0L || width > int.MaxValue || height > int.MaxValue)
        {
            return null;
        }

        if (!ReadBitsPerComponent(image.Dictionary, resolver, governor, cancellationToken))
        {
            return null;
        }

        int components = ReadComponents(image.Dictionary, resolver, governor, cancellationToken);
        if (components != 1 && components != 3)
        {
            return null;
        }

        long pixels = width * height; // Both dimensions fit positive Int32 values.
        if ((request.MinWidth > 0L && width <= request.MinWidth)
            || (request.MinHeight > 0L && height <= request.MinHeight)
            || (request.MinArea > 0L && pixels <= request.MinArea))
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

        long maximum = Math.Min(limits.MaxDecodedBytes, Array.MaxLength);
        if (pixels > maximum / components)
        {
            long requested = pixels > long.MaxValue / components ? long.MaxValue : pixels * components;
            throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), requested, maximum);
        }
        long expected = pixels * components;
        governor.CheckDecodedBytes(expected);

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

    private static bool IsSubtype(PdfDictionary dictionary, ReadOnlySpan<byte> subtype,
        ResourceGovernor governor, CancellationToken token)
        => PdfDictionaryLookup.TryGet(dictionary, "Subtype"u8, governor, token, out QpdfObject raw)
            && raw is PdfName name && PdfDictionaryLookup.NameEquals(name, subtype, governor, token);

    private static bool ReadDimensions(PdfDictionary dictionary, PoolResolver resolver,
        ResourceGovernor governor, CancellationToken token, out long width, out long height)
    {
        width = 0L;
        height = 0L;
        return ReadLong(dictionary, resolver, "Width"u8, governor, token, out width)
            && ReadLong(dictionary, resolver, "Height"u8, governor, token, out height);
    }

    private static bool ReadLong(PdfDictionary dictionary, PoolResolver resolver, ReadOnlySpan<byte> key,
        ResourceGovernor governor, CancellationToken token, out long value)
    {
        value = 0L;
        if (!PdfDictionaryLookup.TryGet(dictionary, key, governor, token, out QpdfObject raw)) { return false; }
        QpdfObject? target = raw is PdfReference reference ? ResolveOrNull(resolver, reference) : raw;
        if (target is PdfInteger integer)
        {
            value = integer.Value;
            return true;
        }
        if (target is PdfReal real && double.IsFinite(real.Value))
        {
            value = (long)real.Value;
            return true;
        }
        return false;
    }

    private static bool ReadBitsPerComponent(PdfDictionary dictionary, PoolResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "BitsPerComponent"u8, governor, token, out QpdfObject raw)) { return false; }
        QpdfObject? target = raw is PdfReference reference ? ResolveOrNull(resolver, reference) : raw;
        return target is PdfInteger integer && integer.Value == 8L;
    }

    private static int ReadComponents(PdfDictionary dictionary, PoolResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "ColorSpace"u8, governor, token, out QpdfObject raw)) { return 0; }
        QpdfObject? target = raw is PdfReference reference ? ResolveOrNull(resolver, reference) : raw;
        if (target is not PdfName name) { return 0; }
        if (PdfDictionaryLookup.NameEquals(name, "DeviceGray"u8, governor, token)) { return 1; }
        return PdfDictionaryLookup.NameEquals(name, "DeviceRGB"u8, governor, token) ? 3 : 0;
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
