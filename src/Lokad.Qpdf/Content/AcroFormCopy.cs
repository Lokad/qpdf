using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>
/// One source document's preserved interactive form, already copied into its
/// destination pool: the source-side filtered field references plus the other
/// catalog-level form entries needed to merge and rebuild the output form.
/// </summary>
internal sealed record CopiedForm(
    IReadOnlyList<PdfReference> Fields,
    bool HasNeedAppearances,
    bool NeedAppearances,
    QpdfObject? DefaultResources,
    bool HasCalculationOrder,
    IReadOnlyList<QpdfObject> CalculationOrder,
    IReadOnlyDictionary<PdfName, QpdfObject> Extras);

/// <summary>
/// Interactive-form preservation through the shared copy engine. Ordinary
/// selection and rewrite paths carry the input catalog's form dictionary
/// (field registration, values, parent links and default resources) into the
/// fresh output catalog; only the explicit <c>--remove-acroform</c> prune drops
/// it. Top-level fields whose widgets all fall outside the kept pages are
/// pruned, matching the upstream reference: a field stays when at least one
/// widget in its subtree survives, and widget-free registrations stay verbatim.
/// Merges concatenate per-source registrations without renaming colliding
/// names, keep the first default-resources dictionary, and OR the
/// NeedAppearances flags; collision renaming stays an explicit gap.
/// </summary>
internal static class AcroFormCopy
{
    internal static CopiedForm? TryCopySourceForm(
        DocumentEngine engine,
        GraphCopier copier,
        IReadOnlyList<PageLocation> keptPages,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (keptPages is null)
        {
            throw new ArgumentNullException(nameof(keptPages));
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
        PdfDictionary? source = TryGetSourceForm(engine);
        if (source is null)
        {
            return null;
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        HashSet<int> keptWidgets = CollectKeptWidgets(resolver, keptPages, limits, governor, cancellationToken);
        List<QpdfObject> keptEntries = FilterTopEntries(resolver, source, keptWidgets, limits, governor, cancellationToken);
        List<PdfReference> copiedFields = new(keptEntries.Count);
        foreach (QpdfObject entry in keptEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (entry is PdfReference)
            {
                if (copier.CopyValue(entry, 0) is PdfReference copiedReference)
                {
                    copiedFields.Add(copiedReference);
                }

                continue;
            }

            PdfReference link = copier.AddIndirectObject(entry) ?? throw new QpdfException("Form field copy pruned unexpectedly.");
            copiedFields.Add(link);
        }
        bool hasNeed = false;
        bool need = false;
        QpdfObject? resources = null;
        bool hasOrder = false;
        List<QpdfObject> order = new();
        Dictionary<PdfName, QpdfObject> extras = new();
        foreach (PdfName key in source.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            string text = key.DisplayText;
            if (text == "Fields")
            {
                continue;
            }

            if (!source.TryGet(key, out QpdfObject? raw) || raw is null || raw is PdfNull)
            {
                continue;
            }

            if (text == "NeedAppearances" && raw is PdfBoolean flag)
            {
                hasNeed = true;
                need = flag.Value;
                continue;
            }

            QpdfObject copied = copier.CopyValue(raw, 0);
            if (text == "DR")
            {
                resources = copied;
                continue;
            }

            if (text == "CO" && copied is PdfArray copiedOrder)
            {
                hasOrder = true;
                foreach (QpdfObject item in copiedOrder.Items)
                {
                    order.Add(item);
                }

                continue;
            }

            governor.ReserveMemory(32L);
            extras[key] = copied;
        }

        return new CopiedForm(copiedFields, hasNeed, need, resources, hasOrder, order, extras);
    }

    internal static PdfDictionary? BuildSingle(
        DocumentEngine engine,
        GraphCopier copier,
        IReadOnlyList<PageLocation> keptPages,
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        IReadOnlyDictionary<int, QpdfObject> pool,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (keptPages is null)
        {
            throw new ArgumentNullException(nameof(keptPages));
        }

        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
        }

        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
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

        CopiedForm? part = TryCopySourceForm(engine, copier, keptPages, limits, governor, diagnostics, cancellationToken);
        if (part is null)
        {
            return null;
        }

        HashSet<int> survivors = CollectAnnotRefs(occurrences, governor, cancellationToken);
        return BuildCatalogForm(part, pool, survivors, limits, governor, cancellationToken);
    }

    internal static CopiedForm? Combine(IReadOnlyList<CopiedForm> parts)
    {
        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        List<CopiedForm> present = new(parts.Count);
        foreach (CopiedForm part in parts)
        {
            if (part is null)
            {
                throw new ArgumentException("Form parts must not contain null entries.", nameof(parts));
            }

            present.Add(part);
        }

        if (present.Count == 0)
        {
            return null;
        }

        List<PdfReference> fields = new();
        bool hasNeed = false;
        bool need = false;
        QpdfObject? resources = null;
        bool hasOrder = false;
        List<QpdfObject> order = new();
        Dictionary<PdfName, QpdfObject> extras = new();
        foreach (CopiedForm part in present)
        {
            fields.AddRange(part.Fields);
            if (part.HasNeedAppearances)
            {
                hasNeed = true;
                need = need || part.NeedAppearances;
            }

            if (resources is null && part.DefaultResources is not null)
            {
                resources = part.DefaultResources;
            }

            if (part.HasCalculationOrder)
            {
                hasOrder = true;
                order.AddRange(part.CalculationOrder);
            }

            foreach (KeyValuePair<PdfName, QpdfObject> extra in part.Extras)
            {
                if (!extras.ContainsKey(extra.Key))
                {
                    extras[extra.Key] = extra.Value;
                }
            }
        }

        return new CopiedForm(fields, hasNeed, need, resources, hasOrder, order, extras);
    }

    /// <summary>
    /// Renames colliding field names across merged sources with upstream
    /// `+N` suffixes: each source sees the final names of strictly earlier sources,
    /// walks field subtrees top-down recomputing fully-qualified names, keeps
    /// originals absent from that snapshot, and rewrites the smallest free `+N`
    /// suffix onto colliding named nodes. Single-form merges keep intra-document
    /// duplicates like the reference; nodes without an indirect identity keep
    /// current behavior.
    /// </summary>
    internal static void RenameCollidingFields(
        IReadOnlyList<CopiedForm?> forms,
        IReadOnlyList<GraphCopier> copiers,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (forms is null)
        {
            throw new ArgumentNullException(nameof(forms));
        }
        if (copiers is null)
        {
            throw new ArgumentNullException(nameof(copiers));
        }
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }
        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }
        if (forms.Count != copiers.Count)
        {
            throw new ArgumentException("Form parts and copiers must align.", nameof(copiers));
        }
        limits.Validate();
        int present = 0;
        foreach (CopiedForm? form in forms)
        {
            if (form is not null)
            {
                present++;
            }
        }
        if (present < 2)
        {
            return;
        }
        HashSet<string> accumulated = new(StringComparer.Ordinal);
        for (int s = 0; s < forms.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopiedForm? form = forms[s];
            if (form is null)
            {
                continue;
            }
            GraphCopier copier = copiers[s] ?? throw new ArgumentException("Copiers must not contain null entries.", nameof(copiers));
            HashSet<string> snapshot = new(accumulated, StringComparer.Ordinal);
            List<string> finals = new();
            HashSet<int> visited = new();
            foreach (PdfReference fieldRef in form.Fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (!copier.CopiedObjects.TryGetValue(fieldRef.ObjectNumber, out QpdfObject? nodeRaw) || nodeRaw is not PdfDictionary node)
                {
                    continue;
                }
                RenameTree(copier, fieldRef.ObjectNumber, node, [], snapshot, finals, visited, limits, governor, cancellationToken, 0);
            }
            foreach (string final in finals)
            {
                accumulated.Add(final);
            }
        }
    }

    private static void RenameTree(
        GraphCopier copier,
        int? nodeId,
        PdfDictionary node,
        IReadOnlyList<string?> ancestors,
        HashSet<string> snapshot,
        List<string> finals,
        HashSet<int> visited,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        int depth)
    {
        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }
        if (ancestors is null)
        {
            throw new ArgumentNullException(nameof(ancestors));
        }
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        if (finals is null)
        {
            throw new ArgumentNullException(nameof(finals));
        }
        if (visited is null)
        {
            throw new ArgumentNullException(nameof(visited));
        }
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }
        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }
        cancellationToken.ThrowIfCancellationRequested();
        governor.ChargeWork(1L);
        if (depth > limits.MaxNestingDepth)
        {
            return;
        }
        if (nodeId.HasValue && !visited.Add(nodeId.Value))
        {
            return;
        }
        PdfByteString? ownBytes = ReadFieldText(copier, node);
        string? own = ownBytes is null ? null : Attachments.PdfText.Decode(ownBytes.Value);
        string? effective = own;
        if (own is not null)
        {
            List<string> parts = new();
            foreach (string? ancestor in ancestors)
            {
                if (ancestor is not null)
                {
                    parts.Add(ancestor);
                }
            }
            parts.Add(own);
            string qualified = string.Join(".", parts);
            if (!snapshot.Contains(qualified))
            {
                finals.Add(qualified);
            }
            else if (nodeId.HasValue)
            {
                string renamed = qualified;
                int suffix = 0;
                while (snapshot.Contains(renamed))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.ChargeWork(1L);
                    suffix++;
                    renamed = qualified.Substring(0, qualified.Length - own.Length) + own + "+" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                byte[] renamedBytes;
                if (!Attachments.PdfText.TryEncodePdfDoc(renamed.Substring(qualified.Length - own.Length), out renamedBytes))
                {
                    renamedBytes = Utf16Be(renamed.Substring(qualified.Length - own.Length));
                }
                governor.ReserveMemory(renamedBytes.Length + 64L);
                Dictionary<PdfName, QpdfObject> entries = new();
                foreach (PdfName key in node.Keys)
                {
                    if (!node.TryGet(key, out QpdfObject? entry) || entry is null)
                    {
                        continue;
                    }
                    governor.ChargeWork(1L);
                    entries[key] = key.DisplayText == "T" ? new PdfByteString(renamedBytes, false) : entry;
                }
                copier.ReplaceCopiedObject(nodeId.Value, new PdfDictionary(entries));
                effective = renamed.Substring(qualified.Length - own.Length);
                finals.Add(renamed);
                }
                else
                {
                    finals.Add(qualified);
                }
            }
            List<string?> childAncestors = [..ancestors, effective];
            if (node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) && kidsRaw is not null && kidsRaw is not PdfNull)
            {
                PdfArray? kids = null;
                if (kidsRaw is PdfArray directKids)
                {
                    kids = directKids;
                }
                else if (kidsRaw is PdfReference kidsRef && copier.CopiedObjects.TryGetValue(kidsRef.ObjectNumber, out QpdfObject? kidsResolved) && kidsResolved is PdfArray indirectKids)
                {
                    kids = indirectKids;
                }
                if (kids is not null)
                {
                    for (int i = 0; i < kids.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        governor.ChargeWork(1L);
                        if (kids[i] is PdfReference kidRef && copier.CopiedObjects.TryGetValue(kidRef.ObjectNumber, out QpdfObject? kidRaw) && kidRaw is PdfDictionary kid)
                        {
                            RenameTree(copier, kidRef.ObjectNumber, kid, childAncestors, snapshot, finals, visited, limits, governor, cancellationToken, depth + 1);
                        }
                        else if (kids[i] is PdfDictionary directKid)
                        {
                            RenameTree(copier, null, directKid, childAncestors, snapshot, finals, visited, limits, governor, cancellationToken, depth + 1);
                        }
                    }
                }
            }
    }

    private static PdfByteString? ReadFieldText(GraphCopier copier, PdfDictionary node)
    {
        if (!node.TryGet(PdfName.FromAscii("T"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }
        if (raw is PdfReference textRef)
        {
            if (!copier.CopiedObjects.TryGetValue(textRef.ObjectNumber, out QpdfObject? resolved) || resolved is not PdfByteString resolvedText)
            {
                return null;
            }
            return resolvedText;
        }
        return raw as PdfByteString;
    }

    private static byte[] Utf16Be(string value)
    {
        byte[] bytes = new byte[checked(2 + value.Length * 2)];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        for (int i = 0; i < value.Length; i++)
        {
            bytes[2 + (i * 2)] = (byte)(value[i] >> 8);
            bytes[2 + (i * 2) + 1] = (byte)(value[i] & 0xFF);
        }
        return bytes;
    }

    internal static HashSet<int> CollectAnnotRefs(
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        HashSet<int> found = new();
        foreach ((GraphCopier copier, int pageId) in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (copier is null)
            {
                throw new ArgumentException("Occurrences must not contain null copiers.", nameof(occurrences));
            }

            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary page)
            {
                continue;
            }

            if (!page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            PdfArray? annots = ResolvePoolArray(copier.CopiedObjects, annotsRaw);
            if (annots is null)
            {
                continue;
            }

            for (int i = 0; i < annots.Count; i++)
            {
                governor.ChargeWork(1L);
                if (annots[i] is PdfReference reference)
                {
                    found.Add(reference.ObjectNumber);
                }
            }
        }

        return found;
    }

    internal static PdfDictionary? BuildCatalogForm(
        CopiedForm combined,
        IReadOnlyDictionary<int, QpdfObject> pool,
        HashSet<int> survivingAnnots,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (combined is null)
        {
            throw new ArgumentNullException(nameof(combined));
        }

        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        if (survivingAnnots is null)
        {
            throw new ArgumentNullException(nameof(survivingAnnots));
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
        if (combined.Fields.Count == 0)
        {
            return Assemble(combined, [], []);
        }

        (List<PdfReference> kept, HashSet<int> closure) = PruneFields(pool, combined.Fields, survivingAnnots, limits, governor, cancellationToken);
        if (kept.Count == 0)
        {
            return null;
        }

        List<QpdfObject> order = new();
        if (combined.HasCalculationOrder)
        {
            foreach (QpdfObject item in combined.CalculationOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (item is PdfReference reference && !closure.Contains(reference.ObjectNumber))
                {
                    continue;
                }

                order.Add(item);
            }
        }

        return Assemble(combined, kept, order);
    }

    internal static HashSet<int> CollectRefNumbers(QpdfObject value, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        HashSet<int> found = new();
        Stack<QpdfObject> stack = new();
        stack.Push(value);
        while (stack.TryPop(out QpdfObject? current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (current is PdfReference reference)
            {
                found.Add(reference.ObjectNumber);
            }
            else if (current is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    stack.Push(array[i]);
                }
            }
            else if (current is PdfDictionary dictionary)
            {
                foreach (PdfName key in dictionary.Keys)
                {
                    if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null)
                    {
                        stack.Push(entry);
                    }
                }
            }
            else if (current is PdfStream stream)
            {
                stack.Push(stream.Dictionary);
            }
        }

        return found;
    }

    private static PdfDictionary Assemble(CopiedForm combined, IReadOnlyList<PdfReference> fields, IReadOnlyList<QpdfObject> order)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        entries[PdfName.FromAscii("Fields")] = new PdfArray([.. fields]);
        if (combined.HasNeedAppearances)
        {
            entries[PdfName.FromAscii("NeedAppearances")] = new PdfBoolean(combined.NeedAppearances);
        }

        if (combined.DefaultResources is not null)
        {
            entries[PdfName.FromAscii("DR")] = combined.DefaultResources;
        }

        if (combined.HasCalculationOrder)
        {
            entries[PdfName.FromAscii("CO")] = new PdfArray([.. order]);
        }

        foreach (KeyValuePair<PdfName, QpdfObject> extra in combined.Extras)
        {
            if (!entries.ContainsKey(extra.Key))
            {
                entries[extra.Key] = extra.Value;
            }
        }

        return new PdfDictionary(entries);
    }

    private static (List<PdfReference> Kept, HashSet<int> Closure) PruneFields(
        IReadOnlyDictionary<int, QpdfObject> pool,
        IReadOnlyList<PdfReference> fields,
        HashSet<int> survivingAnnots,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        List<PdfReference> kept = new(fields.Count);
        HashSet<int> closure = new();
        foreach (PdfReference field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (field is null)
            {
                continue;
            }

            HashSet<int> members = WalkPoolMembers(pool, field.ObjectNumber, limits, governor, cancellationToken);
            if (members.Count == 0)
            {
                kept.Add(field);
                closure.Add(field.ObjectNumber);
                foreach (int id in CollectPoolSubtree(pool, field.ObjectNumber, limits, governor, cancellationToken))
                {
                    closure.Add(id);
                }

                continue;
            }

            bool survives = false;
            foreach (int member in members)
            {
                if (survivingAnnots.Contains(member))
                {
                    survives = true;
                    break;
                }
            }

            if (!survives)
            {
                continue;
            }

            kept.Add(field);
            closure.Add(field.ObjectNumber);
            foreach (int id in CollectPoolSubtree(pool, field.ObjectNumber, limits, governor, cancellationToken))
            {
                closure.Add(id);
            }
        }

        return (kept, closure);
    }

    private static PdfDictionary? TryGetSourceForm(DocumentEngine engine)
    {
        if (!engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) || root is null || root is PdfNull)
        {
            return null;
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        PdfDictionary? catalog = null;
        if (root is PdfReference rootReference)
        {
            if (resolver.TryResolve(rootReference, out QpdfObject? resolved) && resolved is PdfDictionary resolvedCatalog)
            {
                catalog = resolvedCatalog;
            }
        }
        else
        {
            catalog = root as PdfDictionary;
        }

        if (catalog is null)
        {
            return null;
        }

        if (!catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? form) || form is null || form is PdfNull)
        {
            return null;
        }

        if (form is PdfReference formReference)
        {
            if (resolver.TryResolve(formReference, out QpdfObject? resolved) && resolved is PdfDictionary resolvedForm)
            {
                return resolvedForm;
            }

            throw new QpdfParseException("Unresolvable catalog /AcroForm reference.", null, formReference.ObjectNumber);
        }

        if (form is PdfDictionary direct)
        {
            return direct;
        }

        throw new QpdfParseException("Catalog /AcroForm must be a dictionary.", null, null);
    }

    private static HashSet<int> CollectKeptWidgets(
        IIndirectResolver resolver,
        IReadOnlyList<PageLocation> keptPages,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> found = new();
        foreach (PageLocation location in keptPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (location.Page is null)
            {
                continue;
            }

            if (!location.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            QpdfObject annotsTarget = annotsRaw is PdfReference annotsReference
                && resolver.TryResolve(annotsReference, out QpdfObject? resolved)
                ? resolved ?? annotsRaw
                : annotsRaw;
            if (annotsTarget is not PdfArray annots)
            {
                continue;
            }

            for (int i = 0; i < annots.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (annots[i] is not PdfReference itemReference)
                {
                    continue;
                }

                if (!resolver.TryResolve(itemReference, out QpdfObject? target) || target is not PdfDictionary annot)
                {
                    continue;
                }

                if (IsWidget(annot))
                {
                    found.Add(itemReference.ObjectNumber);
                }
            }
        }

        return found;
    }

    private static List<QpdfObject> FilterTopEntries(
        IIndirectResolver resolver,
        PdfDictionary source,
        HashSet<int> keptWidgets,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        List<QpdfObject> kept = new();
        if (!source.TryGet(PdfName.FromAscii("Fields"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return kept;
        }

        QpdfObject target = raw is PdfReference fieldsReference
            && resolver.TryResolve(fieldsReference, out QpdfObject? resolved)
            ? resolved ?? raw
            : raw;
        if (target is not PdfArray fields)
        {
            throw new QpdfParseException("Catalog /AcroForm /Fields must be an array.", null, null);
        }

        for (int i = 0; i < fields.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            QpdfObject entry = fields[i];
            if (entry is null || entry is PdfNull)
            {
                continue;
            }

            if (entry is not PdfReference reference)
            {
                kept.Add(entry);
                continue;
            }

            if (!resolver.TryResolve(reference, out QpdfObject? fieldTarget) || fieldTarget is null || fieldTarget is PdfNull)
            {
                throw new QpdfParseException("Unresolvable /AcroForm /Fields entry.", null, reference.ObjectNumber);
            }

            if (fieldTarget is not PdfDictionary field)
            {
                throw new QpdfParseException("Catalog /AcroForm /Fields entries must be dictionaries.", null, reference.ObjectNumber);
            }

            HashSet<int> members = WalkSourceMembers(resolver, field, reference.ObjectNumber, limits, governor, cancellationToken);
            if (members.Count == 0)
            {
                kept.Add(entry);
                continue;
            }

            bool survives = false;
            foreach (int member in members)
            {
                if (keptWidgets.Contains(member))
                {
                    survives = true;
                    break;
                }
            }

            if (survives)
            {
                kept.Add(entry);
            }
        }

        return kept;
    }

    private static HashSet<int> WalkSourceMembers(
        IIndirectResolver resolver,
        PdfDictionary root,
        int rootNumber,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> members = new();
        if (IsWidget(root))
        {
            members.Add(rootNumber);
        }

        HashSet<int> visited = new() { rootNumber };
        Stack<(PdfDictionary Node, int Depth)> stack = new();
        stack.Push((root, 0));
        while (stack.TryPop(out (PdfDictionary Node, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (current.Depth > limits.MaxNestingDepth)
            {
                continue;
            }

            if (!current.Node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) || kidsRaw is null || kidsRaw is PdfNull)
            {
                continue;
            }

            QpdfObject kidsTarget = kidsRaw is PdfReference kidsReference
                && resolver.TryResolve(kidsReference, out QpdfObject? kidsResolved)
                ? kidsResolved ?? kidsRaw
                : kidsRaw;
            if (kidsTarget is not PdfArray kids)
            {
                continue;
            }

            for (int i = 0; i < kids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (kids[i] is not PdfReference kidReference)
                {
                    continue;
                }

                if (!visited.Add(kidReference.ObjectNumber))
                {
                    continue;
                }

                if (!resolver.TryResolve(kidReference, out QpdfObject? kidTarget) || kidTarget is not PdfDictionary kid)
                {
                    continue;
                }

                if (IsWidget(kid))
                {
                    members.Add(kidReference.ObjectNumber);
                }

                stack.Push((kid, current.Depth + 1));
            }
        }

        return members;
    }

    private static HashSet<int> WalkPoolMembers(
        IReadOnlyDictionary<int, QpdfObject> pool,
        int rootNumber,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> members = new();
        if (!pool.TryGetValue(rootNumber, out QpdfObject? root) || root is not PdfDictionary rootDict)
        {
            return members;
        }

        if (IsWidget(rootDict))
        {
            members.Add(rootNumber);
        }

        HashSet<int> visited = new() { rootNumber };
        Stack<(PdfDictionary Node, int Depth)> stack = new();
        stack.Push((rootDict, 0));
        while (stack.TryPop(out (PdfDictionary Node, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (current.Depth > limits.MaxNestingDepth)
            {
                continue;
            }

            if (!current.Node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) || kidsRaw is null || kidsRaw is PdfNull)
            {
                continue;
            }

            PdfArray? kids = ResolvePoolArray(pool, kidsRaw);
            if (kids is null)
            {
                continue;
            }

            for (int i = 0; i < kids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (kids[i] is not PdfReference kidReference)
                {
                    continue;
                }

                if (!visited.Add(kidReference.ObjectNumber))
                {
                    continue;
                }

                if (!pool.TryGetValue(kidReference.ObjectNumber, out QpdfObject? kidTarget) || kidTarget is not PdfDictionary kid)
                {
                    continue;
                }

                if (IsWidget(kid))
                {
                    members.Add(kidReference.ObjectNumber);
                }

                stack.Push((kid, current.Depth + 1));
            }
        }

        return members;
    }

    private static HashSet<int> CollectPoolSubtree(
        IReadOnlyDictionary<int, QpdfObject> pool,
        int rootNumber,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> found = new();
        Stack<int> stack = new();
        stack.Push(rootNumber);
        while (stack.TryPop(out int number))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (!found.Add(number))
            {
                continue;
            }

            if (found.Count > limits.MaxWork)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), found.Count, limits.MaxWork);
            }

            if (!pool.TryGetValue(number, out QpdfObject? value) || value is null)
            {
                continue;
            }

            foreach (int id in CollectRefNumbers(value, governor, cancellationToken))
            {
                if (!found.Contains(id))
                {
                    stack.Push(id);
                }
            }
        }

        return found;
    }

    private static PdfArray? ResolvePoolArray(IReadOnlyDictionary<int, QpdfObject> pool, QpdfObject raw)
    {
        if (raw is PdfArray direct)
        {
            return direct;
        }

        if (raw is PdfReference reference
            && pool.TryGetValue(reference.ObjectNumber, out QpdfObject? resolved)
            && resolved is PdfArray array)
        {
            return array;
        }

        return null;
    }

    private static bool IsWidget(PdfDictionary dictionary)
    {
        return dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? raw)
            && raw is PdfName name
            && name.DisplayText == "Widget";
    }
}
