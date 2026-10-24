using System.Globalization;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Writing;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Json;

/// <summary>Validated JSON inspection request: format version plus key filter.</summary>
internal sealed record JsonRequest(
    int Version,
    IReadOnlyList<string> Keys,
    string DecodeLevel,
    string StreamData,
    string? StreamPrefix,
    HashSet<(bool Trailer, int Number, int Generation)>? Objects,
    bool MinimalOutput)
{
    internal bool Wants(string key)
    {
        return Keys.Count == 0 || Keys.Contains(key);
    }

    internal bool WantsObject(int number, int generation)
        => Objects is null || Objects.Contains((false, number, generation));

    internal bool WantsTrailer => Objects is null || Objects.Contains((true, 0, 0));
}

/// <summary>
/// Document to JSON inspection driver. Emits the upstream top-level layout
/// (version, parameters, then pages, pagelabels, attachments, encrypt,
/// outlines, objects or qpdf, objectinfo) with stream data omitted.
/// </summary>
internal static class DocumentJson
{
    internal static DisplayBuffer WriteDocument(
        QpdfDocument document,
        JsonRequest request,
        PreparedJsonOutput output,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        DocumentEngine engine = document.Engine;
        DisplayBuffer builder = DisplayBuffer.CreateJson(governor);
        try
        {
            builder.Append((char)123);
            bool firstKey = request.MinimalOutput;
            if (!request.MinimalOutput)
            {
                AppendVersionAndParameters(builder, request);
            }

            using ParserMemoryScope inspectionMemory = ParserMemoryScope.Create(governor);
            bool touchesPages = request.Wants("pages") || request.Wants("pagelabels") || request.Wants("outlines") || request.Wants("acroform");
            SortedDictionary<int, QpdfObject> labels = new();
            if (request.Wants("pages") || request.Wants("pagelabels"))
            {
                labels = ReadLabelTree(engine, limits, governor, inspectionMemory, cancellationToken);
            }

            List<OutlineEntry> outlines = new();
            if (request.Wants("outlines") || request.Wants("pages"))
            {
                outlines = ReadOutlines(document, limits, governor, inspectionMemory, cancellationToken);
            }

            if (request.Wants("pages"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendKey(builder, ref firstKey, "pages");
                AppendPages(builder, document, outlines, request, labels, limits, governor, inspectionMemory, cancellationToken);
            }

            if (request.Wants("pagelabels"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendKey(builder, ref firstKey, "pagelabels");
                AppendPageLabels(builder, engine, request, labels, governor, limits);
            }

            if (request.Wants("attachments"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendKey(builder, ref firstKey, "attachments");
                AppendAttachments(builder, engine, limits, governor, diagnostics, inspectionMemory, cancellationToken);
            }

            if (request.Wants("encrypt"))
            {
                AppendKey(builder, ref firstKey, "encrypt");
                AppendEncrypt(builder, request.Version);
            }

            if (request.Wants("outlines"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendKey(builder, ref firstKey, "outlines");
                AppendOutlines(builder, outlines, request, governor, limits);
            }

            if (request.Wants("acroform"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendKey(builder, ref firstKey, "acroform");
                AppendAcroform(builder, document, request, limits, governor, cancellationToken);
            }

            if (request.Wants("objects") || request.Wants("qpdf"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendKey(builder, ref firstKey, request.Version == 1 ? "objects" : "qpdf");
                AppendObjects(builder, document, request, touchesPages, output, limits, governor, diagnostics, cancellationToken);
            }

            if (request.Version == 1 && request.Wants("objectinfo"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendKey(builder, ref firstKey, "objectinfo");
                AppendObjectInfo(builder, document, request, output, limits, governor, diagnostics, cancellationToken);
            }

            builder.Append((char)125);
            builder.Append((char)10);
            return builder;
        }
        catch { builder.Dispose(); throw; }
    }
    private static void AppendKey(DisplayBuffer builder, ref bool firstKey, string key)
    {
        if (!firstKey)
        {
            builder.Append((char)44);
        }

        firstKey = false;
        PdfJsonWriter.AppendJsonString(builder, key);
        builder.Append(": ");
    }

    private static void AppendVersionAndParameters(DisplayBuffer builder, JsonRequest request)
    {
        builder.Append("\"version\": ");
        builder.AppendInteger(request.Version);
        builder.Append(",\"parameters\": {\"decodelevel\": ");
        PdfJsonWriter.AppendJsonString(builder, request.DecodeLevel);
        builder.Append((char)125);
    }

    private static GovernedIntDictionary<int> BuildPageIndex(QpdfDocument document,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        GovernedIntDictionary<int> index = GovernedIntDictionary<int>.Create(memory, governor, sizeof(int), token);
        IReadOnlyList<PageLocation> pages = document.PageReferences;
        for (int i = 0; i < pages.Count; i++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (pages[i].Source is PdfReference source) { index.TryAdd(memory, source.ObjectNumber, i + 1); }
        }
        return index;
    }

    private static PdfDictionary? ResolveChild(
        DocumentEngine engine,
        PdfDictionary parent,
        string key,
        string role)
    {
        if (!parent.TryGet(PdfName.FromAscii(key), out QpdfObject raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        QpdfObject target = raw;
        if (raw is PdfReference reference)
        {
            if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
            {
                throw new QpdfParseException("Unresolvable JSON inspection " + role + " reference.", null, reference.ObjectNumber);
            }

            target = resolved;
        }

        if (target is not PdfDictionary dictionary)
        {
            throw new QpdfParseException("JSON inspection " + role + " must be a dictionary.", null, null);
        }

        return dictionary;
    }
    internal static SortedDictionary<int, QpdfObject> ReadLabelTree(
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor); cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        memory.Reserve(96L); SortedDictionary<int, QpdfObject> labels = new();
        PdfDictionary? catalog = ResolveChild(engine, engine.Trailer, "Root", "catalog");
        if (catalog is null)
        {
            return labels;
        }

        PdfDictionary? tree = ResolveChild(engine, catalog, "PageLabels", "page labels");
        if (tree is null)
        {
            return labels;
        }

        WalkLabelNode(engine, tree, 0, GovernedIntSet.Create(memory, governor, cancellationToken), labels, limits, governor, memory, cancellationToken);
        return labels;
    }

    private static void WalkLabelNode(
        DocumentEngine engine,
        PdfDictionary node,
        int depth,
        GovernedIntSet visited,
        SortedDictionary<int, QpdfObject> labels,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException("Page-label number tree exceeds nesting budget.", null, null);
        }

        governor.NoteOperation();
        if (node.TryGet(PdfName.FromAscii("Nums"), out QpdfObject? numsRaw) && numsRaw is not null && numsRaw is not PdfNull)
        {
            if (numsRaw is PdfReference numbersReference)
            { PdfObjectLookup.TryResolve(numbersReference, (IIndirectResolver)engine, governor, cancellationToken, out numsRaw); }
            if (numsRaw is not PdfArray nums || nums.Count % 2 != 0)
            {
                throw new QpdfParseException("Page-label number array must hold index/value pairs.", null, null);
            }

            for (int i = 0; i < nums.Count; i += 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                QpdfObject? keyRaw = nums[i];
                if (keyRaw is PdfReference keyReference)
                { PdfObjectLookup.TryResolve(keyReference, (IIndirectResolver)engine, governor, cancellationToken, out keyRaw); }
                if (keyRaw is not PdfInteger key)
                {
                    throw new QpdfParseException("Page-label number-tree keys must be integers.", null, null);
                }

                if (key.Value < 0L || key.Value > int.MaxValue)
                {
                    throw new QpdfParseException("Page-label index is out of range.", null, null);
                }

                int index = (int)key.Value;
                if (!labels.ContainsKey(index)) { memory.Reserve(64L); }
                labels[index] = nums[i + 1];
            }
        }

        if (node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) && kidsRaw is not null && kidsRaw is not PdfNull)
        {
            if (kidsRaw is PdfReference childrenReference)
            { PdfObjectLookup.TryResolve(childrenReference, (IIndirectResolver)engine, governor, cancellationToken, out kidsRaw); }
            if (kidsRaw is not PdfArray kids)
            {
                throw new QpdfParseException("Page-label kids must be an array.", null, null);
            }

            for (int i = 0; i < kids.Count; i++)
            {
                QpdfObject item = kids[i];
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                int kidNumber = item is PdfReference kidReference ? kidReference.ObjectNumber : -1;
                QpdfObject target = item;
                if (item is PdfReference reference)
                {
                    if (!PdfObjectLookup.TryResolve(reference, (IIndirectResolver)engine, governor, cancellationToken, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
                    {
                        throw new QpdfParseException("Unresolvable page-label kid reference.", null, reference.ObjectNumber);
                    }

                    target = resolved;
                }

                if (target is not PdfDictionary kid)
                {
                    throw new QpdfParseException("Page-label kid must be a dictionary.", null, null);
                }

                bool track = kidNumber >= 0;
                if (track && !visited.Add(memory, kidNumber))
                {
                    throw new QpdfParseException("Page-label number tree contains a cycle.", null, null);
                }

                try
                {
                    WalkLabelNode(engine, kid, depth + 1, visited, labels, limits, governor, memory, cancellationToken);
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

    private static void AppendPageLabels(
        DisplayBuffer builder,
        DocumentEngine engine,
        JsonRequest request,
        SortedDictionary<int, QpdfObject> labels,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        builder.Append((char)91);
        bool first = true;
        foreach (KeyValuePair<int, QpdfObject> entry in labels)
        {
            governor.NoteOperation();
            if (!first)
            {
                builder.Append((char)44);
            }

            first = false;
            builder.Append("{\"index\": ");
            builder.AppendInteger(entry.Key);
            builder.Append(",\"label\": ");
            AppendLabelValue(builder, engine, entry.Value, request, governor, limits);
            builder.Append((char)125);
        }

        builder.Append((char)93);
    }

    private static void AppendLabelValue(DisplayBuffer builder, DocumentEngine engine, QpdfObject value,
        JsonRequest request, ResourceGovernor governor, QpdfLimits limits)
    {
        if (value is PdfReference reference
            && PdfObjectLookup.TryResolve(reference, (IIndirectResolver)engine, governor, CancellationToken.None, out QpdfObject? resolved)
            && resolved is not null) { value = resolved; }
        if (value is PdfDictionary dictionary)
        { PdfJsonWriter.AppendLabelDictionary(builder, dictionary, request.Version, governor, limits, (IIndirectResolver)engine); }
        else { PdfJsonWriter.AppendValue(builder, value, request.Version, governor, 0, limits); }
    }

    private sealed record OutlineEntry(
        PdfReference? NodeReference,
        PdfDictionary Node,
        string Title,
        QpdfObject? Destination,
        bool Open,
        int? DestinationPage,
        List<OutlineEntry> Children);

    private static List<OutlineEntry> ReadOutlines(QpdfDocument document, QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        DocumentEngine engine = document.Engine;
        List<OutlineEntry> roots = ParserContainers.CreateList<OutlineEntry>(memory);
        PdfDictionary? catalog = ResolveChild(engine, engine.Trailer, "Root", "catalog");
        PdfDictionary? tree = catalog is null ? null : ResolveChild(engine, catalog, "Outlines", "outlines");
        if (tree is null || !tree.TryGet(PdfName.FromAscii("First"), out QpdfObject? firstRaw)
            || firstRaw is null || firstRaw is PdfNull) { return roots; }

        GovernedIntDictionary<int> pageIndex = BuildPageIndex(document, memory, governor, cancellationToken);
        GovernedIntSet visited = GovernedIntSet.Create(memory, governor, cancellationToken);
        ReadOutlineSiblings(engine, pageIndex, firstRaw, roots, 0, visited, limits, governor, memory, cancellationToken);
        return roots;
    }

    private static void ReadOutlineSiblings(DocumentEngine engine, GovernedIntDictionary<int> pageIndex,
        QpdfObject sibling, List<OutlineEntry> entries, int depth, GovernedIntSet visited,
        QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        while (sibling is not PdfNull)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            PdfReference? reference = sibling as PdfReference;
            QpdfObject? node = reference is null ? sibling : TryResolve(engine, reference);
            if (node is not PdfDictionary outline)
            { throw new QpdfParseException("Outline entry must resolve to a dictionary.", null, reference?.ObjectNumber); }
            if (reference is not null && !visited.Add(memory, reference.ObjectNumber))
            { throw new QpdfParseException("Outline tree contains a cycle.", null, reference.ObjectNumber); }

            OutlineEntry entry = ReadOutlineEntry(engine, pageIndex, reference, outline, depth, visited,
                limits, governor, memory, token);
            ParserContainers.AddItem(memory, entries, entry, IntPtr.Size);
            if (!PdfDictionaryLookup.TryGet(outline, "Next"u8, governor, token, out QpdfObject next)) { break; }
            sibling = next;
        }
        // Keep siblings on the active path until the chain ends, just like ancestors.
        foreach (OutlineEntry entry in entries)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (entry.NodeReference is PdfReference reference) { visited.Remove(reference.ObjectNumber); }
        }
    }

    private static OutlineEntry ReadOutlineEntry(
        DocumentEngine engine,
        GovernedIntDictionary<int> pageIndex,
        PdfReference? nodeReference,
        PdfDictionary outline,
        int depth,
        GovernedIntSet visited,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException("Outline tree exceeds nesting budget.", null, null);
        }

        governor.NoteOperation();
        string title = string.Empty;
        if (outline.TryGet(PdfName.FromAscii("Title"), out QpdfObject? titleRaw) && titleRaw is PdfByteString titleText)
        {
            title = PdfText.Decode(titleText.ValueMemory, memory, governor, cancellationToken);
        }

        QpdfObject? destination = null;
        if (outline.TryGet(PdfName.FromAscii("Dest"), out QpdfObject? destRaw) && destRaw is not null && destRaw is not PdfNull)
        {
            destination = destRaw;
            if (destRaw is PdfReference destReference)
            {
                if (!((IIndirectResolver)engine).TryResolve(destReference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
                {
                    destination = null;
                }
                else
                {
                    destination = resolved;
                }
            }
        }

        bool open = true;
        if (outline.TryGet(PdfName.FromAscii("Count"), out QpdfObject? countRaw) && countRaw is PdfInteger count && count.Value < 0L)
        {
            open = false;
        }

        int? destinationPage = null;
        if (destination is PdfArray destArray && destArray.Count > 0 && destArray[0] is PdfReference pageReference)
        {
            if (pageIndex.TryGetValue(pageReference.ObjectNumber, out int position))
            {
                destinationPage = position;
            }
        }

        List<OutlineEntry> children = ParserContainers.CreateList<OutlineEntry>(memory);
        if (outline.TryGet(PdfName.FromAscii("First"), out QpdfObject? firstRaw) && firstRaw is not null && firstRaw is not PdfNull)
        {
            ReadOutlineSiblings(engine, pageIndex, firstRaw, children, depth + 1, visited, limits, governor, memory, cancellationToken);
        }

        memory.Reserve(96L);
        return new OutlineEntry(nodeReference, outline, title, destination, open, destinationPage, children);
    }

    private static void AppendOutlineEntry(
        DisplayBuffer builder,
        OutlineEntry entry,
        bool forPage,
        JsonRequest request,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        builder.Append("{\"object\": ");
        if (entry.NodeReference is PdfReference nodeReference)
        {
            builder.Append((char)34);
            PdfJsonWriter.AppendReference(builder, nodeReference.ObjectNumber, nodeReference.Generation);
            builder.Append((char)34);
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, entry.Node, request.Version, governor, 0, limits);
        }

        builder.Append(",\"title\": ");
        PdfJsonWriter.AppendJsonString(builder, entry.Title);
        builder.Append(",\"dest\": ");
        if (entry.Destination is null)
        {
            builder.Append("null");
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, entry.Destination, request.Version, governor, 0, limits);
        }

        if (!forPage)
        {
            builder.Append(",\"open\": ");
            builder.Append(entry.Open ? "true" : "false");
            builder.Append(",\"destpageposfrom1\": ");
            if (entry.DestinationPage.HasValue)
            {
                builder.AppendInteger(entry.DestinationPage.Value);
            }
            else
            {
                builder.Append("null");
            }

            builder.Append(",\"kids\": [");
            for (int i = 0; i < entry.Children.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append((char)44);
                }

                AppendOutlineEntry(builder, entry.Children[i], false, request, governor, limits);
            }

            builder.Append((char)93);
        }

        builder.Append((char)125);
    }

    private static void AppendOutlines(
        DisplayBuffer builder,
        List<OutlineEntry> outlines,
        JsonRequest request,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        builder.Append((char)91);
        for (int i = 0; i < outlines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append((char)44);
            }

            AppendOutlineEntry(builder, outlines[i], false, request, governor, limits);
        }

        builder.Append((char)93);
    }
    private static void AppendPages(
        DisplayBuffer builder,
        QpdfDocument document,
        List<OutlineEntry> outlines,
        JsonRequest request,
        SortedDictionary<int, QpdfObject> labels,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PageLocation> pages = document.PageReferences;
        GovernedIntDictionary<List<OutlineEntry>>? pageOutlines = null;
        if (outlines.Count > 0)
        {
            pageOutlines = GovernedIntDictionary<List<OutlineEntry>>.Create(memory, governor, IntPtr.Size, cancellationToken);
            IndexPageOutlines(outlines, pageOutlines, memory, governor, cancellationToken);
        }
        using SortedDictionary<int, QpdfObject>.Enumerator labelEntries = labels.GetEnumerator();
        bool hasLabel = labelEntries.MoveNext();
        QpdfObject? label = null;
        builder.Append((char)91);
        for (int i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (i > 0)
            {
                builder.Append((char)44);
            }

            PageLocation location = pages[i];
            builder.Append("{\"object\": ");
            if (location.Source is PdfReference source)
            {
                builder.Append((char)34);
                PdfJsonWriter.AppendReference(builder, source.ObjectNumber, source.Generation);
                builder.Append((char)34);
            }
            else
            {
                PdfJsonWriter.AppendValue(builder, location.Page, request.Version, governor, 0, limits);
            }

            builder.Append(",\"images\": ");
            AppendImages(builder, document.Engine, location, request, limits, governor, cancellationToken);
            builder.Append(",\"contents\": ");
            AppendContents(builder, document.Engine, location.Page, request, governor, limits);
            builder.Append(",\"label\": ");
            while (hasLabel && labelEntries.Current.Key <= i)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                label = labelEntries.Current.Value;
                hasLabel = labelEntries.MoveNext();
            }
            if (label is null || label is PdfNull)
            {
                builder.Append("null");
            }
            else
            {
                AppendLabelValue(builder, document.Engine, label, request, governor, limits);
            }

            builder.Append(",\"outlines\": ");
            AppendPageOutlines(builder, pageOutlines, i + 1, request, governor, limits);
            builder.Append(",\"pageposfrom1\": ");
            builder.AppendInteger((i + 1));
            builder.Append((char)125);
        }

        builder.Append((char)93);
    }

    private readonly record struct PageImageDetail(PdfName Name, PdfReference Reference, PdfStream Stream);

    private static void AppendImages(
        DisplayBuffer builder,
        DocumentEngine engine,
        PageLocation location,
        JsonRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        builder.Append((char)91);
        bool first = true;
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        foreach (PageImageDetail detail in ListImages(engine, location, limits, memory, governor, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!first)
            {
                builder.Append((char)44);
            }

            first = false;
            AppendImage(builder, engine, detail, request, limits, governor, cancellationToken);
        }

        builder.Append((char)93);
    }

    private static void AppendImage(
        DisplayBuffer builder,
        DocumentEngine engine,
        PageImageDetail detail,
        JsonRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        using FilterPlan stages = FilterChain.Resolve(detail.Stream.Dictionary, (IIndirectResolver)engine, governor, cancellationToken);
        builder.Append("{\"bitspercomponent\": ");
        AppendOptionalInteger(builder, ImageInteger(detail.Stream.Dictionary, "BitsPerComponent", engine));
        builder.Append(",\"colorspace\": ");
        if (!detail.Stream.Dictionary.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject colorspace) || colorspace is null || colorspace is PdfNull)
        {
            builder.Append("null");
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, colorspace, request.Version, governor, 0, limits);
        }

        builder.Append(",\"decodeparms\": [");
        if (stages.Count == 0)
        {
            builder.Append("null");
        }

        for (int i = 0; i < stages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (i > 0)
            {
                builder.Append((char)44);
            }

            if (stages[i].Parms is PdfDictionary parms)
            {
                PdfJsonWriter.AppendValue(builder, parms, request.Version, governor, 0, limits);
            }
            else
            {
                builder.Append("null");
            }
        }

        builder.Append("],\"filter\": [");
        if (stages.Count == 0)
        {
            builder.Append("null");
        }

        for (int i = 0; i < stages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (i > 0)
            {
                builder.Append((char)44);
            }

            builder.Append('"'); builder.Append('/'); PdfJsonWriter.AppendEscaped(builder, stages[i].Name); builder.Append('"');
        }

        builder.Append("],\"filterable\": ");
        builder.Append(FilterChain.AllStagesInLevel(stages, request.DecodeLevel) ? "true" : "false");
        builder.Append(",\"height\": ");
        AppendOptionalInteger(builder, ImageInteger(detail.Stream.Dictionary, "Height", engine));
        builder.Append(",\"name\": ");
        PdfJsonWriter.AppendAsciiName(builder, detail.Name);
        builder.Append(",\"object\": \"");
        PdfJsonWriter.AppendReference(builder, detail.Reference.ObjectNumber, detail.Reference.Generation);
        builder.Append("\",\"width\": ");
        AppendOptionalInteger(builder, ImageInteger(detail.Stream.Dictionary, "Width", engine));
        builder.Append((char)125);
    }

    private static void AppendOptionalInteger(DisplayBuffer builder, long? value)
    {
        if (value.HasValue)
        {
            builder.AppendInteger(value.Value);
        }
        else
        {
            builder.Append("null");
        }
    }

    private static long? ImageInteger(PdfDictionary dictionary, string key, DocumentEngine engine)
    {
        if (!dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw) || raw is PdfNull)
        {
            return null;
        }

        if (raw is PdfInteger direct)
        {
            return direct.Value;
        }

        if (raw is PdfReference reference
            && ((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved)
            && resolved is PdfInteger target)
        {
            return target.Value;
        }

        return null;
    }

    private static List<PageImageDetail> ListImages(
        DocumentEngine engine,
        PageLocation location,
        QpdfLimits limits,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor); cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        List<PageImageDetail> images = ParserContainers.CreateList<PageImageDetail>(memory);
        PdfDictionary? resources = EffectiveImageResources(engine, location, limits, memory, governor, cancellationToken);
        if (resources is null)
        {
            return images;
        }

        if (!resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjectsRaw) || xobjectsRaw is PdfNull)
        {
            return images;
        }

        PdfDictionary? xobjects = xobjectsRaw as PdfDictionary;
        if (xobjects is null && xobjectsRaw is PdfReference xobjectsReference
            && ((IIndirectResolver)engine).TryResolve(xobjectsReference, out QpdfObject? xobjectsResolved))
        {
            xobjects = xobjectsResolved as PdfDictionary;
        }

        if (xobjects is null)
        {
            return images;
        }

        memory.Reserve(checked(32L + (xobjects.Count == 0 ? 0L : 32L + IntPtr.Size * (long)xobjects.Count)));
        List<PdfName> names = new(xobjects.Count);
        using (PdfDictionary.EntryEnumerator entries = xobjects.GetEntryEnumerator())
        {
            while (entries.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                names.Add(entries.Current.Key);
            }
        }
        GovernedCollections.SortNames(memory, governor, names);
        foreach (PdfName key in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!xobjects.TryGet(key, out QpdfObject entry) || entry is not PdfReference reference)
            {
                continue;
            }

            if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? target) || target is not PdfStream stream)
            {
                continue;
            }

            if (!stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype) || subtype is not PdfName subtypeName || !subtypeName.RawSpan.SequenceEqual("Image"u8))
            {
                continue;
            }

