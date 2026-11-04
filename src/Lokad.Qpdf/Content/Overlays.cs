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

internal sealed record OverlayApplication(List<GraphCopier> Copiers, int NextId,
    IReadOnlyList<AcroFormCopy.FieldImport> FieldImports, QpdfDocument.VersionParts? Version, int ExtensionLevel);

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
        IIndirectResolver? retainedResolver,
        HashSet<int>? processedContentForms)
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
        if ((underlays.Count == 0 && overlays.Count == 0) || occurrences.Count == 0)
        {
            return new OverlayApplication([], nextId, [], null, 0);
        }

        if ((long)occurrences.Count > limits.MaxContainerSize)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), occurrences.Count, limits.MaxContainerSize);
        }

        using ParserMemoryScope planning = ParserMemoryScope.Create(governor);
        Dictionary<string, OverlaySource> sources = ParserContainers.CreateDictionary<string, OverlaySource>(planning);
        try
        {
            int allocator = nextId;
            GovernedIntDictionary<List<OverlayPlacement>> underlayPages = GovernedIntDictionary<List<OverlayPlacement>>.Create(planning, governor, IntPtr.Size, cancellationToken);
            GovernedIntDictionary<List<OverlayPlacement>> overlayPages = GovernedIntDictionary<List<OverlayPlacement>>.Create(planning, governor, IntPtr.Size, cancellationToken);
            for (int index = 0; index < underlays.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ResolveSpecAsync(host, primaryInput, underlays[index], index, allocator, occurrences.Count, recovery, limits, governor, diagnostics, sources, underlayPages, planning, ownership, cancellationToken, retainedResolver is not null).ConfigureAwait(false);
                allocator = MaxAllocator(allocator, sources);
            }

            for (int index = 0; index < overlays.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ResolveSpecAsync(host, primaryInput, overlays[index], index, allocator, occurrences.Count, recovery, limits, governor, diagnostics, sources, overlayPages, planning, ownership, cancellationToken, retainedResolver is not null).ConfigureAwait(false);
                allocator = MaxAllocator(allocator, sources);
            }

            Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache = ParserContainers.CreateDictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm>(planning);
            List<AcroFormCopy.FieldImport> imports = ParserContainers.CreateList<AcroFormCopy.FieldImport>(ownership);
            for (int dest = 0; dest < occurrences.Count; dest++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                IReadOnlyList<OverlayPlacement> below = underlayPages.TryGetValue(dest, out List<OverlayPlacement>? under) ? under : Array.Empty<OverlayPlacement>();
                IReadOnlyList<OverlayPlacement> above = overlayPages.TryGetValue(dest, out List<OverlayPlacement>? over) ? over : Array.Empty<OverlayPlacement>();
                if (below.Count == 0 && above.Count == 0)
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
                    below,
                    above,
                    formCache,
                    imports,
                    retainedResolver,
                    processedContentForms,
                    compress,
                    level,
                    limits,
                    governor, planning, ownership,
                    diagnostics,
                    cancellationToken,
                    ref allocator);
            }

            List<GraphCopier> copiers = ParserContainers.CreateList<GraphCopier>(ownership);
            QpdfDocument.VersionParts? version = null; int extensionLevel = 0;
            foreach (OverlaySource source in sources.Values)
            {
                ParserContainers.AddItem(ownership, copiers, source.Copier, IntPtr.Size);
                CatalogCopy.IncludeSourceVersion(ref version, ref extensionLevel, source.Document, governor, cancellationToken);
            }

            return new OverlayApplication(copiers, allocator, imports, version, extensionLevel);
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

    internal sealed record OverlaySource(QpdfDocument Document, GraphCopier Copier, int PageCount, AcroFormCopy.OverlaySourceForm? FormInfo, HashSet<string> ImportNotes);

    private sealed record PageBundle(QpdfObject? Resources, QpdfObject? Group, QpdfObject BoxObject, double[] Box, byte[] Content, long Rotate, double UserUnit, bool HasTransform, bool DeferredCopy);

    private sealed record ResolvedSpec(OverlaySource Source, int SpecIndex, GovernedIntDictionary<PageBundle> Bundles);

    private readonly record struct OverlayPlacement(ResolvedSpec Spec, int FromPage);

    private static async ValueTask ResolveSpecAsync(
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
        GovernedIntDictionary<List<OverlayPlacement>> pages,
        ParserMemoryScope planning,
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
                planning.Reserve(160L);
                ParserContainers.EnsureEntryCapacity(planning, sources, IntPtr.Size);
                source = new OverlaySource(opened, copier, count, null, new HashSet<string>(StringComparer.Ordinal));
                sources[path.Value] = source;
            }
            catch
            {
                await opened.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        // Default ranges are implicit. Explicit ranges are fully validated even
        // when their intersection yields no placements.
        IReadOnlyList<int>? fromPages = spec.From is null ? null
            : OverlayRanges.Resolve(spec.From, source.PageCount, limits.MaxContainerSize, governor, planning, cancellationToken);
        IReadOnlyList<int>? toPages = spec.To is null ? null
            : OverlayRanges.Resolve(spec.To, destCount, limits.MaxContainerSize, governor, planning, cancellationToken);
        IReadOnlyList<int> repeatPages = spec.Repeat is null ? []
            : OverlayRanges.Resolve(spec.Repeat, source.PageCount, limits.MaxContainerSize, governor, planning, cancellationToken);
        int fromCount = fromPages?.Count ?? source.PageCount;
        int toCount = toPages?.Count ?? destCount;
        int placementCount = repeatPages.Count == 0 ? Math.Min(fromCount, toCount) : toCount;
        if (placementCount == 0) { return; }

        if (source.FormInfo is null)
        {
            AcroFormCopy.OverlaySourceForm? formInfo = AcroFormCopy.TryReadOverlaySourceForm(source.Document.Engine, limits, governor, planning, cancellationToken);
            if (formInfo is not null)
            {
                if (formInfo.FieldsNotArray)
                { diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "Ignoring a source /Fields entry that is not an array while importing overlay form fields."); }
                if (formInfo.HasDirectNodes)
                { diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "Ignoring a direct object as a field or annotation while importing overlay form fields."); }
                planning.Reserve(64L);
                source = source with { FormInfo = formInfo };
                sources[path.Value] = source;
            }
        }

        GovernedIntDictionary<PageBundle> bundles = GovernedIntDictionary<PageBundle>.Create(planning, governor, IntPtr.Size, cancellationToken);
        GovernedIntSet needed = GovernedIntSet.Create(planning, governor, cancellationToken);
        planning.Reserve(40L);
        ResolvedSpec resolved = new(source, specIndex, bundles);
        for (int position = 0; position < placementCount; position++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int dest = toPages is null ? position : toPages[position] - 1;
            int from = position < fromCount ? (fromPages is null ? position + 1 : fromPages[position])
                : repeatPages[(position - fromCount) % repeatPages.Count];
            if (!pages.TryGetValue(dest, out List<OverlayPlacement>? placements))
            {
                placements = ParserContainers.CreateList<OverlayPlacement>(planning);
                pages.TryAdd(planning, dest, placements);
            }
            // Specs append in declaration order; each spec preserves range order
            // and duplicates, so neither grouping nor sorting is needed later.
            ParserContainers.AddItem(planning, placements, new OverlayPlacement(resolved, from), 16L);
            needed.Add(planning, from);
        }
        foreach (int page in needed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            planning.Reserve(224L); // Rectangle/scratch, bundle and later deferred clone.
            PageBundle? bundle = BuildBundle(source, page, limits, governor, ownership, diagnostics, cancellationToken, deferCopy);
            if (bundle is not null) { bundles.TryAdd(planning, page, bundle); }
            else { planning.Release(224L); }
        }
    }

    private static PageBundle? BuildBundle(
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
        QpdfObject? trim = null;
        if (location.Page.TryGet(PdfName.FromAscii("TrimBox"), out QpdfObject? trimRaw) && trimRaw is not null && trimRaw is not PdfNull)
        {
            if (trimRaw is PdfReference trimReference
                && PdfObjectLookup.TryResolve(trimReference, engine, governor, cancellationToken, out QpdfObject? resolvedTrim)
                && resolvedTrim is PdfNull) { trimRaw = null; }
            trim = trimRaw;
        }

        QpdfObject? crop = FindInherited(engine, location, "CropBox", limits, governor, cancellationToken);
        QpdfObject? media = FindInherited(engine, location, "MediaBox", limits, governor, cancellationToken);
        QpdfObject? boxObject = trim ?? crop ?? media;
        if (boxObject is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page has no bounding box; skipping it.");
            return null;
        }
        double[]? box = ReadRectangle(boxObject, engine);
        if (box is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page bounding box is invalid; skipping it.");
            return null;
        }
        // The bundle's coarse admission covers this fixed scratch beside its
        // rectangle. The deferred bundle clone is created later, after it retires.
        Span<double> mediaBox = stackalloc double[4];
        if (ReferenceEquals(boxObject, media)) { box.AsSpan().CopyTo(mediaBox); }
        else if (!TryReadRectangle(media, engine, mediaBox))
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page media box is invalid; skipping it.");
            return null;
        }
        if (!TryIntersectRectangle(box, mediaBox, out bool rebuildBox))
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page has an empty media intersection; skipping it.");
            return null;
        }

        QpdfObject? resources = FindInherited(engine, location, "Resources", limits, governor, cancellationToken);
        QpdfObject? group = null;
        if (location.Page.TryGet(PdfName.FromAscii("Group"), out QpdfObject? groupRaw) && groupRaw is not null && groupRaw is not PdfNull)
        {
            group = groupRaw;
        }

        long rotate = 0L;
        bool hasRotate = false;
        QpdfObject? rotateRaw = FindInherited(engine, location, "Rotate", limits, governor, cancellationToken);
        if (rotateRaw is PdfReference rotateReference)
        { PdfObjectLookup.TryResolve(rotateReference, engine, governor, cancellationToken, out rotateRaw); }
        if (rotateRaw is PdfInteger rotateInteger)
        {
            rotate = rotateInteger.Value;
            hasRotate = true;
        }

        double userUnit = 1.0;
        bool hasUnit = false;
        QpdfObject? unitRaw = PdfDictionaryLookup.TryGet(location.Page, "UserUnit"u8, governor, cancellationToken, out QpdfObject declaredUnit)
            ? declaredUnit : null;
        if (unitRaw is PdfReference unitReference)
        { PdfObjectLookup.TryResolve(unitReference, engine, governor, cancellationToken, out unitRaw); }
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
        if (content is null) { return null; }
        // Replace this role's bounds, leaving independent raw-box aliases intact.
        if (rebuildBox) { boxObject = NumberArray(box, governor, ownership); }
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

        governor.NoteOperation();
        QpdfObject copiedBox = source.Copier.CopyValue(boxObject, 0);

        return new PageBundle(copiedResources, copiedGroup, copiedBox, box, content, rotate, userUnit, hasRotate || hasUnit, false);
    }

    private static QpdfObject? FindInherited(
        DocumentEngine engine, PageLocation location, string key, QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
        => PageInheritance.Find(engine, location, PdfName.FromAscii(key), limits, governor, cancellationToken);

    private static byte[]? CollectSourceContents(
        PageLocation location, DocumentEngine engine, QpdfLimits limits, ResourceGovernor governor,
        ParserMemoryScope ownership, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        byte[]? content = CollectContents(location.Page, (IIndirectResolver)engine, limits, governor, ownership, cancellationToken);
        if (content is null)
        { diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Overlay page content could not be decoded or read; skipping it."); }
        return content;
    }

    private static byte[]? CollectContents(PdfDictionary page, IIndirectResolver resolver, QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, token, out QpdfObject contents) || contents is PdfNull) { return []; }
        using ContentEntries? entries = ContentEntries.TryRead(contents, resolver, governor, token);
        if (entries is null) { return null; }
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, token);
        for (int i = 0; i < entries.Count; i++)
        {
            PdfStream stream = entries.Get(i).Stream;
            if (!chunks.TryAdd(stream, resolver, limits))
            { FilterChain.RequireEmbeddedData(stream, resolver, governor, token); return null; }
        }
        using JoinedContent joined = chunks.Join();
        return joined.TakeBytes(ownership);
    }

    private static bool TryIntersectRectangle(Span<double> box, ReadOnlySpan<double> media, out bool changed)
    {
        double left = Math.Max(Math.Min(box[0], box[2]), Math.Min(media[0], media[2]));
        double bottom = Math.Max(Math.Min(box[1], box[3]), Math.Min(media[1], media[3]));
        double right = Math.Min(Math.Max(box[0], box[2]), Math.Max(media[0], media[2]));
        double top = Math.Min(Math.Max(box[1], box[3]), Math.Max(media[1], media[3]));
        changed = left != box[0] || bottom != box[1] || right != box[2] || top != box[3];
        if (left >= right || bottom >= top) { return false; }
        box[0] = left; box[1] = bottom; box[2] = right; box[3] = top;
        return true;
    }

    private static double[]? ReadRectangle(QpdfObject? value, IIndirectResolver resolver)
    {
        Span<double> numbers = stackalloc double[4];
        return TryReadRectangle(value, resolver, numbers) ? numbers.ToArray() : null;
    }

    private static bool TryReadRectangle(QpdfObject? value, IIndirectResolver resolver, Span<double> numbers)
    {
        QpdfObject? target = value;
        if (target is PdfReference reference)
        {
            if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
            {
                return false;
            }

            target = resolved;
        }

        if (target is not PdfArray array || array.Count != 4)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            QpdfObject item = array[i];
            if (item is PdfReference itemReference)
            {
                if (!resolver.TryResolve(itemReference, out QpdfObject? resolvedItem) || resolvedItem is null)
                {
                    return false;
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
                return false;
            }

            if (!double.IsFinite(numbers[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyToPage(
        GraphCopier copier,
        int pageId,
        PdfDictionary page,
        IReadOnlyList<OverlayPlacement> underlays,
        IReadOnlyList<OverlayPlacement> overlays,
        Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache,
        List<AcroFormCopy.FieldImport> imports,
        IIndirectResolver? retainedResolver,
        HashSet<int>? processedContentForms,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope planning,
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
        if (!TryIntersectRectangle(placeBox, media, out _))
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Output page has an empty media intersection; skipping overlay for it.");
            return;
        }

        long rotate = 0L;
        page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject? rotateRaw);
        if (rotateRaw is PdfReference rotateReference)
        { PdfObjectLookup.TryResolve(rotateReference, resolver, governor, cancellationToken, out rotateRaw); }
        if (rotateRaw is PdfInteger rotateInteger)
        {
            rotate = rotateInteger.Value;
        }

        double userUnit = 1.0;
        if (page.TryGet(PdfName.FromAscii("UserUnit"), out QpdfObject? unitRaw))
        {
            if (unitRaw is PdfReference unitReference)
            { PdfObjectLookup.TryResolve(unitReference, resolver, governor, cancellationToken, out unitRaw); }
            if (unitRaw is PdfInteger unitInteger)
            {
                userUnit = (double)unitInteger.Value;
            }
            else if (unitRaw is PdfReal unitReal)
            {
                userUnit = unitReal.Value;
            }
        }

        byte[]? pageContent = CollectContents(page, resolver, limits, governor, ownership, cancellationToken);
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

        if (!page.TryGet(PdfName.FromAscii("MediaBox"), out QpdfObject? pageBoxObject) || pageBoxObject is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Output page has no bounding box; skipping overlay for it.");
            return;
        }

        ownership.Reserve(512L); // Bounded Form header; resources and payloads are borrowed.
        Dictionary<PdfName, QpdfObject> formEntries = new();
        formEntries[PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject");
        formEntries[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form");
        formEntries[PdfName.FromAscii("BBox")] = pageBoxObject;

        if (pageResources is not null)
        {
            formEntries[PdfName.FromAscii("Resources")] = pageResources;
        }

        if (page.TryGet(PdfName.FromAscii("Group"), out QpdfObject? groupRaw) && groupRaw is not null && groupRaw is not PdfNull)
        {
            formEntries[PdfName.FromAscii("Group")] = groupRaw;
        }

        PdfStream pageForm = EncodeForm(new PdfDictionary(formEntries), pageContent, compress, level, limits, governor, ownership);
        PdfReference pageFormLink = copier.AddIndirectObjectWithId(pageForm, allocator);
        if (processedContentForms is not null)
        { ParserContainers.AddSetItem(ownership, processedContentForms, pageFormLink.ObjectNumber, sizeof(int)); }
        allocator = checked(allocator + 1);
        // Most pages use a fresh namespace. Retained appearances without local
        // resources still need the old page namespace, including XObject names.
        bool keepResources = pageResources is not null && AppearanceResources.PageNeedsResources(page, resolver, governor, cancellationToken);
        int suffix = 1;
        PdfNameTable<QpdfObject> xobjects = PdfNameTable<QpdfObject>.Create(ownership, governor, IntPtr.Size, cancellationToken);
        if (keepResources && pageResources is not null
            && PdfDictionaryLookup.TryGet(pageResources, "XObject"u8, governor, cancellationToken, out QpdfObject oldObjects)
            && CatalogCopy.ResolveDictionary(oldObjects, resolver, governor, cancellationToken) is PdfDictionary previous)
        {
            using PdfDictionary.EntryEnumerator entries = previous.GetEntryEnumerator();
            while (entries.MoveNext(governor, cancellationToken))
            { xobjects.Assign(ownership, governor, entries.Current.Key, entries.Current.Value, cancellationToken); }
        }
        string background = "Fx0";
        if (keepResources) { suffix = 0; background = NextPlacementName(xobjects, ref suffix, governor, ownership, cancellationToken); }
        ownership.Reserve(64L);
        xobjects.Assign(ownership, governor, PdfName.FromAscii(background), pageFormLink, cancellationToken);
        using GovernedBufferStream stacked = new(governor, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), cancellationToken, 256);
        double[] destTransform = TransformMatrix(placeBox, rotate, userUnit, true);
        foreach (OverlayPlacement placement in underlays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OverlayForm? form = GetOrBuildOverlayForm(placement, formCache, compress, level, limits, governor, planning, ownership, cancellationToken, ref allocator);
            if (form is null) { continue; }
            string name = NextPlacementName(xobjects, ref suffix, governor, ownership, cancellationToken);
            xobjects.Assign(ownership, governor, PdfName.FromAscii(name), form.Link, cancellationToken);
            AppendPlacement(stacked, name, PlacementMatrix(form.Box, form.FormMatrix, placeBox, destTransform, true, false));
        }

        // The outer page already supplies its rotation, scale and visible clip.
        // Only imported artwork is fitted to the preferred placement rectangle.
        AppendPlacement(stacked, background, IdentityArray());
        foreach (OverlayPlacement placement in overlays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OverlayForm? form = GetOrBuildOverlayForm(placement, formCache, compress, level, limits, governor, planning, ownership, cancellationToken, ref allocator);
            if (form is null) { continue; }
            string name = NextPlacementName(xobjects, ref suffix, governor, ownership, cancellationToken);
            xobjects.Assign(ownership, governor, PdfName.FromAscii(name), form.Link, cancellationToken);
            AppendPlacement(stacked, name, PlacementMatrix(form.Box, form.FormMatrix, placeBox, destTransform, true, false));
        }

        xobjects.Freeze(ownership, governor, cancellationToken);
        ownership.Reserve(24L); PdfDictionary forms = PdfDictionary.FromOwnedEntries(xobjects);
        PdfDictionary outputResources;
        if (keepResources && pageResources is not null)
        {
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
            outputResources = PdfDictionaryRebuild.ReplaceEntry(graph, pageResources, "XObject"u8, forms);
            graph.Memory.TransferTo(ownership, graph.Bytes);
        }
        else
        {
            ownership.Reserve(512L); // Fixed one-entry resource dictionary and its key.
            outputResources = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("XObject")] = forms });
        }
        byte[] stackedBytes = stacked.ToArray(ownership, cancellationToken);
        PdfStream stackedStream = EncodeForm(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()),
            stackedBytes, compress, level, limits, governor, ownership);

        PdfReference stackedLink = copier.AddIndirectObjectWithId(stackedStream, allocator);
        allocator = checked(allocator + 1);
        PdfDictionary replaced = RebuildOverlayPage(page, outputResources, stackedLink, governor, ownership, cancellationToken);
        copier.ReplaceCopiedObject(pageId, replaced);
        CopyPlacementAnnotations(copier, pageId, placeBox, destTransform, underlays, overlays, formCache, imports, compress, level, limits, governor, planning, ownership, diagnostics, cancellationToken, ref allocator);
    }

    private sealed record OverlayForm(PdfReference Link, double[] Box, double[]? FormMatrix);

    private static string NextPlacementName(PdfNameTable<QpdfObject> objects, ref int suffix,
        ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
    {
        Span<byte> bytes = stackalloc byte[12]; "Fx"u8.CopyTo(bytes);
        while (true)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (suffix == int.MaxValue) { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), (long)suffix + 1L, int.MaxValue); }
            if (!suffix.TryFormat(bytes[2..], out int digits, provider: CultureInfo.InvariantCulture))
            { throw new InvalidOperationException("Placement name exceeds bounded formatting storage."); }
            suffix = checked(suffix + 1);
            if (objects.TryGetValue(bytes[..(digits + 2)], governor, token, out _)) { continue; }
            ownership.Reserve(192L); // Selected name, immutable key bytes and formatting.
            return Encoding.ASCII.GetString(bytes[..(digits + 2)]);
        }
    }

    private static OverlayForm? GetOrBuildOverlayForm(
        OverlayPlacement placement,
        Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope planning,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        ResolvedSpec spec = placement.Spec;
        (GraphCopier Copier, int Spec, int Page) key = (spec.Source.Copier, spec.SpecIndex, placement.FromPage);
        if (formCache.TryGetValue(key, out OverlayForm? cached))
        {
            return cached;
        }

        if (!spec.Bundles.TryGetValue(placement.FromPage, out PageBundle? bundle) || bundle is null)
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
        ownership.Reserve(512L); // Bounded Form header, independent of borrowed resource size.
        Dictionary<PdfName, QpdfObject> entries = new();
        entries[PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject");
        entries[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form");
        entries[PdfName.FromAscii("BBox")] = bundle.BoxObject;
        if (formMatrix is not null)
        {
            entries[PdfName.FromAscii("Matrix")] = NumberArray(formMatrix, governor, ownership);
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
        planning.Reserve(40L);
        ParserContainers.EnsureEntryCapacity(planning, formCache, 32L);
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
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, governor.CancellationToken);
            PdfDictionary header = PdfDictionaryRebuild.ReplaceEntry(graph, dictionary, "Filter"u8, PdfName.FromAscii("FlateDecode"));
            graph.Memory.Reserve(32L); PdfStream result = PdfStream.FromOwnedData(header, encoded);
            graph.Add(32L); graph.Retain(); return result;
        }

        ownership.Reserve(32L);
        return PdfStream.FromOwnedData(dictionary, content);
    }

    private static void AppendPlacement(GovernedBufferStream output, string name, double[] cm)
    {
        AppendAscii(output, "q");
        output.WriteByte((byte)10);
        for (int index = 0; index < cm.Length; index++)
        {
            if (index > 0) { output.WriteByte((byte)' '); }
            WriterNumber.AppendGeometry(output, cm[index]);
        }
        AppendAscii(output, " cm");
        output.WriteByte((byte)10);
        output.WriteByte((byte)47);
        AppendAscii(output, name);
        AppendAscii(output, " Do");
        output.WriteByte((byte)10);
        AppendAscii(output, "Q");
        output.WriteByte((byte)10);
    }

    private static void AppendAscii(GovernedBufferStream output, string text)
    {
        Span<byte> buffer = stackalloc byte[256];
        for (int at = 0; at < text.Length; at += Math.Min(buffer.Length, text.Length - at))
        {
            int count = Math.Min(buffer.Length, text.Length - at);
            int bytes = Encoding.ASCII.GetBytes(text.AsSpan(at, count), buffer);
            output.Write(buffer[..bytes]);
        }
    }

    internal static PdfDictionary RebuildOverlayPage(PdfDictionary page, PdfDictionary resources, PdfReference contents,
        ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
    {
        ownership.CheckOwner(governor);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        bool hasResources = false, hasContents = false;
        using PdfDictionary.EntryEnumerator source = page.GetEntryEnumerator();
        while (source.MoveNext(governor, token))
        {
            PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
            if (key.RawSpan.SequenceEqual("Resources"u8)) { value = resources; hasResources = true; }
            else if (key.RawSpan.SequenceEqual("Contents"u8)) { value = contents; hasContents = true; }
            entries.Assign(graph.Memory, governor, key, value, token);
        }
        if (!hasResources) { entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Resources"u8), resources, token); }
        if (!hasContents) { entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Contents"u8), contents, token); }
        PdfDictionary result = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Memory.TransferTo(ownership, graph.Bytes); return result;
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
        long angle = rotate % 360L;
        if (invert)
        {
            if (scale == 0.0)
            {
                return IdentityArray();
            }

            scale = 1.0 / scale;
            angle = -angle;
        }

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

    private static PdfArray NumberArray(double[] values, ResourceGovernor governor, ParserMemoryScope ownership)
    {
        ownership.Reserve(checked(56L + IntPtr.Size * (long)values.Length));
        QpdfObject[] items = new QpdfObject[values.Length];
        Span<char> text = stackalloc char[384];
        for (int i = 0; i < values.Length; i++)
        {
            governor.CheckCancellation();
            int length = WriterNumber.FormatGeometry(values[i], text);
            ownership.Reserve(32L + GovernedText.StringBytes(length));
            items[i] = new PdfReal(new string(text[..length]), values[i]);
        }
        return PdfArray.FromOwnedItems(items);
    }

    private static double[]? ReadPageBox(PdfDictionary page, string key, IIndirectResolver resolver)
    {
        if (!page.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        double[]? rectangle = ReadRectangle(raw, resolver);
        if (rectangle is null) { return null; }
        if (rectangle[0] > rectangle[2]) { (rectangle[0], rectangle[2]) = (rectangle[2], rectangle[0]); }
        if (rectangle[1] > rectangle[3]) { (rectangle[1], rectangle[3]) = (rectangle[3], rectangle[1]); }
        return rectangle;
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
        IReadOnlyList<OverlayPlacement> underlays,
        IReadOnlyList<OverlayPlacement> overlays,
        Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache,
        List<AcroFormCopy.FieldImport> imports,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope planning,
        ParserMemoryScope ownership,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        limits.Validate();
        CopyGroupAnnotations(copier, pageId, placeBox, destTransform, underlays, formCache, imports, compress, level, limits, governor, planning, ownership, diagnostics, cancellationToken, ref allocator);
        CopyGroupAnnotations(copier, pageId, placeBox, destTransform, overlays, formCache, imports, compress, level, limits, governor, planning, ownership, diagnostics, cancellationToken, ref allocator);
    }

    private static void CopyGroupAnnotations(
        GraphCopier copier,
        int pageId,
        double[] placeBox,
        double[] destTransform,
        IReadOnlyList<OverlayPlacement> placements,
        Dictionary<(GraphCopier Copier, int Spec, int Page), OverlayForm> formCache,
        List<AcroFormCopy.FieldImport> imports,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope planning,
        ParserMemoryScope ownership,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        foreach (OverlayPlacement placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            OverlayForm? form = GetOrBuildOverlayForm(placement, formCache, compress, level, limits, governor, planning, ownership, cancellationToken, ref allocator);
            if (form is null) { continue; }
            double[] cm = PlacementMatrix(form.Box, form.FormMatrix, placeBox, destTransform, true, false);
            CopyOverlayPageAnnots(copier, pageId, placement.Spec.Source, placement.FromPage, cm, imports, limits, governor, ownership, diagnostics, cancellationToken, ref allocator);
        }
    }

    internal static void CopyOverlayPageAnnots(
        GraphCopier destCopier,
        int destPageId,
        OverlaySource source,
        int fromPage,
        double[] cm,
        List<AcroFormCopy.FieldImport> imports,
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

        if (annots.Count == 0) { return; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        Dictionary<int, PdfReference> memo = ParserContainers.CreateDictionary<int, PdfReference>(scratch);
        int nullId = -1;
        List<PdfReference> fresh = ParserContainers.CreateList<PdfReference>(scratch);
        AcroFormCopy.OverlaySourceForm? sourceForm = source.FormInfo;
        PdfReference? placementDr = null;
        bool needsPageResources = false;
        int annotIndex = 0;
        for (int index = 0; index < annots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            governor.NoteOperation(); QpdfObject item = annots[index];
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

            needsPageResources |= AppearanceResources.NeedsPageResources(annot, resolver, governor, cancellationToken);
            if (IsWidgetAnnotation(annot, resolver))
            {
                if (TryImportOverlayWidget(destCopier, engine, annot, item, fromPage, annotIndex, source, sourceForm, memo, scratch, imports, ref placementDr, limits, governor, ownership, diagnostics, cancellationToken, ref allocator, ref nullId, fresh))
                {
                    annotIndex += 1;
                    continue;
                }
            }

            annotIndex += 1;
            // Root and nested references share one placement memo, including
            // Popup/Parent and reply links between ordinary annotations.
            QpdfObject built = CopyOverlayNode(item, engine, source.Copier, destCopier, memo, scratch, ref allocator, ref nullId,
                limits, governor, ownership, cancellationToken, 0);
            PdfReference clone;
            if (built is PdfReference mapped) { clone = mapped; }
            else
            {
                int id = allocator; allocator = checked(allocator + 1);
                ownership.Reserve(32L); clone = destCopier.AddIndirectObjectWithId(built, id);
            }
            ParserContainers.AddItem(scratch, fresh, clone, IntPtr.Size);
        }

        if (fresh.Count == 0) { return; }

        QpdfObject? appearanceResources = null;
        if (needsPageResources && PageInheritance.Find(engine, location, PdfName.FromAscii("Resources"), limits, governor, cancellationToken) is QpdfObject sourceResources
            && CatalogCopy.ResolveDictionary(sourceResources, resolver, governor, cancellationToken) is not null)
        {
            appearanceResources = CopyOverlayNode(sourceResources, engine, source.Copier, destCopier, memo, scratch,
                ref allocator, ref nullId, limits, governor, ownership, cancellationToken, 0);
        }

        // Geometry changes each annotation once. Appearance roles get fresh headers;
        // independently retained aliases keep the original stream and containers.
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        GovernedIntSet transformed = GovernedIntSet.Create(traversal, governor, cancellationToken);
        Dictionary<(int Id, bool States), PdfReference> appearances = ParserContainers.CreateDictionary<(int, bool), PdfReference>(traversal);
        using OwnedAllocationBatch appearanceGraph = OwnedAllocationBatch.Create(governor, cancellationToken);
        foreach (PdfReference annotation in fresh)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!transformed.Add(traversal, annotation.ObjectNumber)) { continue; }
            TransformOverlayRect(destCopier, annotation.ObjectNumber, cm, governor, ownership, cancellationToken);
            CopyOverlayAppearances(destCopier, annotation.ObjectNumber, cm, appearanceResources, appearances, traversal, appearanceGraph, diagnostics, ref allocator);
        }
        appearanceGraph.Memory.TransferTo(ownership, appearanceGraph.Bytes);

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

        long count = (long)(current?.Count ?? 0) + fresh.Count;
        if (count > limits.MaxContainerSize || count > Array.MaxLength)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), count, Math.Min(limits.MaxContainerSize, Array.MaxLength)); }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
        long arrayBytes = checked(32L + IntPtr.Size * count);
        graph.Memory.Reserve(arrayBytes); QpdfObject[] items = new QpdfObject[(int)count]; graph.Add(arrayBytes);
        int at = 0;
        if (current is not null)
        {
            for (; at < current.Count; at++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); items[at] = current[at];
            }
        }
        foreach (PdfReference clone in fresh)
        { cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); items[at++] = clone; }
        graph.Memory.Reserve(24L); PdfArray rebuilt = PdfArray.FromOwnedItems(items); graph.Add(24L);
        int targetId = currentId >= 0 ? currentId : destPageId;
        QpdfObject replacement = currentId >= 0 ? rebuilt : PdfDictionaryRebuild.ReplaceEntry(graph, page, "Annots"u8, rebuilt);
        destCopier.ValidateReplacement(targetId);
        graph.Memory.TransferTo(ownership, graph.Bytes); destCopier.ReplaceCopiedObject(targetId, replacement);
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
        QpdfObject item,
        int fromPage,
        int annotIndex,
        OverlaySource source,
        AcroFormCopy.OverlaySourceForm? sourceForm,
        Dictionary<int, PdfReference> memo,
        ParserMemoryScope scratch,
        List<AcroFormCopy.FieldImport> imports,
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

        if (item is not PdfReference annotationReference)
        {
            if (source.ImportNotes.Add("d:" + fromPage.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + annotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "Ignoring an overlay widget annotation that the source form does not reference; cloning it without a field.");
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "Ignoring an overlay form field that is not indirect; cloning its annotation without a field.");
            }

            return false;
        }

        AcroFormCopy.OverlayTopMatch? top = AcroFormCopy.FindOverlayTopField(annot, annotationReference, true, sourceEngine, governor, cancellationToken);
        if (top is null)
        {
            return false;
        }

        if (!sourceForm.ReachedObjects.Contains(annotationReference.ObjectNumber))
        {
            if (source.ImportNotes.Add("r:" + annotationReference.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)))
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

        PdfReference topReference = top.Reference ?? annotationReference;
        source.Copier.SignaturePolicy.ValidateField(topReference);
        if (!memo.ContainsKey(topReference.ObjectNumber))
        {
            QpdfObject fieldCopy = CopyOverlayNode(topReference, sourceEngine, source.Copier, destCopier, memo, scratch, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, 0);
            if (fieldCopy is not PdfReference fieldRef)
            {
                return false;
            }

            placementDr ??= CopyOverlayImportDR(sourceForm, sourceEngine, source.Copier, destCopier, memo, scratch, limits, governor, ownership, cancellationToken, ref allocator, ref nullId);
            Dictionary<PdfName, QpdfObject> extras = new();
            CopiedForm part = new([fieldRef], true, sourceForm.NeedAppearances, sourceForm.NeedAppearances, placementDr, false, [], extras);
            ownership.Reserve(56L);
            ParserContainers.AddItem(ownership, imports,
                new AcroFormCopy.FieldImport(destCopier, part, sourceForm.DefaultAppearance, sourceForm.Quadding, true), IntPtr.Size);
        }

        QpdfObject cloned = CopyOverlayNode(item, sourceEngine, source.Copier, destCopier, memo, scratch, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, 0);
        if (cloned is not PdfReference cloneRef)
        {
            return false;
        }

        ParserContainers.AddItem(scratch, fresh, cloneRef, IntPtr.Size);
        return true;
    }

    private static PdfReference? CopyOverlayImportDR(
        AcroFormCopy.OverlaySourceForm sourceForm,
        DocumentEngine sourceEngine,
        GraphCopier sourceCopier,
        GraphCopier destCopier,
        Dictionary<int, PdfReference> memo,
        ParserMemoryScope scratch,
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

        QpdfObject copied = CopyOverlayNode(sourceForm.DefaultResources, sourceEngine, sourceCopier, destCopier, memo, scratch, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, 0);
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

        return target is PdfName name && name.RawSpan.SequenceEqual("Widget"u8);
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

        return name.RawSpan.SequenceEqual("Page"u8) || name.RawSpan.SequenceEqual("Pages"u8) || name.RawSpan.SequenceEqual("Catalog"u8);
    }

    private static QpdfObject CopyOverlayNode(
        QpdfObject node,
        DocumentEngine sourceEngine,
        GraphCopier sourceCopier,
        GraphCopier destCopier,
        Dictionary<int, PdfReference> memo,
        ParserMemoryScope scratch,
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
            throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, limits.MaxNestingDepth);
        }

        if (node is PdfReference reference)
        {
            IIndirectResolver resolver = (IIndirectResolver)sourceEngine;
            if (!PdfObjectLookup.TryResolve(reference, resolver, governor, cancellationToken, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
            {
                return PdfNull.Instance;
            }

            // A number-only memo is safe after the source generation has been validated.
            if (memo.TryGetValue(reference.ObjectNumber, out PdfReference? mapped)) { return mapped; }

            if (resolved is PdfDictionary dictionary)
            {
                string? kind = OverlayNodeKind(dictionary, resolver);
                if (kind == "Page")
                {
                    return OverlayNullRef(destCopier, ref allocator, ref nullId, ownership);
                }

                if (kind == "Pages" || kind == "Catalog")
                {
                    return PdfNull.Instance;
                }
            }

            int id = allocator;
            checked { allocator += 1; }
            ownership.Reserve(32L); PdfReference copiedReference = new(id, 0);
            ParserContainers.EnsureEntryCapacity(scratch, memo, IntPtr.Size); memo.Add(reference.ObjectNumber, copiedReference);
            QpdfObject built = BuildOverlayContainer(resolved, sourceEngine, sourceCopier, destCopier, memo, scratch, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth + 1);
            destCopier.AddIndirectObjectWithId(built, id);
            return copiedReference;
        }

        if (node is PdfDictionary || node is PdfArray || node is PdfStream)
        {
            return BuildOverlayContainer(node, sourceEngine, sourceCopier, destCopier, memo, scratch, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth + 1);
        }

        return node;
    }

    internal static QpdfObject BuildOverlayContainer(
        QpdfObject node,
        DocumentEngine sourceEngine,
        GraphCopier sourceCopier,
        GraphCopier destCopier,
        Dictionary<int, PdfReference> memo,
        ParserMemoryScope scratch,
        ref int allocator,
        ref int nullId,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken,
        int depth)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (node is PdfDictionary dictionary)
        {
            sourceCopier.SignaturePolicy.RejectDictionary(dictionary);
            PdfNameTable<QpdfObject> entries = PdfNameTable<QpdfObject>.Create(ownership, governor, IntPtr.Size, cancellationToken);
            using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
            while (source.MoveNext(governor, cancellationToken))
            {
                PdfName key = source.Current.Key; QpdfObject found = source.Current.Value;
                if (found is PdfNull)
                {
                    continue;
                }

                QpdfObject child = CopyOverlayNode(found, sourceEngine, sourceCopier, destCopier, memo, scratch, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth);
                if (child is PdfNull)
                {
                    continue;
                }

                if (child is PdfReference childReference && nullId >= 0 && childReference.ObjectNumber == nullId)
                {
                    continue;
                }

                entries.Assign(ownership, governor, key, child, cancellationToken);
            }

            entries.Freeze(ownership, governor, cancellationToken);
            ownership.Reserve(24L);
            return PdfDictionary.FromOwnedEntries(entries);
        }

        if (node is PdfArray array)
        {
            List<QpdfObject> items = ParserContainers.CreateList<QpdfObject>(ownership);
            for (int i = 0; i < array.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QpdfObject copied = CopyOverlayNode(array[i], sourceEngine, sourceCopier, destCopier, memo, scratch, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth);
                ParserContainers.AddItem(ownership, items, copied, IntPtr.Size);
            }

            return ParserContainers.CopyArray(ownership, items);
        }

        if (node is PdfStream stream)
        {
            QpdfObject rebuiltDictionary = BuildOverlayContainer(stream.Dictionary, sourceEngine, sourceCopier, destCopier, memo, scratch, ref allocator, ref nullId, limits, governor, ownership, cancellationToken, depth);
            PdfDictionary dictionaryValue = rebuiltDictionary as PdfDictionary ?? stream.Dictionary;
            // Exclusive values and backing input transfer before the overlay
            // document closes (ApplyAsync's finally).
            ownership.Reserve(32L);
            return stream.WithDictionary(dictionaryValue);
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

        if (name.RawSpan.SequenceEqual("Page"u8)) { return "Page"; }
        if (name.RawSpan.SequenceEqual("Pages"u8)) { return "Pages"; }
        if (name.RawSpan.SequenceEqual("Catalog"u8)) { return "Catalog"; }

        return null;
    }

    private static PdfReference OverlayNullRef(GraphCopier destCopier, ref int allocator, ref int nullId, ParserMemoryScope ownership)
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

    internal static void TransformOverlayRect(GraphCopier destCopier, int annotId, double[] cm,
        ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation(); ownership.CheckOwner(governor);
        if (!destCopier.CopiedObjects.TryGetValue(annotId, out QpdfObject? raw) || raw is not PdfDictionary annot) { return; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(512L); // Fixed resolver, coordinate arrays and transform state.
        PoolResolver pool = new(destCopier.CopiedObjects);
        double[] rect = ReadOverlayNumbers(annot, "Rect", 4, pool) ?? new double[4];
        (rect[0], rect[2]) = (Math.Min(rect[0], rect[2]), Math.Max(rect[0], rect[2]));
        (rect[1], rect[3]) = (Math.Min(rect[1], rect[3]), Math.Max(rect[1], rect[3]));
        double[] moved = FromArray(cm).TransformRect(rect);
        if (!IsFiniteRect(moved)) { moved = new double[4]; }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        long before = graph.Memory.ReservedBytes;
        PdfArray values = NumberArray(moved, governor, graph.Memory); graph.Add(graph.Memory.ReservedBytes - before);
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, annot, "Rect"u8, values);
        destCopier.ValidateReplacement(annotId); graph.Memory.TransferTo(ownership, graph.Bytes);
        destCopier.ReplaceCopiedObject(annotId, result);
    }

    private static void CopyOverlayAppearances(GraphCopier copier, int annotId, double[] cm,
        QpdfObject? pageResources, Dictionary<(int Id, bool States), PdfReference> copies,
        ParserMemoryScope traversal, OwnedAllocationBatch graph, DiagnosticBag diagnostics, ref int allocator)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        if (!copier.CopiedObjects.TryGetValue(annotId, out QpdfObject? raw) || raw is not PdfDictionary annot
            || !PdfDictionaryLookup.TryGet(annot, "AP"u8, governor, token, out QpdfObject apRaw)) { return; }
        QpdfObject replacement = CopyOverlayAppearanceDictionary(copier, apRaw, false, cm, pageResources,
            copies, traversal, graph, diagnostics, ref allocator);
        if (Equals(apRaw, replacement)) { return; }
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, annot, "AP"u8, replacement);
        copier.ValidateReplacement(annotId); copier.ReplaceCopiedObject(annotId, result);
    }

    private static QpdfObject CopyOverlayAppearanceDictionary(GraphCopier copier, QpdfObject raw, bool states,
        double[] cm, QpdfObject? pageResources, Dictionary<(int Id, bool States), PdfReference> copies,
        ParserMemoryScope traversal, OwnedAllocationBatch graph, DiagnosticBag diagnostics, ref int allocator)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        PoolResolver pool = new(copier.CopiedObjects);
        if (CatalogCopy.ResolveValue(raw, pool, governor, token) is not PdfDictionary dictionary) { return raw; }
        PdfReference? reference = raw as PdfReference;
        if (reference is not null && copies.TryGetValue((reference.ObjectNumber, states), out PdfReference? cached)) { return cached; }
        PdfNameTable<QpdfObject>? entries = null; long allocation = 0L;
        using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
        while (source.MoveNext(governor, token))
        {
            PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
            ReadOnlySpan<byte> slot = key.RawSpan;
            if (!states && !slot.SequenceEqual("N"u8) && !slot.SequenceEqual("R"u8) && !slot.SequenceEqual("D"u8)) { continue; }
            QpdfObject replacement = value;
            QpdfObject? target = CatalogCopy.ResolveValue(value, pool, governor, token);
            if (value is PdfReference streamReference && target is PdfStream)
            {
                if (!copies.TryGetValue((streamReference.ObjectNumber, false), out PdfReference? placed))
                {
                    placed = CopyOverlayAppearanceStream(copier, streamReference, cm, pageResources, graph, diagnostics, ref allocator);
                    ParserContainers.EnsureEntryCapacity(traversal, copies, 2L * sizeof(int) + IntPtr.Size);
                    copies.Add((streamReference.ObjectNumber, false), placed);
                }
                replacement = placed;
            }
            else if (!states && target is PdfDictionary)
            {
                replacement = CopyOverlayAppearanceDictionary(copier, value, true, cm, pageResources,
                    copies, traversal, graph, diagnostics, ref allocator);
            }
            if (Equals(value, replacement)) { continue; }
            if (entries is null)
            {
                entries = PdfDictionaryRebuild.CreateEntries(graph, out allocation);
                using PdfDictionary.EntryEnumerator original = dictionary.GetEntryEnumerator();
                while (original.MoveNext(governor, token))
                { entries.Assign(graph.Memory, governor, original.Current.Key, original.Current.Value, token); }
            }
            entries.Assign(graph.Memory, governor, key, replacement, token);
        }
        QpdfObject result = raw;
        if (entries is not null)
        {
            PdfDictionary rebuilt = PdfDictionaryRebuild.Complete(graph, entries, allocation);
            if (reference is null) { return rebuilt; }
            result = copier.AddIndirectObjectWithId(rebuilt, allocator); allocator = checked(allocator + 1);
        }
        if (reference is not null)
        {
            ParserContainers.EnsureEntryCapacity(traversal, copies, 2L * sizeof(int) + IntPtr.Size);
            copies.Add((reference.ObjectNumber, states), (PdfReference)result);
        }
        return result;
    }

    internal static PdfReference CopyOverlayAppearanceStream(GraphCopier copier, PdfReference reference,
        double[] cm, QpdfObject? pageResources, OwnedAllocationBatch graph, DiagnosticBag diagnostics, ref int allocator)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (!copier.CopiedObjects.TryGetValue(reference.ObjectNumber, out QpdfObject? raw) || raw is not PdfStream stream) { return reference; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor); scratch.Reserve(256L);
        PoolResolver pool = new(copier.CopiedObjects);
        PdfDictionaryLookup.TryGet(stream.Dictionary, "Matrix"u8, governor, token, out QpdfObject matrixRaw);
        QpdfObject matrixValue = CatalogCopy.ResolveValue(matrixRaw, pool, governor, token) ?? matrixRaw;
        bool present = matrixValue is not PdfNull;
        double[]? next = null;
        if (present && matrixValue is not PdfArray)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning,
                "Kept a non-array appearance Matrix unchanged during placement.");
        }
        else
        {
            // Malformed arrays retain the existing native-compatible zero fallback.
            double[] matrix = matrixValue is PdfArray array ? ReadOverlayMatrix(array, pool) ?? new double[6]
                : [1, 0, 0, 1, 0, 0];
            ContentMatrix work = FromArray(matrix); work.Concat(FromArray(cm));
            next = work.ToArray();
        }
        bool bindResources = pageResources is not null && AppearanceResources.UsesPageResources(stream, pool, governor, token);
        PdfArray? values = null;
        if (next is not null && (present || !IsIdentityMatrix(next)))
        {
            long before = graph.Memory.ReservedBytes;
            values = NumberArray(next, governor, graph.Memory); graph.Add(graph.Memory.ReservedBytes - before);
        }
        if (!bindResources && values is null) { return reference; }
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        bool sawResources = false, sawMatrix = false;
        using PdfDictionary.EntryEnumerator source = stream.Dictionary.GetEntryEnumerator();
        while (source.MoveNext(governor, token))
        {
            PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
            if (bindResources && pageResources is not null && PdfDictionaryLookup.NameEquals(key, "Resources"u8, governor, token))
            { value = pageResources; sawResources = true; }
            if (values is not null && PdfDictionaryLookup.NameEquals(key, "Matrix"u8, governor, token))
            { value = values; sawMatrix = true; }
            entries.Assign(graph.Memory, governor, key, value, token);
        }
        if (bindResources && pageResources is not null && !sawResources)
        { entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Resources"u8), pageResources, token); }
        if (values is not null && !sawMatrix)
        { entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Matrix"u8), values, token); }
        PdfDictionary header = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Memory.Reserve(32L); PdfStream result = stream.WithDictionary(header); graph.Add(32L);
        PdfReference placedReference = copier.AddIndirectObjectWithId(result, allocator); allocator = checked(allocator + 1);
        return placedReference;
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

    private static double[]? ReadOverlayMatrix(PdfArray array, PoolResolver pool)
    {
        if (array.Count != 6)
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

}

