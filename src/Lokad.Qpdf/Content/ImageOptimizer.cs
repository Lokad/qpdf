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
            governor.ChargeWork(1L);
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

            PdfStream? optimized = TryOptimize(stream, request, resolver, limits, governor);
            if (optimized is null)
            {
                continue;
            }

            governor.ChargeWork(1L);
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

        return new PdfStream(WithEntry(form.Dictionary, PdfName.FromAscii("Resources"), rebuilt, governor), form.Data);
    }

    private static PdfStream? TryOptimize(
        PdfStream image,
        ImageOptimizeRequest request,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
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

        IReadOnlyList<FilterStage> stages;
        try
        {
            stages = FilterChain.Resolve(image.Dictionary, resolver);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            return null;
        }

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

        byte[] raw;
        try
        {
            raw = stages.Count == 0
                ? CopyBytes(image.Data, limits, governor)
                : FilterChain.Decode(image.Data, stages, limits, governor);
        }
        catch (QpdfFilterException)
        {
            return null;
        }

        long expected = checked(checked(width * height) * components);
        if ((long)raw.Length != expected || width > int.MaxValue || height > int.MaxValue)
        {
            return null;
        }

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

        if ((long)encoded.Length >= (long)image.Data.Length)
        {
            return null;
        }

        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in image.Dictionary.Keys)
        {
            if (key.DisplayText == "Filter" || key.DisplayText == "DecodeParms")
            {
                continue;
            }

            if (image.Dictionary.TryGet(key, out QpdfObject? kept) && kept is not null)
            {
                entries[key] = kept;
            }
        }

        governor.ReserveMemory(64L);
        entries[PdfName.FromAscii("Filter")] = PdfName.FromAscii("DCTDecode");
        return new PdfStream(new PdfDictionary(entries), encoded);
    }

    private static byte[] CopyBytes(byte[] data, QpdfLimits limits, ResourceGovernor governor)
    {
        if ((long)data.Length > limits.MaxDecodedBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), data.Length, limits.MaxDecodedBytes);
        }

        governor.ReserveMemory(data.Length);
        try
        {
            byte[] copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            governor.ChargeDecodedBytes(copy.Length);
            return copy;
        }
        finally
        {
            governor.ReleaseMemory(data.Length);
        }
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