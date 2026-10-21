using System.Globalization;
using System.Text;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class GenerateAppearances
{
    private const int FlagCombo = 131072;

    internal static int Apply(
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        IReadOnlyDictionary<GraphCopier, PdfDictionary?> formResources,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        HashSet<int>? reachedAnnots,
        bool warnUnreachable,
        int allocator)
    {
        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
        }
        if (formResources is null)
        {
            throw new ArgumentNullException(nameof(formResources));
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
        HashSet<int> generated = new();
        foreach ((GraphCopier copier, int pageId) in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary page)
            {
                continue;
            }

            if (!page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            PoolResolver resolver = new(copier.CopiedObjects);
            QpdfObject annotsTarget = annotsRaw is PdfReference annotsReference
                ? ResolveOrNull(resolver, annotsReference) ?? annotsRaw
                : annotsRaw;
            if (annotsTarget is not PdfArray annots)
            {
                continue;
            }

            bool pageChanged = false;
            List<QpdfObject> entries = new(annots.Count);
            for (int index = 0; index < annots.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                QpdfObject item = annots[index];
                QpdfObject entryTarget = item is PdfReference itemReference
                    ? ResolveOrNull(resolver, itemReference) ?? item
                    : item;
                if (entryTarget is not PdfDictionary annot)
                {
                    entries.Add(item);
                    continue;
                }

                if (!IsWidget(annot))
                {
                    entries.Add(item);
                    continue;
                }

                int? annotId = (item as PdfReference)?.ObjectNumber;
                if (annotId.HasValue && !generated.Add(annotId.Value))
                {
                    entries.Add(item);
                    continue;
                }

                if (!IsReached(annotId, reachedAnnots, warnUnreachable, diagnostics))
                {
                    entries.Add(item);
                    continue;
                }

                FieldContext field = ResolveField(copier, annot, annotId, resolver);
                if (field.Type == "Btn")
                {
                    if (SyncButton(copier, annot, item, annotId, field, resolver, entries, diagnostics, governor, cancellationToken))
                    {
                        pageChanged = true;
                    }
                    else
                    {
                        entries.Add(item);
                    }

                    continue;
                }

                if (field.Type != "Tx" && field.Type != "Ch")
                {
                    entries.Add(item);
                    continue;
                }

                PdfDictionary? formResourcesForCopier = null;
                if (formResources.TryGetValue(copier, out PdfDictionary? mappedResources))
                {
                    formResourcesForCopier = mappedResources;
                }
                if (GenerateText(copier, annot, item, annotId, field, resolver, entries, limits, governor, diagnostics, cancellationToken, formResourcesForCopier, ref allocator))
                {
                    pageChanged = true;
                }
            }

            if (pageChanged)
            {
                PdfArray rebuilt = new(entries);
                if (annotsRaw is PdfReference annotsOriginal)
                {
                    copier.ReplaceCopiedObject(annotsOriginal.ObjectNumber, rebuilt);
                }
                else
                {
                    Dictionary<PdfName, QpdfObject> pageEntries = new();
                    foreach (PdfName key in page.Keys)
                    {
                        if (page.TryGet(key, out QpdfObject? found) && found is not null)
                        {
                            pageEntries[key] = found;
                        }
                    }

                    governor.ReserveMemory(64L);
                    pageEntries[PdfName.FromAscii("Annots")] = rebuilt;
                    copier.ReplaceCopiedObject(pageId, new PdfDictionary(pageEntries));
                }
            }
        }

        return allocator;
    }
    /// <summary>
    /// Reports whether a widget keeps generation under the shared reachability rule:
    /// a null set means the output form carries no field registrations, so every widget
    /// stays untouched like the reference; otherwise only reached widgets generate, with
    /// whole-file paths warning once per unreached widget and selection paths skipping silently.
    /// </summary>
    private static bool IsReached(int? annotId, HashSet<int>? reachedAnnots, bool warnUnreachable, DiagnosticBag diagnostics)
    {
        if (reachedAnnots is null)
        {
            return false;
        }

        if (annotId.HasValue && reachedAnnots.Contains(annotId.Value))
        {
            return true;
        }

        if (warnUnreachable)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "A widget annotation is not reachable from the document form; generating its appearance.");
        }

        return warnUnreachable;
    }

    private sealed record FieldContext(
        string Type,
        long Flags,
        QpdfObject? Value,
        ReadOnlyMemory<byte>? DefaultAppearance,
        PdfDictionary? Node,
        int? NodeId);

    private static bool IsWidget(PdfDictionary annot)
    {
        return annot.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtype)
            && subtype is PdfName name
            && name.DisplayText == "Widget";
    }

    private static FieldContext ResolveField(
        GraphCopier copier,
        PdfDictionary annot,
        int? annotId,
        PoolResolver resolver)
    {
        List<(PdfDictionary Node, int? Id)> chain = FindFieldChain(copier, annot, annotId, resolver);
        string type = string.Empty;
        long flags = 0L;
        QpdfObject? value = null;
        ReadOnlyMemory<byte>? appearance = null;
        PdfDictionary? node = null;
        int? nodeId = null;
        bool flagsFound = false;
        foreach ((PdfDictionary current, int? currentId) in chain)
        {
            if (type.Length == 0 && current.TryGet(PdfName.FromAscii("FT"), out QpdfObject? typeRaw) && typeRaw is PdfName typeName)
            {
                type = typeName.DisplayText;
                node = current;
                nodeId = currentId;
            }

            if (value is null && current.TryGet(PdfName.FromAscii("V"), out QpdfObject? valueRaw) && valueRaw is not null && valueRaw is not PdfNull)
            {
                value = valueRaw;
            }

            if (appearance is null && current.TryGet(PdfName.FromAscii("DA"), out QpdfObject? appearanceRaw) && appearanceRaw is PdfByteString appearanceString)
            {
                appearance = appearanceString.ValueMemory;
            }

            if (!flagsFound && current.TryGet(PdfName.FromAscii("Ff"), out QpdfObject? flagsRaw) && flagsRaw is not null && flagsRaw is not PdfNull)
            {
                QpdfObject flagsTarget = flagsRaw is PdfReference flagsReference
                    ? ResolveOrNull(resolver, flagsReference) ?? flagsRaw
                    : flagsRaw;
                if (flagsTarget is PdfInteger flagsInteger)
                {
                    flags = flagsInteger.Value;
                    flagsFound = true;
                }
            }
        }

        return new FieldContext(type, flags, value, appearance, node, nodeId);
    }

    private static List<(PdfDictionary Node, int? Id)> FindFieldChain(
        GraphCopier copier,
        PdfDictionary annot,
        int? annotId,
        PoolResolver resolver)
    {
        List<(PdfDictionary Node, int? Id)> chain = new()
        {
            (annot, annotId),
        };
        HashSet<int> visited = new();
        if (annotId.HasValue)
        {
            visited.Add(annotId.Value);
        }

        // Follow preserved /Parent links first like the reference: a widget whose
        // field holds FT/V/DA without a /Kids back-link still resolves outward.
        PdfDictionary? cursor = annot;
        int parentDepth = 0;
        while (parentDepth < 32 && cursor is not null)
        {
            parentDepth += 1;
            if (!cursor.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw) || parentRaw is null || parentRaw is PdfNull)
            {
                break;
            }

            if (parentRaw is PdfReference parentReference)
            {
                if (!visited.Add(parentReference.ObjectNumber))
                {
                    break;
                }

                if (ResolveOrNull(resolver, parentReference) is not PdfDictionary parentDict)
                {
                    break;
                }

                chain.Add((parentDict, parentReference.ObjectNumber));
                cursor = parentDict;
                continue;
            }

            if (parentRaw is PdfDictionary parentDirect)
            {
                bool seen = false;
                foreach ((PdfDictionary node, int? _) in chain)
                {
                    if (ReferenceEquals(node, parentDirect))
                    {
                        seen = true;
                        break;
                    }
                }

                if (seen)
                {
                    break;
                }

                chain.Add((parentDirect, null));
                cursor = parentDirect;
                continue;
            }

            break;
        }

        int? current = chain[chain.Count - 1].Id;
        PdfDictionary? direct = chain[chain.Count - 1].Id.HasValue ? null : chain[chain.Count - 1].Node;
        int depth = 0;
        while (depth < 32)
        {
            depth += 1;
            bool found = false;
            foreach (KeyValuePair<int, QpdfObject> entry in copier.CopiedObjects)
            {
                if (entry.Value is not PdfDictionary candidate)
                {
                    continue;
                }

                if (!candidate.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) || kidsRaw is null || kidsRaw is PdfNull)
                {
                    continue;
                }

                QpdfObject kidsTarget = kidsRaw is PdfReference kidsReference
                    ? ResolveOrNull(resolver, kidsReference) ?? kidsRaw
                    : kidsRaw;
                if (kidsTarget is not PdfArray kids)
                {
                    continue;
                }

                bool contains = false;
                foreach (QpdfObject kid in kids.Items)
                {
                    if (current.HasValue && kid is PdfReference kidReference && kidReference.ObjectNumber == current.Value)
                    {
                        contains = true;
                        break;
                    }

                    if (!current.HasValue && direct is not null && ReferenceEquals(kid, direct))
                    {
                        contains = true;
                        break;
                    }
                }

                if (!contains || !visited.Add(entry.Key))
                {
                    continue;
                }

                chain.Add((candidate, entry.Key));
                current = entry.Key;
                direct = null;
                found = true;
                break;
            }

            if (!found)
            {
                break;
            }
        }

        return chain;
    }
    private static List<string> ReadChoices(PdfDictionary field, PoolResolver resolver, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        List<string> choices = ParserContainers.CreateList<string>(memory);
        if (!field.TryGet(PdfName.FromAscii("Opt"), out QpdfObject? opt) || opt is null || opt is PdfNull)
        {
            return choices;
        }

        QpdfObject optTarget = opt is PdfReference optReference
            ? ResolveOrNull(resolver, optReference) ?? opt
            : opt;
        if (optTarget is not PdfArray array)
        {
            return choices;
        }

        foreach (QpdfObject item in array.Items)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            QpdfObject entry = item is PdfReference entryReference
                ? ResolveOrNull(resolver, entryReference) ?? item
                : item;
            if (entry is PdfByteString text)
            {
                ParserContainers.AddItem(memory, choices, PdfText.Decode(text.ValueMemory, memory, governor, token), IntPtr.Size);
                continue;
            }

            if (entry is PdfArray pair && pair.Count == 2)
            {
                QpdfObject display = pair[1] is PdfReference displayReference
                    ? ResolveOrNull(resolver, displayReference) ?? pair[1]
                    : pair[1];
                if (display is PdfByteString displayText)
                {
                    ParserContainers.AddItem(memory, choices, PdfText.Decode(displayText.ValueMemory, memory, governor, token), IntPtr.Size);
                }
            }
        }

        return choices;
    }

    private static bool SyncButton(
        GraphCopier copier,
        PdfDictionary annot,
        QpdfObject item,
        int? annotId,
        FieldContext field,
        PoolResolver resolver,
        List<QpdfObject> entries,
        DiagnosticBag diagnostics,
        ResourceGovernor governor,
        CancellationToken token)
    {
        if (field.Value is not PdfName valueName)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot synchronize a button without a name value; keeping it as is.");
            return false;
        }

        bool radio = (field.Flags & 32768L) != 0L;
        bool pushbutton = (field.Flags & 65536L) != 0L;
        if (pushbutton)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Pushbutton values cannot be synchronized; keeping it as is.");
            return false;
        }

        if (radio)
        {
            // The reference leaves radio buttons untouched while checkboxes
            // synchronize, so radios degrade to skip on every path, with the
            // reference warning that it cannot set a radio button value.
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Radio button values cannot be synchronized; keeping it as is.");
            return false;
        }

        if (field.Node is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot update a field without a field node; keeping it as is.");
            return false;
        }
        bool merged = ReferenceEquals(field.Node, annot);
        if (!merged && !field.NodeId.HasValue)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot update a field embedded without identity; keeping it as is.");
            return false;
        }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfName selected = valueName.RawSpan.SequenceEqual("Off"u8) ? valueName
            : FirstOnState(annot, resolver, governor, token) ?? PdfDictionaryRebuild.CreateName(graph, "Yes"u8);
        PdfDictionary? updatedField = merged ? null : PdfDictionaryRebuild.ReplaceEntry(graph, field.Node, "V"u8, selected);
        PdfNameTable<QpdfObject> annotation = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        bool hasValue = false, hasState = false;
        using (PdfDictionary.EntryEnumerator source = annot.GetEntryEnumerator())
        {
            while (source.MoveNext(governor, token))
            {
                PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
                if (merged && key.RawSpan.SequenceEqual("V"u8)) { value = selected; hasValue = true; }
                if (key.RawSpan.SequenceEqual("AS"u8)) { value = selected; hasState = true; }
                annotation.Assign(graph.Memory, governor, key, value, token);
            }
        }
        if (merged && !hasValue) { annotation.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "V"u8), selected, token); }
        if (!hasState) { annotation.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "AS"u8), selected, token); }
        PdfDictionary updatedAnnot = PdfDictionaryRebuild.Complete(graph, annotation, allocation);
        if (field.NodeId is int fieldId) { copier.ValidateReplacement(fieldId); }
        if (annotId is int widgetId) { copier.ValidateReplacement(widgetId); }
        // Prepare both dictionaries before changing either graph slot. A merged
        // widget publishes V and AS together instead of overwriting its V update.
        graph.Retain();
        if (updatedField is not null && field.NodeId is int separateId) { copier.ReplaceCopiedObject(separateId, updatedField); }
        if (annotId is int id) { copier.ReplaceCopiedObject(id, updatedAnnot); entries.Add(item); }
        else { entries.Add(updatedAnnot); }
        return true;
    }

    private static PdfName? FirstOnState(PdfDictionary annot, PoolResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        if (!annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? ap) || ap is null || ap is PdfNull)
        {
            return null;
        }

        QpdfObject apTarget = ap is PdfReference apReference
            ? ResolveOrNull(resolver, apReference) ?? ap
            : ap;
        if (apTarget is not PdfDictionary apDict)
        {
            return null;
        }

        if (!apDict.TryGet(PdfName.FromAscii("N"), out QpdfObject? normal) || normal is null || normal is PdfNull)
        {
            return null;
        }

        QpdfObject normalTarget = normal is PdfReference normalReference
            ? ResolveOrNull(resolver, normalReference) ?? normal
            : normal;
        if (normalTarget is not PdfDictionary states)
        {
            return null;
        }

        using PdfDictionary.EntryEnumerator entries = states.GetEntryEnumerator();
        while (entries.MoveNext(governor, token))
        {
            PdfName key = entries.Current.Key;
            if (!key.RawSpan.SequenceEqual("Off"u8)) { return key; }
        }
        return null;
    }

    private static bool GenerateText(
        GraphCopier copier,
        PdfDictionary annot,
        QpdfObject item,
        int? annotId,
        FieldContext field,
        PoolResolver resolver,
        List<QpdfObject> entries,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        PdfDictionary? formResources,
        ref int allocator)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        GenAppearance foundAp = FindGenAppearance(annot, resolver);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
        PdfStream? found = foundAp.Stream;
        ReadOnlyMemory<byte> decoded;
        PdfDictionary appearanceDict;
        if (found is null)
        {
            double[] box = ReadBox(annot, "Rect", resolver) ?? [0.0, 0.0, 0.0, 0.0];
            double width = box[2] - box[0];
            double height = box[3] - box[1];
            // Fixed header/resource containers; numeric text is admitted separately.
            graph.Memory.Reserve(1024L); graph.Add(1024L);
            Dictionary<PdfName, QpdfObject> fresh = new()
            {
                [PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject"),
                [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form"),
                [PdfName.FromAscii("BBox")] = BoxArray([0.0, 0.0, width, height], graph),
                [PdfName.FromAscii("Resources")] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>
                {
                    [PdfName.FromAscii("ProcSet")] = new PdfArray([PdfName.FromAscii("PDF"), PdfName.FromAscii("Text")]),
                }),
            };
            scratch.Reserve(44L);
            byte[] initial = Encoding.ASCII.GetBytes("/Tx BMC\nEMC\n");
            appearanceDict = new PdfDictionary(fresh);
            decoded = initial;
        }
        else
        {
            ReadOnlyMemory<byte>? raw = DecodeAppearance(found, resolver, limits, governor, scratch, cancellationToken);
            if (raw is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot decode an appearance stream; keeping the field as is.");
                entries.Add(item);
                return false;
            }

            decoded = raw.Value;
            appearanceDict = found.Dictionary;
        }

        double[]? bbox = ReadBox(appearanceDict, "BBox", resolver);
        if (bbox is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot generate an appearance without a bounding box; keeping the field as is.");
            entries.Add(item);
            return false;
        }

        string valueText = string.Empty;
        if (field.Value is PdfByteString valueString)
        {
            valueText = PdfText.Decode(valueString.ValueMemory, scratch, governor, cancellationToken);
        }

        TfResult font = AnalyzeDa(field.DefaultAppearance ?? ReadOnlyMemory<byte>.Empty, limits, governor, scratch, cancellationToken);
        AppearanceEncoding encoding = SelectEncoding(appearanceDict, font.FontName, formResources, resolver, governor, cancellationToken);
        byte[] encodedValue = EncodeValue(valueText, encoding, scratch, governor, cancellationToken);
        List<byte[]> encodedOpt = ParserContainers.CreateList<byte[]>(scratch);
        if (field.Type == "Ch" && (field.Flags & FlagCombo) == 0L && field.Node is not null)
        {
            foreach (string choice in ReadChoices(field.Node, resolver, scratch, governor, cancellationToken))
            {
                ParserContainers.AddItem(scratch, encodedOpt, EncodeValue(choice, encoding, scratch, governor, cancellationToken), IntPtr.Size);
            }
        }

        byte[] built = FilterAppearance(decoded, font.Appearance, encodedValue, encodedOpt, font.Size, bbox, limits, governor, scratch, cancellationToken);
        PdfDictionary? mergedResources = TryMergeDrFont(appearanceDict, font.FontName, formResources, resolver, governor, cancellationToken);
        PdfNameTable<QpdfObject> streamEntries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using (PdfDictionary.EntryEnumerator source = appearanceDict.GetEntryEnumerator())
        {
            while (source.MoveNext(governor, cancellationToken))
            {
                PdfName key = source.Current.Key;
                if (PdfDictionaryLookup.NameEquals(key, "Filter"u8, governor, cancellationToken)
                    || PdfDictionaryLookup.NameEquals(key, "DecodeParms"u8, governor, cancellationToken)) { continue; }
                QpdfObject kept = mergedResources is not null && PdfDictionaryLookup.NameEquals(key, "Resources"u8, governor, cancellationToken)
                    ? mergedResources : source.Current.Value;
                streamEntries.Assign(graph.Memory, governor, key, kept, cancellationToken);
            }
        }
        streamEntries.Assign(graph.Memory, governor, PdfName.FromAscii("Type"), PdfName.FromAscii("XObject"), cancellationToken);
        streamEntries.Assign(graph.Memory, governor, PdfName.FromAscii("Subtype"), PdfName.FromAscii("Form"), cancellationToken);
        PdfDictionary header = PdfDictionaryRebuild.Complete(graph, streamEntries, allocation);
        long builtBytes = built.Length == 0 ? 0L : built.LongLength + GovernedBufferStream.ArrayOverheadBytes;
        scratch.TransferTo(graph.Memory, builtBytes); graph.Add(builtBytes);
        graph.Memory.Reserve(32L);
        PdfStream replacement = PdfStream.FromOwnedData(header, built); graph.Add(32L);
        PdfReference builtLink = copier.AddIndirectObjectWithId(replacement, allocator);
        allocator = checked(allocator + 1);
        PdfDictionary newAp = BuildAp(foundAp.Ap, builtLink, graph);
        PdfDictionary updatedAnnot = PdfDictionaryRebuild.ReplaceEntry(graph, annot, "AP"u8, newAp);
        graph.Retain();
        if (annotId.HasValue)
        {
            copier.ReplaceCopiedObject(annotId.Value, updatedAnnot);
            entries.Add(item);
        }
        else
        {
            entries.Add(updatedAnnot);
        }

        return true;
    }

    private readonly record struct GenAppearance(PdfStream? Stream, PdfDictionary? Ap);

    private static GenAppearance FindGenAppearance(PdfDictionary annot, PoolResolver resolver)
    {
        if (!annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? ap) || ap is null || ap is PdfNull)
        {
            return new GenAppearance(null, null);
        }

        QpdfObject apTarget = ap is PdfReference apReference
            ? ResolveOrNull(resolver, apReference) ?? ap
            : ap;
        if (apTarget is not PdfDictionary apDict)
        {
            return new GenAppearance(null, null);
        }

        if (!apDict.TryGet(PdfName.FromAscii("N"), out QpdfObject? normal) || normal is null || normal is PdfNull)
        {
            return new GenAppearance(null, apDict);
        }

        QpdfObject normalTarget = normal is PdfReference normalReference
            ? ResolveOrNull(resolver, normalReference) ?? normal
            : normal;
        if (normalTarget is PdfStream direct)
        {
            return new GenAppearance(direct, apDict);
        }

        if (normalTarget is not PdfDictionary states)
        {
            return new GenAppearance(null, apDict);
        }

        string state = string.Empty;
        if (annot.TryGet(PdfName.FromAscii("AS"), out QpdfObject? stateRaw) && stateRaw is PdfName stateName)
        {
            state = stateName.DisplayText;
        }

        if (state.Length == 0)
        {
            return new GenAppearance(null, apDict);
        }

        foreach (PdfName key in states.Keys)
        {
            if (key.DisplayText != state)
            {
                continue;
            }

            if (!states.TryGet(key, out QpdfObject? found) || found is null)
            {
                return new GenAppearance(null, apDict);
            }

            QpdfObject stateTarget = found is PdfReference foundReference
                ? ResolveOrNull(resolver, foundReference) ?? found
                : found;
            if (stateTarget is PdfStream stateStream)
            {
                return new GenAppearance(stateStream, apDict);
            }

            return new GenAppearance(null, apDict);
        }

        return new GenAppearance(null, apDict);
    }

    private static PdfDictionary BuildAp(PdfDictionary? ap, PdfReference link, OwnedAllocationBatch graph)
    {
        if (ap is not null) { return PdfDictionaryRebuild.ReplaceEntry(graph, ap, "N"u8, link); }
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        entries.Assign(graph.Memory, graph.Governor, PdfName.FromAscii("N"), link, graph.Token);
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }
    private static double[]? ReadBox(PdfDictionary dictionary, string key, PoolResolver resolver)
    {
        if (!dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        QpdfObject target = raw is PdfReference reference
            ? ResolveOrNull(resolver, reference) ?? raw
            : raw;
        if (target is not PdfArray array || array.Count != 4)
        {
            return null;
        }

        double[] numbers = new double[4];
        for (int i = 0; i < 4; i++)
        {
            QpdfObject item = array[i];
            if (item is PdfReference itemReference)
            {
                QpdfObject? resolved = ResolveOrNull(resolver, itemReference);
                if (resolved is null)
                {
                    return null;
                }

                item = resolved;
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

    private static PdfArray BoxArray(double[] values, OwnedAllocationBatch graph)
    {
        long bytes = checked(56L + IntPtr.Size * (long)values.Length);
        graph.Memory.Reserve(bytes); graph.Add(bytes);
        QpdfObject[] items = new QpdfObject[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
            string text = FormatDouble(values[i]);
            long itemBytes = checked(32L + GovernedText.StringBytes(text.Length));
            graph.Memory.Reserve(itemBytes); graph.Add(itemBytes);
            items[i] = new PdfReal(text, values[i]);
        }
        return PdfArray.FromOwnedItems(items);
    }

    private static ReadOnlyMemory<byte>? DecodeAppearance(
        PdfStream stream,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        FilterPlan stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, resolver, governor, cancellationToken);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            return null;
        }
        using FilterPlan stageOwner = stages;

        if (stages.Count == 0)
        {
            if (stream.RawMemory.Length > limits.MaxDecodedBytes)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), stream.RawMemory.Length, limits.MaxDecodedBytes); }
            return stream.RawMemory;
        }

        foreach (FilterStage stage in stages)
        {
            if (!IsDecodable(stage.Name))
            {
                return null;
            }
        }

        try
        {
            return FilterChain.Decode(stream.ArrayData(memory, governor, cancellationToken), stages, limits, governor, memory, cancellationToken);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            return null;
        }
    }

    private static bool IsDecodable(string filter)
    {
        return filter == FlateFilter.FilterName
            || filter == LzwFilter.FilterName
            || filter == AsciiHexFilter.FilterName
            || filter == Ascii85Filter.FilterName
            || filter == RunLengthFilter.FilterName;
    }

    internal readonly record struct TfResult(double Size, byte[] FontName, ReadOnlyMemory<byte> Appearance);

    internal static TfResult AnalyzeDa(ReadOnlyMemory<byte> appearance, QpdfLimits limits, ResourceGovernor governor,
        ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        try
        {
            ContentTokenizer tokenizer = new(appearance, limits, governor, cancellationToken);
            double size = 11.0, lastNumber = 0.0, stored = 0.0;
            int numberOffset = -1, numberLength = 0, sizeOffset = -1, sizeLength = 0;
            ReadOnlyMemory<byte> lastName = ReadOnlyMemory<byte>.Empty, font = ReadOnlyMemory<byte>.Empty;
            while (tokenizer.TryReadToken(out ContentToken token))
            {
                governor.NoteOperation();
                if (token.Kind is ContentTokenKind.Integer or ContentTokenKind.Real)
                {
                    lastNumber = double.TryParse(token.Data.Span, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0.0;
                    numberOffset = checked((int)token.Offset); numberLength = token.Data.Length;
                }
                else if (token.Kind == ContentTokenKind.Name)
                {
                    lastName = token.Data;
                }
                else if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("Tf"u8))
                {
                    if (lastNumber > 1.0 && lastNumber < 1000.0)
                    {
                        size = lastNumber;
                    }

                    sizeOffset = numberOffset; sizeLength = numberLength;
                    stored = lastNumber; font = lastName;
                }
            }
            ReadOnlyMemory<byte> rebuilt = appearance;
            if (sizeOffset >= 0 && Math.Abs(stored - size) > 0.001)
            {
                string replacement = FormatDouble(size); // A bounded font-size scalar.
                int length = checked(appearance.Length - sizeLength + replacement.Length);
                memory.Reserve(32L + length);
                byte[] bytes = new byte[length];
                GovernedBufferStream.CopyBytes(appearance.Span[..sizeOffset], bytes.AsSpan(0, sizeOffset), governor, cancellationToken, CancellationToken.None);
                Encoding.ASCII.GetBytes(replacement, bytes.AsSpan(sizeOffset, replacement.Length));
                GovernedBufferStream.CopyBytes(appearance.Span[(sizeOffset + sizeLength)..], bytes.AsSpan(sizeOffset + replacement.Length), governor, cancellationToken, CancellationToken.None);
                rebuilt = bytes;
            }
            PdfNameBytes decoded = PdfNameBytes.Decode(font.Span, governor, cancellationToken);
            byte[] name = Array.Empty<byte>();
            if (decoded.Length > 0)
            {
                memory.Reserve(32L + decoded.Length);
                name = new byte[decoded.Length]; decoded.CopyTo(name, governor, cancellationToken);
            }
            return new(size, name, rebuilt);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
        }

        return new(11.0, Array.Empty<byte>(), appearance);
    }

    private static PdfName? FindFontEncoding(QpdfObject? resourcesRaw, ReadOnlySpan<byte> fontName,
        PoolResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        QpdfObject? resources = resourcesRaw is PdfReference link ? ResolveOrNull(resolver, link) : resourcesRaw;
        if (resources is PdfDictionary dictionary
            && TryGetFontRaw(dictionary, fontName, resolver, governor, token) is QpdfObject fontRaw)
        {
            QpdfObject? font = fontRaw is PdfReference fontLink ? ResolveOrNull(resolver, fontLink) : fontRaw;
            if (font is PdfDictionary fontDictionary
                && PdfDictionaryLookup.TryGet(fontDictionary, "Encoding"u8, governor, token, out QpdfObject encoding)
                && encoding is PdfName name) { return name; }
        }
        return null;
    }

    private static PdfDictionary? TryMergeDrFont(PdfDictionary appearance, ReadOnlySpan<byte> fontName,
        PdfDictionary? formResources, PoolResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        if (fontName.IsEmpty || formResources is null
            || !PdfDictionaryLookup.TryGet(appearance, "Resources"u8, governor, token, out QpdfObject resourcesRaw)) { return null; }
        QpdfObject? resources = resourcesRaw is PdfReference link ? ResolveOrNull(resolver, link) : resourcesRaw;
        if (resources is not PdfDictionary appearanceResources
            || TryGetFontRaw(appearanceResources, fontName, resolver, governor, token) is not null
            || TryGetFontRaw(formResources, fontName, resolver, governor, token) is not QpdfObject drFont) { return null; }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfNameTable<QpdfObject> fonts = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        if (PdfDictionaryLookup.TryGet(appearanceResources, "Font"u8, governor, token, out QpdfObject fontsRaw))
        {
            QpdfObject? target = fontsRaw is PdfReference fontLink ? ResolveOrNull(resolver, fontLink) : fontsRaw;
            if (target is PdfDictionary fontDictionary)
            {
                using PdfDictionary.EntryEnumerator entries = fontDictionary.GetEntryEnumerator();
                while (entries.MoveNext(governor, token))
                { fonts.Assign(graph.Memory, governor, entries.Current.Key, entries.Current.Value, token); }
            }
        }
        fonts.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, fontName), drFont, token);
        PdfDictionary mergedFonts = PdfDictionaryRebuild.Complete(graph, fonts, allocation);
        PdfDictionary merged = PdfDictionaryRebuild.ReplaceEntry(graph, appearanceResources, "Font"u8, mergedFonts);
        graph.Retain(); return merged;
    }

    private static QpdfObject? TryGetFontRaw(PdfDictionary resources, ReadOnlySpan<byte> fontName,
        PoolResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(resources, "Font"u8, governor, token, out QpdfObject fontsRaw)) { return null; }
        QpdfObject? target = fontsRaw is PdfReference link ? ResolveOrNull(resolver, link) : fontsRaw;
        return target is PdfDictionary fonts
            && PdfDictionaryLookup.TryGet(fonts, fontName, governor, token, out QpdfObject font)
            && font is not PdfNull ? font : null;
    }
    internal enum AppearanceEncoding { Ascii, WinAnsi, MacRoman }

    private static AppearanceEncoding SelectEncoding(PdfDictionary appearance, ReadOnlySpan<byte> fontName,
        PdfDictionary? formResources, PoolResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        if (fontName.IsEmpty) { return AppearanceEncoding.Ascii; }
        PdfName? encoding = null;
        if (appearance.TryGet(PdfName.FromAscii("Resources"), out QpdfObject? resourcesRaw))
        { encoding = FindFontEncoding(resourcesRaw, fontName, resolver, governor, token); }
        // Encoding inheritance consults document-level AcroForm DR, not field DR or page resources.
        if ((encoding is null || encoding.RawSpan.IsEmpty) && formResources is not null)
        { encoding = FindFontEncoding(formResources, fontName, resolver, governor, token); }
        if (encoding is not null)
        {
            if (PdfDictionaryLookup.NameEquals(encoding, "WinAnsiEncoding"u8, governor, token)) { return AppearanceEncoding.WinAnsi; }
            if (PdfDictionaryLookup.NameEquals(encoding, "MacRomanEncoding"u8, governor, token)) { return AppearanceEncoding.MacRoman; }
        }
        return AppearanceEncoding.Ascii;
    }

    internal static byte[] EncodeValue(string text, AppearanceEncoding encoding, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        int length = text.Length;
        if (encoding != AppearanceEncoding.Ascii)
        {
            for (int i = 0; i < text.Length; i++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                { length--; i++; }
            }
        }
        if (length == 0) { return Array.Empty<byte>(); }
        long reserved = checked(32L + length); memory.Reserve(reserved);
        try
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            byte[] bytes = new byte[length]; int at = 0;
            for (int i = 0; i < text.Length; i++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                int code = text[i];
                if (encoding != AppearanceEncoding.Ascii && char.IsHighSurrogate(text[i])
                    && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                { code = char.ConvertToUtf32(text[i], text[++i]); }
                byte mapped = (byte)'?';
                if (code <= 127) { mapped = (byte)code; }
                else if (encoding == AppearanceEncoding.WinAnsi)
                {
                    if (code is >= 0xA0 and <= 0xFF) { mapped = (byte)code; }
                    else if (WinAnsiSpecial.TryGetValue(code, out byte special)) { mapped = special; }
                }
                else if (encoding == AppearanceEncoding.MacRoman)
                {
                    foreach ((int entry, byte value) in MacRomanTable)
                    { if (entry == code) { mapped = value; break; } }
                }
                bytes[at++] = mapped;
            }
            return bytes;
        }
        catch { memory.Release(reserved); throw; }
    }

    private static readonly Dictionary<int, byte> WinAnsiSpecial = new()
    {
        [0x20AC] = 0x80,
        [0x201A] = 0x82,
        [0x0192] = 0x83,
        [0x201E] = 0x84,
        [0x2026] = 0x85,
        [0x2020] = 0x86,
        [0x2021] = 0x87,
        [0x02C6] = 0x88,
        [0x2030] = 0x89,
        [0x0160] = 0x8A,
        [0x2039] = 0x8B,
        [0x0152] = 0x8C,
        [0x017D] = 0x8E,
        [0x2018] = 0x91,
        [0x2019] = 0x92,
        [0x201C] = 0x93,
        [0x201D] = 0x94,
        [0x2022] = 0x95,
        [0x2013] = 0x96,
        [0x2014] = 0x97,
        [0x02DC] = 0x98,
        [0x2122] = 0x99,
        [0x0161] = 0x9A,
        [0x203A] = 0x9B,
        [0x0153] = 0x9C,
        [0x017E] = 0x9E,
        [0x0178] = 0x9F,
    };

    private static readonly (int Code, byte Value)[] MacRomanTable =
    [
        (0x00C4, 0x80), (0x00C5, 0x81), (0x00C7, 0x82), (0x00C9, 0x83),
        (0x00D1, 0x84), (0x00D6, 0x85), (0x00DC, 0x86), (0x00E1, 0x87),
        (0x00E0, 0x88), (0x00E2, 0x89), (0x00E4, 0x8A), (0x00E3, 0x8B),
        (0x00E5, 0x8C), (0x00E7, 0x8D), (0x00E9, 0x8E), (0x00E8, 0x8F),
        (0x00EA, 0x90), (0x00EB, 0x91), (0x00ED, 0x92), (0x00EC, 0x93),
        (0x00EE, 0x94), (0x00EF, 0x95), (0x00F1, 0x96), (0x00F3, 0x97),
        (0x00F2, 0x98), (0x00F4, 0x99), (0x00F6, 0x9A), (0x00F5, 0x9B),
        (0x00FA, 0x9C), (0x00F9, 0x9D), (0x00FB, 0x9E), (0x00FC, 0x9F),
        (0x2020, 0xA0), (0x00B0, 0xA1), (0x00A2, 0xA2), (0x00A3, 0xA3),
        (0x00A7, 0xA4), (0x2022, 0xA5), (0x00B6, 0xA6), (0x00DF, 0xA7),
        (0x00AE, 0xA8), (0x00A9, 0xA9), (0x2122, 0xAA), (0x00B4, 0xAB),
        (0x00A8, 0xAC), (0x2260, 0xAD), (0x00C6, 0xAE), (0x00D8, 0xAF),
        (0x221E, 0xB0), (0x00B1, 0xB1), (0x2264, 0xB2), (0x2265, 0xB3),
        (0x00A5, 0xB4), (0x00B5, 0xB5), (0x2202, 0xB6), (0x2211, 0xB7),
        (0x03A0, 0xB8), (0x03C0, 0xB9), (0x222B, 0xBA), (0x00AA, 0xBB),
        (0x00BA, 0xBC), (0x2126, 0xBD), (0x00E6, 0xBE), (0x00F8, 0xBF),
        (0x00BF, 0xC0), (0x00A1, 0xC1), (0x00AC, 0xC2), (0x221A, 0xC3),
        (0x0192, 0xC4), (0x2248, 0xC5), (0x2206, 0xC6), (0x00AB, 0xC7),
        (0x00BB, 0xC8), (0x2026, 0xC9), (0x00A0, 0xCA), (0x00C0, 0xCB),
        (0x00C3, 0xCC), (0x00D5, 0xCD), (0x0152, 0xCE), (0x0153, 0xCF),
        (0x2013, 0xD0), (0x2014, 0xD1), (0x201C, 0xD2), (0x201D, 0xD3),
        (0x2018, 0xD4), (0x2019, 0xD5), (0x00F7, 0xD6), (0x25CA, 0xD7),
        (0x00FF, 0xD8), (0x0178, 0xD9), (0x2044, 0xDA), (0x00A4, 0xDB),
        (0x2039, 0xDC), (0x203A, 0xDD), (0xFB01, 0xDE), (0xFB02, 0xDF),
        (0x2021, 0xE0), (0x00B7, 0xE1), (0x201A, 0xE2), (0x201E, 0xE3),
        (0x2030, 0xE4), (0x00C2, 0xE5), (0x00CA, 0xE6), (0x00C1, 0xE7),
        (0x00CB, 0xE8), (0x00C8, 0xE9), (0x00CD, 0xEA), (0x00CE, 0xEB),
        (0x00CF, 0xEC), (0x00CC, 0xED), (0x00D3, 0xEE), (0x00D4, 0xEF),
        (0x00D2, 0xF1), (0x00DA, 0xF2), (0x00DB, 0xF3), (0x00D9, 0xF4),
        (0x0131, 0xF5), (0x02C6, 0xF6), (0x02DC, 0xF7), (0x00AF, 0xF8),
        (0x02D8, 0xF9), (0x02D9, 0xFA), (0x02DA, 0xFB), (0x00B8, 0xFC),
        (0x02DD, 0xFD), (0x02DB, 0xFE), (0x02C7, 0xFF),
    ];

    private static byte[] FilterAppearance(
        ReadOnlyMemory<byte> data,
        ReadOnlyMemory<byte> appearance,
        byte[] value,
        List<byte[]> options,
        double size,
        double[] box,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        ContentTokenizer tokenizer = new(data, limits, governor, cancellationToken)
        {
            IncludeComments = true,
            IncludeWhitespace = true,
        };
        using GovernedBufferStream output = new(governor, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes));
        int state = 0;
        bool replaced = false;
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            governor.NoteOperation();
            if (state == 2)
            {
                if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("EMC"u8))
                {
                    WriteTextAppearance(output, appearance, value, options, size, box, governor, memory, cancellationToken);
                    replaced = true;
                    state = 3;
                }

                continue;
            }

            if (state == 3)
            {
                AppendRaw(output, token, cancellationToken);
                continue;
            }

            if (state == 0)
            {
                AppendRaw(output, token, cancellationToken);
                if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("BMC"u8))
                {
                    state = 1;
                }

                continue;
            }

            if (token.Kind == ContentTokenKind.Comment || token.Kind == ContentTokenKind.Whitespace)
            {
                AppendRaw(output, token, cancellationToken);
                continue;
            }

            state = 2;
            if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("EMC"u8))
            {
                WriteTextAppearance(output, appearance, value, options, size, box, governor, memory, cancellationToken);
                replaced = true;
                state = 3;
            }
        }

        if (!replaced)
        {
            AppendAscii(output, "/Tx BMC\n");
            WriteTextAppearance(output, appearance, value, options, size, box, governor, memory, cancellationToken);
        }

        return output.ToArray(memory, cancellationToken);
    }
    private static void WriteTextAppearance(
        GovernedBufferStream built,
        ReadOnlyMemory<byte> appearance,
        byte[] value,
        List<byte[]> options,
        double size,
        double[] box,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        double lineHeight = 1.2 * size;
        int step = 1;
        double rows = (box[3] - box[1]) / lineHeight;
        long maxRows = rows < 0.0 ? 0L : (long)rows;
        bool highlight = false;
        long highlightIndex = 0L;
        List<byte[]> lines = ParserContainers.CreateList<byte[]>(memory);
        if (options.Count == 0 || maxRows < 2L)
        {
            ParserContainers.AddItem(memory, lines, value, IntPtr.Size);
        }
        else
        {
            long found = -1L;
            for (long i = 0L; i < options.Count; i++)
            {
                if (BytesEqual(options[(int)i], value, governor, token))
                {
                    found = i;
                    break;
                }
            }

            if (found >= 0L)
            {
                long wantedFirst = found - 1L;
                long wantedLast = found + maxRows - 2L;
                if (wantedFirst < 0L)
                {
                    wantedLast -= wantedFirst;
                    wantedFirst = 0L;
                }

                if (wantedLast >= options.Count)
                {
                    long diff = wantedLast - options.Count + 1L;
                    wantedFirst = wantedFirst - diff < 0L ? 0L : wantedFirst - diff;
                    wantedLast -= diff;
                }

                highlight = true;
                highlightIndex = found - wantedFirst;
                for (long i = wantedFirst; i <= wantedLast; i++)
                {
                    ParserContainers.AddItem(memory, lines, options[(int)i], IntPtr.Size);
                }
            }
            else
            {
                highlightIndex = 0L;
                highlight = true;
                ParserContainers.AddItem(memory, lines, value, IntPtr.Size);
                for (int i = 0; i < options.Count && i < maxRows - 1L; i++)
                {
                    ParserContainers.AddItem(memory, lines, options[i], IntPtr.Size);
                }
            }
        }

        long count = lines.Count;
        double top = box[3] - ((box[3] - box[1] - count * lineHeight) / 2.0);
        if (highlight)
        {
            AppendAscii(built, "q\n0.85 0.85 0.85 rg\n");
            AppendAscii(built, FormatDouble(box[0]));
            AppendAscii(built, " ");
            AppendAscii(built, FormatDouble(box[1] + top - (lineHeight * (highlightIndex + 1L))));
            AppendAscii(built, " ");
            AppendAscii(built, FormatDouble(box[2] - box[0]));
            AppendAscii(built, " ");
            AppendAscii(built, FormatDouble(lineHeight));
            AppendAscii(built, " re f\nQ\n");
        }

        top -= size;
        AppendAscii(built, "q\nBT\n");
        built.Write(appearance.Span, token);
        AppendAscii(built, "\n");
        for (int i = 0; i < lines.Count; i++)
        {
            if (i == 0)
            {
                AppendAscii(built, FormatDouble(box[0] + step));
                AppendAscii(built, " ");
                AppendAscii(built, FormatDouble(box[1] + top));
                AppendAscii(built, " Td\n");
            }
            else
            {
                AppendAscii(built, "0 ");
                AppendAscii(built, FormatDouble(-lineHeight));
                AppendAscii(built, " Td\n");
            }

            WriteLiteral(built, lines[i], governor, token);
            AppendAscii(built, " Tj\n");
        }

        AppendAscii(built, "ET\nQ\nEMC");
    }
    private static bool BytesEqual(byte[] left, byte[] right, ResourceGovernor governor, CancellationToken token)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendRaw(GovernedBufferStream output, ContentToken token, CancellationToken cancellationToken)
    {
        if (token.Kind == ContentTokenKind.Name) { output.WriteByte((byte)'/'); }
        output.Write(token.Data.Span, cancellationToken);
    }

    private static void AppendAscii(GovernedBufferStream output, string text)
    {
        Span<byte> buffer = stackalloc byte[256];
        for (int offset = 0; offset < text.Length;)
        {
            int count = Math.Min(buffer.Length, text.Length - offset);
            Encoding.ASCII.GetBytes(text.AsSpan(offset, count), buffer);
            output.Write(buffer[..count]); offset += count;
        }
    }

    private static void WriteLiteral(GovernedBufferStream output, byte[] value, ResourceGovernor governor, CancellationToken token)
    {
        output.WriteByte((byte)'(');
        foreach (byte b in value)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (b is (byte)'\\' or (byte)'(' or (byte)')')
            { output.WriteByte((byte)'\\'); output.WriteByte(b); }
            else if (b == 10) { output.Write("\\n"u8); }
            else if (b == 13) { output.Write("\\r"u8); }
            else { output.WriteByte(b); }
        }
        output.WriteByte((byte)')');
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

    private static QpdfObject? ResolveOrNull(PoolResolver resolver, PdfReference reference)
    {
        if (((IIndirectResolver)resolver).TryResolve(reference, out QpdfObject? found) && found is not null && found is not PdfNull)
        {
            return found;
        }

        return null;
    }

}

