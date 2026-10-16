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
        bool compress,
        int? level,
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
            governor.ChargeWork(1L);
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
                governor.ChargeWork(1L);
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
                    if (SyncButton(copier, annot, item, annotId, field, resolver, entries, diagnostics, governor))
                    {
                        pageChanged = true;
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
        byte[]? DefaultAppearance,
        List<string> Choices,
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
        byte[]? appearance = null;
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
                appearance = appearanceString.Value;
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

        List<string> choices = new();
        if (type == "Ch" && node is not null)
        {
            choices = ReadChoices(node, resolver);
        }

        return new FieldContext(type, flags, value, appearance, choices, node, nodeId);
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
    private static List<string> ReadChoices(PdfDictionary field, PoolResolver resolver)
    {
        List<string> choices = new();
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
            QpdfObject entry = item is PdfReference entryReference
                ? ResolveOrNull(resolver, entryReference) ?? item
                : item;
            if (entry is PdfByteString text)
            {
                choices.Add(PdfText.Decode(text.Value));
                continue;
            }

            if (entry is PdfArray pair && pair.Count == 2)
            {
                QpdfObject display = pair[1] is PdfReference displayReference
                    ? ResolveOrNull(resolver, displayReference) ?? pair[1]
                    : pair[1];
                if (display is PdfByteString displayText)
                {
                    choices.Add(PdfText.Decode(displayText.Value));
                }
            }
        }

        return choices;
    }

    private static PdfDictionary SetName(PdfDictionary dictionary, string key, string name, ResourceGovernor governor)
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
        entries[PdfName.FromAscii(key)] = new PdfName(Encoding.ASCII.GetBytes(name));
        return new PdfDictionary(entries);
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
        ResourceGovernor governor)
    {
        string? valueName = field.Value is PdfName valueNameObject ? valueNameObject.DisplayText : null;
        if (valueName is null)
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

        string on = FirstOnState(annot, resolver) ?? "Yes";
        string selected = valueName == "Off" ? "Off" : on;
        if (!WriteFieldName(copier, field, annot, item, annotId, entries, "V", selected, governor, diagnostics))
        {
            return false;
        }

        WriteAnnotName(copier, annot, item, annotId, entries, "AS", selected, governor);
        return true;
    }

    private static string? FirstOnState(PdfDictionary annot, PoolResolver resolver)
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

        foreach (PdfName key in states.Keys)
        {
            if (key.DisplayText != "Off")
            {
                return key.DisplayText;
            }
        }

        return null;
    }

    private static bool WriteFieldName(
        GraphCopier copier,
        FieldContext field,
        PdfDictionary annot,
        QpdfObject item,
        int? annotId,
        List<QpdfObject> entries,
        string key,
        string name,
        ResourceGovernor governor,
        DiagnosticBag diagnostics)
    {
        if (field.Node is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot update a field without a field node; keeping it as is.");
            return false;
        }

        if (field.NodeId.HasValue)
        {
            copier.ReplaceCopiedObject(field.NodeId.Value, SetName(field.Node, key, name, governor));
            return true;
        }

        if (ReferenceEquals(field.Node, annot))
        {
            if (annotId.HasValue)
            {
                copier.ReplaceCopiedObject(annotId.Value, SetName(annot, key, name, governor));
            }
            else
            {
                entries.Add(SetName(annot, key, name, governor));
            }

            return true;
        }

        diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot update a field embedded without identity; keeping it as is.");
        return false;
    }

    private static void WriteAnnotName(
        GraphCopier copier,
        PdfDictionary annot,
        QpdfObject item,
        int? annotId,
        List<QpdfObject> entries,
        string key,
        string name,
        ResourceGovernor governor)
    {
        if (annotId.HasValue)
        {
            copier.ReplaceCopiedObject(annotId.Value, SetName(annot, key, name, governor));
            entries.Add(item);
            return;
        }

        entries.Add(SetName(annot, key, name, governor));
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
        governor.ChargeWork(1L);
        GenAppearance foundAp = FindGenAppearance(annot, resolver);
        PdfStream? found = foundAp.Stream;
        byte[] decoded;
        PdfDictionary appearanceDict;
        if (found is null)
        {
            double[] box = ReadBox(annot, "Rect", resolver) ?? [0.0, 0.0, 0.0, 0.0];
            double width = box[2] - box[0];
            double height = box[3] - box[1];
            Dictionary<PdfName, QpdfObject> fresh = new()
            {
                [PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject"),
                [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form"),
                [PdfName.FromAscii("BBox")] = BoxArray([0.0, 0.0, width, height], governor),
                [PdfName.FromAscii("Resources")] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>
                {
                    [PdfName.FromAscii("ProcSet")] = new PdfArray([PdfName.FromAscii("PDF"), PdfName.FromAscii("Text")]),
                }),
            };
            byte[] initial = Encoding.ASCII.GetBytes("/Tx BMC\nEMC\n");
            PdfReference freshLink = copier.AddIndirectObjectWithId(new PdfStream(new PdfDictionary(fresh), initial), allocator);
            allocator = checked(allocator + 1);
            appearanceDict = new PdfDictionary(fresh);
            decoded = initial;
        }
        else
        {
            byte[]? raw = DecodeAppearance(found, resolver, limits, governor);
            if (raw is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot decode an appearance stream; keeping the field as is.");
                entries.Add(item);
                return false;
            }

            decoded = raw;
            appearanceDict = found.Dictionary;
        }

        double[]? bbox = ReadBox(appearanceDict, "BBox", resolver);
        if (bbox is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot generate an appearance without a bounding box; keeping the field as is.");
            entries.Add(item);
            return false;
        }

        string daText = field.DefaultAppearance is null ? string.Empty : Encoding.ASCII.GetString(field.DefaultAppearance);
        string valueText = string.Empty;
        if (field.Value is PdfByteString valueString)
        {
            valueText = PdfText.Decode(valueString.Value);
        }

        TfResult font = AnalyzeDa(daText, limits, governor);
        Func<string, byte[]> encoder = SelectEncoder(appearanceDict, font.FontName, formResources, resolver);
        byte[] encodedValue = encoder(valueText);
        List<byte[]> encodedOpt = new();
        if (field.Type == "Ch" && (field.Flags & FlagCombo) == 0L)
        {
            foreach (string choice in field.Choices)
            {
                encodedOpt.Add(encoder(choice));
            }
        }

        byte[] built = FilterAppearance(decoded, font.Appearance, encodedValue, encodedOpt, font.Size, bbox, limits, governor);
        PdfDictionary? mergedResources = TryMergeDrFont(appearanceDict, font.FontName, formResources, resolver, governor);
        Dictionary<PdfName, QpdfObject> streamEntries = new();
        foreach (PdfName key in appearanceDict.Keys)
        {
            if (key.DisplayText == "Filter" || key.DisplayText == "DecodeParms")
            {
                continue;
            }

            if (key.DisplayText == "Resources" && mergedResources is not null)
            {
                streamEntries[key] = mergedResources;
                continue;
            }

            if (appearanceDict.TryGet(key, out QpdfObject? kept) && kept is not null)
            {
                streamEntries[key] = kept;
            }
        }

        governor.ReserveMemory(64L);
        streamEntries[PdfName.FromAscii("Type")] = PdfName.FromAscii("XObject");
        streamEntries[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form");
        PdfReference builtLink = copier.AddIndirectObjectWithId(new PdfStream(new PdfDictionary(streamEntries), built), allocator);
        allocator = checked(allocator + 1);
        PdfDictionary newAp = BuildAp(foundAp.Ap, builtLink, governor);
        Dictionary<PdfName, QpdfObject> annotEntries = new();
        foreach (PdfName key in annot.Keys)
        {
            if (annot.TryGet(key, out QpdfObject? annotKept) && annotKept is not null)
            {
                annotEntries[key] = annotKept;
            }
        }

        governor.ReserveMemory(64L);
        annotEntries[PdfName.FromAscii("AP")] = newAp;
        PdfDictionary updatedAnnot = new(annotEntries);
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

    private sealed record GenAppearance(PdfStream? Stream, PdfReference? Link, PdfDictionary? Ap, string? State);

    private static GenAppearance FindGenAppearance(PdfDictionary annot, PoolResolver resolver)
    {
        if (!annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? ap) || ap is null || ap is PdfNull)
        {
            return new GenAppearance(null, null, null, null);
        }

        QpdfObject apTarget = ap is PdfReference apReference
            ? ResolveOrNull(resolver, apReference) ?? ap
            : ap;
        if (apTarget is not PdfDictionary apDict)
        {
            return new GenAppearance(null, null, null, null);
        }

        if (!apDict.TryGet(PdfName.FromAscii("N"), out QpdfObject? normal) || normal is null || normal is PdfNull)
        {
            return new GenAppearance(null, null, apDict, null);
        }

        QpdfObject normalTarget = normal is PdfReference normalReference
            ? ResolveOrNull(resolver, normalReference) ?? normal
            : normal;
        if (normalTarget is PdfStream direct)
        {
            return new GenAppearance(direct, normal as PdfReference, apDict, null);
        }

        if (normalTarget is not PdfDictionary states)
        {
            return new GenAppearance(null, null, apDict, null);
        }

        string state = string.Empty;
        if (annot.TryGet(PdfName.FromAscii("AS"), out QpdfObject? stateRaw) && stateRaw is PdfName stateName)
        {
            state = stateName.DisplayText;
        }

        if (state.Length == 0)
        {
            return new GenAppearance(null, null, apDict, null);
        }

        foreach (PdfName key in states.Keys)
        {
            if (key.DisplayText != state)
            {
                continue;
            }

            if (!states.TryGet(key, out QpdfObject? found) || found is null)
            {
                return new GenAppearance(null, null, apDict, state);
            }

            QpdfObject stateTarget = found is PdfReference foundReference
                ? ResolveOrNull(resolver, foundReference) ?? found
                : found;
            if (stateTarget is PdfStream stateStream)
            {
                return new GenAppearance(stateStream, found as PdfReference, apDict, state);
            }

            return new GenAppearance(null, null, apDict, state);
        }

        return new GenAppearance(null, null, apDict, state);
    }

    private static PdfDictionary BuildAp(PdfDictionary? ap, PdfReference link, ResourceGovernor governor)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        if (ap is not null)
        {
            foreach (PdfName key in ap.Keys)
            {
                if (ap.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    entries[key] = found;
                }
            }
        }

        governor.ReserveMemory(64L);
        entries[PdfName.FromAscii("N")] = link;
        return new PdfDictionary(entries);
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

    private static PdfArray BoxArray(double[] values, ResourceGovernor governor)
    {
        List<QpdfObject> items = new(values.Length);
        foreach (double value in values)
        {
            governor.ReserveMemory(32L);
            items.Add(new PdfReal(FormatDouble(value), value));
        }

        return new PdfArray(items);
    }

    private static byte[]? DecodeAppearance(
        PdfStream stream,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor)
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

        foreach (FilterStage stage in stages)
        {
            if (!IsDecodable(stage.Name))
            {
                return null;
            }
        }

        try
        {
            return FilterChain.Decode(stream.Data, stages, limits, governor);
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

    private sealed record TfResult(double Size, string FontName, string Appearance);

    private static TfResult AnalyzeDa(string appearance, QpdfLimits limits, ResourceGovernor governor)
    {
        double size = 11.0;
        string font = string.Empty;
        string rebuilt = appearance;
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes(appearance);
            ContentTokenizer tokenizer = new(bytes, limits, governor)
            {
                IncludeComments = true,
                IncludeWhitespace = true,
            };
            List<string> raws = new();
            List<bool> isNumber = new();
            List<double> numbers = new();
            List<bool> isName = new();
            List<string> names = new();
            List<bool> isOperator = new();
            List<string> operators = new();
            while (tokenizer.TryReadToken(out ContentToken token))
            {
                governor.ChargeWork(1L);
                string raw = Encoding.ASCII.GetString(token.Data.ToArray());
                raws.Add(raw);
                bool number = token.Kind == ContentTokenKind.Integer || token.Kind == ContentTokenKind.Real;
                isNumber.Add(number);
                numbers.Add(number && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0.0);
                bool name = token.Kind == ContentTokenKind.Name;
                isName.Add(name);
                names.Add(raw);
                bool word = token.Kind == ContentTokenKind.Operator;
                isOperator.Add(word);
                operators.Add(word ? raw : string.Empty);
            }

            double lastNumber = 0.0;
            int lastNumberIndex = -1;
            string lastName = string.Empty;
            int sizeIndex = -1;
            for (int i = 0; i < raws.Count; i++)
            {
                if (isNumber[i])
                {
                    lastNumber = numbers[i];
                    lastNumberIndex = i;
                }
                else if (isName[i])
                {
                    lastName = names[i];
                }
                else if (isOperator[i] && operators[i] == "Tf")
                {
                    if (lastNumber > 1.0 && lastNumber < 1000.0)
                    {
                        size = lastNumber;
                    }

                    sizeIndex = lastNumberIndex;
                    font = lastName;
                }
            }

            if (sizeIndex >= 0 && sizeIndex < raws.Count)
            {
                double stored = 0.0;
                bool parsed = double.TryParse(raws[sizeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out stored);
                if (parsed && (stored - size > 0.001 || stored - size < -0.001))
                {
                    raws[sizeIndex] = FormatDouble(size);
                }
            }

            StringBuilder builder = new();
            for (int r = 0; r < raws.Count; r++)
            {
                if (isName[r])
                {
                    builder.Append((char)47);
                }

                builder.Append(raws[r]);
            }

            rebuilt = builder.ToString();
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
        }

        return new TfResult(size, font, rebuilt);
    }

    private static string FindFontEncoding(QpdfObject? resourcesRaw, string fontName, PoolResolver resolver)
    {
        string encoding = string.Empty;
        if (resourcesRaw is null || resourcesRaw is PdfNull)
        {
            return encoding;
        }

            QpdfObject resourcesTarget = resourcesRaw is PdfReference resourcesReference
                ? ResolveOrNull(resolver, resourcesReference) ?? resourcesRaw
                : resourcesRaw;
            if (resourcesTarget is PdfDictionary resources
                && resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject? fontsRaw)
                && fontsRaw is not null
                && fontsRaw is not PdfNull)
            {
                QpdfObject fontsTarget = fontsRaw is PdfReference fontsReference
                    ? ResolveOrNull(resolver, fontsReference) ?? fontsRaw
                    : fontsRaw;
                if (fontsTarget is PdfDictionary fonts)
                {
                    foreach (PdfName key in fonts.Keys)
                    {
                        if (key.DisplayText != fontName)
                        {
                            continue;
                        }

                        if (!fonts.TryGet(key, out QpdfObject? fontRaw) || fontRaw is null)
                        {
                            break;
                        }

                        QpdfObject fontTarget = fontRaw is PdfReference fontReference
                            ? ResolveOrNull(resolver, fontReference) ?? fontRaw
                            : fontRaw;
                        if (fontTarget is PdfDictionary fontDict
                            && fontDict.TryGet(PdfName.FromAscii("Encoding"), out QpdfObject? encodingRaw)
                            && encodingRaw is PdfName encodingName)
                        {
                            encoding = encodingName.DisplayText;
                        }

                        break;
                    }
                }
            }

        return encoding;
    }

    private static PdfDictionary? TryMergeDrFont(
        PdfDictionary appearance,
        string fontName,
        PdfDictionary? formResources,
        PoolResolver resolver,
        ResourceGovernor governor)
    {
        if (fontName.Length == 0 || formResources is null)
        {
            return null;
        }

        if (!appearance.TryGet(PdfName.FromAscii("Resources"), out QpdfObject? appearanceResourcesRaw) || appearanceResourcesRaw is null || appearanceResourcesRaw is PdfNull)
        {
            return null;
        }

        QpdfObject appearanceResourcesTarget = appearanceResourcesRaw is PdfReference appearanceResourcesReference
            ? ResolveOrNull(resolver, appearanceResourcesReference) ?? appearanceResourcesRaw
            : appearanceResourcesRaw;
        if (appearanceResourcesTarget is not PdfDictionary appearanceResources)
        {
            return null;
        }

        if (TryGetFontRaw(appearanceResources, fontName, resolver) is not null)
        {
            return null;
        }

        QpdfObject? drFont = TryGetFontRaw(formResources, fontName, resolver);
        if (drFont is null)
        {
            return null;
        }

        Dictionary<PdfName, QpdfObject> fonts = new();
        if (appearanceResources.TryGet(PdfName.FromAscii("Font"), out QpdfObject? fontsRaw) && fontsRaw is not null && fontsRaw is not PdfNull)
        {
            QpdfObject fontsTarget = fontsRaw is PdfReference fontsReference
                ? ResolveOrNull(resolver, fontsReference) ?? fontsRaw
                : fontsRaw;
            if (fontsTarget is PdfDictionary fontsDict)
            {
                foreach (PdfName key in fontsDict.Keys)
                {
                    if (fontsDict.TryGet(key, out QpdfObject? kept) && kept is not null)
                    {
                        fonts[key] = kept;
                    }
                }
            }
        }

        governor.ReserveMemory(64L);
        fonts[PdfName.FromAscii(fontName)] = drFont;
        Dictionary<PdfName, QpdfObject> merged = new();
        foreach (PdfName key in appearanceResources.Keys)
        {
            if (key.DisplayText == "Font")
            {
                continue;
            }

            if (appearanceResources.TryGet(key, out QpdfObject? kept) && kept is not null)
            {
                merged[key] = kept;
            }
        }

        governor.ReserveMemory(64L);
        merged[PdfName.FromAscii("Font")] = new PdfDictionary(fonts);
        return new PdfDictionary(merged);
    }

    private static QpdfObject? TryGetFontRaw(
        PdfDictionary resources,
        string fontName,
        PoolResolver resolver)
    {
        if (!resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject? fontsRaw) || fontsRaw is null || fontsRaw is PdfNull)
        {
            return null;
        }

        QpdfObject fontsTarget = fontsRaw is PdfReference fontsReference
            ? ResolveOrNull(resolver, fontsReference) ?? fontsRaw
            : fontsRaw;
        if (fontsTarget is not PdfDictionary fonts)
        {
            return null;
        }

        foreach (PdfName key in fonts.Keys)
        {
            if (key.DisplayText != fontName)
            {
                continue;
            }

            if (fonts.TryGet(key, out QpdfObject? fontRaw) && fontRaw is not null && fontRaw is not PdfNull)
            {
                return fontRaw;
            }

            return null;
        }

        return null;
    }

    private static Func<string, byte[]> SelectEncoder(PdfDictionary appearance, string fontName, PdfDictionary? formResources, PoolResolver resolver)
    {
        if (fontName.Length == 0)
        {
            return EncodeAscii;
        }

        string encoding = string.Empty;
        if (appearance.TryGet(PdfName.FromAscii("Resources"), out QpdfObject? resourcesRaw) && resourcesRaw is not null && resourcesRaw is not PdfNull)
        {
            encoding = FindFontEncoding(resourcesRaw, fontName, resolver);
        }

        // Field-level /DR is ignored like the reference: upstream getDefaultResources()
        // reads only the document-level /AcroForm /DR (field /DR is ignored by
        // popular readers), and page /Resources is never consulted for encoding.
        if (encoding.Length == 0 && formResources is not null)
        {
            encoding = FindFontEncoding(formResources, fontName, resolver);
        }

        if (encoding == "WinAnsiEncoding")
        {
            return EncodeWinAnsi;
        }

        if (encoding == "MacRomanEncoding")
        {
            return EncodeMacRoman;
        }

        return EncodeAscii;
    }

    private static byte[] EncodeAscii(string text)
    {
        List<byte> output = new(text.Length);
        foreach (char code in text)
        {
            output.Add(code <= 127 ? (byte)code : (byte)63);
        }

        return [.. output];
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

    private static byte[] EncodeWinAnsi(string text)
    {
        List<byte> output = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int code = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                code = char.ConvertToUtf32(text[i], text[i + 1]);
                i += 2;
            }
            else
            {
                i += 1;
            }

            if (code <= 0x7F)
            {
                output.Add((byte)code);
            }
            else if (code >= 0xA0 && code <= 0xFF)
            {
                output.Add((byte)code);
            }
            else if (WinAnsiSpecial.TryGetValue(code, out byte mapped))
            {
                output.Add(mapped);
            }
            else
            {
                output.Add((byte)63);
            }
        }

        return [.. output];
    }

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

    private static byte[] EncodeMacRoman(string text)
    {
        List<byte> output = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int code = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                code = char.ConvertToUtf32(text[i], text[i + 1]);
                i += 2;
            }
            else
            {
                i += 1;
            }

            if (code <= 0x7F)
            {
                output.Add((byte)code);
                continue;
            }

            byte mapped = 63;
            foreach ((int entry, byte value) in MacRomanTable)
            {
                if (entry == code)
                {
                    mapped = value;
                    break;
                }
            }

            output.Add(mapped);
        }

        return [.. output];
    }

    private static byte[] FilterAppearance(
        byte[] data,
        string appearance,
        byte[] value,
        List<byte[]> options,
        double size,
        double[] box,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        ContentTokenizer tokenizer = new(data, limits, governor)
        {
            IncludeComments = true,
            IncludeWhitespace = true,
        };
        List<byte> output = new(data.Length + 256);
        int state = 0;
        bool replaced = false;
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            governor.ChargeWork(1L);
            if (state == 2)
            {
                if (token.Kind == ContentTokenKind.Operator && TokenText(token) == "EMC")
                {
                    output.AddRange(WriteTextAppearance(appearance, value, options, size, box));
                    replaced = true;
                    state = 3;
                }

                continue;
            }

            if (state == 3)
            {
                AppendRaw(output, token);
                continue;
            }

            if (state == 0)
            {
                AppendRaw(output, token);
                if (token.Kind == ContentTokenKind.Operator && TokenText(token) == "BMC")
                {
                    state = 1;
                }

                continue;
            }

            if (token.Kind == ContentTokenKind.Comment || token.Kind == ContentTokenKind.Whitespace)
            {
                AppendRaw(output, token);
                continue;
            }

            state = 2;
            if (token.Kind == ContentTokenKind.Operator && TokenText(token) == "EMC")
            {
                output.AddRange(WriteTextAppearance(appearance, value, options, size, box));
                replaced = true;
                state = 3;
            }
        }

        if (!replaced)
        {
            AppendAscii(output, "/Tx BMC\n");
            output.AddRange(WriteTextAppearance(appearance, value, options, size, box));
        }

        return [.. output];
    }
    private static byte[] WriteTextAppearance(
        string appearance,
        byte[] value,
        List<byte[]> options,
        double size,
        double[] box)
    {
        double lineHeight = 1.2 * size;
        int step = 1;
        double rows = (box[3] - box[1]) / lineHeight;
        long maxRows = rows < 0.0 ? 0L : (long)rows;
        bool highlight = false;
        long highlightIndex = 0L;
        List<byte[]> lines = new();
        if (options.Count == 0 || maxRows < 2L)
        {
            lines.Add(value);
        }
        else
        {
            long found = -1L;
            for (long i = 0L; i < options.Count; i++)
            {
                if (BytesEqual(options[(int)i], value))
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
                    lines.Add(options[(int)i]);
                }
            }
            else
            {
                highlightIndex = 0L;
                highlight = true;
                lines.Add(value);
                for (int i = 0; i < options.Count && i < maxRows - 1L; i++)
                {
                    lines.Add(options[i]);
                }
            }
        }

        List<byte> built = new();
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
        AppendAscii(built, appearance);
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

            built.AddRange(EscapeLiteral(lines[i]));
            AppendAscii(built, " Tj\n");
        }

        AppendAscii(built, "ET\nQ\nEMC");
        return [.. built];
    }
    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string TokenText(ContentToken token)
    {
        return Encoding.ASCII.GetString(token.Data.ToArray());
    }

    private static void AppendRaw(List<byte> output, ContentToken token)
    {
        if (token.Kind == ContentTokenKind.Name)
        {
            output.Add((byte)47);
        }

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

    private static byte[] EscapeLiteral(byte[] value)
    {
        List<byte> output = new(value.Length + 2);
        output.Add((byte)40);
        foreach (byte b in value)
        {
            if (b == 92 || b == 40 || b == 41)
            {
                output.Add((byte)92);
                output.Add(b);
            }
            else if (b == 10)
            {
                output.Add((byte)92);
                output.Add((byte)110);
            }
            else if (b == 13)
            {
                output.Add((byte)92);
                output.Add((byte)114);
            }
            else
            {
                output.Add(b);
            }
        }

        output.Add((byte)41);
        return [.. output];
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

