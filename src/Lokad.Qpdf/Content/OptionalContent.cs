using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal sealed record CopiedOptionalContent(QpdfObject Properties, bool CanCombine);

/// <summary>Preserves copied groups' source default states without importing secondary catalogs.</summary>
internal static class OptionalContent
{
    internal static void Capture(DocumentEngine engine, GraphCopier copier, IReadOnlyDictionary<PdfName, QpdfObject>? extras, ParserMemoryScope ownership,
        ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary? catalog = CatalogCopy.ResolveDictionary(Read(engine.Trailer, "Root"u8, governor, token), engine, governor, token);
        QpdfObject? rawProperties = catalog is null ? null : Read(catalog, "OCProperties"u8, governor, token);
        PdfDictionary? properties = CatalogCopy.ResolveDictionary(rawProperties, engine, governor, token);
        if (properties is null || CatalogCopy.ResolveValue(Read(properties, "OCGs"u8, governor, token), engine, governor, token) is not PdfArray sourceGroups)
        { return; }
        // The source's registration array supplies group identities. Ordinary
        // documents never scan the copied pool, and unused groups stay unread.
        bool any = false;
        PdfDictionary? defaults = CatalogCopy.ResolveDictionary(Read(properties, "D"u8, governor, token), engine, governor, token);
        bool simple = Simple(defaults, engine, governor, token) && NoAlternates(properties, engine, governor, token);
        for (int index = 0; index < sourceGroups.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (TryMappedGroup(sourceGroups[index], engine, copier, governor, token, out _, out PdfDictionary? group))
            {
                any = true;
                if (group is not null && !DefaultIntent(Read(group, "Intent"u8, governor, token), engine, governor, token)) { simple = false; }
            }
        }
        if (!any) { return; }
        QpdfObject? retained = CatalogCopy.ReadExtra(extras, "OCProperties"u8, governor, token);
        if (retained is not null || !simple || rawProperties is PdfReference existing && copier.SourceToDestination.ContainsKey(existing.ObjectNumber))
        {
            QpdfObject result = retained ?? copier.CopyValue(rawProperties ?? properties, 0);
            ownership.Reserve(32L); copier.OptionalContent = new(result, simple); return;
        }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        HashSet<int> off = ParserContainers.CreateSet<int>(scratch);
        if (defaults is not null)
        {
            QpdfObject? rawOff = CatalogCopy.ResolveValue(Read(defaults, "OFF"u8, governor, token), engine, governor, token);
            if (rawOff is not null and not PdfNull && rawOff is not PdfArray)
            { throw new QpdfException("Optional-content OFF states must be an array."); }
            if (rawOff is PdfArray disabled)
            {
                for (int index = 0; index < disabled.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (TryMappedGroup(disabled[index], engine, copier, governor, token, out int mapped, out _))
                    { ParserContainers.AddSetItem(scratch, off, mapped, sizeof(int)); }
                }
            }
        }
        List<QpdfObject> members = ParserContainers.CreateList<QpdfObject>(scratch);
        List<QpdfObject> disabledMembers = ParserContainers.CreateList<QpdfObject>(scratch);
        HashSet<int> seen = ParserContainers.CreateSet<int>(scratch);
        for (int index = 0; index < sourceGroups.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (!TryMappedGroup(sourceGroups[index], engine, copier, governor, token, out int number, out _)
                || !ParserContainers.AddSetItem(scratch, seen, number, sizeof(int))) { continue; }
            graph.Memory.Reserve(32L); graph.Add(32L); PdfReference member = new(number, 0);
            ParserContainers.AddItem(scratch, members, member, IntPtr.Size);
            if (off.Contains(number)) { ParserContainers.AddItem(scratch, disabledMembers, member, IntPtr.Size); }
        }
        PdfDictionary minimal = Properties(graph, members, disabledMembers, null, null);
        graph.Memory.Reserve(32L); graph.Add(32L);
        copier.OptionalContent = new(minimal, true);
        graph.Memory.TransferTo(ownership, graph.Bytes);
    }

    private static bool TryMappedGroup(QpdfObject value, DocumentEngine engine, GraphCopier copier, ResourceGovernor governor,
        CancellationToken token, out int number, out PdfDictionary? group)
    {
        number = 0; group = null;
        if (value is not PdfReference reference || !copier.SourceToDestination.TryGetValue(reference.ObjectNumber, out number)
            || !IsGroup(copier, number, governor, token)) { return false; }
        group = CatalogCopy.ResolveDictionary(reference, engine, governor, token); return group is not null;
    }