            ParserContainers.AddItem(memory, images, new PageImageDetail(key, reference, stream), 3L * IntPtr.Size);
        }

        return images;
    }

    private static PdfDictionary? EffectiveImageResources(
        DocumentEngine engine,
        PageLocation location,
        QpdfLimits limits,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        GovernedIntSet? visited = null;

        PdfDictionary current = location.Page;
        int depth = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resourcesRaw) && resourcesRaw is not PdfNull)
            {
                if (resourcesRaw is PdfDictionary direct)
                {
                    return direct;
                }

                if (resourcesRaw is PdfReference resourcesReference
                    && ((IIndirectResolver)engine).TryResolve(resourcesReference, out QpdfObject? resourcesResolved)
                    && resourcesResolved is PdfDictionary resolved)
                {
                    return resolved;
                }

                return null;
            }

            if (!current.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parentRaw) || parentRaw is PdfNull)
            {
                return null;
            }

            PdfDictionary parent;
            if (parentRaw is PdfReference parentReference)
            {
                if (visited is null)
                {
                    visited = GovernedIntSet.Create(memory, governor, cancellationToken);
                    if (location.Source is not null) { visited.Add(memory, location.Source.ObjectNumber); }
                }
                if (!visited.Add(memory, parentReference.ObjectNumber))
                {
                    throw new QpdfParseException($"Page ancestry cycle at object {parentReference.ObjectNumber}.", null, parentReference.ObjectNumber);
                }

                if (!((IIndirectResolver)engine).TryResolve(parentReference, out QpdfObject? resolved) || resolved is not PdfDictionary resolvedParent)
                {
                    throw new QpdfParseException("Unresolvable page parent.", null, parentReference.ObjectNumber);
                }

                parent = resolvedParent;
            }
            else if (parentRaw is PdfDictionary directParent)
            {
                parent = directParent;
            }
            else
            {
                throw new QpdfParseException("Page /Parent must be a dictionary or a reference.", null, null);
            }

            depth += 1;
            if (depth > limits.MaxNestingDepth)
            {
                throw new QpdfParseException($"Page ancestry depth exceeds {limits.MaxNestingDepth}.", null, null);
            }

            governor.NoteOperation();
            current = parent;
        }
    }

    private static void AppendContents(
        DisplayBuffer builder,
        DocumentEngine engine,
        PdfDictionary page,
        JsonRequest request,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        if (!page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null || contents is PdfNull)
        {
            builder.Append("[]");
            return;
        }

        // Resolve only the outer container. Entries keep their stream identity;
        // malformed non-array values remain visible in the inspection report.
        if (contents is PdfReference reference && engine.ResolveArray(reference) is PdfArray indirect) { contents = indirect; }
        builder.Append((char)91);
        if (contents is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append((char)44);
                }

                PdfJsonWriter.AppendValue(builder, array[i], request.Version, governor, 0, limits);
            }
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, contents, request.Version, governor, 0, limits);
        }

        builder.Append((char)93);
    }
    private static void IndexPageOutlines(List<OutlineEntry> entries, GovernedIntDictionary<List<OutlineEntry>> index,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        foreach (OutlineEntry entry in entries)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (entry.DestinationPage is int page)
            {
                if (!index.TryGetValue(page, out List<OutlineEntry>? targets))
                {
                    targets = ParserContainers.CreateList<OutlineEntry>(memory);
                    index.TryAdd(memory, page, targets);
                }
                ParserContainers.AddItem(memory, targets, entry, IntPtr.Size);
            }
            IndexPageOutlines(entry.Children, index, memory, governor, token);
        }
    }

    private static void AppendPageOutlines(
        DisplayBuffer builder,
        GovernedIntDictionary<List<OutlineEntry>>? outlines,
        int pageposfrom1,
        JsonRequest request,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        builder.Append((char)91);
        bool first = true;
        if (outlines is not null && outlines.TryGetValue(pageposfrom1, out List<OutlineEntry>? entries))
        {
            foreach (OutlineEntry entry in entries)
            {
                if (!first)
                {
                    builder.Append((char)44);
                }

                first = false;
                AppendOutlineEntry(builder, entry, true, request, governor, limits);
            }
        }

        builder.Append((char)93);
    }

    private static void AppendEncrypt(DisplayBuffer builder, int version)
    {
        string modifyKey = version == 1 ? "moddifyannotations" : "modifyannotations";
        builder.Append("{\"encrypted\": false,\"userpasswordmatched\": false,");
        builder.Append("\"ownerpasswordmatched\": false,\"recovereduserpassword\": null,");
        builder.Append("\"capabilities\": {\"accessibility\": true,\"extract\": true,");
        builder.Append("\"printlow\": true,\"printhigh\": true,\"modifyassembly\": true,");
        builder.Append("\"modifyforms\": true,\"");
        builder.Append(modifyKey);
        builder.Append("\": true,\"modifyother\": true,\"modify\": true},");
        builder.Append("\"parameters\": {\"R\": 0,\"V\": 0,\"P\": 0,\"bits\": 0,");
        builder.Append("\"key\": null,\"method\": \"none\",\"streammethod\": \"none\",");
        builder.Append("\"stringmethod\": \"none\",\"filemethod\": \"none\"}}");
    }
    private static void AppendAttachments(
        DisplayBuffer builder,
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(engine, limits, governor, diagnostics, memory, cancellationToken);
        builder.Append((char)123);
        bool firstAttachment = true;
        foreach (EmbeddedAttachment attachment in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!firstAttachment)
            {
                builder.Append((char)44);
            }

            firstAttachment = false;
            PdfJsonWriter.AppendJsonString(builder, attachment.Key);
            builder.Append(": {\"filespec\": ");
            if (attachment.Spec is not PdfReference specReference)
            {
                builder.Append("null");
            }
            else
            {
                builder.Append((char)34);
                PdfJsonWriter.AppendReference(builder, specReference.ObjectNumber, specReference.Generation);
                builder.Append((char)34);
            }

            builder.Append(",\"preferredname\": ");
            PdfJsonWriter.AppendJsonString(builder, attachment.FileName);
            builder.Append(",\"preferredcontents\": ");
            if (attachment.StreamReference is not { } streamReference)
            {
                builder.Append("null");
            }
            else
            {
                builder.Append((char)34);
                PdfJsonWriter.AppendReference(builder, streamReference.ObjectNumber, streamReference.Generation);
                builder.Append((char)34);
            }

            builder.Append(",\"description\": ");
            if (attachment.Description.Length == 0)
            {
                builder.Append("null");
            }
            else
            {
                PdfJsonWriter.AppendJsonString(builder, attachment.Description);
            }

            builder.Append(",\"names\": ");
            AppendAttachmentNames(builder, attachment, engine, governor, cancellationToken);
            builder.Append(",\"streams\": ");
            AppendAttachmentStreams(builder, engine, attachment, governor, cancellationToken);
            builder.Append((char)125);
        }

        builder.Append((char)125);
    }

    private static readonly string[] NameKeys = ["F", "UF", "Unix", "DOS", "Mac"];

    private static PdfDictionary? AttachmentSpec(DocumentEngine engine, EmbeddedAttachment attachment)
    {
        if (attachment.Spec is PdfDictionary direct)
        {
            return direct;
        }

        return TryResolve(engine, attachment.Spec) as PdfDictionary;
    }

    private static void AppendAttachmentNames(DisplayBuffer builder, EmbeddedAttachment attachment, DocumentEngine engine,
        ResourceGovernor governor, CancellationToken token)
    {
        builder.Append((char)123);
        bool first = true;
        using ParserMemoryScope textMemory = ParserMemoryScope.Create(governor);
        PdfDictionary? spec = AttachmentSpec(engine, attachment);
        if (spec is not null)
        {
            foreach (string name in NameKeys)
            {
                if (spec.TryGet(PdfName.FromAscii(name), out QpdfObject raw) && raw is PdfByteString text)
                {
                    if (!first)
                    {
                        builder.Append((char)44);
                    }

                    first = false;
                    PdfJsonWriter.AppendJsonString(builder, name);
                    builder.Append(": ");
                    PdfJsonWriter.AppendJsonString(builder, PdfText.Decode(text.ValueMemory, textMemory, governor, token));
                    textMemory.Discard();
                }
            }
        }

        builder.Append((char)125);
    }

    private static void AppendAttachmentStreams(DisplayBuffer builder, DocumentEngine engine,
        EmbeddedAttachment attachment, ResourceGovernor governor, CancellationToken token)
    {
        builder.Append('{'); bool first = true;
        PdfDictionary? spec = AttachmentSpec(engine, attachment);
        PdfDictionary? streams = spec is not null && spec.TryGet(PdfName.FromAscii("EF"), out QpdfObject raw)
            ? TryResolve(engine, raw) as PdfDictionary : null;
        using ParserMemoryScope textMemory = ParserMemoryScope.Create(governor);
        if (streams is not null)
        {
            foreach (string name in NameKeys)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (!streams.TryGet(PdfName.FromAscii(name), out QpdfObject candidate)
                    || TryResolve(engine, candidate) is not PdfStream stream) { continue; }
                if (!first) { builder.Append(','); } first = false;
                PdfJsonWriter.AppendJsonString(builder, name); builder.Append(": ");
                EmbeddedFiles.EmbeddedStreamMetadata metadata;
                if (ReferenceEquals(stream, attachment.Stream))
                { metadata = new(attachment.MimeType, attachment.CreationDate, attachment.ModDate, attachment.Checksum, attachment.Size); }
                else
                {
                    PdfDictionary? parameters = stream.Dictionary.TryGet(PdfName.FromAscii("Params"), out QpdfObject parametersRaw)
                        ? TryResolve(engine, parametersRaw) as PdfDictionary : null;
                    metadata = EmbeddedFiles.ReadStreamMetadata(stream, parameters, textMemory, governor, token);
                }
                AppendEmbeddedStream(builder, metadata);
                textMemory.Discard();
            }
        }
        builder.Append('}');
    }

    private static QpdfObject? TryResolve(DocumentEngine engine, QpdfObject? raw)
    {
        if (raw is PdfReference reference)
        {
            if (((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved) && resolved is not null && resolved is not PdfNull)
            {
                return resolved;
            }

            return null;
        }

        return raw;
    }

    private static void AppendEmbeddedStream(DisplayBuffer builder, EmbeddedFiles.EmbeddedStreamMetadata metadata)
    {
        builder.Append("{\"creationdate\": "); AppendIsoDate(builder, metadata.CreationDate);
        builder.Append(",\"modificationdate\": "); AppendIsoDate(builder, metadata.ModDate);
        builder.Append(",\"mimetype\": ");
        if (metadata.MimeType.Length == 0) { builder.Append("null"); }
        else { PdfJsonWriter.AppendJsonString(builder, metadata.MimeType); }
        builder.Append(",\"checksum\": ");
        if (metadata.Checksum.Length == 0) { builder.Append("null"); }
        else { PdfJsonWriter.AppendJsonString(builder, metadata.Checksum); }
        builder.Append('}');
    }

    internal static void AppendIsoDate(DisplayBuffer builder, string pdfDate)
    {
        string? iso = ToIso8601(pdfDate);
        if (iso is null)
        {
            builder.Append("null");
        }
        else
        {
            PdfJsonWriter.AppendJsonString(builder, iso);
        }
    }

    internal static string? ToIso8601(string pdfDate)
    {
        // Inspect the bounded timestamp syntax before allocating any presentation text.
        if (pdfDate is null || pdfDate.Length is not (16 or 17 or 23)
            || !pdfDate.StartsWith("D:", StringComparison.Ordinal)) { return null; }
        for (int i = 2; i < 16; i++) { if (pdfDate[i] is < '0' or > '9') { return null; } }
        bool utc = true;
        if (pdfDate.Length == 17 && pdfDate[16] != 'Z') { return null; }
        if (pdfDate.Length == 23)
        {
            if (pdfDate[16] is not ('+' or '-') || pdfDate[19] != '\'' || pdfDate[22] != '\'') { return null; }
            for (int i = 17; i < 22; i++) { if (i != 19 && pdfDate[i] is < '0' or > '9') { return null; } }
            int hours = (pdfDate[17] - '0') * 10 + pdfDate[18] - '0';
            int minutes = (pdfDate[20] - '0') * 10 + pdfDate[21] - '0';
            if (hours > 23 || minutes > 59) { return null; }
            utc = hours == 0 && minutes == 0;
        }
        return string.Create(utc ? 20 : 25, (pdfDate, utc), static (target, state) =>
        {
            ReadOnlySpan<char> digits = state.pdfDate.AsSpan(2, 14);
            digits[..4].CopyTo(target); target[4] = '-'; digits.Slice(4, 2).CopyTo(target[5..]); target[7] = '-';
            digits.Slice(6, 2).CopyTo(target[8..]); target[10] = 'T'; digits.Slice(8, 2).CopyTo(target[11..]); target[13] = ':';
            digits.Slice(10, 2).CopyTo(target[14..]); target[16] = ':'; digits.Slice(12, 2).CopyTo(target[17..]);
            if (state.utc) { target[19] = 'Z'; }
            else
            {
                target[19] = state.pdfDate[16]; state.pdfDate.AsSpan(17, 2).CopyTo(target[20..]); target[22] = ':';
                state.pdfDate.AsSpan(20, 2).CopyTo(target[23..]);
            }
        });
    }

    private static void AppendObjects(
        DisplayBuffer builder,
        QpdfDocument document,
        JsonRequest request,
        bool touchesPages,
        PreparedJsonOutput output,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        DocumentEngine engine = document.Engine;
        List<int> numbers = output.CreateObjectNumbers();
        try
        {
            int maxObjectId = 0;
            foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
            {
                governor.CheckCancellation(); cancellationToken.ThrowIfCancellationRequested();
                if (entry.Value.Kind == XrefEntryKind.Free)
                {
                    continue;
                }

                governor.CheckCancellation();
                output.AddObjectNumber(numbers, entry.Key);
                if (entry.Key > maxObjectId)
                {
                    maxObjectId = entry.Key;
                }
            }

            GovernedSort.Sort<int, ObjectNumberComparer>(CollectionsMarshal.AsSpan(numbers), new(), governor, cancellationToken);
            if (request.Version == 1)
            {
                builder.Append((char)123);
                bool first = true;
                foreach (int number in numbers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    int generation = engine.XRefEntries[number].Kind == XrefEntryKind.Compressed
                        ? 0
                        : engine.XRefEntries[number].Second;
                    if (!request.WantsObject(number, generation))
                    {
                        continue;
                    }

                    QpdfObject value = engine.Load(number);
                    if (!first)
                    {
                        builder.Append((char)44);
                    }

                    first = false;
                    PdfJsonWriter.AppendObjectKey(builder, number, generation, false);
                    builder.Append(": ");
                    PdfJsonWriter.AppendValue(builder, value, 1, governor, 0, limits);
                }

                if (request.WantsTrailer)
                {
                    if (!first)
                    {
                        builder.Append((char)44);
                    }

                    builder.Append("\"trailer\": ");
                    PdfJsonWriter.AppendValue(builder, engine.Trailer, 1, governor, 0, limits);
                }

                builder.Append((char)125);
                return;
            }

            builder.Append("[{");
            builder.Append("\"jsonversion\": 2,\"pdfversion\": ");
            builder.Append('"'); builder.AppendInteger(document.HeaderVersionParts.Major); builder.Append('.'); builder.AppendInteger(document.HeaderVersionParts.Minor); builder.Append('"');
            builder.Append(",\"pushedinheritedpageresources\": false,\"calledgetallpages\": ");
            builder.Append(touchesPages ? "true" : "false");
            builder.Append(",\"maxobjectid\": ");
            builder.AppendInteger(maxObjectId);
            builder.Append("}, {");
            bool firstObject = true;
            foreach (int number in numbers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                int generation = engine.XRefEntries[number].Kind == XrefEntryKind.Compressed
                    ? 0
                    : engine.XRefEntries[number].Second;
                if (!request.WantsObject(number, generation))
                {
                    continue;
                }

                QpdfObject value = engine.Load(number);
                if (!firstObject)
                {
                    builder.Append((char)44);
                }

                firstObject = false;
                PdfJsonWriter.AppendObjectKey(builder, number, generation, true);
                builder.Append(": ");
                if (value is PdfStream stream)
                {
                    AppendJsonStream(builder, engine, number, stream, request, output, governor, limits, cancellationToken);
                }
                else
                {
                    builder.Append("{\"value\": ");
                    PdfJsonWriter.AppendValue(builder, value, 2, governor, 0, limits);
                    builder.Append((char)125);
                }
            }

            if (request.WantsTrailer)
            {
                if (!firstObject)
                {
                    builder.Append((char)44);
                }

                builder.Append("\"trailer\": {\"value\": ");
                PdfJsonWriter.AppendValue(builder, engine.Trailer, 2, governor, 0, limits);
                builder.Append((char)125);
            }

            builder.Append("}]");
        }
        finally { output.ReleaseObjectNumbers(numbers); }
    }

    private readonly struct ObjectNumberComparer : IComparer<int>
    {
        public int Compare(int left, int right) => left.CompareTo(right);
    }

    private static (byte[] Data, bool Decoded) ResolveStreamPayload(DocumentEngine engine,
        PdfStream stream, string decodeLevel, QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory)
    {
        // Borrow immutable reader storage. A file sidecar receives its own
        // admitted copy before the document can close; inline encoding does not.
        byte[] raw = stream.ArrayData(memory, governor, governor.CancellationToken);
        if (decodeLevel == "none") { return (raw, false); }
        FilterPlan stages;
        try { stages = FilterChain.ResolveScoped(stream.Dictionary, engine, governor, governor.CancellationToken); }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return (raw, false); }
        using FilterPlan stageOwner = stages;
        if (stages.Count == 0 || !FilterChain.AllStagesInLevel(stages, decodeLevel)) { return (raw, false); }
        try { return (FilterChain.Decode(raw, stages, limits, governor, memory, governor.CancellationToken), true); }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return (raw, false); }
    }

    private static void AppendJsonStream(DisplayBuffer builder, DocumentEngine engine, int number,
        PdfStream stream, JsonRequest request, PreparedJsonOutput output,
        ResourceGovernor governor, QpdfLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (request.Version == 1 || request.StreamData == "none")
        {
            builder.Append("{\"stream\": {\"dict\": ");
            PdfJsonWriter.AppendDictionary(builder, stream.Dictionary, request.Version, governor, 0, limits);
            builder.Append("}}"); return;
        }
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        (byte[] data, bool decoded) = ResolveStreamPayload(engine, stream, request.DecodeLevel, limits, governor, memory);
        if (request.StreamData == "file")
        {
            JsonPayload payload = output.AddSidecar(request.StreamPrefix ?? string.Empty, number, data, limits);
            builder.Append("{\"stream\": {\"datafile\": ");
            PdfJsonWriter.AppendJsonString(builder, payload.Name);
        }
        else
        {
            builder.Append("{\"stream\": {\"data\": ");
            PdfJsonWriter.AppendBase64(builder, data);
        }
        builder.Append(",\"dict\": ");
        PdfJsonWriter.AppendStreamDictionary(builder, stream.Dictionary, request.Version, governor, limits, decoded);
        builder.Append("}}");
    }

    private static void AppendObjectInfo(
        DisplayBuffer builder,
        QpdfDocument document,
        JsonRequest request,
        PreparedJsonOutput output,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        DocumentEngine engine = document.Engine;
        List<int> numbers = output.CreateObjectNumbers();
        try
        {
            foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
            {
                governor.CheckCancellation(); cancellationToken.ThrowIfCancellationRequested();
                if (entry.Value.Kind != XrefEntryKind.Free)
                {
                    governor.CheckCancellation();
                    output.AddObjectNumber(numbers, entry.Key);
                }
            }

            GovernedSort.Sort<int, ObjectNumberComparer>(CollectionsMarshal.AsSpan(numbers), new(), governor, cancellationToken);
            builder.Append((char)123);
            bool first = true;
            foreach (int number in numbers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                int generation = engine.XRefEntries[number].Kind == XrefEntryKind.Compressed
                    ? 0
                    : engine.XRefEntries[number].Second;
                if (!request.WantsObject(number, generation))
                {
                    continue;
                }

                QpdfObject value = engine.Load(number);
                if (!first)
                {
                    builder.Append((char)44);
                }

                first = false;
                PdfJsonWriter.AppendObjectKey(builder, number, generation, false);
                builder.Append(": {\"stream\": {\"is\": ");
                if (value is PdfStream stream)
                {
                    builder.Append("true,\"length\": ");
                    if (stream.Dictionary.TryGet(PdfName.FromAscii("Length"), out QpdfObject? length) && length is not null && length is not PdfNull)
                    {
                        PdfJsonWriter.AppendValue(builder, DereferenceOnce(engine, length), request.Version, governor, 0, limits);
                    }
                    else
                    {
                        builder.Append("null");
                    }

                    builder.Append(",\"filter\": ");
                    if (stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject? filter) && filter is not null && filter is not PdfNull)
                    {
                        PdfJsonWriter.AppendValue(builder, DereferenceOnce(engine, filter), request.Version, governor, 0, limits);
                    }
                    else
                    {
                        builder.Append("null");
                    }
                }
                else
                {
                    builder.Append("false,\"length\": null,\"filter\": null");
                }

                builder.Append("}}");
            }

            builder.Append((char)125);
        }
        finally { output.ReleaseObjectNumbers(numbers); }
    }

    private static QpdfObject DereferenceOnce(DocumentEngine engine, QpdfObject value)
    {
        if (value is PdfReference reference
            && ((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved)
            && resolved is not null
            && resolved is not PdfNull)
        {
            return resolved;
        }

        return value;
    }
    private static void AppendDisplayName(DisplayBuffer builder, PdfName? name)
    {
        builder.Append('"');
        if (name is not null)
        {
            builder.Append('/');
            Span<char> escaped = stackalloc char[3];
            foreach (byte value in name.RawSpan)
            {
                builder.Governor.CheckCancellation();
                PdfJsonWriter.AppendEscaped(builder, escaped[..PdfNameDisplay.WriteByte(value, escaped)]);
            }
        }
        builder.Append('"');
    }

    private static void AppendAcroform(
        DisplayBuffer builder,
        QpdfDocument document,
        JsonRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        DocumentEngine engine = document.Engine;
        PdfDictionary? catalog = ResolveChild(engine, engine.Trailer, "Root", "catalog");
        PdfDictionary? acroform = null;
        if (catalog is not null
            && catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? acroRaw)
            && acroRaw is not null
            && acroRaw is not PdfNull)
        {
            QpdfObject target = acroRaw;
            if (acroRaw is PdfReference acroReference)
            {
                target = TryResolve(engine, acroRaw) ?? acroRaw;
            }

            acroform = target as PdfDictionary;
        }

        bool hasAcroform = catalog is not null
            && catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? hasRaw)
            && hasRaw is not null
            && hasRaw is not PdfNull;
        bool needAppearances = false;
        if (acroform is not null
            && acroform.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject? needRaw)
            && needRaw is PdfBoolean need)
        {
            needAppearances = need.Value;
        }

        builder.Append("{\"hasacroform\": ");
        builder.Append(hasAcroform ? "true" : "false");
        builder.Append(",\"needappearances\": ");
        builder.Append(needAppearances ? "true" : "false");
        builder.Append(",\"fields\": [");
        bool firstField = true;
        IReadOnlyList<PageLocation> pages = document.PageReferences;
        for (int i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!pages[i].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            QpdfObject annotsTarget = TryResolve(engine, annotsRaw) ?? annotsRaw;
            if (annotsTarget is not PdfArray annots)
            {
                continue;
            }

            for (int annotationIndex = 0; annotationIndex < annots.Count; annotationIndex++)
            {
                QpdfObject item = annots[annotationIndex];
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                PdfReference? annotReference = item as PdfReference;
                QpdfObject node = TryResolve(engine, item) ?? item;
                if (node is not PdfDictionary annot)
                {
                    continue;
                }

                if (!annot.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtypeRaw) || subtypeRaw is not PdfName subtype || !subtype.RawSpan.SequenceEqual("Widget"u8))
                {
                    continue;
                }

                using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
                AcroField field = ResolveAcroField(engine, annotReference, annot, limits, memory, governor, cancellationToken);
                if (!firstField)
                {
                    builder.Append((char)44);
                }

                firstField = false;
                AppendAcroField(builder, engine, field, annotReference, annot, i + 1, acroform, request, memory, governor, limits, cancellationToken);
            }
        }

        builder.Append("]}");
    }
    private sealed record AcroField(
        List<(PdfReference? Reference, PdfDictionary Node)> Ancestors,
        int FieldIndex,
        PdfName? FieldType,
        string QualifiedName,
        string PartialName)
    {
        internal PdfReference? NodeReference => Ancestors[FieldIndex].Reference;
        internal PdfDictionary Node => Ancestors[FieldIndex].Node;
    }

    private static AcroField ResolveAcroField(
        DocumentEngine engine,
        PdfReference? annotReference,
        PdfDictionary annot,
        QpdfLimits limits,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        List<(PdfReference? Reference, PdfDictionary Node)> chain = ParserContainers.CreateList<(PdfReference?, PdfDictionary)>(memory);
        ParserContainers.AddItem(memory, chain, (annotReference, annot), 2L * IntPtr.Size);
        GovernedIntSet visited = GovernedIntSet.Create(memory, governor, token);
        if (annotReference is not null) { visited.Add(memory, annotReference.ObjectNumber); }
        PdfDictionary current = annot;
        int depth = 0;
        while (depth < limits.MaxNestingDepth)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (!current.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw) || parentRaw is null || parentRaw is PdfNull) { break; }
            PdfReference? parentReference = parentRaw as PdfReference;
            QpdfObject parent = TryResolve(engine, parentRaw) ?? parentRaw;
            if (parent is not PdfDictionary parentDict) { break; }
            if (parentReference is not null && !visited.Add(memory, parentReference.ObjectNumber)) { break; }
            ParserContainers.AddItem(memory, chain, (parentReference, parentDict), 2L * IntPtr.Size);
            current = parentDict;
            depth++;
        }

        // A separate widget points at its terminal field, which may inherit FT
        // from a named group. A merged widget carries field entries itself.
        int fieldIndex = chain.Count > 1 && !Content.AcroFormCopy.HasFieldEntries(annot, governor, token) ? 1 : 0;
        PdfName? fieldType = null;
        foreach ((PdfReference? _, PdfDictionary node) in chain)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (node.TryGet(PdfName.FromAscii("FT"), out QpdfObject? ftRaw)
                && ftRaw is not null && TryResolve(engine, ftRaw) is PdfName ftName)
            { fieldType = ftName; break; }
        }

        List<string> parts = ParserContainers.CreateList<string>(memory);
        string partial = string.Empty;
        int length = 0;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (chain[i].Node.TryGet(PdfName.FromAscii("T"), out QpdfObject? tRaw)
                && tRaw is not null && TryResolve(engine, tRaw) is PdfByteString text)
            {
                string part = PdfText.Decode(text.ValueMemory, memory, governor, token);
                length = checked(length + part.Length + (parts.Count == 0 ? 0 : 1));
                ParserContainers.AddItem(memory, parts, part, IntPtr.Size);
                if (i == fieldIndex) { partial = part; }
            }
        }
        if (parts.Count > 1) { memory.Reserve(GovernedText.StringBytes(length)); }
        string qualified = parts.Count == 1 ? parts[0] : string.Join(".", parts);
        memory.Reserve(64L);
        return new AcroField(chain, fieldIndex, fieldType, qualified, partial);
    }

    private static QpdfObject? InheritedValue(DocumentEngine engine, List<(PdfReference? Reference, PdfDictionary Node)> chain, string key)
    {
        foreach ((PdfReference? _, PdfDictionary node) in chain)
        {
            if (node.TryGet(PdfName.FromAscii(key), out QpdfObject raw) && raw is not null && raw is not PdfNull)
            { return TryResolve(engine, raw) ?? raw; }
        }
        return null;
    }

    private static int InheritedInt(DocumentEngine engine, List<(PdfReference? Reference, PdfDictionary Node)> chain, string key)
    {
        return InheritedIntOrNull(engine, chain, key) ?? 0;
    }

    private static int? InheritedIntOrNull(DocumentEngine engine, List<(PdfReference? Reference, PdfDictionary Node)> chain, string key)
    {
        QpdfObject? found = InheritedValue(engine, chain, key);
        return found is PdfInteger integer ? (int?)Math.Min(Math.Max(integer.Value, (long)int.MinValue), (long)int.MaxValue) : null;
    }
    private static void AppendAcroField(
        DisplayBuffer builder,
        DocumentEngine engine,
        AcroField field,
        PdfReference? annotReference,
        PdfDictionary annot,
        int pageposfrom1,
        PdfDictionary? acroform,
        JsonRequest request,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        QpdfLimits limits,
        CancellationToken token)
    {
        builder.Append("{\"object\": ");
        if (field.NodeReference is PdfReference fieldReference)
        {
            builder.Append((char)34);
            PdfJsonWriter.AppendReference(builder, fieldReference.ObjectNumber, fieldReference.Generation);
            builder.Append((char)34);
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, field.Node, request.Version, governor, 0, limits);
        }

        builder.Append(",\"parent\": ");
        if (field.Node.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw) && parentRaw is not null && parentRaw is not PdfNull)
        {
            PdfJsonWriter.AppendValue(builder, parentRaw, request.Version, governor, 0, limits);
        }
        else
        {
            builder.Append("null");
        }

        builder.Append(",\"pageposfrom1\": ");
        builder.AppendInteger(pageposfrom1);
        builder.Append(",\"fieldtype\": ");
        AppendDisplayName(builder, field.FieldType);
        int flags = InheritedInt(engine, field.Ancestors, "Ff");
        builder.Append(",\"fieldflags\": ");
        builder.AppendInteger(flags);
        builder.Append(",\"fullname\": ");
        PdfJsonWriter.AppendJsonString(builder, field.QualifiedName);
        builder.Append(",\"partialname\": ");
        PdfJsonWriter.AppendJsonString(builder, field.PartialName);
        string alternative = field.QualifiedName;
        QpdfObject? tu = InheritedValue(engine, field.Ancestors, "TU");
        if (tu is PdfByteString tuText)
        {
            alternative = PdfText.Decode(tuText.ValueMemory, memory, governor, token);
        }

        builder.Append(",\"alternativename\": ");
        PdfJsonWriter.AppendJsonString(builder, alternative);
        string mapping = alternative;
        QpdfObject? tm = InheritedValue(engine, field.Ancestors, "TM");
        if (tm is PdfByteString tmText)
        {
            mapping = PdfText.Decode(tmText.ValueMemory, memory, governor, token);
        }

        builder.Append(",\"mappingname\": ");
        PdfJsonWriter.AppendJsonString(builder, mapping);
        builder.Append(",\"value\": ");
        QpdfObject? value = InheritedValue(engine, field.Ancestors, "V");
        if (value is null)
        {
            builder.Append("null");
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, value, request.Version, governor, 0, limits);
        }

        builder.Append(",\"defaultvalue\": ");
        QpdfObject? defaultValue = InheritedValue(engine, field.Ancestors, "DV");
        if (defaultValue is null)
        {
            builder.Append("null");
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, defaultValue, request.Version, governor, 0, limits);
        }

        int? quadding = InheritedIntOrNull(engine, field.Ancestors, "Q");
        if (!quadding.HasValue && acroform is not null
            && acroform.TryGet(PdfName.FromAscii("Q"), out QpdfObject? acroQ)
            && acroQ is not null && acroQ is not PdfNull)
        {
            QpdfObject resolvedQ = TryResolve(engine, acroQ) ?? acroQ;
            if (resolvedQ is PdfInteger acroQInt)
            {
                quadding = (int)Math.Min(Math.Max(acroQInt.Value, (long)int.MinValue), (long)int.MaxValue);
            }
        }

        int quad = quadding ?? 0;

        builder.Append(",\"quadding\": ");
        builder.AppendInteger(quad);
        bool isText = field.FieldType is PdfName textType && textType.RawSpan.SequenceEqual("Tx"u8);
        bool isChoice = field.FieldType is PdfName choiceType && choiceType.RawSpan.SequenceEqual("Ch"u8);
        bool isRadio = field.FieldType is PdfName radioType && radioType.RawSpan.SequenceEqual("Btn"u8) && (flags & 32768) == 32768;
        bool isCheckbox = field.FieldType is PdfName checkboxType && checkboxType.RawSpan.SequenceEqual("Btn"u8) && (flags & (32768 | 65536)) == 0;
        builder.Append(",\"ischeckbox\": ");
        builder.Append(isCheckbox ? "true" : "false");
        builder.Append(",\"isradiobutton\": ");
        builder.Append(isRadio ? "true" : "false");
        builder.Append(",\"ischoice\": ");
        builder.Append(isChoice ? "true" : "false");
        builder.Append(",\"istext\": ");
        builder.Append(isText ? "true" : "false");
        builder.Append(",\"choices\": [");
        if (isChoice)
        {
            QpdfObject? opt = InheritedValue(engine, field.Ancestors, "Opt");
            QpdfObject optTarget = opt is null ? PdfNull.Instance : opt;
            if (optTarget is PdfReference optReference)
            {
                optTarget = TryResolve(engine, optReference) ?? optTarget;
            }

            if (optTarget is PdfArray optArray)
            {
                bool firstChoice = true;
                for (int optionIndex = 0; optionIndex < optArray.Count; optionIndex++)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    QpdfObject item = optArray[optionIndex];
                    long before = memory.ReservedBytes;
                    QpdfObject entry = item is PdfReference itemReference ? TryResolve(engine, itemReference) ?? item : item;
                    string? choice = null;
                    if (entry is PdfByteString entryText)
                    {
                        choice = PdfText.Decode(entryText.ValueMemory, memory, governor, token);
                    }
                    else if (entry is PdfArray pair && pair.Count == 2)
                    {
                        QpdfObject display = pair[1] is PdfReference displayReference ? TryResolve(engine, displayReference) ?? pair[1] : pair[1];
                        if (display is PdfByteString displayText)
                        {
                            choice = PdfText.Decode(displayText.ValueMemory, memory, governor, token);
                        }
                    }

                    if (choice is not null)
                    {
                        if (!firstChoice)
                        {
                            builder.Append((char)44);
                        }

                        firstChoice = false;
                        PdfJsonWriter.AppendJsonString(builder, choice);
                    }
                    memory.Release(memory.ReservedBytes - before);
                }
            }
        }

        builder.Append("],\"annotation\": {\"object\": ");
        if (annotReference is PdfReference annotLink)
        {
            builder.Append((char)34);
            PdfJsonWriter.AppendReference(builder, annotLink.ObjectNumber, annotLink.Generation);
            builder.Append((char)34);
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, annot, request.Version, governor, 0, limits);
        }

        PdfName? appearance = null;
        if (annot.TryGet(PdfName.FromAscii("AS"), out QpdfObject? asRaw) && asRaw is not null && asRaw is not PdfNull)
        {
            QpdfObject asTarget = TryResolve(engine, asRaw) ?? asRaw;
            if (asTarget is PdfName asName)
            {
                appearance = asName;
            }
        }

        builder.Append(",\"appearancestate\": ");
        AppendDisplayName(builder, appearance);
        int annotFlags = 0;
        if (annot.TryGet(PdfName.FromAscii("F"), out QpdfObject? fRaw) && fRaw is not null && fRaw is not PdfNull)
        {
            QpdfObject fTarget = TryResolve(engine, fRaw) ?? fRaw;
            if (fTarget is PdfInteger fInt)
            {
                annotFlags = (int)Math.Min(Math.Max(fInt.Value, (long)int.MinValue), (long)int.MaxValue);
            }
        }

        builder.Append(",\"annotationflags\": ");
        builder.AppendInteger(annotFlags);
        builder.Append("}}");
    }
}
