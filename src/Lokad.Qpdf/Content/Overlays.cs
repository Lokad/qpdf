using System.Globalization;
using System.Text;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal sealed record OverlayApplication(List<GraphCopier> Copiers, int NextId);

internal static class Overlays
{
    internal static async ValueTask<OverlayApplication> ApplyAsync(
        IQpdfHost host,
        QpdfPath primaryInput,
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        IReadOnlyList<OverlaySpec> underlays,
        IReadOnlyList<OverlaySpec> overlays,
        bool compress,
        int? level,
        QpdfRecoveryMode recovery,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        int nextId)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }


        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
        }

        if (underlays is null)
        {
            throw new ArgumentNullException(nameof(underlays));
        }

        if (overlays is null)
        {
            throw new ArgumentNullException(nameof(overlays));
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
        Dictionary<string, OverlaySource> sources = new(StringComparer.Ordinal);
        if ((underlays.Count == 0 && overlays.Count == 0) || occurrences.Count == 0)
        {
            return new OverlayApplication(new List<GraphCopier>(sources.Values.Select(static source => source.Copier)), nextId);
        }

        if ((long)occurrences.Count > limits.MaxWork)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), occurrences.Count, limits.MaxWork);
        }

        int allocator = nextId;
        List<ResolvedSpec> resolvedUnderlays = new(underlays.Count);
        for (int index = 0; index < underlays.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedSpec resolved = await ResolveSpecAsync(host, primaryInput, underlays[index], index, allocator, occurrences.Count, recovery, limits, governor, diagnostics, sources, cancellationToken).ConfigureAwait(false);
            resolvedUnderlays.Add(resolved);
            allocator = MaxAllocator(allocator, sources);
        }

        List<ResolvedSpec> resolvedOverlays = new(overlays.Count);
        for (int index = 0; index < overlays.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedSpec resolved = await ResolveSpecAsync(host, primaryInput, overlays[index], index, allocator, occurrences.Count, recovery, limits, governor, diagnostics, sources, cancellationToken).ConfigureAwait(false);
            resolvedOverlays.Add(resolved);
            allocator = MaxAllocator(allocator, sources);
        }

        List<Dictionary<int, List<OverlayPlacement>>> underlayPages = BuildMapping(resolvedUnderlays, occurrences.Count);
        List<Dictionary<int, List<OverlayPlacement>>> overlayPages = BuildMapping(resolvedOverlays, occurrences.Count);
        Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache = new();
        for (int dest = 0; dest < occurrences.Count; dest++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (underlayPages[dest].Count == 0 && overlayPages[dest].Count == 0)
            {
                continue;
            }

            (GraphCopier copier, int pageId) = occurrences[dest];
            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary)
            {
                continue;
            }

            ApplyToPage(
                copier,
                pageId,
                (PdfDictionary)raw,
                underlayPages[dest],
                overlayPages[dest],
                resolvedUnderlays,
                resolvedOverlays,
                formCache,
                compress,
                level,
                limits,
                governor,
                diagnostics,
                cancellationToken,
                ref allocator);
        }

        foreach (OverlaySource source in sources.Values)
        {
            await source.Document.DisposeAsync().ConfigureAwait(false);
        }

        List<GraphCopier> copiers = new(sources.Count);
        foreach (OverlaySource source in sources.Values)
        {
            copiers.Add(source.Copier);
        }

        return new OverlayApplication(copiers, allocator);
    }

    private static int MaxAllocator(int allocator, Dictionary<string, OverlaySource> sources)
    {
        foreach (OverlaySource source in sources.Values)
        {
            if (source.Copier.NextId > allocator)
            {
                allocator = source.Copier.NextId;
            }
        }

        return allocator;
    }

    private sealed record OverlaySource(QpdfDocument Document, GraphCopier Copier, int PageCount);

    private sealed record PageBundle(QpdfObject? Resources, QpdfObject? Group, QpdfObject? BoxObject, double[]? Box, byte[]? Content, long Rotate, double UserUnit, bool HasTransform);

    private sealed record ResolvedSpec(OverlaySource Source, int SpecIndex, IReadOnlyList<int> FromPages, IReadOnlyList<int> ToPages, IReadOnlyList<int> RepeatPages, Dictionary<int, PageBundle> Bundles);

    private sealed record OverlayPlacement(int SpecIndex, int FromPage);

    private static async ValueTask<ResolvedSpec> ResolveSpecAsync(
        IQpdfHost host,
        QpdfPath primaryInput,
        OverlaySpec spec,
        int specIndex,
        int nextId,
        int destCount,
        QpdfRecoveryMode recovery,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        Dictionary<string, OverlaySource> sources,
        CancellationToken cancellationToken)
    {
        if (spec.File is null || spec.File.Length == 0)
        {
            throw new QpdfParseException("Overlay file must not be empty.", 0L, null);
        }

        QpdfPath path = spec.File == "." ? primaryInput : SelectionPlan.CheckedPath(spec.File);
        if (!sources.TryGetValue(path.Value, out OverlaySource? source))
        {
            QpdfDocument opened = await QpdfDocument.OpenCore(host, path, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
            int count = opened.PageReferences.Count;
            if (count <= 0)
            {
                throw new QpdfParseException("The overlay file has no pages.", 0L, null);
            }

            int firstId = nextId;
            foreach (OverlaySource existing in sources.Values)
            {
                if (existing.Copier.NextId > firstId)
                {
                    firstId = existing.Copier.NextId;
                }
            }

            GraphCopier copier = new(opened.Engine, governor, limits, firstId);
            source = new OverlaySource(opened, copier, count);
            sources[path.Value] = source;
        }

        IReadOnlyList<int> fromPages = OverlayRanges.Resolve(spec.From, source.PageCount, "--from");
        IReadOnlyList<int> toPages = OverlayRanges.Resolve(spec.To, destCount, "--to");
        IReadOnlyList<int> repeatPages = spec.Repeat is null || spec.Repeat.Length == 0
            ? []
            : OverlayRanges.Resolve(spec.Repeat, source.PageCount, "--repeat");
        Dictionary<int, PageBundle> bundles = new();
        HashSet<int> needed = new();
        foreach (int page in fromPages)
        {
            needed.Add(page);
        }

        foreach (int page in repeatPages)
        {
            needed.Add(page);
        }

        foreach (int page in needed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bundles[page] = BuildBundle(source, page, limits, governor, diagnostics, cancellationToken);
        }

        return new ResolvedSpec(source, specIndex, fromPages, toPages, repeatPages, bundles);
    }

    private static List<Dictionary<int, List<OverlayPlacement>>> BuildMapping(
        List<ResolvedSpec> specs,
        int destCount)
    {
        List<Dictionary<int, List<OverlayPlacement>>> pages = new(destCount);
        for (int dest = 0; dest < destCount; dest++)
        {
            pages.Add(new Dictionary<int, List<OverlayPlacement>>());
        }

        for (int specIndex = 0; specIndex < specs.Count; specIndex++)
        {
            ResolvedSpec spec = specs[specIndex];
            for (int position = 0; position < spec.ToPages.Count; position++)
            {
                int dest = spec.ToPages[position] - 1;
                if (dest < 0 || dest >= destCount)
                {
                    continue;
                }

                int from;
                if (position < spec.FromPages.Count)
                {
                    from = spec.FromPages[position];
                }
                else if (spec.RepeatPages.Count > 0)
                {
                    from = spec.RepeatPages[(position - spec.FromPages.Count) % spec.RepeatPages.Count];
                }
                else
                {
                    continue;
                }

                if (!pages[dest].TryGetValue(specIndex, out List<OverlayPlacement>? list))
                {
                    list = new List<OverlayPlacement>();
                    pages[dest][specIndex] = list;
                }

                list.Add(new OverlayPlacement(specIndex, from));
            }
        }

        return pages;
    }

    private static PageBundle BuildBundle(
        OverlaySource source,
        int pageNumber,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.ChargeWork(1L);
        PageLocation location = source.Document.PageReferences[pageNumber - 1];
        DocumentEngine engine = source.Document.Engine;
        QpdfObject? resources = FindInherited(engine, location, "Resources", limits, governor, cancellationToken);
        QpdfObject? group = null;
        if (location.Page.TryGet(PdfName.FromAscii("Group"), out QpdfObject? groupRaw) && groupRaw is not null && groupRaw is not PdfNull)
        {
            group = groupRaw;
        }

        QpdfObject? trim = null;
        if (location.Page.TryGet(PdfName.FromAscii("TrimBox"), out QpdfObject? trimRaw) && trimRaw is not null && trimRaw is not PdfNull)
        {
            trim = trimRaw;
        }

        QpdfObject? crop = FindInherited(engine, location, "CropBox", limits, governor, cancellationToken);
        QpdfObject? media = FindInherited(engine, location, "MediaBox", limits, governor, cancellationToken);
        QpdfObject? boxObject = trim ?? crop ?? media;
        double[]? box = null;
        if (boxObject is not null)
        {
            box = ReadRectangle(boxObject, engine);
            if (box is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page bounding box is invalid; skipping it.");
            }
        }
        else
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page has no bounding box; skipping it.");
        }

        long rotate = 0L;
        bool hasRotate = false;
        QpdfObject? rotateRaw = FindInherited(engine, location, "Rotate", limits, governor, cancellationToken);
        if (rotateRaw is PdfInteger rotateInteger)
        {
            rotate = rotateInteger.Value;
            hasRotate = true;
        }

        double userUnit = 1.0;
        bool hasUnit = false;
        QpdfObject? unitRaw = FindInherited(engine, location, "UserUnit", limits, governor, cancellationToken);
        if (unitRaw is PdfInteger unitInteger)
        {
            userUnit = (double)unitInteger.Value;
            hasUnit = true;
        }
        else if (unitRaw is PdfReal unitReal)
        {
            userUnit = unitReal.Value;
            hasUnit = true;
        }

        byte[]? content = CollectSourceContents(location, engine, limits, governor, diagnostics, cancellationToken);
        QpdfObject? copiedResources = null;
        if (resources is not null)
        {
            governor.ChargeWork(1L);
            copiedResources = source.Copier.CopyValue(resources, 0);
        }

        QpdfObject? copiedGroup = null;
        if (group is not null)
        {
            governor.ChargeWork(1L);
            copiedGroup = source.Copier.CopyValue(group, 0);
        }

        QpdfObject? copiedBox = null;
        if (box is not null && boxObject is not null)
        {
            governor.ChargeWork(1L);
            copiedBox = source.Copier.CopyValue(boxObject, 0);
        }

        return new PageBundle(copiedResources, copiedGroup, copiedBox, box, content, rotate, userUnit, hasRotate || hasUnit);
    }

    private static QpdfObject? FindInherited(
        DocumentEngine engine,
        PageLocation location,
        string key,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        PdfName name = PdfName.FromAscii(key);
        if (location.Page.TryGet(name, out QpdfObject? present) && present is not null && present is not PdfNull)
        {
            return present;
        }

        HashSet<int> visited = new();
        if (location.Source is not null)
        {
            visited.Add(location.Source.ObjectNumber);
        }

        PdfDictionary current = location.Page;
        PdfName parentKey = PdfName.FromAscii("Parent");
        int depth = 0;
        while (current.TryGet(parentKey, out QpdfObject? parentRaw) && parentRaw is not null && parentRaw is not PdfNull)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            PdfDictionary? parent = null;
            if (parentRaw is PdfReference parentReference)
            {
                if (!visited.Add(parentReference.ObjectNumber))
                {
                    throw new QpdfParseException("Page ancestry cycle in overlay file.", null, parentReference.ObjectNumber);
                }

                if (!((IIndirectResolver)engine).TryResolve(parentReference, out QpdfObject? resolved) || resolved is not PdfDictionary resolvedParent)
                {
                    throw new QpdfParseException("Unresolvable page parent in overlay file.", null, parentReference.ObjectNumber);
                }

                parent = resolvedParent;
            }
            else if (parentRaw is PdfDictionary direct)
            {
                parent = direct;
            }
            else
            {
                throw new QpdfParseException("Page parent in overlay file must be a dictionary or a reference.", null, null);
            }

            depth += 1;
            if (depth > limits.MaxNestingDepth)
            {
                throw new QpdfParseException("Page ancestry depth in overlay file exceeds the limit.", null, null);
            }

            if (parent.TryGet(name, out QpdfObject? inherited) && inherited is not null && inherited is not PdfNull)
            {
                return inherited;
            }

            current = parent;
        }

        return null;
    }

    private static byte[]? CollectSourceContents(
        PageLocation location,
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (!location.Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null || contents is PdfNull)
        {
            return [];
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        List<PdfStream> streams = new();
        if (contents is PdfArray array)
        {
            foreach (QpdfObject item in array.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QpdfObject target = item;
                if (item is PdfReference reference)
                {
                    if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is not PdfStream resolvedStream)
                    {
                        diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page content could not be read; skipping it.");
                        return null;
                    }

                    target = resolvedStream;
                }

                if (target is not PdfStream stream)
                {
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page content could not be read; skipping it.");
                    return null;
                }

                streams.Add(stream);
            }
        }
        else if (contents is PdfReference singleReference)
        {
            if (!resolver.TryResolve(singleReference, out QpdfObject? resolved) || resolved is not PdfStream resolvedStream)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page content could not be read; skipping it.");
                return null;
            }

            streams.Add(resolvedStream);
        }
        else if (contents is PdfStream single)
        {
            streams.Add(single);
        }
        else
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page content could not be read; skipping it.");
            return null;
        }

        List<byte[]> chunks = new(streams.Count);
        foreach (PdfStream stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            byte[]? decoded = DecodeStreamData(stream, resolver, limits, governor);
            if (decoded is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page content could not be decoded; skipping it.");
                return null;
            }

            chunks.Add(decoded);
        }

        return JoinContentBytes(chunks, governor);
    }

    private static byte[]? DecodeStreamData(
        PdfStream stream,
        IIndirectResolver resolver,
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

    private static byte[] JoinContentBytes(List<byte[]> parts, ResourceGovernor governor)
    {
        List<byte[]> joined = new(parts.Count * 2);
        bool needNewline = false;
        foreach (byte[] part in parts)
        {
            governor.ChargeWork(1L);
            if (needNewline)
            {
                joined.Add([(byte)10]);
            }

            joined.Add(part);
            needNewline = part.Length == 0 || part[part.Length - 1] != (byte)10;
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

    private static double[]? ReadRectangle(QpdfObject? value, IIndirectResolver resolver)
    {
        QpdfObject? target = value;
        if (target is PdfReference reference)
        {
            if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
            {
                return null;
            }

            target = resolved;
        }

        if (target is not PdfArray array || array.Count != 4)
        {
            return null;
        }

        double[] numbers = new double[4];
        for (int i = 0; i < 4; i++)
        {
            QpdfObject item = array.Items[i];
            if (item is PdfReference itemReference)
            {
                if (!resolver.TryResolve(itemReference, out QpdfObject? resolvedItem) || resolvedItem is null)
                {
                    return null;
                }

                item = resolvedItem;
            }

            if (item is PdfInteger integer)
            {
                numbers[i] = (double)integer.Value;
            }
            else if (item is PdfReal real)
            {
                numbers[i] = real.Value;
            }
            else
            {
                return null;
            }

            if (!double.IsFinite(numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    private static void ApplyToPage(
        GraphCopier copier,
        int pageId,
        PdfDictionary page,
        Dictionary<int, List<OverlayPlacement>> underlays,
        Dictionary<int, List<OverlayPlacement>> overlays,
        List<ResolvedSpec> resolvedUnderlays,
        List<ResolvedSpec> resolvedOverlays,
        Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        PoolResolver resolver = new(copier.CopiedObjects);
        double[]? trim = ReadPageBox(page, "TrimBox", resolver);
        double[]? crop = ReadPageBox(page, "CropBox", resolver);
        double[]? media = ReadPageBox(page, "MediaBox", resolver);
        double[]? placeBox = trim ?? crop ?? media;
        if (placeBox is null || media is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Output page has no bounding box; skipping overlay for it.");
            return;
        }

        long rotate = 0L;
        if (page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject? rotateRaw) && rotateRaw is PdfInteger rotateInteger)
        {
            rotate = rotateInteger.Value;
        }

        double userUnit = 1.0;
        if (page.TryGet(PdfName.FromAscii("UserUnit"), out QpdfObject? unitRaw))
        {
            if (unitRaw is PdfInteger unitInteger)
            {
                userUnit = (double)unitInteger.Value;
            }
            else if (unitRaw is PdfReal unitReal)
            {
                userUnit = unitReal.Value;
            }
        }

        byte[]? pageContent = CollectPooledContents(copier, page, resolver, limits, governor, cancellationToken);
        if (pageContent is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Output page content could not be read; skipping overlay for it.");
            return;
        }

        PdfDictionary? pageResources = null;
        if (page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject? resourcesRaw) && resourcesRaw is not null && resourcesRaw is not PdfNull)
        {
            QpdfObject target = resourcesRaw is PdfReference resourcesReference
                ? ResolveOrNull(resolver, resourcesReference) ?? resourcesRaw
                : resourcesRaw;
            pageResources = target as PdfDictionary;
        }

        QpdfObject? pageBoxObject = FindPageBoxObject(page, resolver);
        if (pageBoxObject is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Output page has no bounding box; skipping overlay for it.");
            return;
        }

        double[]? destMatrix = HasTransform(page, resolver) ? TransformMatrix(placeBox, rotate, userUnit, false) : null;
        Dictionary<PdfName, QpdfObject> formEntries = new();
        formEntries[PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject");
        formEntries[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form");
        formEntries[PdfName.FromAscii("BBox")] = pageBoxObject;
        if (destMatrix is not null)
        {
            formEntries[PdfName.FromAscii("Matrix")] = MatrixArray(destMatrix, governor);
        }

        if (pageResources is not null)
        {
            Dictionary<PdfName, QpdfObject> shared = new();
            foreach (PdfName key in pageResources.Keys)
            {
                if (pageResources.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    shared[key] = found;
                }
            }

            governor.ReserveMemory(128L);
            formEntries[PdfName.FromAscii("Resources")] = new PdfDictionary(shared);
        }

        if (page.TryGet(PdfName.FromAscii("Group"), out QpdfObject? groupRaw) && groupRaw is not null && groupRaw is not PdfNull)
        {
            formEntries[PdfName.FromAscii("Group")] = groupRaw;
        }

        PdfStream pageForm = EncodeForm(new PdfDictionary(formEntries), pageContent, compress, level, limits, governor);
        PdfReference pageFormLink = copier.AddIndirectObjectWithId(pageForm, allocator);
        allocator = checked(allocator + 1);
        HashSet<string> taken = new(StringComparer.Ordinal)
        {
            "Fx0",
        };
        CollectTakenNames(pageResources, taken);
        int suffix = 1;
        Dictionary<string, PdfReference> added = new(StringComparer.Ordinal);
        List<byte> stacked = new(pageContent.Length + 256);
        double[] destTransform = TransformMatrix(placeBox, rotate, userUnit, true);
        foreach (KeyValuePair<int, List<OverlayPlacement>> group in SortedGroups(underlays))
        {
            foreach (OverlayPlacement placement in group.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OverlayForm? form = GetOrBuildOverlayForm(resolvedUnderlays[group.Key], placement, formCache, compress, level, limits, governor, diagnostics, cancellationToken, ref allocator);
                if (form is null)
                {
                    continue;
                }

                string name = UniqueFxName(taken, ref suffix);
                added[name] = form.Link;
                AppendPlacement(stacked, name, PlacementMatrix(form.Box, form.FormMatrix, placeBox, destTransform, true, false));
            }
        }

        AppendPlacement(stacked, "Fx0", PlacementMatrix(placeBox, destMatrix, media, destTransform, false, false));
        foreach (KeyValuePair<int, List<OverlayPlacement>> group in SortedGroups(overlays))
        {
            foreach (OverlayPlacement placement in group.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OverlayForm? form = GetOrBuildOverlayForm(resolvedOverlays[group.Key], placement, formCache, compress, level, limits, governor, diagnostics, cancellationToken, ref allocator);
                if (form is null)
                {
                    continue;
                }

                string name = UniqueFxName(taken, ref suffix);
                added[name] = form.Link;
                AppendPlacement(stacked, name, PlacementMatrix(form.Box, form.FormMatrix, placeBox, destTransform, true, false));
            }
        }

        Dictionary<PdfName, QpdfObject> xobjects = new();
        xobjects[new PdfName(Encoding.ASCII.GetBytes("Fx0"))] = pageFormLink;
        foreach (KeyValuePair<string, PdfReference> entry in added)
        {
            xobjects[new PdfName(Encoding.ASCII.GetBytes(entry.Key))] = entry.Value;
        }

        governor.ReserveMemory(128L);
        Dictionary<PdfName, QpdfObject> resources = new()
        {
            [PdfName.FromAscii("XObject")] = new PdfDictionary(xobjects),
        };
        PdfDictionary replaced = ReplaceEntry(page, PdfName.FromAscii("Resources"), new PdfDictionary(resources), governor);
        byte[] stackedBytes = [.. stacked];
        PdfStream stackedStream;
        if (compress && stackedBytes.Length > 0)
        {
            byte[] encoded = FlateFilter.Encode(stackedBytes, level, limits, governor);
            governor.ReserveMemory(64L);
            stackedStream = new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"),
            }), encoded);
        }
        else
        {
            stackedStream = new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), stackedBytes);
        }

        PdfReference stackedLink = copier.AddIndirectObjectWithId(stackedStream, allocator);
        allocator = checked(allocator + 1);
        replaced = ReplaceEntry(replaced, PdfName.FromAscii("Contents"), stackedLink, governor);
        copier.ReplaceCopiedObject(pageId, replaced);
    }

    private sealed record OverlayForm(PdfReference Link, double[] Box, double[]? FormMatrix);

    private static OverlayForm? GetOrBuildOverlayForm(
        ResolvedSpec spec,
        OverlayPlacement placement,
        Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        (GraphCopier Copier, int Spec, int Page) key = (spec.Source.Copier, spec.SpecIndex, placement.FromPage);
        if (formCache.TryGetValue(key, out OverlayForm? cached))
        {
            return cached;
        }

        if (!spec.Bundles.TryGetValue(placement.FromPage, out PageBundle? bundle) || bundle is null)
        {
            return null;
        }

        if (bundle.Box is null || bundle.BoxObject is null || bundle.Content is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        governor.ChargeWork(1L);
        double[]? formMatrix = bundle.HasTransform ? TransformMatrix(bundle.Box, bundle.Rotate, bundle.UserUnit, false) : null;
        Dictionary<PdfName, QpdfObject> entries = new();
        entries[PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject");
        entries[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form");
        entries[PdfName.FromAscii("BBox")] = bundle.BoxObject;
        if (formMatrix is not null)
        {
            entries[PdfName.FromAscii("Matrix")] = MatrixArray(formMatrix, governor);
        }

        if (bundle.Resources is not null)
        {
            entries[PdfName.FromAscii("Resources")] = bundle.Resources;
        }

        if (bundle.Group is not null)
        {
            entries[PdfName.FromAscii("Group")] = bundle.Group;
        }

        PdfStream form = EncodeForm(new PdfDictionary(entries), bundle.Content, compress, level, limits, governor);
        PdfReference link = spec.Source.Copier.AddIndirectObjectWithId(form, allocator);
        allocator = checked(allocator + 1);
        OverlayForm built = new(link, bundle.Box, formMatrix);
        formCache[key] = built;
        return built;
    }

    private static PdfStream EncodeForm(
        PdfDictionary dictionary,
        byte[] content,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        if (compress && content.Length > 0)
        {
            byte[] encoded = FlateFilter.Encode(content, level, limits, governor);
            governor.ReserveMemory(64L);
            Dictionary<PdfName, QpdfObject> entries = new();
            foreach (PdfName key in dictionary.Keys)
            {
                if (dictionary.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    entries[key] = found;
                }
            }

            entries[PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode");
            return new PdfStream(new PdfDictionary(entries), encoded);
        }

        return new PdfStream(dictionary, content);
    }

    private static string UniqueFxName(HashSet<string> taken, ref int suffix)
    {
        while (true)
        {
            string candidate = "Fx" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix += 1;
            if (!taken.Contains(candidate))
            {
                taken.Add(candidate);
                return candidate;
            }
        }
    }

    private static void CollectTakenNames(PdfDictionary? resources, HashSet<string> taken)
    {
        if (resources is null)
        {
            return;
        }

        if (!resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject? xobjectsRaw) || xobjectsRaw is not PdfDictionary xobjects)
        {
            return;
        }

        foreach (PdfName key in xobjects.Keys)
        {
            taken.Add(key.DisplayText);
        }
    }

    private static void AppendPlacement(List<byte> output, string name, double[] cm)
    {
        AppendAscii(output, "q");
        output.Add((byte)10);
        AppendAscii(output, FormatMatrix(cm));
        AppendAscii(output, " cm");
        output.Add((byte)10);
        output.Add((byte)47);
        AppendAscii(output, name);
        AppendAscii(output, " Do");
        output.Add((byte)10);
        AppendAscii(output, "Q");
        output.Add((byte)10);
    }

    private static void AppendAscii(List<byte> output, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        foreach (byte b in bytes)
        {
            output.Add(b);
        }
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

    private struct ContentMatrix
    {
        public double A;
        public double B;
        public double C;
        public double D;
        public double E;
        public double F;

        public ContentMatrix(double a, double b, double c, double d, double e, double f)
        {
            A = a;
            B = b;
            C = c;
            D = d;
            E = e;
            F = f;
        }

        public static ContentMatrix Identity()
        {
            return new ContentMatrix(1.0, 0.0, 0.0, 1.0, 0.0, 0.0);
        }

        public void Concat(ContentMatrix other)
        {
            double a = (A * other.A) + (C * other.B);
            double b = (B * other.A) + (D * other.B);
            double c = (A * other.C) + (C * other.D);
            double d = (B * other.C) + (D * other.D);
            double e = (A * other.E) + (C * other.F) + E;
            double f = (B * other.E) + (D * other.F) + F;
            A = a;
            B = b;
            C = c;
            D = d;
            E = e;
            F = f;
        }

        public void Scale(double sx, double sy)
        {
            Concat(new ContentMatrix(sx, 0.0, 0.0, sy, 0.0, 0.0));
        }

        public void Translate(double tx, double ty)
        {
            Concat(new ContentMatrix(1.0, 0.0, 0.0, 1.0, tx, ty));
        }

        public double[] TransformRect(double[] rect)
        {
            double x0 = (A * rect[0]) + (C * rect[1]) + E;
            double y0 = (B * rect[0]) + (D * rect[1]) + F;
            double x1 = (A * rect[0]) + (C * rect[3]) + E;
            double y1 = (B * rect[0]) + (D * rect[3]) + F;
            double x2 = (A * rect[2]) + (C * rect[1]) + E;
            double y2 = (B * rect[2]) + (D * rect[1]) + F;
            double x3 = (A * rect[2]) + (C * rect[3]) + E;
            double y3 = (B * rect[2]) + (D * rect[3]) + F;
            double llx = x0;
            if (x1 < llx) llx = x1;
            if (x2 < llx) llx = x2;
            if (x3 < llx) llx = x3;
            double lly = y0;
            if (y1 < lly) lly = y1;
            if (y2 < lly) lly = y2;
            if (y3 < lly) lly = y3;
            double urx = x0;
            if (x1 > urx) urx = x1;
            if (x2 > urx) urx = x2;
            if (x3 > urx) urx = x3;
            double ury = y0;
            if (y1 > ury) ury = y1;
            if (y2 > ury) ury = y2;
            if (y3 > ury) ury = y3;
            return [llx, lly, urx, ury];
        }

        public double[] ToArray()
        {
            return [A, B, C, D, E, F];
        }
    }

    private static double[] TransformMatrix(double[] box, long rotate, double unit, bool invert)
    {
        double width = box[2] - box[0];
        double height = box[3] - box[1];
        double scale = unit;
        long angle = rotate;
        if (invert)
        {
            if (scale == 0.0)
            {
                return IdentityArray();
            }

            scale = 1.0 / scale;
            angle = 360L - angle;
        }

        angle %= 360L;
        if (angle < 0L)
        {
            angle += 360L;
        }

        if (angle == 90L)
        {
            return [0.0, -scale, scale, 0.0, 0.0, width * scale];
        }

        if (angle == 180L)
        {
            return [-scale, 0.0, 0.0, -scale, width * scale, height * scale];
        }

        if (angle == 270L)
        {
            return [0.0, scale, -scale, 0.0, height * scale, 0.0];
        }

        return [scale, 0.0, 0.0, scale, 0.0, 0.0];
    }

    private static double[] IdentityArray()
    {
        return [1.0, 0.0, 0.0, 1.0, 0.0, 0.0];
    }

    private static double[] PlacementMatrix(
        double[] formBox,
        double[]? formMatrix,
        double[] destRect,
        double[] destTransform,
        bool allowShrink,
        bool allowExpand)
    {
        ContentMatrix work = ContentMatrix.Identity();
        work.Concat(FromArray(destTransform));
        if (formMatrix is not null)
        {
            work.Concat(FromArray(formMatrix));
        }

        double[] moved = work.TransformRect(formBox);
        double movedWidth = moved[2] - moved[0];
        double movedHeight = moved[3] - moved[1];
        if (movedWidth == 0.0 || movedHeight == 0.0)
        {
            return IdentityArray();
        }

        double rectWidth = destRect[2] - destRect[0];
        double rectHeight = destRect[3] - destRect[1];
        double scale = Math.Min(rectWidth / movedWidth, rectHeight / movedHeight);
        if (!double.IsFinite(scale))
        {
            return IdentityArray();
        }

        if (scale > 1.0)
        {
            if (!allowExpand)
            {
                scale = 1.0;
            }
        }
        else if (scale < 1.0)
        {
            if (!allowShrink)
            {
                scale = 1.0;
            }
        }

        ContentMatrix sized = ContentMatrix.Identity();
        sized.Scale(scale, scale);
        sized.Concat(FromArray(destTransform));
        if (formMatrix is not null)
        {
            sized.Concat(FromArray(formMatrix));
        }

        double[] fitted = sized.TransformRect(formBox);
        double tx = ((destRect[0] + destRect[2]) / 2.0) - ((fitted[0] + fitted[2]) / 2.0);
        double ty = ((destRect[1] + destRect[3]) / 2.0) - ((fitted[1] + fitted[3]) / 2.0);
        ContentMatrix result = ContentMatrix.Identity();
        result.Translate(tx, ty);
        result.Scale(scale, scale);
        result.Concat(FromArray(destTransform));
        return result.ToArray();
    }

    private static ContentMatrix FromArray(double[] values)
    {
        return new ContentMatrix(values[0], values[1], values[2], values[3], values[4], values[5]);
    }

    private static PdfArray MatrixArray(double[] values, ResourceGovernor governor)
    {
        List<QpdfObject> items = new(values.Length);
        foreach (double value in values)
        {
            governor.ReserveMemory(32L);
            items.Add(new PdfReal(FormatDouble(value), value));
        }

        return new PdfArray(items);
    }

    private static string FormatMatrix(double[] values)
    {
        StringBuilder builder = new();
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                builder.Append((char)32);
            }

            builder.Append(FormatDouble(values[i]));
        }

        return builder.ToString();
    }

    private static string FormatDouble(double value)
    {
        if (value > -0.00001 && value < 0.00001)
        {
            value = 0.0;
        }

        double rounded = Math.Round(value, 5, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static double[]? ReadPageBox(PdfDictionary page, string key, PoolResolver resolver)
    {
        if (!page.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        return ReadRectangle(raw, resolver);
    }

    private static QpdfObject? FindPageBoxObject(PdfDictionary page, PoolResolver resolver)
    {
        foreach (string key in new string[] { "TrimBox", "CropBox", "MediaBox" })
        {
            if (page.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) && raw is not null && raw is not PdfNull)
            {
                if (ReadRectangle(raw, resolver) is not null)
                {
                    return raw;
                }
            }
        }

        return null;
    }

    private static bool HasTransform(PdfDictionary page, PoolResolver resolver)
    {
        if (page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject? rotate) && rotate is not null && rotate is not PdfNull)
        {
            return true;
        }

        if (page.TryGet(PdfName.FromAscii("UserUnit"), out QpdfObject? unit) && unit is not null && unit is not PdfNull)
        {
            return true;
        }

        return false;
    }

    private static byte[]? CollectPooledContents(
        GraphCopier copier,
        PdfDictionary page,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null || contents is PdfNull)
        {
            return [];
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
            return null;
        }

        List<byte[]> chunks = new(streams.Count);
        foreach (PdfStream stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            byte[]? decoded = DecodeStreamData(stream, resolver, limits, governor);
            if (decoded is null)
            {
                return null;
            }

            chunks.Add(decoded);
        }

        return JoinContentBytes(chunks, governor);
    }

    private static IEnumerable<KeyValuePair<int, List<OverlayPlacement>>> SortedGroups(Dictionary<int, List<OverlayPlacement>> groups)
    {
        List<int> keys = new(groups.Keys);
        keys.Sort();
        foreach (int key in keys)
        {
            yield return new KeyValuePair<int, List<OverlayPlacement>>(key, groups[key]);
        }
    }
}

