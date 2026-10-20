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

internal sealed record OverlayApplication(List<GraphCopier> Copiers, int NextId, IReadOnlyList<AcroFormCopy.OverlayFieldImport> FieldImports);

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
        ParserMemoryScope ownership,
        CancellationToken cancellationToken,
        int nextId,
        IIndirectResolver? retainedResolver)
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
        ownership.CheckOwner(governor);
        Dictionary<string, OverlaySource> sources = new(StringComparer.Ordinal);
        if ((underlays.Count == 0 && overlays.Count == 0) || occurrences.Count == 0)
        {
            return new OverlayApplication(new List<GraphCopier>(sources.Values.Select(static source => source.Copier)), nextId, []);
        }

        if ((long)occurrences.Count > limits.MaxContainerSize)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), occurrences.Count, limits.MaxContainerSize);
        }

        try
        {
            int allocator = nextId;
            List<ResolvedSpec> resolvedUnderlays = new(underlays.Count);
            for (int index = 0; index < underlays.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResolvedSpec resolved = await ResolveSpecAsync(host, primaryInput, underlays[index], index, allocator, occurrences.Count, recovery, limits, governor, diagnostics, sources, ownership, cancellationToken, retainedResolver is not null).ConfigureAwait(false);
                resolvedUnderlays.Add(resolved);
                allocator = MaxAllocator(allocator, sources);
            }

            List<ResolvedSpec> resolvedOverlays = new(overlays.Count);
            for (int index = 0; index < overlays.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResolvedSpec resolved = await ResolveSpecAsync(host, primaryInput, overlays[index], index, allocator, occurrences.Count, recovery, limits, governor, diagnostics, sources, ownership, cancellationToken, retainedResolver is not null).ConfigureAwait(false);
                resolvedOverlays.Add(resolved);
                allocator = MaxAllocator(allocator, sources);
            }

            List<Dictionary<int, List<OverlayPlacement>>> underlayPages = BuildMapping(resolvedUnderlays, occurrences.Count);
            List<Dictionary<int, List<OverlayPlacement>>> overlayPages = BuildMapping(resolvedOverlays, occurrences.Count);
            Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache = new();
            List<AcroFormCopy.OverlayFieldImport> imports = new();
            for (int dest = 0; dest < occurrences.Count; dest++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
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
                    imports,
                    retainedResolver,
                    compress,
                    level,
                    limits,
                    governor, ownership,
                    diagnostics,
                    cancellationToken,
                    ref allocator);
            }

            List<GraphCopier> copiers = new(sources.Count);
            foreach (OverlaySource source in sources.Values)
            {
                copiers.Add(source.Copier);
            }

            return new OverlayApplication(copiers, allocator, imports);
        }
        finally
        {
            Exception? closeFailure = null;
            foreach (OverlaySource source in sources.Values)
            {
                try
                {
                    try { source.Document.Engine.TransferExclusiveValuesTo(ownership); }
                    finally { await source.Document.DisposeAsync().ConfigureAwait(false); }
                }
                catch (Exception exception) { closeFailure ??= exception; }
            }
            if (closeFailure is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw(); }
        }
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

    private sealed record OverlaySource(QpdfDocument Document, GraphCopier Copier, int PageCount, AcroFormCopy.OverlaySourceForm? FormInfo, HashSet<string> ImportNotes);

    private sealed record PageBundle(QpdfObject? Resources, QpdfObject? Group, QpdfObject? BoxObject, double[]? Box, byte[]? Content, long Rotate, double UserUnit, bool HasTransform, bool DeferredCopy);

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
        ParserMemoryScope ownership,
        CancellationToken cancellationToken,
        bool deferCopy)
    {
        if (spec.File is null || spec.File.Length == 0)
        {
            throw new QpdfParseException("Overlay file must not be empty.", 0L, null);
        }

        QpdfPath path = spec.File == "." ? primaryInput : SelectionPlan.CheckedPath(spec.File);
        if (!sources.TryGetValue(path.Value, out OverlaySource? source))
        {
            QpdfDocument opened = await QpdfDocument.OpenCore(host, path, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
            try
            {
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

                GraphCopier copier = GraphCopier.CreateOwned(opened.Engine, governor, limits, firstId, ownership);
                AcroFormCopy.OverlaySourceForm? formInfo = AcroFormCopy.TryReadOverlaySourceForm(opened.Engine, limits, governor, cancellationToken);
                if (formInfo is not null)
                {
                    if (formInfo.FieldsNotArray)
                    {
                        diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "Ignoring a source /Fields entry that is not an array while importing overlay form fields.");
                    }

                    if (formInfo.HasDirectNodes)
                    {
                        diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "Ignoring a direct object as a field or annotation while importing overlay form fields.");
                    }
                }

                source = new OverlaySource(opened, copier, count, formInfo, new HashSet<string>(StringComparer.Ordinal));
                sources[path.Value] = source;
            }
            catch
            {
                await opened.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        IReadOnlyList<int> fromPages = spec.From is not null && spec.From.Length == 0
            ? []
            : OverlayRanges.Resolve(spec.From, source.PageCount, limits.MaxContainerSize);
        IReadOnlyList<int> toPages = OverlayRanges.Resolve(spec.To, destCount, limits.MaxContainerSize);
        IReadOnlyList<int> repeatPages = spec.Repeat is null || spec.Repeat.Length == 0
            ? []
            : OverlayRanges.Resolve(spec.Repeat, source.PageCount, limits.MaxContainerSize);
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
            bundles[page] = BuildBundle(source, page, limits, governor, ownership, diagnostics, cancellationToken, deferCopy);
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
        ParserMemoryScope ownership,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        bool deferCopy)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        PageLocation location = source.Document.PageReferences[pageNumber - 1];
        DocumentEngine engine = source.Document.Engine;
        QpdfObject? resources = FindInherited(engine, location, "Resources", limits, governor, ownership, cancellationToken);
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

        QpdfObject? crop = FindInherited(engine, location, "CropBox", limits, governor, ownership, cancellationToken);
        QpdfObject? media = FindInherited(engine, location, "MediaBox", limits, governor, ownership, cancellationToken);
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
        QpdfObject? rotateRaw = FindInherited(engine, location, "Rotate", limits, governor, ownership, cancellationToken);
        if (rotateRaw is PdfInteger rotateInteger)
        {
            rotate = rotateInteger.Value;
            hasRotate = true;
        }

        double userUnit = 1.0;
        bool hasUnit = false;
        QpdfObject? unitRaw = FindInherited(engine, location, "UserUnit", limits, governor, ownership, cancellationToken);
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

        byte[]? content = CollectSourceContents(location, engine, limits, governor, ownership, diagnostics, cancellationToken);
        if (deferCopy)
        {
            return new PageBundle(resources, group, boxObject, box, content, rotate, userUnit, hasRotate || hasUnit, true);
        }
        QpdfObject? copiedResources = null;
        if (resources is not null)
        {
            governor.NoteOperation();
            copiedResources = source.Copier.CopyValue(resources, 0);
        }

        QpdfObject? copiedGroup = null;
        if (group is not null)
        {
            governor.NoteOperation();
            copiedGroup = source.Copier.CopyValue(group, 0);
        }

        QpdfObject? copiedBox = null;
        if (box is not null && boxObject is not null)
        {
            governor.NoteOperation();
            copiedBox = source.Copier.CopyValue(boxObject, 0);
        }

        return new PageBundle(copiedResources, copiedGroup, copiedBox, box, content, rotate, userUnit, hasRotate || hasUnit, false);
    }

    private static QpdfObject? FindInherited(
        DocumentEngine engine, PageLocation location, string key, QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership, CancellationToken cancellationToken)
        => PageInheritance.Find(engine, location, PdfName.FromAscii(key), limits, governor, cancellationToken);

    private static byte[]? CollectSourceContents(
        PageLocation location,
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
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
            governor.NoteOperation();
            byte[]? decoded = DecodeStreamData(stream, resolver, limits, governor, ownership);
            if (decoded is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page content could not be decoded; skipping it.");
                return null;
            }

            chunks.Add(decoded);
        }

        return JoinContentBytes(chunks, governor, ownership);
    }

    private static byte[]? DecodeStreamData(
        PdfStream stream,
        IIndirectResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope ownership)
    {
        FilterPlan stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, resolver, governor, governor.CancellationToken);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            return null;
        }
        using FilterPlan stageOwner = stages;

        if (stages.Count == 0)
        {
            return stream.Data;
        }
        // A Form contains PDF instructions. Opaque bytes cannot be promoted to
        // instructions by dropping their source encoding dictionary.
        if (!FilterChain.AllStagesInLevel(stages, "specialized")) { return null; }

        try
        {
            return FilterChain.Decode(stream.Data, stages, limits, governor, ownership, governor.CancellationToken);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            return null;
        }
    }

    private static byte[] JoinContentBytes(List<byte[]> parts, ResourceGovernor governor, ParserMemoryScope ownership)
    {
        List<byte[]> joined = new(parts.Count * 2);
        bool needNewline = false;
        foreach (byte[] part in parts)
        {
            governor.NoteOperation();
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

        ownership.Reserve(total);
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
        List<AcroFormCopy.OverlayFieldImport> imports,
        IIndirectResolver? retainedResolver,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        IIndirectResolver resolver = retainedResolver ?? new PoolResolver(copier.CopiedObjects);
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

        byte[]? pageContent = CollectPooledContents(copier, page, resolver, limits, governor, ownership, cancellationToken);
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
            formEntries[PdfName.FromAscii("Matrix")] = MatrixArray(destMatrix, governor, ownership);
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

            ownership.Reserve(128L);
            formEntries[PdfName.FromAscii("Resources")] = new PdfDictionary(shared);
        }

        if (page.TryGet(PdfName.FromAscii("Group"), out QpdfObject? groupRaw) && groupRaw is not null && groupRaw is not PdfNull)
        {
            formEntries[PdfName.FromAscii("Group")] = groupRaw;
        }

        PdfStream pageForm = EncodeForm(new PdfDictionary(formEntries), pageContent, compress, level, limits, governor, ownership);
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
                OverlayForm? form = GetOrBuildOverlayForm(resolvedUnderlays[group.Key], placement, formCache, compress, level, limits, governor, ownership, diagnostics, cancellationToken, ref allocator);
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
                OverlayForm? form = GetOrBuildOverlayForm(resolvedOverlays[group.Key], placement, formCache, compress, level, limits, governor, ownership, diagnostics, cancellationToken, ref allocator);
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

        ownership.Reserve(128L);
        Dictionary<PdfName, QpdfObject> resources = new()
        {
            [PdfName.FromAscii("XObject")] = new PdfDictionary(xobjects),
        };
        PdfDictionary replaced = ReplaceEntry(page, PdfName.FromAscii("Resources"), new PdfDictionary(resources), governor, ownership);
        byte[] stackedBytes = [.. stacked];
        PdfStream stackedStream;
        if (compress && stackedBytes.Length > 0)
        {
            byte[] encoded = FlateFilter.Encode(stackedBytes, level, limits, governor, ownership, governor.CancellationToken);
            ownership.Reserve(64L);
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
        replaced = ReplaceEntry(replaced, PdfName.FromAscii("Contents"), stackedLink, governor, ownership);
        copier.ReplaceCopiedObject(pageId, replaced);
        CopyPlacementAnnotations(copier, pageId, placeBox, destTransform, underlays, overlays, resolvedUnderlays, resolvedOverlays, formCache, imports, compress, level, limits, governor, ownership, diagnostics, cancellationToken, ref allocator);
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
        ParserMemoryScope ownership,
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
        governor.NoteOperation();
        int formId = allocator;
        if (bundle.DeferredCopy)
        {
            // Native inspection allocates the form identity before importing
            // its resources. Keep that ordering in the retained source graph.
            allocator = checked(allocator + 1);
            spec.Source.Copier.AdvanceNextId(allocator);
            bundle = bundle with
            {
                Resources = bundle.Resources is null ? null : spec.Source.Copier.CopyValue(bundle.Resources, 0),
                Group = bundle.Group is null ? null : spec.Source.Copier.CopyValue(bundle.Group, 0),
                BoxObject = spec.Source.Copier.CopyValue(bundle.BoxObject, 0),
            };
            allocator = Math.Max(allocator, spec.Source.Copier.NextId);
        }
        double[]? formMatrix = bundle.HasTransform ? TransformMatrix(bundle.Box, bundle.Rotate, bundle.UserUnit, false) : null;
        Dictionary<PdfName, QpdfObject> entries = new();
        entries[PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject");
        entries[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form");
        entries[PdfName.FromAscii("BBox")] = bundle.BoxObject;
        if (formMatrix is not null)
        {
            entries[PdfName.FromAscii("Matrix")] = MatrixArray(formMatrix, governor, ownership);
        }

        if (bundle.Resources is not null)
        {
            entries[PdfName.FromAscii("Resources")] = bundle.Resources;
        }

        if (bundle.Group is not null)
        {
            entries[PdfName.FromAscii("Group")] = bundle.Group;
        }

        PdfStream form = EncodeForm(new PdfDictionary(entries), bundle.Content, compress, level, limits, governor, ownership);
        PdfReference link = spec.Source.Copier.AddIndirectObjectWithId(form, formId);
        if (!bundle.DeferredCopy) { allocator = checked(allocator + 1); }
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
        ResourceGovernor governor, ParserMemoryScope ownership)
    {
        if (compress && content.Length > 0)
        {
            byte[] encoded = FlateFilter.Encode(content, level, limits, governor, ownership, governor.CancellationToken);
            ownership.Reserve(64L);
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

    private static PdfDictionary ReplaceEntry(PdfDictionary dictionary, PdfName key, QpdfObject value, ResourceGovernor governor, ParserMemoryScope ownership)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName existing in dictionary.Keys)
        {
            if (dictionary.TryGet(existing, out QpdfObject? found) && found is not null)
            {
                entries[existing] = found;
            }
        }

        ownership.Reserve(64L);
        entries[key] = value;
        return new PdfDictionary(entries);
    }

    private static QpdfObject? ResolveOrNull(IIndirectResolver resolver, PdfReference reference)
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

    private static PdfArray MatrixArray(double[] values, ResourceGovernor governor, ParserMemoryScope ownership)
    {
        List<QpdfObject> items = new(values.Length);
        foreach (double value in values)
        {
            ownership.Reserve(32L);
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

    private static double[]? ReadPageBox(PdfDictionary page, string key, IIndirectResolver resolver)
    {
        if (!page.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        return ReadRectangle(raw, resolver);
    }

    private static QpdfObject? FindPageBoxObject(PdfDictionary page, IIndirectResolver resolver)
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

    private static bool HasTransform(PdfDictionary page, IIndirectResolver resolver)
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
        IIndirectResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
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
            governor.NoteOperation();
            byte[]? decoded = DecodeStreamData(stream, resolver, limits, governor, ownership);
            if (decoded is null)
            {
                return null;
            }

            chunks.Add(decoded);
        }

        return JoinContentBytes(chunks, governor, ownership);
    }

    /// <summary>
    /// Copies overlay/underlay source-page annotations onto the stamped
    /// destination page, mirroring the upstream reference
    /// (<c>QPDFPageObjectHelper::copyAnnotations</c> over
    /// <c>QPDFAcroFormDocumentHelper::transformAnnotations</c>). Each applied
    /// source page contributes fresh per-placement clones appended to the
    /// destination <c>/Annots</c> array, so repeated placements never share
    /// annotation objects. Clone <c>/Rect</c> entries are transformed by the
    /// same placement matrix used for content stamping (missing or malformed
    /// rectangles behave as all-zeros, matching the reference), and
    /// appearance-stream <c>/Matrix</c> entries are concatenated with it
    /// (missing matrices stay absent under identity placements; malformed
    /// matrices behave as all-zeros, matching the reference). References that
    /// would resurrect overlay source pages prune to null exactly as the
    /// reference stops foreign copies at page boundaries; null-valued
    /// dictionary keys are dropped while null array elements are kept as
    /// indirect null objects, matching reference output. Entries that are not
    /// dictionaries are skipped with a warning, matching the reference.
    /// <c>/Widget</c> annotations import their top-level fields through the
    /// shared form machinery: one fresh field copy per placement with
    /// per-placement default resources, document default-appearance and
    /// quadding overrides, destination-wins resource merging with conflict
    /// renaming, and merge collision renaming, matching the reference.
    /// Widgets without an importable field (direct annotations, formless
    /// sources, bare tops) fall back to plain clones, also matching the
    /// reference.
    /// </summary>
    private static void CopyPlacementAnnotations(
        GraphCopier copier,
        int pageId,
        double[] placeBox,
        double[] destTransform,
        Dictionary<int, List<OverlayPlacement>> underlays,
        Dictionary<int, List<OverlayPlacement>> overlays,
        List<ResolvedSpec> resolvedUnderlays,
        List<ResolvedSpec> resolvedOverlays,
        Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache,
        List<AcroFormCopy.OverlayFieldImport> imports,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        limits.Validate();
        CopyGroupAnnotations(copier, pageId, placeBox, destTransform, underlays, resolvedUnderlays, formCache, imports, compress, level, limits, governor, ownership, diagnostics, cancellationToken, ref allocator);
        CopyGroupAnnotations(copier, pageId, placeBox, destTransform, overlays, resolvedOverlays, formCache, imports, compress, level, limits, governor, ownership, diagnostics, cancellationToken, ref allocator);
    }

    private static void CopyGroupAnnotations(
        GraphCopier copier,
        int pageId,
        double[] placeBox,
        double[] destTransform,
        Dictionary<int, List<OverlayPlacement>> groups,
        List<ResolvedSpec> resolved,
        Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache,
        List<AcroFormCopy.OverlayFieldImport> imports,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        foreach (KeyValuePair<int, List<OverlayPlacement>> group in SortedGroups(groups))
        {
            ResolvedSpec spec = resolved[group.Key];
            foreach (OverlayPlacement placement in group.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                OverlayForm? form = GetOrBuildOverlayForm(spec, placement, formCache, compress, level, limits, governor, ownership, diagnostics, cancellationToken, ref allocator);
                if (form is null)
                {
                    continue;
                }

                double[] cm = PlacementMatrix(form.Box, form.FormMatrix, placeBox, destTransform, true, false);
                CopyOverlayPageAnnots(copier, pageId, spec.Source, placement.FromPage, cm, imports, limits, governor, ownership, diagnostics, cancellationToken, ref allocator);
            }
        }
    }

    private static void CopyOverlayPageAnnots(
        GraphCopier destCopier,
        int destPageId,
        OverlaySource source,
        int fromPage,
        double[] cm,
        List<AcroFormCopy.OverlayFieldImport> imports,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        if (fromPage < 1 || fromPage > source.Document.PageReferences.Count)
        {
            return;
        }

        PageLocation location = source.Document.PageReferences[fromPage - 1];
        DocumentEngine engine = source.Document.Engine;
        IIndirectResolver resolver = (IIndirectResolver)engine;
        PdfName annotsKey = PdfName.FromAscii("Annots");
        if (!location.Page.TryGet(annotsKey, out QpdfObject? annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
        {
            return;
        }

        QpdfObject annotsTarget = annotsRaw;
        if (annotsRaw is PdfReference annotsReference)
        {
            if (!resolver.TryResolve(annotsReference, out QpdfObject? resolvedAnnots) || resolvedAnnots is null || resolvedAnnots is PdfNull)
            {
                return;
            }

            annotsTarget = resolvedAnnots;
        }

        if (annotsTarget is not PdfArray annots)
        {
            return;
        }

        Dictionary<int, int> memo = new();
        int nullId = -1;
        List<PdfReference> fresh = new(annots.Count);
        AcroFormCopy.OverlaySourceForm? sourceForm = source.FormInfo;
        PdfReference? placementDr = null;
        int annotIndex = 0;
        foreach (QpdfObject item in annots.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            QpdfObject target = item;
            if (item is PdfReference itemReference)
            {
                if (!resolver.TryResolve(itemReference, out QpdfObject? resolvedItem) || resolvedItem is null || resolvedItem is PdfNull)
                {
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Ignoring overlay annotation that is not a dictionary.");
                    continue;
                }

                target = resolvedItem;
            }

            if (target is not PdfDictionary annot)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Ignoring overlay annotation that is not a dictionary.");
                continue;
            }

            if (IsPageTreeDictionary(annot, resolver))
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Ignoring overlay annotation that is not a dictionary.");
                continue;
            }

            if (IsWidgetAnnotation(annot, resolver))
            {
                int? widgetId = item is PdfReference widgetReference ? widgetReference.ObjectNumber : (int?)null;
                if (TryImportOverlayWidget(destCopier, engine, annot, widgetId, item, fromPage, annotIndex, source, sourceForm, memo, imports, ref placementDr, limits, governor, ownership, diagnostics, cancellationToken, ref allocator, ref nullId, fresh))
                {
                    annotIndex += 1;
                    continue;
                }
            }

            annotIndex += 1;
            QpdfObject built = BuildOverlayContainer(target, engine, destCopier, memo, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, 0);
            int id = allocator;
            checked { allocator += 1; }
            ownership.Reserve(64L);
            destCopier.AddIndirectObjectWithId(built, id);
            fresh.Add(new PdfReference(id, 0));
        }

        // Cloning preserves sharing within this placement. Transform each copied
        // annotation/appearance once, after all fields and annotations are copied.
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        GovernedIntSet transformed = GovernedIntSet.Create(traversal, governor, cancellationToken);
        GovernedIntSet appearances = GovernedIntSet.Create(traversal, governor, cancellationToken);
        foreach (PdfReference annotation in fresh)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!transformed.Add(traversal, annotation.ObjectNumber)) { continue; }
            TransformOverlayRect(destCopier, annotation.ObjectNumber, cm, governor, ownership);
            TransformOverlayAppearances(destCopier, annotation.ObjectNumber, cm, appearances, traversal, governor, ownership, cancellationToken);
        }

        if (!destCopier.CopiedObjects.TryGetValue(destPageId, out QpdfObject? pageRaw) || pageRaw is not PdfDictionary page)
        {
            return;
        }

        PoolResolver pool = new(destCopier.CopiedObjects);
        PdfArray? current = null;
        int currentId = -1;
        if (page.TryGet(annotsKey, out QpdfObject? destRaw) && destRaw is not null && destRaw is not PdfNull)
        {
            if (destRaw is PdfReference destReference
                && ((IIndirectResolver)pool).TryResolve(destReference, out QpdfObject? destResolved)
                && destResolved is PdfArray destFound)
            {
                current = destFound;
                currentId = destReference.ObjectNumber;
            }
            else if (destRaw is PdfArray destDirect)
            {
                current = destDirect;
            }
        }

        List<QpdfObject> items = new();
        if (current is not null)
        {
            for (int i = 0; i < current.Count; i++)
            {
                items.Add(current[i]);
            }
        }

        foreach (PdfReference clone in fresh)
        {
            items.Add(clone);
        }

        ownership.Reserve(64L);
        PdfArray rebuilt = new(items);
        if (currentId >= 0)
        {
            destCopier.ReplaceCopiedObject(currentId, rebuilt);
        }
        else
        {
            destCopier.ReplaceCopiedObject(destPageId, ReplaceEntry(page, annotsKey, rebuilt, governor, ownership));
        }
    }

    /// <summary>
    /// Imports one overlay/underlay widget annotation's field through the shared
    /// form machinery, mirroring the reference annotation transform: the top-level
    /// field is copied once per placement with per-placement default resources,
    /// radio siblings share that copy through the placement memo, and the
    /// annotation clone reuses the field copy (merged fields) or its kid copy so
    /// later field pruning stays linked. Returns true when the annotation was
    /// handled; otherwise the caller falls back to the ordinary plain clone.
    /// </summary>
    private static bool TryImportOverlayWidget(
        GraphCopier destCopier,
        DocumentEngine sourceEngine,
        PdfDictionary annot,
        int? annotId,
        QpdfObject item,
        int fromPage,
        int annotIndex,
        OverlaySource source,
        AcroFormCopy.OverlaySourceForm? sourceForm,
        Dictionary<int, int> memo,
        List<AcroFormCopy.OverlayFieldImport> imports,
        ref PdfReference? placementDr,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ref int allocator,
        ref int nullId,
        List<PdfReference> fresh)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        if (sourceForm is null || !sourceForm.HasFields)
        {
            return false;
        }

        if (!annotId.HasValue)
        {
            if (source.ImportNotes.Add("d:" + fromPage.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + annotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "Ignoring an overlay widget annotation that the source form does not reference; cloning it without a field.");
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "Ignoring an overlay form field that is not indirect; cloning its annotation without a field.");
            }

            return false;
        }

        AcroFormCopy.OverlayTopMatch? top = AcroFormCopy.FindOverlayTopField(annot, annotId, true, (IIndirectResolver)sourceEngine);
        if (top is null)
        {
            return false;
        }

        if (!sourceForm.ReachedObjects.Contains(annotId.Value))
        {
            if (source.ImportNotes.Add("r:" + annotId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "Ignoring an overlay widget annotation that the source form does not reference; cloning it without a field.");
            }
        }

        if (top.DirectTop)
        {
            return false;
        }

        if (top.Cycled)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Detected a cycle in overlay form-field parents; importing the top field as encountered.");
        }

        int topId = top.Id ?? annotId.Value;
        if (!memo.ContainsKey(topId))
        {
            QpdfObject fieldCopy = CopyOverlayNode(new PdfReference(topId, 0), sourceEngine, destCopier, memo, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, 0);
            if (fieldCopy is not PdfReference fieldRef)
            {
                return false;
            }

            placementDr ??= CopyOverlayImportDR(sourceForm, sourceEngine, destCopier, memo, limits, governor, ownership, cancellationToken, ref allocator, ref nullId);
            Dictionary<PdfName, QpdfObject> extras = new();
            CopiedForm part = new([fieldRef], true, sourceForm.NeedAppearances, sourceForm.NeedAppearances, placementDr, false, [], extras);
            imports.Add(new AcroFormCopy.OverlayFieldImport(destCopier, part, sourceForm.DefaultAppearance, sourceForm.Quadding));
        }

        QpdfObject cloned = CopyOverlayNode(item, sourceEngine, destCopier, memo, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, 0);
        if (cloned is not PdfReference cloneRef)
        {
            return false;
        }

        fresh.Add(cloneRef);
        return true;
    }

    private static PdfReference? CopyOverlayImportDR(
        AcroFormCopy.OverlaySourceForm sourceForm,
        DocumentEngine sourceEngine,
        GraphCopier destCopier,
        Dictionary<int, int> memo,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken,
        ref int allocator,
        ref int nullId)
    {
        if (sourceForm.DefaultResources is null)
        {
            return null;
        }

        QpdfObject copied = CopyOverlayNode(sourceForm.DefaultResources, sourceEngine, destCopier, memo, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, 0);
        if (copied is PdfReference link)
        {
            return link;
        }

        if (copied is PdfDictionary dictionary)
        {
            int id = allocator;
            checked
            {
                allocator += 1;
            }

            ownership.Reserve(64L);
            return destCopier.AddIndirectObjectWithId(dictionary, id);
        }

        return null;
    }

    private static bool IsWidgetAnnotation(PdfDictionary annot, IIndirectResolver source)
    {
        if (!annot.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return false;
        }

        QpdfObject target = raw;
        if (raw is PdfReference reference
            && source.TryResolve(reference, out QpdfObject? resolved)
            && resolved is not null
            && resolved is not PdfNull)
        {
            target = resolved;
        }

        return target is PdfName name && name.DisplayText == "Widget";
    }

    private static bool IsPageTreeDictionary(PdfDictionary dictionary, IIndirectResolver source)
    {
        if (!dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return false;
        }

        QpdfObject target = raw;
        if (raw is PdfReference reference
            && source.TryResolve(reference, out QpdfObject? resolved)
            && resolved is not null
            && resolved is not PdfNull)
        {
            target = resolved;
        }

        if (target is not PdfName name)
        {
            return false;
        }

        return name.DisplayText == "Page" || name.DisplayText == "Pages" || name.DisplayText == "Catalog";
    }

    private static QpdfObject CopyOverlayNode(
        QpdfObject node,
        DocumentEngine sourceEngine,
        GraphCopier destCopier,
        Dictionary<int, int> memo,
        ref int allocator,
        ref int nullId,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken,
        int depth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException("Overlay annotation depth exceeds the limit.", null, null);
        }

        if (node is PdfReference reference)
        {
            if (memo.TryGetValue(reference.ObjectNumber, out int mapped))
            {
                return new PdfReference(mapped, 0);
            }

            IIndirectResolver resolver = (IIndirectResolver)sourceEngine;
            if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
            {
                return PdfNull.Instance;
            }

            if (resolved is PdfDictionary dictionary)
            {
                string? kind = OverlayNodeKind(dictionary, resolver);
                if (kind == "Page")
                {
                    return OverlayNullRef(destCopier, ref allocator, ref nullId, governor, ownership);
                }

                if (kind == "Pages" || kind == "Catalog")
                {
                    return PdfNull.Instance;
                }
            }

            int id = allocator;
            checked { allocator += 1; }
            ownership.Reserve(64L);
            memo[reference.ObjectNumber] = id;
            QpdfObject built = BuildOverlayContainer(resolved, sourceEngine, destCopier, memo, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth + 1);
            destCopier.AddIndirectObjectWithId(built, id);
            return new PdfReference(id, 0);
        }

        if (node is PdfDictionary || node is PdfArray || node is PdfStream)
        {
            return BuildOverlayContainer(node, sourceEngine, destCopier, memo, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth + 1);
        }

        return node;
    }

    private static QpdfObject BuildOverlayContainer(
        QpdfObject node,
        DocumentEngine sourceEngine,
        GraphCopier destCopier,
        Dictionary<int, int> memo,
        ref int allocator,
        ref int nullId,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken,
        int depth)
    {
        if (node is PdfDictionary dictionary)
        {
            Dictionary<PdfName, QpdfObject> entries = new();
            foreach (PdfName key in dictionary.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!dictionary.TryGet(key, out QpdfObject? found) || found is null || found is PdfNull)
                {
                    continue;
                }

                ownership.Reserve(32L);
                QpdfObject child = CopyOverlayNode(found, sourceEngine, destCopier, memo, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth);
                if (child is PdfNull)
                {
                    continue;
                }

                if (child is PdfReference childReference && nullId >= 0 && childReference.ObjectNumber == nullId)
                {
                    continue;
                }

                entries[key] = child;
            }

            ownership.Reserve(64L);
            return new PdfDictionary(entries);
        }

        if (node is PdfArray array)
        {
            List<QpdfObject> items = new(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ownership.Reserve(16L);
                items.Add(CopyOverlayNode(array[i], sourceEngine, destCopier, memo, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth));
            }

            ownership.Reserve(32L);
            return new PdfArray(items);
        }

        if (node is PdfStream stream)
        {
            QpdfObject rebuiltDictionary = BuildOverlayContainer(stream.Dictionary, sourceEngine, destCopier, memo, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth);
            PdfDictionary dictionaryValue = rebuiltDictionary as PdfDictionary ?? stream.Dictionary;
            byte[] data = stream.Data;
            ownership.Reserve(data.Length);
            return new PdfStream(dictionaryValue, data);
        }

        return node;
    }

    private static string? OverlayNodeKind(PdfDictionary dictionary, IIndirectResolver source)
    {
        if (!dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        QpdfObject target = raw;
        if (raw is PdfReference reference
            && source.TryResolve(reference, out QpdfObject? resolved)
            && resolved is not null
            && resolved is not PdfNull)
        {
            target = resolved;
        }

        if (target is not PdfName name)
        {
            return null;
        }

        if (name.DisplayText == "Page" || name.DisplayText == "Pages" || name.DisplayText == "Catalog")
        {
            return name.DisplayText;
        }

        return null;
    }

    private static PdfReference OverlayNullRef(GraphCopier destCopier, ref int allocator, ref int nullId, ResourceGovernor governor, ParserMemoryScope ownership)
    {
        if (nullId < 0)
        {
            nullId = allocator;
            checked { allocator += 1; }
            ownership.Reserve(64L);
            destCopier.AddIndirectObjectWithId(PdfNull.Instance, nullId);
        }

        return new PdfReference(nullId, 0);
    }

    private static void TransformOverlayRect(
        GraphCopier destCopier,
        int annotId,
        double[] cm,
        ResourceGovernor governor, ParserMemoryScope ownership)
    {
        if (!destCopier.CopiedObjects.TryGetValue(annotId, out QpdfObject? raw) || raw is not PdfDictionary annot)
        {
            return;
        }

        PoolResolver pool = new(destCopier.CopiedObjects);
        double[] rect = ReadOverlayNumbers(annot, "Rect", 4, pool) ?? new double[4];
        (rect[0], rect[2]) = (Math.Min(rect[0], rect[2]), Math.Max(rect[0], rect[2]));
        (rect[1], rect[3]) = (Math.Min(rect[1], rect[3]), Math.Max(rect[1], rect[3]));
        double[] moved = FromArray(cm).TransformRect(rect);
        if (!IsFiniteRect(moved))
        {
            moved = new double[4];
        }

        List<QpdfObject> values = new(4);
        foreach (double value in moved)
        {
            ownership.Reserve(32L);
            values.Add(new PdfReal(FormatDouble(value), value));
        }

        Dictionary<PdfName, QpdfObject> entries = new();
        PdfName rectKey = PdfName.FromAscii("Rect");
        foreach (PdfName key in annot.Keys)
        {
            if (key.DisplayText == "Rect")
            {
                continue;
            }

            if (annot.TryGet(key, out QpdfObject? found) && found is not null)
            {
                entries[key] = found;
            }
        }

        entries[rectKey] = new PdfArray(values);
        ownership.Reserve(64L);
        destCopier.ReplaceCopiedObject(annotId, new PdfDictionary(entries));
    }

    private static void TransformOverlayAppearances(
        GraphCopier destCopier,
        int annotId,
        double[] cm,
        GovernedIntSet done,
        ParserMemoryScope traversal,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
    {
        PoolResolver pool = new(destCopier.CopiedObjects);
        if (!destCopier.CopiedObjects.TryGetValue(annotId, out QpdfObject? raw) || raw is not PdfDictionary annot)
        {
            return;
        }

        if (!annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? apRaw) || apRaw is null || apRaw is PdfNull)
        {
            return;
        }

        QpdfObject apTarget = apRaw is PdfReference apReference
            ? ResolveOrNull(pool, apReference) ?? apRaw
            : apRaw;
        if (apTarget is not PdfDictionary ap)
        {
            return;
        }

        foreach (PdfName key1 in ap.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ap.TryGet(key1, out QpdfObject? value1) || value1 is null)
            {
                continue;
            }

            QpdfObject target1 = value1 is PdfReference reference1
                ? ResolveOrNull(pool, reference1) ?? value1
                : value1;
            if (target1 is PdfStream)
            {
                if (value1 is PdfReference streamReference && done.Add(traversal, streamReference.ObjectNumber))
                {
                    TransformOverlayStreamMatrix(destCopier, streamReference.ObjectNumber, cm, governor, ownership);
                }

                continue;
            }

            if (target1 is PdfDictionary nested)
            {
                foreach (PdfName key2 in nested.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!nested.TryGet(key2, out QpdfObject? value2) || value2 is null)
                    {
                        continue;
                    }

                    if (value2 is PdfReference streamReference2)
                    {
                        QpdfObject? resolved2 = ResolveOrNull(pool, streamReference2);
                        if (resolved2 is PdfStream && done.Add(traversal, streamReference2.ObjectNumber))
                        {
                            TransformOverlayStreamMatrix(destCopier, streamReference2.ObjectNumber, cm, governor, ownership);
                        }
                    }
                }
            }
        }
    }

    private static void TransformOverlayStreamMatrix(
        GraphCopier destCopier,
        int streamId,
        double[] cm,
        ResourceGovernor governor, ParserMemoryScope ownership)
    {
        governor.NoteOperation();
        if (!destCopier.CopiedObjects.TryGetValue(streamId, out QpdfObject? raw) || raw is not PdfStream stream)
        {
            return;
        }

        PoolResolver pool = new(destCopier.CopiedObjects);
        bool present = stream.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject? matrixRaw) && matrixRaw is not null && matrixRaw is not PdfNull;
        double[] matrix = new double[6] { 1.0, 0.0, 0.0, 1.0, 0.0, 0.0 };
        if (present && matrixRaw is not null)
        {
            matrix = ReadOverlayMatrix(matrixRaw, pool) ?? new double[6];
        }

        ContentMatrix work = FromArray(matrix);
        work.Concat(FromArray(cm));
        double[] next = work.ToArray();
        if (!present && IsIdentityMatrix(next))
        {
            return;
        }

        Dictionary<PdfName, QpdfObject> entries = new();
        PdfName matrixKey = PdfName.FromAscii("Matrix");
        foreach (PdfName key in stream.Dictionary.Keys)
        {
            if (key.DisplayText == "Matrix")
            {
                continue;
            }

            if (stream.Dictionary.TryGet(key, out QpdfObject? found) && found is not null)
            {
                entries[key] = found;
            }
        }

        List<QpdfObject> values = new(6);
        foreach (double value in next)
        {
            ownership.Reserve(32L);
            values.Add(new PdfReal(FormatDouble(value), value));
        }

        entries[matrixKey] = new PdfArray(values);
        ownership.Reserve(64L);
        destCopier.ReplaceCopiedObject(streamId, new PdfStream(new PdfDictionary(entries), stream.Data));
    }

    private static double[]? ReadOverlayNumbers(PdfDictionary dictionary, string key, int count, PoolResolver pool)
    {
        if (!dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        QpdfObject target = raw is PdfReference reference
            ? ResolveOrNull(pool, reference) ?? raw
            : raw;
        if (target is not PdfArray array || array.Count != count)
        {
            return null;
        }

        double[] values = new double[count];
        for (int i = 0; i < count; i++)
        {
            QpdfObject item = array[i];
            if (item is PdfReference itemReference)
            {
                QpdfObject? resolved = ResolveOrNull(pool, itemReference);
                if (resolved is null)
                {
                    return null;
                }

                item = resolved;
            }

            if (item is PdfInteger integer)
            {
                values[i] = (double)integer.Value;
            }
            else if (item is PdfReal real)
            {
                values[i] = real.Value;
            }
            else
            {
                return null;
            }

            if (!double.IsFinite(values[i]))
            {
                return null;
            }
        }

        return values;
    }

    private static double[]? ReadOverlayMatrix(QpdfObject raw, PoolResolver pool)
    {
        QpdfObject target = raw is PdfReference reference
            ? ResolveOrNull(pool, reference) ?? raw
            : raw;
        if (target is not PdfArray array || array.Count != 6)
        {
            return null;
        }

        double[] values = new double[6];
        for (int i = 0; i < 6; i++)
        {
            QpdfObject item = array[i];
            if (item is PdfReference itemReference)
            {
                QpdfObject? resolved = ResolveOrNull(pool, itemReference);
                if (resolved is null)
                {
                    return null;
                }

                item = resolved;
            }

            if (item is PdfInteger integer)
            {
                values[i] = (double)integer.Value;
            }
            else if (item is PdfReal real)
            {
                values[i] = real.Value;
            }
            else
            {
                return null;
            }

            if (!double.IsFinite(values[i]))
            {
                return null;
            }
        }

        return values;
    }

    private static bool IsIdentityMatrix(double[] values)
    {
        return values[0] == 1.0 && values[1] == 0.0 && values[2] == 0.0 && values[3] == 1.0 && values[4] == 0.0 && values[5] == 0.0;
    }

    private static bool IsFiniteRect(double[] rect)
    {
        return double.IsFinite(rect[0]) && double.IsFinite(rect[1]) && double.IsFinite(rect[2]) && double.IsFinite(rect[3]);
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

