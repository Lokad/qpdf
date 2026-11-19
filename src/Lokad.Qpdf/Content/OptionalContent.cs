using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal enum OptionalContentFailure { None, BaseState, AutomaticStates, ConfigurationIntent, GroupIntent, AlternateConfigurations }

internal sealed record CopiedOptionalContent(QpdfObject Properties, OptionalContentFailure Failure);

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
        OptionalContentFailure failure = PolicyFailure(defaults, engine, governor, token);
        if (failure == OptionalContentFailure.None && !NoAlternates(properties, engine, governor, token))
        { failure = OptionalContentFailure.AlternateConfigurations; }
        for (int index = 0; index < sourceGroups.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (TryMappedGroup(sourceGroups[index], engine, copier, governor, token, out _, out PdfDictionary? group))
            {
                any = true;
                if (group is not null && !DefaultIntent(Read(group, "Intent"u8, governor, token), engine, governor, token)
                    && failure == OptionalContentFailure.None) { failure = OptionalContentFailure.GroupIntent; }
            }
        }
        if (!any) { return; }
        QpdfObject? retained = CatalogCopy.ReadExtra(extras, "OCProperties"u8, governor, token);
        if (retained is not null || failure != OptionalContentFailure.None || rawProperties is PdfReference existing && copier.SourceToDestination.ContainsKey(existing.ObjectNumber))
        {
            QpdfObject result = retained ?? copier.CopyValue(rawProperties ?? properties, 0);
            ownership.Reserve(32L); copier.OptionalContent = new(result, failure); return;
        }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
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
        }
        HashSet<int> off = ReadDisabled(defaults, seen, new(engine, copier), scratch, governor, token);
        foreach (QpdfObject member in members)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (member is PdfReference reference && off.Contains(reference.ObjectNumber))
            { ParserContainers.AddItem(scratch, disabledMembers, member, IntPtr.Size); }
        }
        PdfDictionary minimal = Properties(graph, members, disabledMembers, null, null);
        graph.Memory.Reserve(32L); graph.Add(32L);
        copier.OptionalContent = new(minimal, OptionalContentFailure.None);
        graph.Memory.TransferTo(ownership, graph.Bytes);
    }

    private static bool TryMappedGroup(QpdfObject value, DocumentEngine engine, GraphCopier copier, ResourceGovernor governor,
        CancellationToken token, out int number, out PdfDictionary? group)
    {
        number = 0; group = null;
        if (value is not PdfReference reference) { return false; }
        group = CatalogCopy.ResolveDictionary(reference, engine, governor, token);
        if (!copier.SourceToDestination.TryGetValue(reference.ObjectNumber, out number))
        {
            // Inspection can retain an original group under its unchanged pair.
            // Numeric coincidence alone does not establish a retained identity.
            if (group is null || !copier.CopiedObjects.TryGetValue(reference.ObjectNumber, out QpdfObject? retained)
                || !ReferenceEquals(group, retained)) { return false; }
            number = reference.ObjectNumber;
        }
        return group is not null && IsGroup(copier, number, governor, token);
    }


    internal static QpdfObject? Combine(GraphCopier? primaryCopier, IReadOnlyList<GraphCopier> sources, IReadOnlyList<GraphCopier> placements,
        IReadOnlyDictionary<PdfName, QpdfObject>? extras, IReadOnlyDictionary<int, QpdfObject> pool,
        ResourceGovernor governor, CancellationToken token)
        => CombineCore(primaryCopier, sources, placements, null, extras, pool, governor, token);

    internal static QpdfObject? CombinePlacementLayers(PdfDictionary catalog, GraphCopier primary, PoolResolver resolver,
        IReadOnlyList<GraphCopier> placements, ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        bool hasLayers = false;
        foreach (GraphCopier source in placements)
        { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); hasLayers |= source.OptionalContent is not null; }
        if (!hasLayers) { return null; }

        QpdfObject? role = Read(catalog, "OCProperties"u8, governor, token);
        GraphCopier? registeredPrimary = null;
        if (CatalogCopy.ResolveDictionary(role, resolver, governor, token) is PdfDictionary properties
            && CatalogCopy.ResolveValue(Read(properties, "OCGs"u8, governor, token), resolver, governor, token) is PdfArray members)
        {
            for (int index = 0; index < members.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (CatalogCopy.ResolveDictionary(members[index], resolver, governor, token) is PdfDictionary group
                    && !DefaultIntent(Read(group, "Intent"u8, governor, token), resolver, governor, token))
                { RequireCombinable(OptionalContentFailure.GroupIntent); }
            }
            if (primary.RetainsSourceReferences)
            { role = primary.RetainSourceValue(role ?? properties); }
            ownership.Reserve(32L);
            primary.OptionalContent = new(role ?? properties, OptionalContentFailure.None);
            registeredPrimary = primary;
        }
        return CombineCore(registeredPrimary, [], placements, role, null, primary.CopiedObjects, governor, token);
    }

    private static QpdfObject? CombineCore(GraphCopier? primaryCopier, IReadOnlyList<GraphCopier> sources,
        IReadOnlyList<GraphCopier> placements, QpdfObject? primaryRaw, IReadOnlyDictionary<PdfName, QpdfObject>? extras,
        IReadOnlyDictionary<int, QpdfObject> pool,
        ResourceGovernor governor, CancellationToken token)
    {
        bool any = primaryCopier?.OptionalContent is not null;
        foreach (GraphCopier copier in sources) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); any |= copier.OptionalContent is not null; }
        foreach (GraphCopier copier in placements) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); any |= copier.OptionalContent is not null; }
        if (!any) { return null; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(32L);
        PoolResolver resolver = new(pool);
        primaryRaw ??= CatalogCopy.ReadExtra(extras, "OCProperties"u8, governor, token);
        PdfDictionary? primary = CatalogCopy.ResolveDictionary(primaryRaw, resolver, governor, token);
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
        RequireCombinable(PolicyFailure(primaryDefaults, resolver, governor, token));
        if (primary is not null && !NoAlternates(primary, resolver, governor, token))
        { RequireCombinable(OptionalContentFailure.AlternateConfigurations); }
        List<QpdfObject> members = ParserContainers.CreateList<QpdfObject>(scratch);
        List<QpdfObject> off = ParserContainers.CreateList<QpdfObject>(scratch);
        HashSet<int> groupIds = ParserContainers.CreateSet<int>(scratch), offIds = ParserContainers.CreateSet<int>(scratch);
        foreach (CopiedOptionalContent contribution in contributions)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            RequireCombinable(contribution.Failure);
            PdfDictionary properties = CatalogCopy.ResolveDictionary(contribution.Properties, resolver, governor, token)
                ?? throw new QpdfException("Missing copied optional-content properties.");
            PdfArray groups = CatalogCopy.ResolveValue(Read(properties, "OCGs"u8, governor, token), resolver, governor, token) as PdfArray
                ?? throw new QpdfException("Missing copied optional-content groups.");
            using ParserMemoryScope contributionMemory = ParserMemoryScope.Create(governor);
            HashSet<int> registered = ParserContainers.CreateSet<int>(contributionMemory);
            for (int index = 0; index < groups.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (groups[index] is PdfReference group)
                {
                    ParserContainers.AddSetItem(contributionMemory, registered, group.ObjectNumber, sizeof(int));
                    if (ParserContainers.AddSetItem(scratch, groupIds, group.ObjectNumber, sizeof(int)))
                    { ParserContainers.AddItem(scratch, members, group, IntPtr.Size); }
                }
            }
            PdfDictionary? defaults = CatalogCopy.ResolveDictionary(Read(properties, "D"u8, governor, token), resolver, governor, token);
            HashSet<int> disabled = ReadDisabled(defaults, registered, new(resolver, null), contributionMemory, governor, token);
            for (int index = 0; index < groups.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (groups[index] is PdfReference group && disabled.Contains(group.ObjectNumber)
                    && ParserContainers.AddSetItem(scratch, offIds, group.ObjectNumber, sizeof(int)))
                { ParserContainers.AddItem(scratch, off, group, IntPtr.Size); }
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

    private static OptionalContentFailure PolicyFailure(PdfDictionary? defaults, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        if (defaults is null) { return OptionalContentFailure.None; }
        QpdfObject? state = CatalogCopy.ResolveValue(Read(defaults, "BaseState"u8, governor, token), resolver, governor, token);
        if (state is not null and not PdfNull && (state is not PdfName name || !(name.RawSpan.SequenceEqual("ON"u8) || name.RawSpan.SequenceEqual("OFF"u8))))
        { return OptionalContentFailure.BaseState; }
        QpdfObject? automatic = CatalogCopy.ResolveValue(Read(defaults, "AS"u8, governor, token), resolver, governor, token);
        if (automatic is not null and not PdfNull && automatic is not PdfArray { Count: 0 }) { return OptionalContentFailure.AutomaticStates; }
        return DefaultIntent(Read(defaults, "Intent"u8, governor, token), resolver, governor, token)
            ? OptionalContentFailure.None : OptionalContentFailure.ConfigurationIntent;
    }

    private static void RequireCombinable(OptionalContentFailure failure)
    {
        if (failure == OptionalContentFailure.None) { return; }
        throw new QpdfUnsupportedFeatureException(failure switch
        {
            OptionalContentFailure.BaseState => "Combining optional content requires a static ON/OFF BaseState.",
            OptionalContentFailure.AutomaticStates => "Combining optional content with automatic states is outside the supported profile.",
            OptionalContentFailure.AlternateConfigurations => "Combining optional content with alternate configurations is outside the supported profile.",
            OptionalContentFailure.ConfigurationIntent => "Combining optional content requires View configuration intent.",
            OptionalContentFailure.GroupIntent => "Combining optional content requires View group intents.",
            _ => throw new InvalidOperationException("Unknown optional-content policy.")
        });
    }

    private readonly record struct GroupMap(IIndirectResolver Resolver, GraphCopier? Copier)
    {
        internal bool TryGet(QpdfObject value, ResourceGovernor governor, CancellationToken token, out int number)
        {
            if (Copier is not null && Resolver is DocumentEngine engine)
            { return TryMappedGroup(value, engine, Copier, governor, token, out number, out _); }
            number = value is PdfReference reference ? reference.ObjectNumber : 0;
            return value is PdfReference;
        }
    }

    private static HashSet<int> ReadDisabled(PdfDictionary? defaults, IReadOnlySet<int> registered, GroupMap map,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        HashSet<int> disabled = ParserContainers.CreateSet<int>(memory);
        QpdfObject? state = defaults is null ? null : CatalogCopy.ResolveValue(Read(defaults, "BaseState"u8, governor, token), map.Resolver, governor, token);
        if (state is PdfName name && name.RawSpan.SequenceEqual("OFF"u8))
        {
            foreach (int number in registered)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddSetItem(memory, disabled, number, sizeof(int));
            }
        }
        Apply("ON"u8, false); Apply("OFF"u8, true);
        return disabled;

        void Apply(ReadOnlySpan<byte> key, bool off)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            QpdfObject? raw = defaults is null ? null : CatalogCopy.ResolveValue(Read(defaults, key, governor, token), map.Resolver, governor, token);
            if (raw is not null and not PdfNull && raw is not PdfArray)
            { throw new QpdfParseException(off ? "Optional-content OFF states must be an array." : "Optional-content ON states must be an array.", null, null); }
            if (raw is not PdfArray values) { return; }
            for (int index = 0; index < values.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (!map.TryGet(values[index], governor, token, out int number) || !registered.Contains(number)) { continue; }
                if (off) { ParserContainers.AddSetItem(memory, disabled, number, sizeof(int)); }
                else { disabled.Remove(number); }
            }
        }
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
