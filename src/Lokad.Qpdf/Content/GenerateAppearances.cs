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
    private static readonly PdfName DefaultAppearanceKey = PdfName.FromAscii("DA");

    internal static int Apply(
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        IReadOnlyDictionary<GraphCopier, CopiedForm?> forms,
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
        if (forms is null)
        {
            throw new ArgumentNullException(nameof(forms));
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
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        Dictionary<GraphCopier, FieldParents> parents = ParserContainers.CreateDictionary<GraphCopier, FieldParents>(traversal);
        HashSet<int> generated = ParserContainers.CreateSet<int>(traversal);
        foreach ((GraphCopier copier, int pageId) in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary page)
            {
                continue;
            }

            if (!PdfDictionaryLookup.TryGet(page, "Annots"u8, governor, cancellationToken, out QpdfObject annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
            PoolResolver resolver = PoolResolver.Create(graph.Memory, copier.CopiedObjects);
            QpdfObject annotsTarget = annotsRaw is PdfReference annotsReference
                ? ResolveOrNull(resolver, annotsReference) ?? annotsRaw
                : annotsRaw;
            if (annotsTarget is not PdfArray annots)
            {
                continue;
            }

            forms.TryGetValue(copier, out CopiedForm? form);
            PdfDictionary? formResources = form?.DefaultResources is QpdfObject resourcesRaw
                ? ResolveFieldValue(resourcesRaw, resolver, governor, cancellationToken) as PdfDictionary : null;
            PdfByteString? defaultAppearance = form is not null && form.Extras.TryGetValue(DefaultAppearanceKey, out QpdfObject? daRaw)
                && daRaw is not null ? ResolveFieldValue(daRaw, resolver, governor, cancellationToken) as PdfByteString : null;
            bool pageChanged = false;
            QpdfObject[]? directEntries = null;
            for (int index = 0; index < annots.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.NoteOperation();
                QpdfObject item = annots[index];
                QpdfObject entryTarget = item is PdfReference itemReference
                    ? ResolveOrNull(resolver, itemReference) ?? item : item;
                if (entryTarget is not PdfDictionary annot || !IsWidget(annot, governor, cancellationToken)) { continue; }
                int? annotId = (item as PdfReference)?.ObjectNumber;
                if (annotId.HasValue && !ParserContainers.AddSetItem(traversal, generated, annotId.Value, sizeof(int))) { continue; }
                if (!IsReached(annotId, reachedAnnots, warnUnreachable, diagnostics)) { continue; }
                FieldContext field = ResolveField(copier, annot, annotId, resolver, parents, traversal, limits, governor, cancellationToken);
                if (field.Type == FieldType.Other) { continue; }
                // Indirect widgets replace their existing graph slot. Only direct
                // widgets need an annotation-array copy, funded before mutation.
                if (!annotId.HasValue && directEntries is null)
                {
                    long bytes = checked(32L + IntPtr.Size * (long)annots.Count);
                    graph.Memory.Reserve(bytes); directEntries = new QpdfObject[annots.Count]; graph.Add(bytes);
                    for (int at = 0; at < annots.Count; at++)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        directEntries[at] = annots[at];
                    }
                }
                PdfDictionary? updated;
                if (field.Type == FieldType.Button)
                { updated = SyncButton(copier, annot, annotId, field, resolver, diagnostics, governor, cancellationToken); }
                else
                {
                    updated = GenerateText(copier, annot, annotId, field, resolver, limits, governor, diagnostics,
                        cancellationToken, formResources, defaultAppearance, ref allocator);
                }
                if (updated is not null && !annotId.HasValue && directEntries is not null)
                { directEntries[index] = updated; pageChanged = true; }
            }
            if (pageChanged && directEntries is not null)
            {
                graph.Memory.Reserve(24L); PdfArray rebuilt = PdfArray.FromOwnedItems(directEntries); graph.Add(24L);
                int target = (annotsRaw as PdfReference)?.ObjectNumber ?? pageId;
                QpdfObject replacement = annotsRaw is PdfReference ? rebuilt : PdfDictionaryRebuild.ReplaceEntry(graph, page, "Annots"u8, rebuilt);
                copier.ValidateReplacement(target); graph.Retain(); copier.ReplaceCopiedObject(target, replacement);
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

    private enum FieldType { Other, Button, Text, Choice }

    private readonly record struct FieldContext(
        FieldType Type, long Flags, QpdfObject? Value, ReadOnlyMemory<byte>? DefaultAppearance,
        PdfDictionary Node, int? NodeId);

    private static bool IsWidget(PdfDictionary annot, ResourceGovernor governor, CancellationToken token)
    {
        return PdfDictionaryLookup.TryGet(annot, "Subtype"u8, governor, token, out QpdfObject subtype)
            && subtype is PdfName name && name.RawSpan.SequenceEqual("Widget"u8);
    }

    private static FieldContext ResolveField(GraphCopier copier, PdfDictionary annot, int? annotId,
        PoolResolver resolver, Dictionary<GraphCopier, FieldParents> parents, ParserMemoryScope traversal,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<(PdfDictionary Node, int? Id)> chain = FindFieldChain(copier, annot, annotId, resolver, parents,
            traversal, scratch, limits, governor, token);
        // A separate widget belongs to its immediate terminal field, independently
        // of which ancestor supplies FT. A merged widget carries field entries.
        int fieldIndex = chain.Count > 1 && !AcroFormCopy.HasFieldEntries(annot, governor, token) ? 1 : 0;
        FieldType type = FieldType.Other;
        bool typeFound = false, flagsFound = false;
        long flags = 0;
        QpdfObject? value = null;
        ReadOnlyMemory<byte>? appearance = null;
        foreach ((PdfDictionary current, int? _) in chain)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (!typeFound && ReadFieldValue(current, "FT"u8, resolver, governor, token) is PdfName name)
            {
                typeFound = true;
                type = name.RawSpan.SequenceEqual("Btn"u8) ? FieldType.Button
                    : name.RawSpan.SequenceEqual("Tx"u8) ? FieldType.Text
                    : name.RawSpan.SequenceEqual("Ch"u8) ? FieldType.Choice : FieldType.Other;
            }
            value ??= ReadFieldValue(current, "V"u8, resolver, governor, token);
            if (appearance is null && ReadFieldValue(current, "DA"u8, resolver, governor, token) is PdfByteString text)
            { appearance = text.ValueMemory; }
            if (!flagsFound && ReadFieldValue(current, "Ff"u8, resolver, governor, token) is PdfInteger integer)
            { flags = integer.Value; flagsFound = true; }
        }
        return new(type, flags, value, appearance, chain[fieldIndex].Node, chain[fieldIndex].Id);
    }

    private static QpdfObject? ReadFieldValue(PdfDictionary node, ReadOnlySpan<byte> key,
        PoolResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(node, key, governor, token, out QpdfObject value)) { return null; }
        return ResolveFieldValue(value, resolver, governor, token);
    }

    private static QpdfObject? ResolveFieldValue(QpdfObject value, PoolResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (value is not PdfReference link) { return value is PdfNull ? null : value; }
            QpdfObject? resolved = ResolveOrNull(resolver, link);
            if (resolved is null) { return null; }
            value = resolved;
        }
        return null;
    }

    // A single lazy reverse index preserves lenient Kids-only parents. Keys use
    // object numbers or direct identity, so later V/AS replacements stay visible.
    private sealed class FieldParents
    {
        internal readonly Dictionary<int, int> Ids;
        internal readonly Dictionary<PdfDictionary, int> Direct;
        internal FieldParents(ParserMemoryScope memory)
        {
            memory.Reserve(32L);
            Ids = ParserContainers.CreateDictionary<int, int>(memory);
            memory.Reserve(96L);
            Direct = new(ReferenceEqualityComparer.Instance);
        }
    }

    private static FieldParents GetParents(GraphCopier copier, PoolResolver resolver,
        Dictionary<GraphCopier, FieldParents> parents, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        if (parents.TryGetValue(copier, out FieldParents? found)) { return found; }
        FieldParents built = new(memory);
        foreach ((int id, QpdfObject raw) in copier.CopiedObjects)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (raw is not PdfDictionary node || ReadFieldValue(node, "Kids"u8, resolver, governor, token) is not PdfArray kids) { continue; }
            for (int index = 0; index < kids.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (kids[index] is PdfReference link && !built.Ids.ContainsKey(link.ObjectNumber))
                {
                    ParserContainers.EnsureEntryCapacity(memory, built.Ids, sizeof(int));
                    built.Ids.Add(link.ObjectNumber, id);
                }
                else if (kids[index] is PdfDictionary direct && !built.Direct.ContainsKey(direct))
                {
                    ParserContainers.EnsureEntryCapacity(memory, built.Direct, sizeof(int));
                    built.Direct.Add(direct, id);
                }
            }
        }
        ParserContainers.EnsureEntryCapacity(memory, parents, IntPtr.Size);
        parents.Add(copier, built); return built;
    }

    private static List<(PdfDictionary Node, int? Id)> FindFieldChain(GraphCopier copier,
        PdfDictionary annot, int? annotId, PoolResolver resolver, Dictionary<GraphCopier, FieldParents> parents,
        ParserMemoryScope traversal, ParserMemoryScope scratch, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken token)
    {
        List<(PdfDictionary Node, int? Id)> chain = ParserContainers.CreateList<(PdfDictionary Node, int? Id)>(scratch);
        PdfDictionary current = annot; int? currentId = annotId;
        for (int depth = 0; depth <= Math.Min(64L, limits.MaxNestingDepth); depth++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            bool seen = false;
            foreach ((PdfDictionary node, int? id) in chain)
            { if (ReferenceEquals(node, current) || (currentId.HasValue && id == currentId)) { seen = true; break; } }
            if (seen) { break; }
            ParserContainers.AddItem(scratch, chain, (current, currentId), 16L);
            if (PdfDictionaryLookup.TryGet(current, "Parent"u8, governor, token, out QpdfObject parentRaw)
                && parentRaw is not PdfNull)
            {
                QpdfObject? parent = parentRaw is PdfReference link ? ResolveOrNull(resolver, link) : parentRaw;
                if (parent is PdfDictionary dictionary)
                {
                    current = dictionary; currentId = (parentRaw as PdfReference)?.ObjectNumber;
                    continue;
                }
            }
            FieldParents index = GetParents(copier, resolver, parents, traversal, governor, token);
            bool hasParent = currentId is int number ? index.Ids.TryGetValue(number, out int parentId)
                : index.Direct.TryGetValue(current, out parentId);
            if (!hasParent || !copier.CopiedObjects.TryGetValue(parentId, out QpdfObject? candidate) || candidate is not PdfDictionary ancestor) { break; }
            current = ancestor; currentId = parentId;
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

        for (int index = 0; index < array.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            QpdfObject item = array[index];
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

    private static PdfDictionary? SyncButton(
        GraphCopier copier,
        PdfDictionary annot,
        int? annotId,
        FieldContext field,
        PoolResolver resolver,
        DiagnosticBag diagnostics,
        ResourceGovernor governor,
        CancellationToken token)
    {
        if (field.Value is not PdfName valueName)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot synchronize a button without a name value; keeping it as is.");
            return null;
        }

        bool radio = (field.Flags & 32768L) != 0L;
        bool pushbutton = (field.Flags & 65536L) != 0L;
        if (pushbutton)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Pushbutton values cannot be synchronized; keeping it as is.");
            return null;
        }

        if (radio)
        {
            // The reference leaves radio buttons untouched while checkboxes
            // synchronize, so radios degrade to skip on every path, with the
            // reference warning that it cannot set a radio button value.
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Radio button values cannot be synchronized; keeping it as is.");
            return null;
        }

        bool merged = ReferenceEquals(field.Node, annot);
        if (!merged && !field.NodeId.HasValue)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot update a field embedded without identity; keeping it as is.");
            return null;
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
        if (annotId is int id) { copier.ReplaceCopiedObject(id, updatedAnnot); }
        return updatedAnnot;
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

    private static PdfDictionary? GenerateText(
        GraphCopier copier,
        PdfDictionary annot,
        int? annotId,
        FieldContext field,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        PdfDictionary? formResources,
        PdfByteString? defaultAppearance,
        ref int allocator)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        GenAppearance foundAp = FindGenAppearance(annot, resolver, governor, cancellationToken);
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
                return null;
            }

            decoded = raw.Value;
            appearanceDict = found.Dictionary;
        }

        double[]? bbox = ReadBox(appearanceDict, "BBox", resolver);
        if (bbox is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot generate an appearance without a bounding box; keeping the field as is.");
            return null;
        }
        if (PdfDictionaryLookup.TryGet(appearanceDict, "Resources"u8, governor, cancellationToken, out QpdfObject resourceValue)
            && resourceValue is not PdfNull && ResolveFieldValue(resourceValue, resolver, governor, cancellationToken) is not PdfDictionary)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot resolve appearance resources; keeping the field as is.");
            return null;
        }

        string valueText = string.Empty;
        if (field.Value is PdfByteString valueString)
        {
            valueText = PdfText.Decode(valueString.ValueMemory, scratch, governor, cancellationToken);
        }

        TfResult font = AnalyzeDa(field.DefaultAppearance ?? defaultAppearance?.ValueMemory ?? ReadOnlyMemory<byte>.Empty, governor, scratch, cancellationToken);
        if (font.FontName.Length == 0)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot generate text without a usable default appearance; keeping the field as is.");
            return null;
        }
        AppearanceEncoding encoding = SelectEncoding(appearanceDict, font.FontName, formResources, resolver, governor, cancellationToken);
        byte[] encodedValue = EncodeValue(valueText, encoding, scratch, governor, cancellationToken);
        List<byte[]> encodedOpt = ParserContainers.CreateList<byte[]>(scratch);
        if (field.Type == FieldType.Choice && (field.Flags & FlagCombo) == 0L)
        {
            foreach (string choice in ReadChoices(field.Node, resolver, scratch, governor, cancellationToken))
            {
                ParserContainers.AddItem(scratch, encodedOpt, EncodeValue(choice, encoding, scratch, governor, cancellationToken), IntPtr.Size);
            }
        }

        byte[] built;
        try { built = FilterAppearance(decoded, font.Appearance, encodedValue, encodedOpt, font.Size, bbox, limits, governor, scratch, cancellationToken); }
        catch (QpdfParseException)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot rewrite a malformed appearance program; keeping the field as is.");
            return null;
        }
        PdfDictionary? mergedResources = TryMergeDrFont(appearanceDict, font.FontName, formResources, resolver, graph);
        PdfNameTable<QpdfObject> streamEntries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using (PdfDictionary.EntryEnumerator source = appearanceDict.GetEntryEnumerator())
        {
            while (source.MoveNext(governor, cancellationToken))
            {
                PdfName key = source.Current.Key;
                if (PdfDictionaryLookup.NameEquals(key, "Filter"u8, governor, cancellationToken)
                    || PdfDictionaryLookup.NameEquals(key, "DecodeParms"u8, governor, cancellationToken)) { continue; }
                if (mergedResources is not null && PdfDictionaryLookup.NameEquals(key, "Resources"u8, governor, cancellationToken)) { continue; }
                streamEntries.Assign(graph.Memory, governor, key, source.Current.Value, cancellationToken);
            }
        }
        if (mergedResources is not null)
        { streamEntries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Resources"u8), mergedResources, cancellationToken); }
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
        if (annotId.HasValue) { copier.ReplaceCopiedObject(annotId.Value, updatedAnnot); }
        return updatedAnnot;
    }

    private readonly record struct GenAppearance(PdfStream? Stream, PdfDictionary? Ap);

    private static GenAppearance FindGenAppearance(PdfDictionary annot, PoolResolver resolver, ResourceGovernor governor, CancellationToken token)
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

        if (!PdfDictionaryLookup.TryGet(annot, "AS"u8, governor, token, out QpdfObject stateRaw) || stateRaw is not PdfName state
            || !PdfDictionaryLookup.TryGet(states, state.RawSpan, governor, token, out QpdfObject found))
        { return new GenAppearance(null, apDict); }
        QpdfObject target = found is PdfReference link ? ResolveOrNull(resolver, link) ?? found : found;
        return new GenAppearance(target as PdfStream, apDict);
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
        Span<char> text = stackalloc char[384];
        for (int i = 0; i < values.Length; i++)
        {
            graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
            int length = WriterNumber.FormatGeometry(values[i], text);
            long itemBytes = checked(32L + GovernedText.StringBytes(length));
            graph.Memory.Reserve(itemBytes); graph.Add(itemBytes);
            items[i] = new PdfReal(new string(text[..length]), values[i]);
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

    internal static TfResult AnalyzeDa(ReadOnlyMemory<byte> appearance, ResourceGovernor governor,
        ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        try
        {
            ContentTokenizer tokenizer = new(appearance, governor, cancellationToken);
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
                Span<char> replacement = stackalloc char[384];
                int replacementLength = WriterNumber.FormatGeometry(size, replacement);
                int length = checked(appearance.Length - sizeLength + replacementLength);
                memory.Reserve(32L + length);
                byte[] bytes = new byte[length];
                GovernedBufferStream.CopyBytes(appearance.Span[..sizeOffset], bytes.AsSpan(0, sizeOffset), governor, cancellationToken, CancellationToken.None);
                Encoding.ASCII.GetBytes(replacement[..replacementLength], bytes.AsSpan(sizeOffset, replacementLength));
                GovernedBufferStream.CopyBytes(appearance.Span[(sizeOffset + sizeLength)..], bytes.AsSpan(sizeOffset + replacementLength), governor, cancellationToken, CancellationToken.None);
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
                && ResolveFieldValue(encoding, resolver, governor, token) is PdfName name) { return name; }
        }
        return null;
    }

    private static PdfDictionary? TryMergeDrFont(PdfDictionary appearance, ReadOnlySpan<byte> fontName,
        PdfDictionary? formResources, PoolResolver resolver, OwnedAllocationBatch graph)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        if (fontName.IsEmpty || formResources is null) { return null; }
        PdfDictionary? appearanceResources = null;
        if (PdfDictionaryLookup.TryGet(appearance, "Resources"u8, governor, token, out QpdfObject resourcesRaw)
            && resourcesRaw is not PdfNull)
        {
            appearanceResources = ResolveFieldValue(resourcesRaw, resolver, governor, token) as PdfDictionary;
            if (appearanceResources is null) { return null; }
        }
        if ((appearanceResources is not null && TryGetFontRaw(appearanceResources, fontName, resolver, governor, token) is not null)
            || TryGetFontRaw(formResources, fontName, resolver, governor, token) is not QpdfObject drFont) { return null; }

        PdfNameTable<QpdfObject> fonts = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        if (appearanceResources is not null && PdfDictionaryLookup.TryGet(appearanceResources, "Font"u8, governor, token, out QpdfObject fontsRaw))
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
        PdfDictionary merged;
        if (appearanceResources is not null) { merged = PdfDictionaryRebuild.ReplaceEntry(graph, appearanceResources, "Font"u8, mergedFonts); }
        else
        {
            PdfNameTable<QpdfObject> resources = PdfDictionaryRebuild.CreateEntries(graph, out long resourceBytes);
            resources.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Font"u8), mergedFonts, token);
            merged = PdfDictionaryRebuild.Complete(graph, resources, resourceBytes);
        }
        return merged;
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
        // Locate one complete text block before constructing output. All other
        // bytes are borrowed unchanged, including comments and inline images.
        memory.Reserve(192L);
        ContentTokenizer tokenizer = new(data, governor, cancellationToken);
        List<ContentTokenKind> containers = ParserContainers.CreateList<ContentTokenKind>(memory);
        int depth = 0, selectedDepth = 0, start = -1, end = -1, operands = 0;
        bool textTag = false, namedTag = false, properties = false;
        Span<byte> tagBytes = stackalloc byte[2];
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            governor.NoteOperation();
            if (token.Kind is ContentTokenKind.ArrayClose or ContentTokenKind.DictionaryClose)
            {
                ContentTokenKind opening = token.Kind == ContentTokenKind.ArrayClose ? ContentTokenKind.ArrayOpen : ContentTokenKind.DictionaryOpen;
                if (containers.Count == 0 || containers[^1] != opening)
                { throw new QpdfParseException("Mismatched appearance container.", token.Offset, null); }
                containers.RemoveAt(containers.Count - 1); continue;
            }
            if (token.Kind is ContentTokenKind.ArrayOpen or ContentTokenKind.DictionaryOpen)
            {
                if (containers.Count == 0)
                { if (operands == 1) { properties = token.Kind == ContentTokenKind.DictionaryOpen; } operands++; }
                if (containers.Count >= limits.MaxNestingDepth)
                { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), containers.Count + 1L, limits.MaxNestingDepth); }
                ParserContainers.AddItem(memory, containers, token.Kind, sizeof(int)); continue;
            }
            if (containers.Count != 0) { continue; }
            if (token.Kind != ContentTokenKind.Operator)
            {
                if (operands == 1) { properties = token.Kind == ContentTokenKind.Name; }
                if (operands++ == 0)
                {
                    namedTag = token.Kind == ContentTokenKind.Name;
                    if (namedTag && token.Data.Length <= 6)
                    {
                        PdfNameBytes tag = PdfNameBytes.Decode(token.Data.Span, governor, cancellationToken);
                        if (tag.Length == 2) { tag.CopyTo(tagBytes, governor, cancellationToken); textTag = tagBytes.SequenceEqual("Tx"u8); }
                    }
                }
                continue;
            }
            ReadOnlySpan<byte> word = token.Data.Span;
            if (word.SequenceEqual("BMC"u8) || word.SequenceEqual("BDC"u8))
            {
                bool simple = word.SequenceEqual("BMC"u8);
                if (!namedTag || operands != (simple ? 1 : 2) || (!simple && !properties))
                { throw new QpdfParseException("Invalid marked-content operands in appearance.", token.Offset, null); }
                if (depth >= limits.MaxNestingDepth)
                { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth + 1L, limits.MaxNestingDepth); }
                depth++;
                if (start < 0 && textTag) { start = checked((int)tokenizer.Position); selectedDepth = depth; }
            }
            else if (word.SequenceEqual("EMC"u8))
            {
                if (depth == 0) { throw new QpdfParseException("Unmatched EMC in appearance.", token.Offset, null); }
                if (depth == selectedDepth && end < 0) { end = checked((int)tokenizer.Position); }
                depth--;
            }
            else if (word.SequenceEqual("ID"u8)) { tokenizer.TryReadInlineImageData(out _); }
            operands = 0; textTag = false; namedTag = false; properties = false;
        }
        if (depth != 0 || containers.Count != 0)
        { throw new QpdfParseException("Unterminated marked content or container in appearance.", data.Length, null); }
        using GovernedBufferStream output = new(governor, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), cancellationToken, 256);
        output.Write(start < 0 ? data.Span : data.Span[..start], cancellationToken);
        AppendAscii(output, start < 0 ? "\n/Tx BMC\n" : "\n");
        WriteTextAppearance(output, appearance, value, options, size, box, governor, memory, cancellationToken);
        if (end >= 0) { output.Write(data.Span[end..], cancellationToken); }
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
            WriterNumber.AppendGeometry(built, box[0]);
            AppendAscii(built, " ");
            WriterNumber.AppendGeometry(built, box[1] + top - (lineHeight * (highlightIndex + 1L)));
            AppendAscii(built, " ");
            WriterNumber.AppendGeometry(built, box[2] - box[0]);
            AppendAscii(built, " ");
            WriterNumber.AppendGeometry(built, lineHeight);
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
                WriterNumber.AppendGeometry(built, box[0] + step);
                AppendAscii(built, " ");
                WriterNumber.AppendGeometry(built, box[1] + top);
                AppendAscii(built, " Td\n");
            }
            else
            {
                AppendAscii(built, "0 ");
                WriterNumber.AppendGeometry(built, -lineHeight);
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
    private static QpdfObject? ResolveOrNull(PoolResolver resolver, PdfReference reference)
    {
        if (((IIndirectResolver)resolver).TryResolve(reference, out QpdfObject? found) && found is not null && found is not PdfNull)
        {
            return found;
        }

        return null;
    }

}

