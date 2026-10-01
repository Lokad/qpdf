using System.Globalization;
using System.Text;
using Lokad.Qpdf.Attachments;
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
    IReadOnlyList<string> Objects,
    bool MinimalOutput)
{
    internal bool Wants(string key)
    {
        return Keys.Count == 0 || Keys.Contains(key);
    }

    internal bool WantsObject(string canonical)
    {
        return Objects.Count == 0 || Objects.Contains(canonical);
    }
}

/// <summary>A stream-data sidecar file produced by file stream-data mode.</summary>
internal sealed record JsonSidecar(string Name, byte[] Data);

/// <summary>
/// Document to JSON inspection driver. Emits the upstream top-level layout
/// (version, parameters, then pages, pagelabels, attachments, encrypt,
/// outlines, objects or qpdf, objectinfo) with stream data omitted.
/// </summary>
internal static class DocumentJson
{
    internal static string WriteDocument(
        QpdfDocument document,
        JsonRequest request,
        List<JsonSidecar> sidecars,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        DocumentEngine engine = document.Engine;
        Func<PdfReference, QpdfObject?> resolver = reference =>
        {
            if (((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? value))
            {
                return value;
            }

            return null;
        };

        StringBuilder builder = new(4096);
        builder.Append((char)123);
        bool firstKey = request.MinimalOutput;
        if (!request.MinimalOutput)
        {
            AppendVersionAndParameters(builder, request);
        }

        bool touchesPages = request.Wants("pages") || request.Wants("pagelabels") || request.Wants("outlines") || request.Wants("acroform");
        Dictionary<int, int> pageIndex = BuildPageIndex(document);
        SortedDictionary<int, QpdfObject> labels = new();
        if (request.Wants("pages") || request.Wants("pagelabels"))
        {
            labels = ReadLabelTree(engine, limits, governor, diagnostics, cancellationToken);
        }

        List<OutlineEntry> outlines = new();
        if (request.Wants("outlines") || request.Wants("pages"))
        {
            outlines = ReadOutlines(engine, pageIndex, request.Version, limits, governor, diagnostics, cancellationToken);
        }

        if (request.Wants("pages"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendKey(builder, ref firstKey, "pages");
            AppendPages(builder, document, outlines, request, labels, resolver, limits, governor, diagnostics, cancellationToken);
        }

        if (request.Wants("pagelabels"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendKey(builder, ref firstKey, "pagelabels");
            AppendPageLabels(builder, request, labels, resolver, governor, limits);
        }

        if (request.Wants("attachments"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendKey(builder, ref firstKey, "attachments");
            AppendAttachments(builder, engine, request, limits, governor, diagnostics, cancellationToken);
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
            AppendOutlines(builder, outlines, request, resolver, governor, limits);
        }

        if (request.Wants("acroform"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendKey(builder, ref firstKey, "acroform");
            AppendAcroform(builder, document, request, resolver, limits, governor, diagnostics, cancellationToken);
        }

        if (request.Wants("objects") || request.Wants("qpdf"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendKey(builder, ref firstKey, request.Version == 1 ? "objects" : "qpdf");
            AppendObjects(builder, document, request, touchesPages, sidecars, resolver, limits, governor, diagnostics, cancellationToken);
        }

        if (request.Version == 1 && request.Wants("objectinfo"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendKey(builder, ref firstKey, "objectinfo");
            AppendObjectInfo(builder, document, request, resolver, limits, governor, diagnostics, cancellationToken);
        }

        builder.Append((char)125);
        builder.Append((char)10);
        return builder.ToString();
    }
    private static void AppendKey(StringBuilder builder, ref bool firstKey, string key)
    {
        if (!firstKey)
        {
            builder.Append((char)44);
        }

        firstKey = false;
        PdfJsonWriter.AppendJsonString(builder, key);
        builder.Append(": ");
    }

    private static void AppendVersionAndParameters(StringBuilder builder, JsonRequest request)
    {
        builder.Append("\"version\": ");
        builder.Append(request.Version.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"parameters\": {\"decodelevel\": ");
        PdfJsonWriter.AppendJsonString(builder, request.DecodeLevel);
        builder.Append((char)125);
    }

    private static Dictionary<int, int> BuildPageIndex(QpdfDocument document)
    {
        Dictionary<int, int> index = new();
        IReadOnlyList<PageLocation> pages = document.PageReferences;
        for (int i = 0; i < pages.Count; i++)
        {
            if (pages[i].Source is PdfReference source && !index.ContainsKey(source.ObjectNumber))
            {
                index[source.ObjectNumber] = i + 1;
            }
        }

        return index;
    }

    private static PdfDictionary? ResolveChild(
        DocumentEngine engine,
        PdfDictionary parent,
        string key,
        string role)
    {
        if (!parent.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
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
    private static SortedDictionary<int, QpdfObject> ReadLabelTree(
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        SortedDictionary<int, QpdfObject> labels = new();
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

        WalkLabelNode(engine, tree, 0, new HashSet<int>(), labels, limits, governor, cancellationToken);
        return labels;
    }

    private static void WalkLabelNode(
        DocumentEngine engine,
        PdfDictionary node,
        int depth,
        HashSet<int> visited,
        SortedDictionary<int, QpdfObject> labels,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException("Page-label number tree exceeds nesting budget.", null, null);
        }

        governor.ChargeWork(1L);
        if (node.TryGet(PdfName.FromAscii("Nums"), out QpdfObject? numsRaw) && numsRaw is not null && numsRaw is not PdfNull)
        {
            if (numsRaw is not PdfArray nums || nums.Count % 2 != 0)
            {
                throw new QpdfParseException("Page-label number array must hold index/value pairs.", null, null);
            }

            QpdfObject[] items = nums.Items;
            for (int i = 0; i < items.Length; i += 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (items[i] is not PdfInteger key)
                {
                    throw new QpdfParseException("Page-label number-tree keys must be integers.", null, null);
                }

                if (key.Value < 0L || key.Value > int.MaxValue)
                {
                    throw new QpdfParseException("Page-label index is out of range.", null, null);
                }

                labels[(int)key.Value] = items[i + 1];
            }
        }

        if (node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) && kidsRaw is not null && kidsRaw is not PdfNull)
        {
            if (kidsRaw is not PdfArray kids)
            {
                throw new QpdfParseException("Page-label kids must be an array.", null, null);
            }

            foreach (QpdfObject item in kids.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                int kidNumber = item is PdfReference kidReference ? kidReference.ObjectNumber : -1;
                QpdfObject target = item;
                if (item is PdfReference reference)
                {
                    if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
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
                if (track && !visited.Add(kidNumber))
                {
                    throw new QpdfParseException("Page-label number tree contains a cycle.", null, null);
                }

                try
                {
                    WalkLabelNode(engine, kid, depth + 1, visited, labels, limits, governor, cancellationToken);
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

    private static QpdfObject? LabelForPage(SortedDictionary<int, QpdfObject> labels, int pageZeroBased)
    {
        QpdfObject? best = null;
        foreach (KeyValuePair<int, QpdfObject> entry in labels)
        {
            if (entry.Key > pageZeroBased)
            {
                break;
            }

            best = entry.Value;
        }

        return best;
    }
    private static void AppendPageLabels(
        StringBuilder builder,
        JsonRequest request,
        SortedDictionary<int, QpdfObject> labels,
        Func<PdfReference, QpdfObject?> resolver,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        builder.Append((char)91);
        bool first = true;
        foreach (KeyValuePair<int, QpdfObject> entry in labels)
        {
            governor.ChargeWork(1L);
            if (!first)
            {
                builder.Append((char)44);
            }

            first = false;
            builder.Append("{\"index\": ");
            builder.Append(entry.Key.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"label\": ");
            PdfJsonWriter.AppendValue(builder, entry.Value, request.Version, resolver, governor, 0, limits);
            builder.Append((char)125);
        }

        builder.Append((char)93);
    }

    private sealed record OutlineEntry(
        PdfReference? NodeReference,
        PdfDictionary Node,
        string Title,
        QpdfObject? Destination,
        bool Open,
        int? DestinationPage,
        List<OutlineEntry> Children);

    private static List<OutlineEntry> ReadOutlines(
        DocumentEngine engine,
        Dictionary<int, int> pageIndex,
        int version,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        List<OutlineEntry> roots = new();
        PdfDictionary? catalog = ResolveChild(engine, engine.Trailer, "Root", "catalog");
        if (catalog is null)
        {
            return roots;
        }

        PdfDictionary? tree = ResolveChild(engine, catalog, "Outlines", "outlines");
        if (tree is null)
        {
            return roots;
        }

        if (!tree.TryGet(PdfName.FromAscii("First"), out QpdfObject? firstRaw) || firstRaw is null || firstRaw is PdfNull)
        {
            return roots;
        }

        QpdfObject sibling = firstRaw;
        HashSet<int> visited = new();
        while (sibling is not null && sibling is not PdfNull)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            PdfReference? siblingReference = sibling as PdfReference;
            QpdfObject node = sibling;
            if (sibling is PdfReference nodeReference)
            {
                if (!((IIndirectResolver)engine).TryResolve(nodeReference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
                {
                    throw new QpdfParseException("Unresolvable outline reference.", null, nodeReference.ObjectNumber);
                }

                node = resolved;
            }

            if (node is not PdfDictionary outline)
            {
                throw new QpdfParseException("Outline entry must be a dictionary.", null, null);
            }

            bool track = siblingReference is not null;
            if (track && !visited.Add(siblingReference!.ObjectNumber))
            {
                throw new QpdfParseException("Outline tree contains a cycle.", null, null);
            }

            try
            {
                roots.Add(ReadOutlineEntry(engine, pageIndex, siblingReference, outline, 0, visited, limits, governor, cancellationToken));
            }
            finally
            {
                if (track)
                {
                    visited.Remove(siblingReference!.ObjectNumber);
                }
            }

            if (!outline.TryGet(PdfName.FromAscii("Next"), out QpdfObject? next) || next is null)
            {
                break;
            }

            sibling = next;
        }

        return roots;
    }
    private static OutlineEntry ReadOutlineEntry(
        DocumentEngine engine,
        Dictionary<int, int> pageIndex,
        PdfReference? nodeReference,
        PdfDictionary outline,
        int depth,
        HashSet<int> visited,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException("Outline tree exceeds nesting budget.", null, null);
        }

        governor.ChargeWork(1L);
        string title = string.Empty;
        if (outline.TryGet(PdfName.FromAscii("Title"), out QpdfObject? titleRaw) && titleRaw is PdfByteString titleText)
        {
            title = PdfText.Decode(titleText.Value);
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

        List<OutlineEntry> children = new();
        if (outline.TryGet(PdfName.FromAscii("First"), out QpdfObject? firstRaw) && firstRaw is not null && firstRaw is not PdfNull)
        {
            QpdfObject sibling = firstRaw;
            while (sibling is not null && sibling is not PdfNull)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                PdfReference? siblingReference = sibling as PdfReference;
                QpdfObject node = sibling;
                if (sibling is PdfReference kidReference)
                {
                    if (!((IIndirectResolver)engine).TryResolve(kidReference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
                    {
                        throw new QpdfParseException("Unresolvable outline reference.", null, kidReference.ObjectNumber);
                    }

                    node = resolved;
                }

                if (node is not PdfDictionary kid)
                {
                    throw new QpdfParseException("Outline entry must be a dictionary.", null, null);
                }

                bool track = siblingReference is not null;
                if (track && !visited.Add(siblingReference!.ObjectNumber))
                {
                    throw new QpdfParseException("Outline tree contains a cycle.", null, null);
                }

                try
                {
                    children.Add(ReadOutlineEntry(engine, pageIndex, siblingReference, kid, depth + 1, visited, limits, governor, cancellationToken));
                }
                finally
                {
                    if (track)
                    {
                        visited.Remove(siblingReference!.ObjectNumber);
                    }
                }

                if (!kid.TryGet(PdfName.FromAscii("Next"), out QpdfObject? next) || next is null)
                {
                    break;
                }

                sibling = next;
            }
        }

        return new OutlineEntry(nodeReference, outline, title, destination, open, destinationPage, children);
    }

    private static void AppendOutlineEntry(
        StringBuilder builder,
        OutlineEntry entry,
        bool forPage,
        JsonRequest request,
        Func<PdfReference, QpdfObject?> resolver,
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
            PdfJsonWriter.AppendValue(builder, entry.Node, request.Version, resolver, governor, 0, limits);
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
            PdfJsonWriter.AppendValue(builder, entry.Destination, request.Version, resolver, governor, 0, limits);
        }

        if (!forPage)
        {
            builder.Append(",\"open\": ");
            builder.Append(entry.Open ? "true" : "false");
            builder.Append(",\"destpageposfrom1\": ");
            if (entry.DestinationPage.HasValue)
            {
                builder.Append(entry.DestinationPage.Value.ToString(CultureInfo.InvariantCulture));
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

                AppendOutlineEntry(builder, entry.Children[i], false, request, resolver, governor, limits);
            }

            builder.Append((char)93);
        }

        builder.Append((char)125);
    }

    private static void AppendOutlines(
        StringBuilder builder,
        List<OutlineEntry> outlines,
        JsonRequest request,
        Func<PdfReference, QpdfObject?> resolver,
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

            AppendOutlineEntry(builder, outlines[i], false, request, resolver, governor, limits);
        }

        builder.Append((char)93);
    }
    private static void AppendPages(
        StringBuilder builder,
        QpdfDocument document,
        List<OutlineEntry> outlines,
        JsonRequest request,
        SortedDictionary<int, QpdfObject> labels,
        Func<PdfReference, QpdfObject?> resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PageLocation> pages = document.PageReferences;
        builder.Append((char)91);
        for (int i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
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
                PdfJsonWriter.AppendValue(builder, location.Page, request.Version, resolver, governor, 0, limits);
            }

            builder.Append(",\"images\": [],\"contents\": ");
            AppendContents(builder, location.Page, request, resolver, governor, limits);
            builder.Append(",\"label\": ");
            QpdfObject? label = LabelForPage(labels, i);
            if (label is null || label is PdfNull)
            {
                builder.Append("null");
            }
            else
            {
                PdfJsonWriter.AppendValue(builder, label, request.Version, resolver, governor, 0, limits);
            }

            builder.Append(",\"outlines\": ");
            AppendPageOutlines(builder, outlines, i + 1, request, resolver, governor, limits);
            builder.Append(",\"pageposfrom1\": ");
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append((char)125);
        }

        builder.Append((char)93);
    }

    private static void AppendContents(
        StringBuilder builder,
        PdfDictionary page,
        JsonRequest request,
        Func<PdfReference, QpdfObject?> resolver,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        if (!page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null || contents is PdfNull)
        {
            builder.Append("[]");
            return;
        }

        builder.Append((char)91);
        if (contents is PdfArray array)
        {
            QpdfObject[] items = array.Items;
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append((char)44);
                }

                PdfJsonWriter.AppendValue(builder, items[i], request.Version, resolver, governor, 0, limits);
            }
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, contents, request.Version, resolver, governor, 0, limits);
        }

        builder.Append((char)93);
    }
    private static void AppendPageOutlines(
        StringBuilder builder,
        List<OutlineEntry> outlines,
        int pageposfrom1,
        JsonRequest request,
        Func<PdfReference, QpdfObject?> resolver,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        builder.Append((char)91);
        bool first = true;
        foreach (OutlineEntry entry in outlines)
        {
            if (entry.DestinationPage == pageposfrom1)
            {
                if (!first)
                {
                    builder.Append((char)44);
                }

                first = false;
                AppendOutlineEntry(builder, entry, true, request, resolver, governor, limits);
            }
        }

        builder.Append((char)93);
    }

    private static void AppendEncrypt(StringBuilder builder, int version)
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
        StringBuilder builder,
        DocumentEngine engine,
        JsonRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(engine, limits, governor, diagnostics, cancellationToken);
        builder.Append((char)123);
        bool firstAttachment = true;
        foreach (EmbeddedAttachment attachment in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (!firstAttachment)
            {
                builder.Append((char)44);
            }

            firstAttachment = false;
            PdfJsonWriter.AppendJsonString(builder, attachment.Key);
            builder.Append(": {\"filespec\": ");
            if (attachment.SpecReference is null)
            {
                builder.Append("null");
            }
            else
            {
                builder.Append((char)34);
                builder.Append(attachment.SpecReference);
                builder.Append((char)34);
            }

            builder.Append(",\"preferredname\": ");
            PdfJsonWriter.AppendJsonString(builder, attachment.FileName);
            builder.Append(",\"preferredcontents\": ");
            if (attachment.StreamReference is null)
            {
                builder.Append("null");
            }
            else
            {
                builder.Append((char)34);
                builder.Append(attachment.StreamReference);
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
            AppendAttachmentNames(builder, attachment, engine);
            builder.Append(",\"streams\": ");
            AppendAttachmentStreams(builder, engine, attachment, governor, limits);
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

    private static void AppendAttachmentNames(StringBuilder builder, EmbeddedAttachment attachment, DocumentEngine engine)
    {
        builder.Append((char)123);
        bool first = true;
        PdfDictionary? spec = AttachmentSpec(engine, attachment);
        if (spec is not null)
        {
            foreach (string name in NameKeys)
            {
                if (spec.TryGet(PdfName.FromAscii(name), out QpdfObject? raw) && raw is PdfByteString text)
                {
                    if (!first)
                    {
                        builder.Append((char)44);
                    }

                    first = false;
                    PdfJsonWriter.AppendJsonString(builder, name);
                    builder.Append(": ");
                    PdfJsonWriter.AppendJsonString(builder, PdfText.Decode(text.Value));
                }
            }
        }

        builder.Append((char)125);
    }

    private static void AppendAttachmentStreams(
        StringBuilder builder,
        DocumentEngine engine,
        EmbeddedAttachment attachment,
        ResourceGovernor governor,
        QpdfLimits limits)
    {
        builder.Append((char)123);
        bool first = true;
        PdfDictionary? spec = AttachmentSpec(engine, attachment);
        if (spec is not null
            && spec.TryGet(PdfName.FromAscii("EF"), out QpdfObject? efRaw)
            && efRaw is not null
            && efRaw is not PdfNull)
        {
            QpdfObject efTarget = efRaw;
            if (efRaw is PdfReference efReference)
            {
                if (((IIndirectResolver)engine).TryResolve(efReference, out QpdfObject? resolved) && resolved is not null && resolved is not PdfNull)
                {
                    efTarget = resolved;
                }
            }

            if (efTarget is PdfDictionary ef)
            {
                foreach (string name in NameKeys)
                {
                    if (!ef.TryGet(PdfName.FromAscii(name), out QpdfObject? candidate) || candidate is null || candidate is PdfNull)
                    {
                        continue;
                    }

                    QpdfObject target = candidate;
                    if (candidate is PdfReference streamReference)
                    {
                        if (!((IIndirectResolver)engine).TryResolve(streamReference, out QpdfObject? resolved) || resolved is null)
                        {
                            continue;
                        }

                        target = resolved;
                    }

                    if (target is not PdfStream stream)
                    {
                        continue;
                    }

                    governor.ChargeWork(1L);
                    if (!first)
                    {
                        builder.Append((char)44);
                    }

                    first = false;
                    PdfJsonWriter.AppendJsonString(builder, name);
                    builder.Append(": ");
                    AppendEmbeddedStream(builder, engine, stream);
                }
            }
        }

        builder.Append((char)125);
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

    private static void AppendEmbeddedStream(StringBuilder builder, DocumentEngine engine, PdfStream stream)
    {
        string creation = string.Empty;
        string checksum = string.Empty;
        string mime = string.Empty;
        if (stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtypeRaw) && subtypeRaw is PdfName subtype)
        {
            mime = subtype.DisplayText;
        }

        PdfDictionary? parms = null;
        if (stream.Dictionary.TryGet(PdfName.FromAscii("Params"), out QpdfObject? parmsRaw) && parmsRaw is not null && parmsRaw is not PdfNull)
        {
            parms = TryResolve(engine, parmsRaw) as PdfDictionary;
        }

        if (parms is not null)
        {
            if (parms.TryGet(PdfName.FromAscii("CreationDate"), out QpdfObject? creationRaw) && creationRaw is PdfByteString creationText)
            {
                creation = PdfText.Decode(creationText.Value);
            }

            if (parms.TryGet(PdfName.FromAscii("CheckSum"), out QpdfObject? checksumRaw) && checksumRaw is PdfByteString checksumBytes)
            {
                checksum = Convert.ToHexStringLower(checksumBytes.Value);
            }
        }

        builder.Append("{\"creationdate\": ");
        AppendIsoDate(builder, creation);
        builder.Append(",\"modificationdate\": ");
        AppendIsoDate(builder, creation);
        builder.Append(",\"mimetype\": ");
        if (mime.Length == 0)
        {
            builder.Append("null");
        }
        else
        {
            PdfJsonWriter.AppendJsonString(builder, mime);
        }

        builder.Append(",\"checksum\": ");
        if (checksum.Length == 0)
        {
            builder.Append("null");
        }
        else
        {
            PdfJsonWriter.AppendJsonString(builder, checksum);
        }

        builder.Append((char)125);
    }

    internal static void AppendIsoDate(StringBuilder builder, string pdfDate)
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
        if (pdfDate is null || pdfDate.Length < 16)
        {
            return null;
        }

        if (!pdfDate.StartsWith("D:", StringComparison.Ordinal))
        {
            return null;
        }

        string digits = pdfDate.Substring(2);
        if (digits.Length < 14)
        {
            return null;
        }

        foreach (char c in digits.Substring(0, 14))
        {
            if (c < 48 || c > 57)
            {
                return null;
            }
        }

        string rest = digits.Substring(14);
        int tzMinutes = 0;
        if (rest.Length == 0)
        {
            tzMinutes = 0;
        }
        else if (rest == "Z")
        {
            tzMinutes = 0;
        }
        else
        {
            if (rest.Length != 6 || (rest[0] != 43 && rest[0] != 45) || rest[3] != 39 || rest[5] != 39)
            {
                return null;
            }

            if (!IsTwoDigits(rest, 1) || !IsTwoDigits(rest, 4))
            {
                return null;
            }

            tzMinutes = ((rest[1] - 48) * 10 + (rest[2] - 48)) * 60 + ((rest[4] - 48) * 10 + (rest[5] - 48));
            if (rest[0] == 43)
            {
                tzMinutes = -tzMinutes;
            }
        }

        string suffix = tzMinutes == 0
            ? "Z"
            : (tzMinutes < 0 ? "+" : "-") + Two(Math.Abs(tzMinutes) / 60) + ":" + Two(Math.Abs(tzMinutes) % 60);
        return digits.Substring(0, 4) + "-" + digits.Substring(4, 2) + "-" + digits.Substring(6, 2)
            + "T" + digits.Substring(8, 2) + ":" + digits.Substring(10, 2) + ":" + digits.Substring(12, 2)
            + suffix;
    }

    private static bool IsTwoDigits(string text, int at)
    {
        return text[at] >= 48 && text[at] <= 57 && text[at + 1] >= 48 && text[at + 1] <= 57;
    }

    private static string Two(int value)
    {
        return value < 10 ? "0" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
    }
    private static void AppendObjects(
        StringBuilder builder,
        QpdfDocument document,
        JsonRequest request,
        bool touchesPages,
        List<JsonSidecar> sidecars,
        Func<PdfReference, QpdfObject?> resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        DocumentEngine engine = document.Engine;
        List<int> numbers = new();
        int maxObjectId = 0;
        foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            if (entry.Value.Kind == XrefEntryKind.Free)
            {
                continue;
            }

            numbers.Add(entry.Key);
            if (entry.Key > maxObjectId)
            {
                maxObjectId = entry.Key;
            }
        }

        numbers.Sort();
        if (request.Version == 1)
        {
            builder.Append((char)123);
            bool first = true;
            foreach (int number in numbers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                int generation = engine.XRefEntries[number].Kind == XrefEntryKind.Compressed
                    ? 0
                    : engine.XRefEntries[number].Second;
                if (!request.WantsObject("obj:" + number.ToString(CultureInfo.InvariantCulture) + " " + generation.ToString(CultureInfo.InvariantCulture) + " R"))
                {
                    continue;
                }

                QpdfObject value = engine.Load(number);
                if (!first)
                {
                    builder.Append((char)44);
                }

                first = false;
                PdfJsonWriter.AppendJsonString(builder, number.ToString(CultureInfo.InvariantCulture) + " " + generation.ToString(CultureInfo.InvariantCulture));
                builder.Append(": ");
                PdfJsonWriter.AppendValue(builder, value, 1, resolver, governor, 0, limits);
            }

            if (request.WantsObject("trailer"))
            {
                if (!first)
                {
                    builder.Append((char)44);
                }

                builder.Append("\"trailer\": ");
                PdfJsonWriter.AppendValue(builder, engine.Trailer, 1, resolver, governor, 0, limits);
            }

            builder.Append((char)125);
            return;
        }

        builder.Append("[{");
        builder.Append("\"jsonversion\": 2,\"pdfversion\": ");
        PdfJsonWriter.AppendJsonString(builder, document.HeaderVersion.Major.ToString(CultureInfo.InvariantCulture) + "." + document.HeaderVersion.Minor.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"pushedinheritedpageresources\": false,\"calledgetallpages\": ");
        builder.Append(touchesPages ? "true" : "false");
        builder.Append(",\"maxobjectid\": ");
        builder.Append(maxObjectId.ToString(CultureInfo.InvariantCulture));
        builder.Append("}, {");
        bool firstObject = true;
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            int generation = engine.XRefEntries[number].Kind == XrefEntryKind.Compressed
                ? 0
                : engine.XRefEntries[number].Second;
            if (!request.WantsObject("obj:" + number.ToString(CultureInfo.InvariantCulture) + " " + generation.ToString(CultureInfo.InvariantCulture) + " R"))
            {
                continue;
            }

            QpdfObject value = engine.Load(number);
            if (!firstObject)
            {
                builder.Append((char)44);
            }

            firstObject = false;
            PdfJsonWriter.AppendJsonString(builder, "obj:" + number.ToString(CultureInfo.InvariantCulture) + " " + generation.ToString(CultureInfo.InvariantCulture) + " R");
            builder.Append(": ");
            if (value is PdfStream stream)
            {
                AppendJsonStream(builder, engine, number, stream, request, sidecars, resolver, governor, limits, cancellationToken);
            }
            else
            {
                builder.Append("{\"value\": ");
                PdfJsonWriter.AppendValue(builder, value, 2, resolver, governor, 0, limits);
                builder.Append((char)125);
            }
        }

        if (request.WantsObject("trailer"))
        {
            if (!firstObject)
            {
                builder.Append((char)44);
            }

            builder.Append("\"trailer\": {\"value\": ");
            PdfJsonWriter.AppendValue(builder, engine.Trailer, 2, resolver, governor, 0, limits);
            builder.Append((char)125);
        }

        builder.Append("}]");
    }

    private static (byte[] Data, bool Decoded) ResolveStreamPayload(
        DocumentEngine engine,
        PdfStream stream,
        string decodeLevel,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        byte[] raw = stream.Data;
        governor.ReserveMemory(raw.Length);
        if (decodeLevel == "none")
        {
            return (raw, false);
        }

        IReadOnlyList<Filters.FilterStage> stages;
        try
        {
            stages = Filters.FilterChain.Resolve(stream.Dictionary, engine);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            return (raw, false);
        }

        if (stages.Count == 0 || !Filters.FilterChain.AllStagesInLevel(stages, decodeLevel))
        {
            return (raw, false);
        }

        try
        {
            return (Filters.FilterChain.Decode(raw, stages, limits, governor), true);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            return (raw, false);
        }
    }

    private static PdfDictionary StreamJsonDictionary(PdfDictionary dictionary, bool decoded)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in dictionary.Keys)
        {
            string text = key.DisplayText;
            if (text == "Length" || (decoded && (text == "Filter" || text == "DecodeParms")))
            {
                continue;
            }

            if (dictionary.TryGet(key, out QpdfObject? found) && found is not null && found is not PdfNull)
            {
                entries[key] = found;
            }
        }

        return new PdfDictionary(entries);
    }

    private static void AppendJsonStream(
        StringBuilder builder,
        DocumentEngine engine,
        int number,
        PdfStream stream,
        JsonRequest request,
        List<JsonSidecar> sidecars,
        Func<PdfReference, QpdfObject?> resolver,
        ResourceGovernor governor,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Version == 1 || request.StreamData == "none")
        {
            builder.Append("{\"stream\": {\"dict\": ");
            PdfJsonWriter.AppendDictionary(builder, stream.Dictionary, request.Version, resolver, governor, 0, limits);
            builder.Append("}}");
            return;
        }

        (byte[] data, bool decoded) = ResolveStreamPayload(engine, stream, request.DecodeLevel, limits, governor);
        PdfDictionary slim = StreamJsonDictionary(stream.Dictionary, decoded);
        if (request.StreamData == "file")
        {
            string name = (request.StreamPrefix ?? string.Empty) + "-" + number.ToString(CultureInfo.InvariantCulture);
            if (data.Length > limits.MaxOutputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), data.Length, limits.MaxOutputBytes);
            }

            governor.ReserveMemory(data.Length);
            sidecars.Add(new JsonSidecar(name, data));
            builder.Append("{\"stream\": {\"datafile\": ");
            PdfJsonWriter.AppendJsonString(builder, name);
            builder.Append(",\"dict\": ");
            PdfJsonWriter.AppendDictionary(builder, slim, request.Version, resolver, governor, 0, limits);
            builder.Append("}}");
            return;
        }

        builder.Append("{\"stream\": {\"data\": ");
        PdfJsonWriter.AppendJsonString(builder, Convert.ToBase64String(data));
        builder.Append(",\"dict\": ");
        PdfJsonWriter.AppendDictionary(builder, slim, request.Version, resolver, governor, 0, limits);
        builder.Append("}}");
    }

    private static void AppendObjectInfo(
        StringBuilder builder,
        QpdfDocument document,
        JsonRequest request,
        Func<PdfReference, QpdfObject?> resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        DocumentEngine engine = document.Engine;
        List<int> numbers = new();
        foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            if (entry.Value.Kind != XrefEntryKind.Free)
            {
                numbers.Add(entry.Key);
            }
        }

        numbers.Sort();
        builder.Append((char)123);
        bool first = true;
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            int generation = engine.XRefEntries[number].Kind == XrefEntryKind.Compressed
                ? 0
                : engine.XRefEntries[number].Second;
            if (!request.WantsObject("obj:" + number.ToString(CultureInfo.InvariantCulture) + " " + generation.ToString(CultureInfo.InvariantCulture) + " R"))
            {
                continue;
            }

            QpdfObject value = engine.Load(number);
            if (!first)
            {
                builder.Append((char)44);
            }

            first = false;
            PdfJsonWriter.AppendJsonString(builder, number.ToString(CultureInfo.InvariantCulture) + " " + generation.ToString(CultureInfo.InvariantCulture));
            builder.Append(": {\"stream\": {\"is\": ");
            if (value is PdfStream stream)
            {
                builder.Append("true,\"length\": ");
                if (stream.Dictionary.TryGet(PdfName.FromAscii("Length"), out QpdfObject? length) && length is not null && length is not PdfNull)
                {
                    PdfJsonWriter.AppendValue(builder, DereferenceOnce(engine, length), request.Version, resolver, governor, 0, limits);
                }
                else
                {
                    builder.Append("null");
                }

                builder.Append(",\"filter\": ");
                if (stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject? filter) && filter is not null && filter is not PdfNull)
                {
                    PdfJsonWriter.AppendValue(builder, DereferenceOnce(engine, filter), request.Version, resolver, governor, 0, limits);
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
    private static string DecodeText(byte[] value)
    {
        if (value.Length >= 3 && value[0] == 0xEF && value[1] == 0xBB && value[2] == 0xBF)
        {
            return System.Text.Encoding.UTF8.GetString(value, 3, value.Length - 3);
        }

        return PdfText.Decode(value);
    }

    private static void AppendAcroform(
        StringBuilder builder,
        QpdfDocument document,
        JsonRequest request,
        Func<PdfReference, QpdfObject?> resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
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
            governor.ChargeWork(1L);
            if (!pages[i].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            QpdfObject annotsTarget = TryResolve(engine, annotsRaw) ?? annotsRaw;
            if (annotsTarget is not PdfArray annots)
            {
                continue;
            }

            foreach (QpdfObject item in annots.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                PdfReference? annotReference = item as PdfReference;
                QpdfObject node = TryResolve(engine, item) ?? item;
                if (node is not PdfDictionary annot)
                {
                    continue;
                }

                if (!annot.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtypeRaw) || subtypeRaw is not PdfName subtype || subtype.DisplayText != "Widget")
                {
                    continue;
                }

                AcroField field = ResolveAcroField(engine, annotReference, annot, limits, governor);
                if (!firstField)
                {
                    builder.Append((char)44);
                }

                firstField = false;
                AppendAcroField(builder, engine, field, annotReference, annot, i + 1, acroform, request, resolver, governor, limits);
            }
        }

        builder.Append("]}");
    }
    private sealed record AcroField(
        PdfReference? NodeReference,
        PdfDictionary Node,
        List<PdfDictionary> Ancestors,
        string FieldType,
        string QualifiedName);

    private static AcroField ResolveAcroField(
        DocumentEngine engine,
        PdfReference? annotReference,
        PdfDictionary annot,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        List<PdfDictionary> chain = new() { annot };
        List<PdfReference?> refs = new() { annotReference };
        HashSet<int> visited = new();
        if (annotReference is not null)
        {
            visited.Add(annotReference.ObjectNumber);
        }

        PdfDictionary current = annot;
        int depth = 0;
        while (depth < limits.MaxNestingDepth)
        {
            if (!current.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw) || parentRaw is null || parentRaw is PdfNull)
            {
                break;
            }

            PdfReference? parentReference = parentRaw as PdfReference;
            QpdfObject parent = TryResolve(engine, parentRaw) ?? parentRaw;
            if (parent is not PdfDictionary parentDict)
            {
                break;
            }

            if (parentReference is not null && !visited.Add(parentReference.ObjectNumber))
            {
                break;
            }

            governor.ChargeWork(1L);
            chain.Add(parentDict);
            refs.Add(parentReference);
            current = parentDict;
            depth += 1;
        }

        int fieldIndex = 0;
        for (int i = 0; i < chain.Count; i++)
        {
            if (chain[i].TryGet(PdfName.FromAscii("FT"), out QpdfObject? own) && own is not null && own is not PdfNull)
            {
                fieldIndex = i;
                break;
            }
        }

        string fieldType = string.Empty;
        for (int i = fieldIndex; i < chain.Count; i++)
        {
            if (chain[i].TryGet(PdfName.FromAscii("FT"), out QpdfObject? ftRaw) && ftRaw is not null && ftRaw is not PdfNull)
            {
                QpdfObject ft = TryResolve(engine, ftRaw) ?? ftRaw;
                if (ft is PdfName ftName)
                {
                    fieldType = "/" + ftName.DisplayText;
                    break;
                }
            }
        }

        List<string> parts = new();
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].TryGet(PdfName.FromAscii("T"), out QpdfObject? tRaw) && tRaw is not null && tRaw is not PdfNull)
            {
                QpdfObject t = TryResolve(engine, tRaw) ?? tRaw;
                if (t is PdfByteString text)
                {
                    parts.Add(DecodeText(text.Value));
                }
            }
        }

        return new AcroField(refs[fieldIndex], chain[fieldIndex], chain, fieldType, string.Join(".", parts));
    }

    private static QpdfObject? InheritedValue(DocumentEngine engine, List<PdfDictionary> chain, string key)
    {
        foreach (PdfDictionary node in chain)
        {
            if (node.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) && raw is not null && raw is not PdfNull)
            {
                return TryResolve(engine, raw) ?? raw;
            }
        }

        return null;
    }

    private static int InheritedInt(DocumentEngine engine, List<PdfDictionary> chain, string key)
    {
        return InheritedIntOrNull(engine, chain, key) ?? 0;
    }

    private static int? InheritedIntOrNull(DocumentEngine engine, List<PdfDictionary> chain, string key)
    {
        QpdfObject? found = InheritedValue(engine, chain, key);
        return found is PdfInteger integer ? (int?)Math.Min(Math.Max(integer.Value, (long)int.MinValue), (long)int.MaxValue) : null;
    }
    private static void AppendAcroField(
        StringBuilder builder,
        DocumentEngine engine,
        AcroField field,
        PdfReference? annotReference,
        PdfDictionary annot,
        int pageposfrom1,
        PdfDictionary? acroform,
        JsonRequest request,
        Func<PdfReference, QpdfObject?> resolver,
        ResourceGovernor governor,
        QpdfLimits limits)
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
            PdfJsonWriter.AppendValue(builder, field.Node, request.Version, resolver, governor, 0, limits);
        }

        builder.Append(",\"parent\": ");
        if (field.Node.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw) && parentRaw is not null && parentRaw is not PdfNull)
        {
            PdfJsonWriter.AppendValue(builder, parentRaw, request.Version, resolver, governor, 0, limits);
        }
        else
        {
            builder.Append("null");
        }

        builder.Append(",\"pageposfrom1\": ");
        builder.Append(pageposfrom1.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"fieldtype\": ");
        PdfJsonWriter.AppendJsonString(builder, field.FieldType);
        int flags = InheritedInt(engine, field.Ancestors, "Ff");
        builder.Append(",\"fieldflags\": ");
        builder.Append(flags.ToString(CultureInfo.InvariantCulture));
        string partial = string.Empty;
        if (field.Node.TryGet(PdfName.FromAscii("T"), out QpdfObject? ownRaw) && ownRaw is not null && ownRaw is not PdfNull)
        {
            QpdfObject own = TryResolve(engine, ownRaw) ?? ownRaw;
            if (own is PdfByteString ownText)
            {
                partial = DecodeText(ownText.Value);
            }
        }

        builder.Append(",\"fullname\": ");
        PdfJsonWriter.AppendJsonString(builder, field.QualifiedName);
        builder.Append(",\"partialname\": ");
        PdfJsonWriter.AppendJsonString(builder, partial);
        string alternative = field.QualifiedName;
        QpdfObject? tu = InheritedValue(engine, field.Ancestors, "TU");
        if (tu is PdfByteString tuText)
        {
            alternative = DecodeText(tuText.Value);
        }

        builder.Append(",\"alternativename\": ");
        PdfJsonWriter.AppendJsonString(builder, alternative);
        string mapping = alternative;
        QpdfObject? tm = InheritedValue(engine, field.Ancestors, "TM");
        if (tm is PdfByteString tmText)
        {
            mapping = DecodeText(tmText.Value);
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
            PdfJsonWriter.AppendValue(builder, value, request.Version, resolver, governor, 0, limits);
        }

        builder.Append(",\"defaultvalue\": ");
        QpdfObject? defaultValue = InheritedValue(engine, field.Ancestors, "DV");
        if (defaultValue is null)
        {
            builder.Append("null");
        }
        else
        {
            PdfJsonWriter.AppendValue(builder, defaultValue, request.Version, resolver, governor, 0, limits);
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
        builder.Append(quad.ToString(CultureInfo.InvariantCulture));
        bool isText = field.FieldType == "/Tx";
        bool isChoice = field.FieldType == "/Ch";
        bool isRadio = field.FieldType == "/Btn" && (flags & 32768) == 32768;
        bool isCheckbox = field.FieldType == "/Btn" && (flags & (32768 | 65536)) == 0;
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
                foreach (QpdfObject item in optArray.Items)
                {
                    QpdfObject entry = item is PdfReference itemReference ? TryResolve(engine, itemReference) ?? item : item;
                    string? choice = null;
                    if (entry is PdfByteString entryText)
                    {
                        choice = DecodeText(entryText.Value);
                    }
                    else if (entry is PdfArray pair && pair.Count == 2)
                    {
                        QpdfObject display = pair[1] is PdfReference displayReference ? TryResolve(engine, displayReference) ?? pair[1] : pair[1];
                        if (display is PdfByteString displayText)
                        {
                            choice = DecodeText(displayText.Value);
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
            PdfJsonWriter.AppendValue(builder, annot, request.Version, resolver, governor, 0, limits);
        }

        string appearance = string.Empty;
        if (annot.TryGet(PdfName.FromAscii("AS"), out QpdfObject? asRaw) && asRaw is not null && asRaw is not PdfNull)
        {
            QpdfObject asTarget = TryResolve(engine, asRaw) ?? asRaw;
            if (asTarget is PdfName asName)
            {
                appearance = "/" + asName.DisplayText;
            }
        }

        builder.Append(",\"appearancestate\": ");
        PdfJsonWriter.AppendJsonString(builder, appearance);
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
        builder.Append(annotFlags.ToString(CultureInfo.InvariantCulture));
        builder.Append("}}");
    }
}