    internal static QpdfObject? Combine(GraphCopier? primaryCopier, IReadOnlyList<GraphCopier> sources, IReadOnlyList<GraphCopier> placements,
        IReadOnlyDictionary<PdfName, QpdfObject>? extras, IReadOnlyDictionary<int, QpdfObject> pool,
        ResourceGovernor governor, CancellationToken token)
    {
        bool any = primaryCopier?.OptionalContent is not null;
        foreach (GraphCopier copier in sources) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); any |= copier.OptionalContent is not null; }
        foreach (GraphCopier copier in placements) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); any |= copier.OptionalContent is not null; }
        if (!any) { return null; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(32L);
        PoolResolver resolver = new(pool);
        PdfDictionary? primary = null;
        QpdfObject? primaryRaw = CatalogCopy.ReadExtra(extras, "OCProperties"u8, governor, token);
        primary = CatalogCopy.ResolveDictionary(primaryRaw, resolver, governor, token);
        HashSet<GraphCopier> seen = ParserContainers.CreateSet<GraphCopier>(scratch);
        List<CopiedOptionalContent> contributions = ParserContainers.CreateList<CopiedOptionalContent>(scratch);
        GraphCopier? changedRoots = null;
        if (primaryCopier?.OptionalContent is CopiedOptionalContent primaryContent)
        {
            ParserContainers.AddSetItem(scratch, seen, primaryCopier, IntPtr.Size);
            ParserContainers.AddItem(scratch, contributions, primaryContent, IntPtr.Size); changedRoots = primaryCopier;
        }
        for (int part = 0; part < 2; part++)
        {
            IReadOnlyList<GraphCopier> copiers = part == 0 ? sources : placements;
            foreach (GraphCopier copier in copiers)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (copier.OptionalContent is CopiedOptionalContent content && ParserContainers.AddSetItem(scratch, seen, copier, IntPtr.Size))
                { ParserContainers.AddItem(scratch, contributions, content, IntPtr.Size); changedRoots ??= copier; }
            }
        }
        if (contributions.Count == 1 && primary is null) { return contributions[0].Properties; }
        if (contributions.Count == 1 && ReferenceEquals(primaryCopier?.OptionalContent, contributions[0])) { return primaryRaw; }
        PdfDictionary? primaryDefaults = primary is null ? null : CatalogCopy.ResolveDictionary(Read(primary, "D"u8, governor, token), resolver, governor, token);
        if (!Simple(primaryDefaults, resolver, governor, token) || primary is not null && !NoAlternates(primary, resolver, governor, token)
            || contributions.Exists(static source => !source.CanCombine))
        { throw new QpdfException("Combining optional-content defaults with automatic states, alternate configurations or non-View intents is outside the supported profile."); }
        List<QpdfObject> members = ParserContainers.CreateList<QpdfObject>(scratch);
        List<QpdfObject> off = ParserContainers.CreateList<QpdfObject>(scratch);
        HashSet<int> groupIds = ParserContainers.CreateSet<int>(scratch), offIds = ParserContainers.CreateSet<int>(scratch);
        foreach (CopiedOptionalContent contribution in contributions)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            PdfDictionary properties = CatalogCopy.ResolveDictionary(contribution.Properties, resolver, governor, token)
                ?? throw new QpdfException("Missing copied optional-content properties.");
            PdfArray groups = CatalogCopy.ResolveValue(Read(properties, "OCGs"u8, governor, token), resolver, governor, token) as PdfArray
                ?? throw new QpdfException("Missing copied optional-content groups.");
            for (int index = 0; index < groups.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (groups[index] is PdfReference group && ParserContainers.AddSetItem(scratch, groupIds, group.ObjectNumber, sizeof(int)))
                { ParserContainers.AddItem(scratch, members, group, IntPtr.Size); }
            }
            PdfDictionary? defaults = CatalogCopy.ResolveDictionary(Read(properties, "D"u8, governor, token), resolver, governor, token);
            QpdfObject? rawOff = defaults is null ? null : CatalogCopy.ResolveValue(Read(defaults, "OFF"u8, governor, token), resolver, governor, token);
            if (rawOff is not null and not PdfNull && rawOff is not PdfArray)
            { throw new QpdfException("Optional-content OFF states must be an array."); }
            if (rawOff is PdfArray disabled)
            {
                for (int index = 0; index < disabled.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (disabled[index] is PdfReference group && ParserContainers.AddSetItem(scratch, offIds, group.ObjectNumber, sizeof(int)))
                    { ParserContainers.AddItem(scratch, off, group, IntPtr.Size); }
                }
            }
        }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary result = Properties(graph, members, off, primary, primaryDefaults);
        graph.Retain();
        changedRoots?.MarkChangedRoots();
        return result;
    }

    private static PdfDictionary Properties(OwnedAllocationBatch graph, IReadOnlyList<QpdfObject> groups,
        IReadOnlyList<QpdfObject> off, PdfDictionary? original, PdfDictionary? originalDefaults)
    {
        PdfDictionary Empty() => PdfDictionaryRebuild.Complete(graph, PdfDictionaryRebuild.CreateEntries(graph, out long bytes), bytes);
        PdfDictionary defaults = originalDefaults ?? Empty();
        defaults = PdfDictionaryRebuild.ReplaceEntry(graph, defaults, "BaseState"u8, PdfDictionaryRebuild.CreateName(graph, "ON"u8));
        defaults = PdfDictionaryRebuild.RemoveEntry(graph, defaults, "ON"u8);
        defaults = PdfDictionaryRebuild.ReplaceEntry(graph, defaults, "OFF"u8, Array(graph, off));
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, original ?? Empty(), "OCGs"u8, Array(graph, groups));
        return PdfDictionaryRebuild.ReplaceEntry(graph, result, "D"u8, defaults);
    }

    private static PdfArray Array(OwnedAllocationBatch graph, IReadOnlyList<QpdfObject> source)
    {
        long bytes = checked(64L + IntPtr.Size * (long)source.Count); graph.Memory.Reserve(bytes);
        QpdfObject[] items = new QpdfObject[source.Count];
        for (int index = 0; index < items.Length; index++) { graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation(); items[index] = source[index]; }
        graph.Add(bytes); return PdfArray.FromOwnedItems(items);
    }

    private static QpdfObject? Read(PdfDictionary dictionary, ReadOnlySpan<byte> key, ResourceGovernor governor, CancellationToken token)
        => PdfDictionaryLookup.TryGet(dictionary, key, governor, token, out QpdfObject value) ? value : null;

    private static bool IsGroup(GraphCopier copier, int number, ResourceGovernor governor, CancellationToken token)
    {
        if (!copier.CopiedObjects.TryGetValue(number, out QpdfObject? value) || value is not PdfDictionary dictionary
            || !PdfDictionaryLookup.TryGet(dictionary, "Type"u8, governor, token, out QpdfObject raw)) { return false; }
        QpdfObject? type = raw;
        if (raw is PdfReference reference) { PdfObjectLookup.TryResolve(reference, copier.CopiedObjects, governor, token, out type); }
        return type is PdfName name && name.RawSpan.SequenceEqual("OCG"u8);
    }

    private static bool Simple(PdfDictionary? defaults, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        if (defaults is null) { return true; }
        QpdfObject? state = CatalogCopy.ResolveValue(Read(defaults, "BaseState"u8, governor, token), resolver, governor, token);
        if (state is not null and not PdfNull && (state is not PdfName name || !name.RawSpan.SequenceEqual("ON"u8))) { return false; }
        QpdfObject? automatic = CatalogCopy.ResolveValue(Read(defaults, "AS"u8, governor, token), resolver, governor, token);
        return (automatic is null or PdfNull || automatic is PdfArray { Count: 0 })
            && DefaultIntent(Read(defaults, "Intent"u8, governor, token), resolver, governor, token);
    }

    private static bool NoAlternates(PdfDictionary properties, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        QpdfObject? configurations = CatalogCopy.ResolveValue(Read(properties, "Configs"u8, governor, token), resolver, governor, token);
        return configurations is null or PdfNull || configurations is PdfArray { Count: 0 };
    }

    private static bool DefaultIntent(QpdfObject? raw, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        QpdfObject? value = CatalogCopy.ResolveValue(raw, resolver, governor, token);
        if (value is null or PdfNull) { return true; }
        if (value is PdfName name) { return name.RawSpan.SequenceEqual("View"u8); }
        return value is PdfArray { Count: 1 } array
            && CatalogCopy.ResolveValue(array[0], resolver, governor, token) is PdfName member && member.RawSpan.SequenceEqual("View"u8);
    }
}
