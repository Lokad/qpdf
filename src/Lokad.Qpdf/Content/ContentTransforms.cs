using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Page content operation request: coalescing, normalization, compression.</summary>
internal sealed record ContentTransformRequest(bool Coalesce, bool Normalize, bool Compress, int? Level, bool Externalize, long MinBytes)
{
    internal byte FilterOptions { get; init; }
}

/// <summary>Resource pruning policy: enabled flag plus auto/yes/no mode.</summary>
internal sealed record ContentPrunePolicy(bool Enabled, string? Mode);

/// <summary>An indirect-object resolver backed by a copied pool.</summary>
internal sealed class PoolResolver : IIndirectResolver
{
    private readonly IReadOnlyDictionary<int, QpdfObject> _pool;

    internal PoolResolver(IReadOnlyDictionary<int, QpdfObject> pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    internal static PoolResolver Create(ParserMemoryScope memory, IReadOnlyDictionary<int, QpdfObject> pool)
    { memory.Reserve(32L); return new(pool); }

    bool IIndirectResolver.TryResolve(PdfReference reference, out QpdfObject? value)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (_pool.TryGetValue(reference.ObjectNumber, out QpdfObject? found) && found is not null)
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }
}

/// <summary>
/// Page content transforms applied to copied pages: stream coalescing,
/// newline normalization, and unreferenced-resource pruning. Mirrors the
/// upstream page-operation behavior including its skip-on-damage policy.
/// </summary>
internal static class ContentTransforms
{
    // Copied-object replacement records retirement independently of the transform.
    internal static void ApplyPageTransforms(
        GraphCopier copier,
        IReadOnlyList<int> pageIds,
        ContentTransformRequest request,
        bool prune,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (pageIds is null)
        {
            throw new ArgumentNullException(nameof(pageIds));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
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
        if (!request.Coalesce && !request.Normalize && !prune && !request.Externalize)
        {
            return;
        }

        using ParserMemoryScope phase = ParserMemoryScope.Create(governor);
        if (request.Externalize)
        {
            using FormResourceWalk forms = FormResourceWalk.Create(governor, cancellationToken);
            PoolResolver resolver = forms.CreateResolver(copier.CopiedObjects);
            foreach (int pageId in pageIds)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary page) { continue; }
                page = InlineImages.ExternalizePage(copier, pageId, page, request.MinBytes, request.Compress, request.Level, limits, governor, diagnostics, cancellationToken, request.FilterOptions);
                if (PdfDictionaryLookup.TryGet(page, "Resources"u8, governor, cancellationToken, out QpdfObject resourcesRaw))
                {
                    QpdfObject? resources = resourcesRaw is PdfReference link ? ResolvePool(resolver, link, governor, cancellationToken) : resourcesRaw;
                    if (resources is PdfDictionary dictionary) { forms.AddResources(dictionary, resolver); }
                }
            }
            GovernedIntDictionary<PdfStream> replacements = GovernedIntDictionary<PdfStream>.Create(phase, governor, IntPtr.Size, cancellationToken);
            for (int index = 0; index < forms.Count; index++)
            {
                FormResourceWalk.Form form = forms.Get(index);
                if (form.Id is not int id) { continue; }
                PdfStream? candidate = InlineImages.ExternalizeForm(copier, id, form.Stream, request.MinBytes, request.Compress,
                    request.Level, limits, governor, diagnostics, cancellationToken, request.FilterOptions);
                if (candidate is not null) { replacements.TryAdd(phase, id, candidate); }
            }
            int allocator = copier.NextId;
            forms.ApplyReplacements(copier, pageIds, replacements, ref allocator);
        }
        Dictionary<QpdfObject, (QpdfObject? Context, QpdfObject? Replacement)>? normalized = null;
        if (request.Normalize)
        {
            // This phase redirects page edges and creates fresh stream identities;
            // original stream/array values stay immutable, so identity is the version key.
            phase.Reserve(96L);
            normalized = new(ReferenceEqualityComparer.Instance);
        }
        Dictionary<PdfArray, PdfReference>? coalesced = null;
        if (request.Coalesce)
        {
            phase.Reserve(96L);
            coalesced = new(ReferenceEqualityComparer.Instance);
        }
        foreach (int pageId in pageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary)
            {
                continue;
            }

            PdfDictionary page = (PdfDictionary)raw;
            if (coalesced is not null)
            {
                page = CoalescePage(copier, pageId, page, request, coalesced, phase, limits, governor, diagnostics, cancellationToken);
            }

            if (normalized is not null)
            {
                page = NormalizePage(copier, pageId, page, normalized, phase, limits, governor, diagnostics, cancellationToken);
            }

        }
        if (prune) { ApplyResourcePruning(copier, pageIds, limits, governor, diagnostics, cancellationToken); }
    }
    internal static ContentEntries? ReadPageContents(
        GraphCopier copier,
        PdfDictionary page,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contents) || contents is PdfNull)
        {
            return null;
        }
        if (contents is not (PdfArray or PdfReference or PdfStream)) { return null; }
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        ContentEntries? entries = ContentEntries.TryRead(contents, resolver, governor, cancellationToken);
        if (entries is null)
        { diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped content operation on unreadable page contents."); }
        return entries;
    }

    private static PdfDictionary ReplaceEntry(PdfDictionary dictionary, ReadOnlySpan<byte> key, QpdfObject value, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, dictionary, key, value);
        graph.Retain(); return result;
    }

    private static PdfDictionary CoalescePage(
        GraphCopier copier,
        int pageId,
        PdfDictionary page,
        ContentTransformRequest request,
        Dictionary<PdfArray, PdfReference> coalescedArrays,
        ParserMemoryScope phase,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ContentEntries? entries = ReadPageContents(copier, page, governor, diagnostics, cancellationToken);
        if (entries is null || entries.Count <= 1)
        {
            return page;
        }

        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contents);
        QpdfObject? target = contents;
        if (target is PdfReference originalReference)
        { PdfObjectLookup.TryResolve(originalReference, copier.CopiedObjects, governor, cancellationToken, out target); }
        PdfArray? original = target as PdfArray;
        if (original is not null && coalescedArrays.TryGetValue(original, out PdfReference? existing))
        {
            PdfDictionary shared = ReplaceEntry(page, "Contents"u8, existing, governor, cancellationToken);
            copier.ReplaceCopiedObject(pageId, shared);
            return shared;
        }
        PdfStream? coalesced = BuildCoalescedContent(entries, resolver, request, limits, governor, cancellationToken);
        if (coalesced is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning,
                "Left page contents uncoalesced because a stream could not be decoded.");
            return page;
        }

        // The old Contents reference can identify an array shared with other
        // page or catalog values. Replace this page's edge, leaving those
        // identities intact until final output reachability is known.
        PdfReference? link = copier.AddIndirectObject(coalesced);
        if (link is null) { return page; }
        if (original is not null)
        {
            ParserContainers.EnsureEntryCapacity(phase, coalescedArrays, IntPtr.Size);
            coalescedArrays.Add(original, link);
        }
        PdfDictionary replaced = ReplaceEntry(page, "Contents"u8, link, governor, cancellationToken);
        copier.ReplaceCopiedObject(pageId, replaced);
        return replaced;
    }

    private static PdfStream? BuildCoalescedContent(ContentEntries entries, PoolResolver resolver, ContentTransformRequest request,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, token);
        for (int index = 0; index < entries.Count; index++)
        {
            PdfStream part = entries.Get(index).Stream;
            if (!chunks.TryAdd(part, resolver, limits))
            { FilterChain.RequireEmbeddedData(part, resolver, governor, token); return null; }
        }
        using JoinedContent joined = chunks.Join();
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        bool compress = request.Compress && joined.RawBytes.Length > 0;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        byte[] data = compress ? FlateFilter.Encode(joined.RawBytes, request.Level, limits, governor, graph.Memory, token) : joined.TakeBytes(graph.Memory);
        joined.Dispose(); AddContentBuffer(graph, data);
        PdfNameTable<QpdfObject> header = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        if (compress)
        {
            PdfName key = PdfDictionaryRebuild.CreateName(graph, "Filter"u8), filter = PdfDictionaryRebuild.CreateName(graph, "FlateDecode"u8);
            header.Assign(graph.Memory, governor, key, filter, token);
        }
        PdfDictionary dictionary = PdfDictionaryRebuild.Complete(graph, header, allocation);
        PdfStream stream = CreateContentStream(graph, dictionary, data); graph.Retain(); return stream;
    }

    private static readonly PdfDictionary EmptyContentHeader = new(new Dictionary<PdfName, QpdfObject>());

    internal static PdfStream EncodeContent(byte[] content, bool compress, int? level, QpdfLimits limits, OwnedAllocationBatch graph)
    {
        PdfDictionary header = EmptyContentHeader;
        if (compress && content.Length > 0)
        {
            long rawBytes = 32L + content.Length;
            content = FlateFilter.Encode(content, level, limits, graph.Governor, graph.Memory, graph.Token);
            graph.UpdateBytes(rawBytes, 32L + content.Length);
            header = PdfDictionaryRebuild.ReplaceEntry(graph, header, "Filter"u8, PdfName.FromAscii("FlateDecode"));
        }
        graph.Memory.Reserve(32L); PdfStream stream = PdfStream.FromOwnedData(header, content); graph.Add(32L); return stream;
    }

    private static void AddContentBuffer(OwnedAllocationBatch graph, byte[] data)
    { if (data.Length != 0) { graph.Add(checked(data.Length + GovernedBufferStream.ArrayOverheadBytes)); } }

    private static PdfStream CreateContentStream(OwnedAllocationBatch graph, PdfDictionary dictionary, byte[] data)
    {
        graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation(); graph.Memory.Reserve(32L);
        PdfStream stream = PdfStream.FromOwnedData(dictionary, data); graph.Add(32L); return stream;
    }
    private static PdfDictionary NormalizePage(
        GraphCopier copier, int pageId, PdfDictionary page,
        Dictionary<QpdfObject, (QpdfObject? Context, QpdfObject? Replacement)> normalized,
        ParserMemoryScope phase, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken token)
    {
        using ParserMemoryScope rewriteMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(rewriteMemory, copier.CopiedObjects);
        PdfDictionary? resources = PdfDictionaryLookup.TryGet(page, "Resources"u8, governor, token, out QpdfObject rawResources)
            ? CatalogCopy.ResolveDictionary(rawResources, resolver, governor, token) : null;
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, token, out QpdfObject contents)) { return page; }
        QpdfObject? source = CatalogCopy.ResolveValue(contents, resolver, governor, token);
        if (source is not null && normalized.TryGetValue(source, out var cached) && NormalizationContextMatches(cached.Context, resources))
        {
            QpdfObject? value = cached.Replacement;
            if (value is PdfStream candidate && contents is PdfReference)
            {
                value = copier.AddIndirectObjectWithId(candidate, copier.NextId);
                CacheNormalization(normalized, source, cached.Context, value, phase);
            }
            return value is null ? page : ReplaceNormalizedPage(copier, pageId, page, value, governor, token);
        }
        using ContentEntries? entries = ReadPageContents(copier, page, governor, diagnostics, token);
        if (entries is null || entries.Count == 0 || source is null) { return page; }
        QpdfObject? replacement;
        QpdfObject? context;
        if (entries.Count == 1)
        {
            ContentEntries.Entry entry = entries.Get(0);
            if (normalized.TryGetValue(entry.Stream, out cached) && NormalizationContextMatches(cached.Context, resources))
            { replacement = cached.Replacement; context = cached.Context; }
            else
            {
                PdfStream? stream = BuildNormalizedContent(entry.Stream, resources, resolver, limits, governor, diagnostics, token, out context);
                replacement = stream;
                CacheNormalization(normalized, entry.Stream, context, replacement, phase);
            }
            if (replacement is PdfStream candidate && entry.Reference is not null)
            {
                replacement = copier.AddIndirectObjectWithId(candidate, copier.NextId);
                CacheNormalization(normalized, entry.Stream, context, replacement, phase);
            }
            if (replacement is not null && source is PdfArray)
            {
                GovernedIntDictionary<QpdfObject> rewritten = GovernedIntDictionary<QpdfObject>.Create(rewriteMemory, governor, IntPtr.Size, token);
                rewritten.Assign(rewriteMemory, 0, replacement);
                PdfDictionary rebuilt = ReplaceContentEntries(copier, page, rewritten, governor, token);
                PdfDictionaryLookup.TryGet(rebuilt, "Contents"u8, governor, token, out replacement);
            }
        }
        else
        { replacement = BuildNormalizedContents(copier, entries, resources, resolver, normalized, phase, limits, governor, diagnostics, token, out context); }
        CacheNormalization(normalized, source, context, replacement, phase);
        return replacement is null ? page : ReplaceNormalizedPage(copier, pageId, page, replacement, governor, token);
    }

    private static bool NormalizationContextMatches(QpdfObject? context, PdfDictionary? resources)
        => context is null || ReferenceEquals(context, resources ?? (QpdfObject)PdfNull.Instance);

    private static void CacheNormalization(Dictionary<QpdfObject, (QpdfObject? Context, QpdfObject? Replacement)> normalized,
        QpdfObject source, QpdfObject? context, QpdfObject? replacement, ParserMemoryScope phase)
    {
        if (!normalized.ContainsKey(source)) { ParserContainers.EnsureEntryCapacity(phase, normalized, 2L * IntPtr.Size); }
        normalized[source] = (context, replacement);
    }

    private static PdfDictionary ReplaceNormalizedPage(GraphCopier copier, int pageId, PdfDictionary page,
        QpdfObject replacement, ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary result = ReplaceEntry(page, "Contents"u8, replacement, governor, token);
        copier.ReplaceCopiedObject(pageId, result); return result;
    }

    private static PdfArray? BuildNormalizedContents(GraphCopier copier, ContentEntries entries, PdfDictionary? resources,
        PoolResolver resolver, Dictionary<QpdfObject, (QpdfObject? Context, QpdfObject? Replacement)> normalized,
        ParserMemoryScope phase, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        CancellationToken token, out QpdfObject? context)
    {
        context = null;
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        // Part bounds/context are temporary; only the replacement array/payloads escape.
        graph.Memory.Reserve(checked(32L + 16L * entries.Count));
        var parts = new (int Start, int End, bool Decoded, bool Independent)[entries.Count];
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, token);
        int position = 0; bool newline = false;
        for (int index = 0; index < entries.Count; index++)
        {
            using ParserMemoryScope decoded = ParserMemoryScope.Create(governor);
            PdfStream stream = entries.Get(index).Stream;
            if (!AnalysisChunks.TryDecode(stream, resolver, limits, governor, decoded, token, out ReadOnlyMemory<byte> bytes, out bool filtered))
            {
                FilterChain.RequireEmbeddedData(stream, resolver, governor, token);
                chunks.Dispose();
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning,
                    "Left page contents unnormalized because a stream could not be decoded.");
                return null;
            }
            long start = (long)position + (newline ? 1L : 0L), end = start + bytes.Length;
            if (end > Array.MaxLength) { throw new QpdfLimitException("Joined content bytes", end, Array.MaxLength); }
            position = (int)end; parts[index] = ((int)start, position, filtered, false);
            newline = bytes.Length == 0 || bytes.Span[^1] != (byte)'\n'; chunks.Add(bytes, decoded);
        }
        using JoinedContent joined = chunks.Join();
        graph.Memory.Reserve(checked(32L + IntPtr.Size * (long)entries.Count));
        QpdfObject[] items = new QpdfObject[entries.Count]; graph.Add(checked(32L + IntPtr.Size * (long)entries.Count));
        graph.Memory.Reserve(192L);
        ContentTokenizer tokenizer = new(joined.Bytes, governor, token) { IncludeComments = true, IncludeWhitespace = true };
        // A complete token/inline packet belongs to its starting part. Whitespace
        // may cross several parts: keep each original slice and omit join separators.
        ContentToken? pending = null; long consumed = 0L;
        try
        {
            for (int index = 0; index < entries.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                var part = parts[index]; bool independent = consumed <= part.Start && !tokenizer.HasInlineImageHeader;
                bool requiresResources = false;
                using GovernedBufferStream output = new(governor, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), token,
                    Math.Max(1, Math.Min(8192, part.End - part.Start)));
                try
                {
                    while (pending.HasValue || tokenizer.Position < part.End)
                    {
                        ContentToken current;
                        if (pending is ContentToken saved) { current = saved; pending = null; }
                        else if (!tokenizer.TryReadToken(out current)) { break; }
                        if (current.Kind == ContentTokenKind.Whitespace && current.Offset < part.Start)
                        {
                            int skip = (int)Math.Min(current.Data.Length, part.Start - current.Offset);
                            current = new(current.Kind, current.Data[skip..], current.Offset + skip);
                            if (current.Data.IsEmpty) { continue; }
                        }
                        if (current.Offset >= part.End) { pending = current; break; }
                        if (current.Kind == ContentTokenKind.Whitespace && current.Offset + current.Data.Length > part.End)
                        {
                            int take = checked((int)(part.End - current.Offset));
                            pending = new(current.Kind, current.Data[take..], part.End);
                            current = new(current.Kind, current.Data[..take], current.Offset);
                        }
                        AppendNormalizedToken(output, current, tokenizer, joined.RawBytes, resources, resolver, limits, governor,
                            diagnostics, token, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), ref requiresResources);
                        if (current.Kind != ContentTokenKind.Whitespace) { consumed = tokenizer.Position; }
                    }
                }
                finally { if (requiresResources) { context = resources ?? (QpdfObject)PdfNull.Instance; } }
                independent &= consumed <= part.End && !tokenizer.HasInlineImageHeader;
                QpdfObject? partContext = requiresResources ? resources ?? (QpdfObject)PdfNull.Instance : null;
                PdfStream source = entries.Get(index).Stream;
                if (independent && normalized.TryGetValue(source, out var cached) && cached.Replacement is not null
                    && NormalizationContextMatches(cached.Context, resources)) { items[index] = cached.Replacement; }
                else
                {
                    // Complete fragments can join the existing stream memo now;
                    // context-dependent fragments stay under this page attempt.
                    using OwnedAllocationBatch? independentGraph = independent ? OwnedAllocationBatch.Create(governor, token) : null;
                    OwnedAllocationBatch owner = independentGraph ?? graph;
                    byte[] bytes = output.ToArray(owner.Memory, token); AddContentBuffer(owner, bytes);
                    PdfDictionary header = part.Decoded ? PdfDictionaryRebuild.StripStreamEncoding(owner, source.Dictionary) : source.Dictionary;
                    PdfStream stream = CreateContentStream(owner, header, bytes);
                    items[index] = stream;
                    if (independent)
                    {
                        owner.Retain();
                        // Keep a computed fragment unnumbered until a complete
                        // page uses it. Failed attempts must not emit loose clones.
                        CacheNormalization(normalized, source, partContext, items[index], phase);
                    }
                }
                parts[index] = (part.Start, part.End, part.Decoded, independent);
            }
        }
        catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning,
                "Left page contents unnormalized after a content error."); return null;
        }
        graph.Memory.Reserve(24L); graph.Add(24L);
        graph.Retain();
        for (int index = 0; index < entries.Count; index++)
        {
            ContentEntries.Entry entry = entries.Get(index);
            if (items[index] is PdfStream stream && entry.Reference is not null)
            {
                var cached = parts[index].Independent ? normalized[entry.Stream] : default;
                items[index] = cached.Replacement is PdfReference previous ? previous : copier.AddIndirectObjectWithId(stream, copier.NextId);
                if (parts[index].Independent) { CacheNormalization(normalized, entry.Stream, cached.Context, items[index], phase); }
            }
        }
        return PdfArray.FromOwnedItems(items);
    }

    private static PdfStream? BuildNormalizedContent(PdfStream original, PdfDictionary? resources, PoolResolver resolver, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token, out QpdfObject? context)
    {
        context = null;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool contentError = false;
        using (ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor))
        {
            ReadOnlyMemory<byte> data = ReadOnlyMemory<byte>.Empty; bool decoded;
            try
            {
                if (!AnalysisChunks.TryDecode(original, resolver, limits, governor, decodedMemory, token, out data, out decoded))
                {
                    FilterChain.RequireEmbeddedData(original, resolver, governor, token);
                    decodedMemory.Dispose();
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning,
                        "Left a content stream unnormalized because it could not be decoded.");
                    return null;
                }
                using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
                byte[]? normalized = null;
                bool requiresResources = false;
                try
                {
                    try { normalized = NormalizeContentBytesOwned(data, resources, resolver, limits, governor, diagnostics, token,
                        limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), graph.Memory, out requiresResources); }
                    catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException)) { contentError = true; }
                    if (normalized is not null)
                    {
                        data = ReadOnlyMemory<byte>.Empty; decodedMemory.Dispose(); AddContentBuffer(graph, normalized);
                        PdfDictionary dictionary = decoded ? PdfDictionaryRebuild.StripStreamEncoding(graph, original.Dictionary) : original.Dictionary;
                        PdfStream replacement = CreateContentStream(graph, dictionary, normalized); graph.Retain(); return replacement;
                    }
                }
                finally { normalized = null; context = requiresResources ? resources ?? (QpdfObject)PdfNull.Instance : null; }
            }
            finally { data = Array.Empty<byte>(); }
        }
        if (contentError) { diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Left a content stream unnormalized after a content error."); }
        return null;
    }

    private static PdfDictionary ReplaceContentEntries(
        GraphCopier copier,
        PdfDictionary page,
        GovernedIntDictionary<QpdfObject> rewritten,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contents) || contents is null)
        {
            return page;
        }

        QpdfObject? target = contents;
        if (contents is PdfReference link)
        { PdfObjectLookup.TryResolve(link, copier.CopiedObjects, governor, cancellationToken, out target); }
        if (target is PdfStream && rewritten.TryGetValue(0, out QpdfObject? single))
        { return ReplaceEntry(page, "Contents"u8, single, governor, cancellationToken); }
        if (target is PdfArray array)
        {
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
            QpdfObject[] items = Array.Empty<QpdfObject>();
            if (array.Count != 0)
            {
                long bytes = checked(32L + array.Count * (long)IntPtr.Size);
                graph.Memory.Reserve(bytes); governor.NoteOperation();
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                items = new QpdfObject[array.Count]; graph.Add(bytes);
            }
            for (int i = 0; i < array.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (rewritten.TryGetValue(i, out QpdfObject? replacement))
                {
                    items[i] = replacement;
                    continue;
                }

                items[i] = array[i];
            }
            graph.Memory.Reserve(24L); PdfArray rebuilt = PdfArray.FromOwnedItems(items); graph.Add(24L);
            PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, page, "Contents"u8, rebuilt); graph.Retain(); return result;
        }

        return page;
    }
    internal static byte[] NormalizeContentBytesOwned(ReadOnlyMemory<byte> data, QpdfObject? resources, IIndirectResolver? resolver,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken,
        long maximumBytes, string limitName, ParserMemoryScope memory, out bool requiresResources)
    {
        requiresResources = false;
        memory.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor); scratch.Reserve(192L);
        ContentTokenizer tokenizer = new(data, governor, cancellationToken)
        {
            IncludeComments = true,
            IncludeWhitespace = true,
        };
        using GovernedBufferStream output = new(governor, maximumBytes, limitName, cancellationToken, Math.Max(1, Math.Min(8192, data.Length)));
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            AppendNormalizedToken(output, token, tokenizer, data, resources, resolver, limits, governor, diagnostics,
                cancellationToken, maximumBytes, limitName, ref requiresResources);
        }
        return output.ToArray(memory, cancellationToken);
    }

    private static void AppendNormalizedToken(GovernedBufferStream output, ContentToken token, ContentTokenizer tokenizer,
        ReadOnlyMemory<byte> data, QpdfObject? resources, IIndirectResolver? resolver, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken cancellationToken, long maximumBytes, string limitName, ref bool requiresResources)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.NoteOperation();
        switch (token.Kind)
        {
            case ContentTokenKind.Operator when token.Data.Span.SequenceEqual("ID"u8):
                AppendRaw(output, token);
                int imageStart = checked((int)tokenizer.Position);
                bool imageRequiresResources = false;
                try { InlineImageStorage.ReadData(tokenizer, resources, resolver, limits, governor, diagnostics, cancellationToken, out imageRequiresResources); }
                finally { requiresResources |= imageRequiresResources; }
                // Include the original delimiter and EI; image samples are
                // opaque even when their bytes resemble content tokens.
                output.Write(data.Span.Slice(imageStart, checked((int)tokenizer.Position) - imageStart));
                break;
            case ContentTokenKind.Operator:
            case ContentTokenKind.Integer:
            case ContentTokenKind.Real:
            case ContentTokenKind.Boolean:
            case ContentTokenKind.Null:
            case ContentTokenKind.ArrayOpen:
            case ContentTokenKind.ArrayClose:
            case ContentTokenKind.DictionaryOpen:
            case ContentTokenKind.DictionaryClose:
            case ContentTokenKind.HexString:
                AppendRaw(output, token);
                break;
            case ContentTokenKind.LiteralString:
                using (ParserMemoryScope temporary = ParserMemoryScope.Create(governor))
                {
                    byte[] decoded = DecodeLiteral(token.Data, governor, temporary, cancellationToken);
                    byte[] encoded = EncodeLiteral(decoded, governor, temporary, maximumBytes, limitName, cancellationToken);
                    output.Write(encoded);
                }
                if (ContainsNewline(token.Data, governor, cancellationToken))
                {
                    output.WriteByte((byte)'\n');
                }

                break;
            case ContentTokenKind.Name:
                using (ParserMemoryScope temporary = ParserMemoryScope.Create(governor))
                {
                    byte[] resolved = ResolveNameBytes(token.Data, governor, temporary, cancellationToken);
                    byte[] encoded = EncodeName(resolved, governor, temporary, maximumBytes, limitName, cancellationToken);
                    output.Write(encoded);
                }
                if (ContainsNewline(token.Data, governor, cancellationToken))
                {
                    output.WriteByte((byte)'\n');
                }

                break;
            case ContentTokenKind.Comment:
                AppendRaw(output, token);
                output.WriteByte((byte)'\n');
                break;
            case ContentTokenKind.Whitespace:
                AppendNormalizedSpace(output, token);
                break;
            default:
                AppendRaw(output, token);
                break;
        }
    }

    private static void AppendRaw(GovernedBufferStream output, ContentToken token)
    {
        output.Write(token.Data.Span);
    }

    private static void AppendNormalizedSpace(GovernedBufferStream output, ContentToken token)
    {
        ReadOnlySpan<byte> span = token.Data.Span;
        int position = 0;
        while (position < span.Length)
        {
            int carriage = span.Slice(position).IndexOf((byte)'\r');
            if (carriage < 0)
            {
                for (int i = position; i < span.Length; i++)
                {
                    output.WriteByte(span[i]);
                }

                return;
            }

            for (int i = position; i < position + carriage; i++)
            {
                output.WriteByte(span[i]);
            }

            int absolute = position + carriage;
            if (absolute + 1 >= span.Length)
            {
                output.WriteByte((byte)'\n');
                return;
            }

            if (span[absolute + 1] != (byte)'\n')
            {
                output.WriteByte((byte)'\n');
            }

            position = absolute + 1;
        }
    }
    private static bool ContainsNewline(ReadOnlyMemory<byte> raw, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> span = raw.Span;
        for (int i = 0; i < span.Length; i += 8192)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int count = Math.Min(8192, span.Length - i);
            governor.NoteOperation();
            if (span.Slice(i, count).IndexOfAny((byte)'\n', (byte)'\r') >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] DecodeLiteral(ReadOnlyMemory<byte> raw, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ReadOnlySpan<byte> span = raw.Span;
        memory.Reserve(112L);
        using GovernedBufferStream decoded = new(governor, Math.Max(1, span.Length), nameof(QpdfLimits.MaxDecodedBytes), cancellationToken);
        int i = 0;
        if (span.Length > 0 && span[0] == (byte)'(')
        {
            i = 1;
        }

        int end = span.Length;
        if (end > i && span[end - 1] == (byte)')')
        {
            end -= 1;
        }

        while (i < end)
        {
            governor.NoteOperation();
            if (span[i] == (byte)'\\' && i + 1 < end)
            {
                byte next = span[i + 1];
                if (next == (byte)'n')
                {
                    decoded.WriteByte((byte)'\n');
                    i += 2;
                }
                else if (next == (byte)'r')
                {
                    decoded.WriteByte((byte)'\r');
                    i += 2;
                }
                else if (next == (byte)'t')
                {
                    decoded.WriteByte((byte)'\t');
                    i += 2;
                }
                else if (next == (byte)'b')
                {
                    decoded.WriteByte((byte)'\b');
                    i += 2;
                }
                else if (next == (byte)'f')
                {
                    decoded.WriteByte((byte)'\f');
                    i += 2;
                }
                else if (next == (byte)'\n')
                {
                    i += 2;
                }
                else if (next == (byte)'\r')
                {
                    i += 2;
                    if (i < end && span[i] == (byte)'\n')
                    {
                        i += 1;
                    }
                }
                else if (next >= (byte)'0' && next <= (byte)'7')
                {
                    int value = 0;
                    int digits = 0;
                    while (digits < 3 && i + 1 + digits < end && span[i + 1 + digits] >= (byte)'0' && span[i + 1 + digits] <= (byte)'7')
                    {
                        value = (value * 8) + (span[i + 1 + digits] - (byte)'0');
                        digits += 1;
                    }

                    decoded.WriteByte((byte)(value & 0xFF));
                    i += 1 + digits;
                }
                else
                {
                    decoded.WriteByte(next);
                    i += 2;
                }

                continue;
            }

            byte currentByte = span[i++];
            if (currentByte == (byte)'\r')
            {
                // A physical CR or CRLF is one LF byte in a PDF literal.
                if (i < end && span[i] == (byte)'\n') { i++; }
                currentByte = (byte)'\n';
            }
            decoded.WriteByte(currentByte);
        }

        return decoded.ToArray(memory, cancellationToken);
    }

    private static byte[] EncodeLiteral(byte[] value, ResourceGovernor governor, ParserMemoryScope memory,
        long maximumBytes, string limitName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        memory.Reserve(112L);
        using GovernedBufferStream output = new(governor, maximumBytes, limitName, cancellationToken, (int)Math.Min(8192L, value.Length + 2L));
        output.WriteByte((byte)'(');
        foreach (byte b in value)
        {
            switch (b)
            {
                case (byte)'\n':
                    output.WriteByte((byte)'\\');
                    output.WriteByte((byte)'n');
                    break;
                case (byte)'\r':
                    output.WriteByte((byte)'\\');
                    output.WriteByte((byte)'r');
                    break;
                case (byte)'\t':
                    output.WriteByte((byte)'\\');
                    output.WriteByte((byte)'t');
                    break;
                case (byte)'\b':
                    output.WriteByte((byte)'\\');
                    output.WriteByte((byte)'b');
                    break;
                case (byte)'\f':
                    output.WriteByte((byte)'\\');
                    output.WriteByte((byte)'f');
                    break;
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    output.WriteByte((byte)'\\');
                    output.WriteByte(b);
                    break;
                default:
                    if (b < 0x20 || b > 0x7E)
                    {
                        output.WriteByte((byte)'\\');
                        output.WriteByte((byte)('0' + ((b >> 6) & 0x07)));
                        output.WriteByte((byte)('0' + ((b >> 3) & 0x07)));
                        output.WriteByte((byte)('0' + (b & 0x07)));
                    }
                    else
                    {
                        output.WriteByte(b);
                    }

                    break;
            }
        }

        output.WriteByte((byte)')');
        return output.ToArray(memory, cancellationToken);
    }

    private static byte[] ResolveNameBytes(ReadOnlyMemory<byte> raw, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ReadOnlySpan<byte> span = raw.Span;
        memory.Reserve(112L);
        using GovernedBufferStream resolved = new(governor, Math.Max(1, span.Length), nameof(QpdfLimits.MaxDecodedBytes), cancellationToken);
        PdfNameBytes.Cursor cursor = new(span, true);
        while (cursor.TryRead(out byte value))
        {
            governor.NoteOperation();
            resolved.WriteByte(value);
        }

        return resolved.ToArray(memory, cancellationToken);
    }

    private static byte[] EncodeName(byte[] resolved, ResourceGovernor governor, ParserMemoryScope memory,
        long maximumBytes, string limitName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        memory.Reserve(112L);
        using GovernedBufferStream output = new(governor, maximumBytes, limitName, cancellationToken, (int)Math.Min(8192L, resolved.Length + 1L));
        output.WriteByte((byte)'/');
        foreach (byte b in resolved)
        {
            bool delimiter = b == (byte)'#' || b == (byte)'/' || b == (byte)'(' || b == (byte)')'
                || b == (byte)'<' || b == (byte)'>' || b == (byte)'[' || b == (byte)']'
                || b == (byte)'{' || b == (byte)'}' || b == (byte)'%';
            if (b < 33 || b > 126 || delimiter)
            {
                output.WriteByte((byte)'#');
                output.WriteByte((byte)"0123456789abcdef"[b >> 4]);
                output.WriteByte((byte)"0123456789abcdef"[b & 15]);
            }
            else
            {
                output.WriteByte(b);
            }
        }

        return output.ToArray(memory, cancellationToken);
    }

    private static QpdfObject? ResolvePool(PoolResolver resolver, PdfReference reference,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (PdfObjectLookup.TryResolve(reference, resolver, governor, cancellationToken, out QpdfObject? found)
            && found is not null && found is not PdfNull)
        {
            return found;
        }

        return null;
    }

    private static void ApplyResourcePruning(GraphCopier copier, IReadOnlyList<int> pageIds,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        using FormResourceWalk forms = FormResourceWalk.Create(governor, token);
        PoolResolver resolver = forms.CreateResolver(copier.CopiedObjects);
        foreach (int id in pageIds)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (copier.CopiedObjects.TryGetValue(id, out QpdfObject? raw) && raw is PdfDictionary page
                && PdfDictionaryLookup.TryGet(page, "Resources"u8, governor, token, out QpdfObject resourcesRaw)
                && CatalogCopy.ResolveDictionary(resourcesRaw, resolver, governor, token) is PdfDictionary resources)
            { forms.AddResources(resources, resolver); }
        }
        using ResourceNameSet inherited = ResourceNameSet.Create(governor, token);
        GovernedIntDictionary<PdfStream> replacements = GovernedIntDictionary<PdfStream>.Create(traversal, governor, IntPtr.Size, token);
        // Analyze non-independent Forms first. Their names must protect enclosing
        // dictionaries before any scoped Form is pruned. Every program is read once.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int index = 0; index < forms.Count; index++)
            {
                FormResourceWalk.Form form = forms.Get(index);
                PdfDictionary? resources = PdfDictionaryLookup.TryGet(form.Stream.Dictionary, "Resources"u8, governor, token, out QpdfObject raw)
                    ? CatalogCopy.ResolveDictionary(raw, resolver, governor, token) : null;
                if ((resources is null) != (pass == 0)) { continue; }
                using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
                PdfDictionary? pruned = ScanForm(form.Stream, resources, inherited, resolver, limits, diagnostics, graph);
                if (pruned is null || form.Id is not int id) { continue; }
                PdfStream replacement = ResourceRebuild.ReplaceResources(graph, form.Stream, pruned);
                replacements.TryAdd(traversal, id, replacement); graph.Retain();
            }
        }
        // A failed scoped consumer can depend on another scope. Until its
        // dependencies are known, discard all staged pruning changes.
        if (!inherited.HasUnresolvedScope)
        {
            int allocator = copier.NextId;
            forms.ApplyReplacements(copier, pageIds, replacements, ref allocator);
        }
        foreach (int id in pageIds)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (copier.CopiedObjects.TryGetValue(id, out QpdfObject? raw) && raw is PdfDictionary page)
            { PrunePage(copier, id, page, inherited, resolver, limits, governor, diagnostics, token); }
        }
    }

    private static void PrunePage(GraphCopier copier, int pageId, PdfDictionary page, ResourceNameSet inherited,
        PoolResolver resolver, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Resources"u8, governor, token, out QpdfObject raw)
            || CatalogCopy.ResolveDictionary(raw, resolver, governor, token) is not PdfDictionary resources) { return; }
        if (AppearanceResources.PageNeedsResources(page, resolver, governor, token)) { return; }
        using AnalysisChunks? chunks = CollectPageChunks(page, resolver, limits, governor, token);
        if (chunks is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped resource pruning for content that could not be read.");
            return;
        }
        using JoinedContent content = chunks.Join();
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary? pruned = ScanAndPrune(content.Bytes, resources, true, inherited, resolver, limits, diagnostics, graph);
        if (pruned is null) { return; }
        PdfDictionary replacement = ResourceRebuild.ReplaceResources(graph, page, pruned);
        copier.ValidateReplacement(pageId); graph.Retain(); copier.ReplaceCopiedObject(pageId, replacement);
    }

    private static bool IsFormXObject(PdfStream stream, ResourceGovernor governor, CancellationToken cancellationToken)
        => PdfDictionaryLookup.TryGet(stream.Dictionary, "Subtype"u8, governor, cancellationToken, out QpdfObject subtype)
            && subtype is PdfName name && PdfDictionaryLookup.NameEquals(name, "Form"u8, governor, cancellationToken);

    private static PdfDictionary? ScanForm(PdfStream form, PdfDictionary? resources, ResourceNameSet inherited,
        PoolResolver resolver, QpdfLimits limits, DiagnosticBag diagnostics, OwnedAllocationBatch graph)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        using AnalysisChunks? chunks = CollectFormContent(form, resolver, limits, governor, token);
        if (chunks is null)
        {
            inherited.MarkUnresolvedScope();
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped resource pruning for content that could not be read.");
            return null;
        }
        using JoinedContent content = chunks.Join();
        return ScanAndPrune(content.Bytes, resources, false, inherited, resolver, limits, diagnostics, graph);
    }

    private static PdfDictionary? ScanAndPrune(ReadOnlyMemory<byte> content, PdfDictionary? resources,
        bool isPage, ResourceNameSet unresolved, PoolResolver resolver, QpdfLimits limits,
        DiagnosticBag diagnostics, OwnedAllocationBatch graph)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken cancellationToken = graph.Token;
        using ResourceNameSet names = ResourceNameSet.Create(governor, cancellationToken);
        using ResourceNameSet fontsUsed = ResourceNameSet.Create(governor, cancellationToken);
        using ResourceNameSet objectsUsed = ResourceNameSet.Create(governor, cancellationToken);
        if (!ScanNames(content, names, fontsUsed, objectsUsed, resources, resolver, limits, governor, diagnostics, cancellationToken))
        {
            if (!isPage) { unresolved.MarkUnresolvedScope(); }
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped resource pruning for content that could not be parsed.");
            return null;
        }
        if (resources is null)
        {
            // Inherited consumers can hide glyph and mask dependencies as well.
            using var inherited = names.GetEnumerator();
            while (inherited.MoveNext(governor, cancellationToken))
            { unresolved.AddDecoded(inherited.Current.Key.RawSpan); }
            return null;
        }
        for (int section = 0; section < 2; section++)
        {
            ReadOnlySpan<byte> key = section == 0 ? "Font"u8 : "XObject"u8;
            PdfDictionary? category = PdfDictionaryLookup.TryGet(resources, key, governor, cancellationToken, out QpdfObject rawCategory)
                ? CatalogCopy.ResolveDictionary(rawCategory, resolver, governor, cancellationToken) : null;
            using var uses = (section == 0 ? fontsUsed : objectsUsed).GetEnumerator();
            while (uses.MoveNext(governor, cancellationToken))
            {
                if (category is not null && PdfDictionaryLookup.TryGet(category, uses.Current.Key.RawSpan, governor, cancellationToken, out QpdfObject binding)
                    && CatalogCopy.ResolveValue(binding, resolver, governor, cancellationToken) is not (null or PdfNull)) { continue; }
                // A spelling in another category cannot satisfy this operator.
                if (!isPage) { unresolved.MarkUnresolvedScope(); }
                return null;
            }
        }
        if (unresolved.HasUnresolvedScope) { return null; }
        if (HasUnscannedResourceConsumer(resources, names, unresolved, resolver, governor, cancellationToken))
        {
            if (!isPage) { unresolved.MarkUnresolvedScope(); }
            return null;
        }

        // Fresh section/resource containers borrow their immutable keys and values.
        // Callers publish only role-specific headers after analysis completes.
        PdfNameTable<QpdfObject>? fonts = null, xobjects = null;
        long fontAllocation = -1L, xobjectAllocation = -1L;
        bool changed = false;
        for (int sectionIndex = 0; sectionIndex < 2; sectionIndex++)
        {
            ReadOnlySpan<byte> sectionKey = sectionIndex == 0 ? "Font"u8 : "XObject"u8;
            if (!PdfDictionaryLookup.TryGet(resources, sectionKey, governor, cancellationToken, out QpdfObject sectionRaw) || sectionRaw is PdfNull)
            {
                continue;
            }

            QpdfObject sectionTarget = sectionRaw is PdfReference sectionReference
                ? ResolvePool(resolver, sectionReference, governor, cancellationToken) ?? sectionRaw
                : sectionRaw;
            if (sectionTarget is not PdfDictionary sectionDict)
            {
                continue;
            }

            PdfNameTable<QpdfObject> kept = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
            if (sectionIndex == 0) { fonts = kept; fontAllocation = allocation; }
            else { xobjects = kept; xobjectAllocation = allocation; }
            using var sectionEntries = sectionDict.GetEntryEnumerator();
            while (sectionEntries.MoveNext(governor, cancellationToken))
            {
                PdfName key = sectionEntries.Current.Key;
                QpdfObject held = sectionEntries.Current.Value;
                if (held is null)
                {
                    changed = true;
                    continue;
                }

                if (unresolved.ContainsDecoded(key.RawSpan))
                {
                    kept.Assign(graph.Memory, governor, key, held, cancellationToken);
                    continue;
                }

                if (names.ContainsDecoded(key.RawSpan))
                {
                    kept.Assign(graph.Memory, governor, key, held, cancellationToken);
                    continue;
                }

                changed = true;
            }

        }

        if (!changed)
        {
            return null;
        }

        PdfNameTable<QpdfObject> resourcesEntries = PdfDictionaryRebuild.CreateEntries(graph, out long resourceAllocation);
        PdfName? fontKey = null, xobjectKey = null;
        using var resourceEntries = resources.GetEntryEnumerator();
        while (resourceEntries.MoveNext(governor, cancellationToken))
        {
            QpdfObject found = resourceEntries.Current.Value;
            if (found is not null)
            {
                PdfName key = resourceEntries.Current.Key;
                resourcesEntries.Assign(graph.Memory, governor, key, found, cancellationToken);
                if (fonts is not null && PdfDictionaryLookup.NameEquals(key, "Font"u8, governor, cancellationToken)) { fontKey = key; }
                if (xobjects is not null && PdfDictionaryLookup.NameEquals(key, "XObject"u8, governor, cancellationToken)) { xobjectKey = key; }
            }
        }

        if (fonts is not null)
        {
            PdfDictionary kept = PdfDictionaryRebuild.Complete(graph, fonts, fontAllocation);
            resourcesEntries.Assign(graph.Memory, governor, fontKey ?? throw new InvalidOperationException("Font resource key is missing."), kept, cancellationToken);
        }
        if (xobjects is not null)
        {
            PdfDictionary kept = PdfDictionaryRebuild.Complete(graph, xobjects, xobjectAllocation);
            resourcesEntries.Assign(graph.Memory, governor, xobjectKey ?? throw new InvalidOperationException("XObject resource key is missing."), kept, cancellationToken);
        }
        PdfDictionary rebuilt = PdfDictionaryRebuild.Complete(graph, resourcesEntries, resourceAllocation);
        return rebuilt;
    }
    private static AnalysisChunks? CollectPageChunks(
        PdfDictionary page,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contents))
        { return AnalysisChunks.Create(governor, cancellationToken); }
        using ContentEntries? entries = ContentEntries.TryRead(contents, resolver, governor, cancellationToken);
        if (entries is null) { return null; }
        AnalysisChunks chunks = AnalysisChunks.Create(governor, cancellationToken);
        try
        {
            for (int index = 0; index < entries.Count; index++)
            {
                if (!chunks.TryAdd(entries.Get(index).Stream, resolver, limits))
                { chunks.Dispose(); return null; }
            }
            return chunks;
        }
        catch { chunks.Dispose(); throw; }
    }

    private static AnalysisChunks? CollectFormContent(
        PdfStream form,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        AnalysisChunks chunks = AnalysisChunks.Create(governor, cancellationToken);
        try
        {
            if (chunks.TryAdd(form, resolver, limits)) { return chunks; }
            chunks.Dispose();
            return null;
        }
        catch { chunks.Dispose(); throw; }
    }

    // Glyph and mask programs are outside this scanner. A local resource
    // dictionary does not prove that all of their dependencies are local.
    private static bool HasUnscannedResourceConsumer(PdfDictionary resources, ResourceNameSet names,
        ResourceNameSet inherited, PoolResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        for (int section = 0; section < 2; section++)
        {
            ReadOnlySpan<byte> key = section == 0 ? "Font"u8 : "ExtGState"u8;
            if (!PdfDictionaryLookup.TryGet(resources, key, governor, token, out QpdfObject raw)) { continue; }
            PdfDictionary? category = CatalogCopy.ResolveDictionary(raw, resolver, governor, token);
            if (category is null) { continue; }
            using PdfDictionary.EntryEnumerator entries = category.GetEntryEnumerator();
            while (entries.MoveNext(governor, token))
            {
                if (!names.ContainsDecoded(entries.Current.Key.RawSpan) && !inherited.ContainsDecoded(entries.Current.Key.RawSpan)) { continue; }
                PdfDictionary? consumer = CatalogCopy.ResolveDictionary(entries.Current.Value, resolver, governor, token);
                if (consumer is null) { continue; }
                if (section == 0)
                {
                    if (!PdfDictionaryLookup.HasName(consumer, "Subtype"u8, "Type3"u8, resolver, governor, token)) { continue; }
                }
                else
                {
                    if (PdfDictionaryLookup.TryGet(consumer, "Font"u8, governor, token, out raw)
                        && CatalogCopy.ResolveValue(raw, resolver, governor, token) is PdfArray selection && selection.Count > 0
                        && CatalogCopy.ResolveDictionary(selection[0], resolver, governor, token) is PdfDictionary font
                        && PdfDictionaryLookup.HasName(font, "Subtype"u8, "Type3"u8, resolver, governor, token))
                    { return true; }
                    if (!PdfDictionaryLookup.TryGet(consumer, "SMask"u8, governor, token, out raw)) { continue; }
                    PdfDictionary? mask = CatalogCopy.ResolveDictionary(raw, resolver, governor, token);
                    if (mask is null || !PdfDictionaryLookup.TryGet(mask, "G"u8, governor, token, out raw)
                        || CatalogCopy.ResolveValue(raw, resolver, governor, token) is not PdfStream) { continue; }
                }
                return true;
            }
        }
        return false;
    }

    private static bool ScanNames(
        ReadOnlyMemory<byte> content,
        ResourceNameSet names,
        ResourceNameSet fontsUsed,
        ResourceNameSet objectsUsed,
        PdfDictionary? resources,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope tokenizerMemory = ParserMemoryScope.Create(governor);
        tokenizerMemory.Reserve(192L);
        ContentTokenizer tokenizer = new(content, governor, cancellationToken);
        ReadOnlyMemory<byte> lastName = default;
        bool hasName = false;
        try
        {
            while (tokenizer.TryReadToken(out ContentToken token))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (token.Kind == ContentTokenKind.Name)
                {
                    lastName = token.Data; hasName = true;
                    continue;
                }

                if (token.Kind == ContentTokenKind.Operator)
                {
                    ReadOnlySpan<byte> word = token.Data.Span;
                    governor.CheckCancellation();
                    if (word.SequenceEqual("ID"u8))
                    {
                        InlineImageStorage.ReadData(tokenizer, resources, resolver, limits, governor, diagnostics, cancellationToken, out _);
                        continue;
                    }

                    bool scopedOperator = word.SequenceEqual("Tf"u8) || word.SequenceEqual("Do"u8);
                    if (hasName && (scopedOperator || word.SequenceEqual("CS"u8) || word.SequenceEqual("cs"u8)
                        || word.SequenceEqual("gs"u8) || word.SequenceEqual("SCN"u8) || word.SequenceEqual("scn"u8)
                        || word.SequenceEqual("BDC"u8) || word.SequenceEqual("DP"u8) || word.SequenceEqual("sh"u8)))
                    {
                        names.AddRaw(lastName.Span);
                        if (scopedOperator)
                        {
                            (word.SequenceEqual("Tf"u8) ? fontsUsed : objectsUsed).AddRaw(lastName.Span);
                        }
                    }

                    continue;
                }
            }
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            return false;
        }

        return true;
    }
    internal static bool SharedResourcesHeuristic(
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
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
        if (!PdfDictionaryLookup.TryGet(engine.Trailer, "Root"u8, governor, cancellationToken, out QpdfObject rootRaw) || rootRaw is null || rootRaw is PdfNull)
        {
            return false;
        }

        QpdfObject root = rootRaw is PdfReference rootReference
            ? ResolveEngine(engine, rootReference, governor, cancellationToken) ?? rootRaw
            : rootRaw;
        if (root is not PdfDictionary catalog)
        {
            return false;
        }

        if (!PdfDictionaryLookup.TryGet(catalog, "Pages"u8, governor, cancellationToken, out QpdfObject pagesRaw) || pagesRaw is null || pagesRaw is PdfNull)
        {
            return false;
        }

        QpdfObject pages = pagesRaw is PdfReference pagesReference
            ? ResolveEngine(engine, pagesReference, governor, cancellationToken) ?? pagesRaw
            : pagesRaw;
        using SharedResourceWalk walk = SharedResourceWalk.Create(governor, cancellationToken);
        walk.Push(pages, cancellationToken);
        while (walk.TryPop(cancellationToken, out QpdfObject node))
        {
            PdfDictionary? dictionary = null;
            if (node is PdfReference nodeReference)
            {
                if (!walk.VisitNode(nodeReference.ObjectNumber, cancellationToken))
                {
                    continue;
                }

                QpdfObject? resolved = ResolveEngine(engine, nodeReference, governor, cancellationToken);
                if (resolved is PdfStream resolvedStream)
                {
                    dictionary = resolvedStream.Dictionary;
                }
                else
                {
                    dictionary = resolved as PdfDictionary;
                }
            }
            else if (node is PdfStream directStream)
            {
                dictionary = directStream.Dictionary;
            }
            else if (node is PdfDictionary directDict)
            {
                dictionary = directDict;
            }

            if (dictionary is null)
            {
                continue;
            }

            if (PdfDictionaryLookup.TryGet(dictionary, "Kids"u8, governor, cancellationToken, out QpdfObject kidsRaw) && kidsRaw is PdfArray kids)
            {
                bool hasResources = PdfDictionaryLookup.TryGet(dictionary, "Resources"u8, governor, cancellationToken, out QpdfObject branchResources)
                    && branchResources is not null
                    && branchResources is not PdfNull;
                if (hasResources)
                {
                    return true;
                }

                for (int index = 0; index < kids.Count; index++)
                {
                    walk.Push(kids[index], cancellationToken);
                }

                continue;
            }

            if (!PdfDictionaryLookup.TryGet(dictionary, "Resources"u8, governor, cancellationToken, out QpdfObject leafResources) || leafResources is null || leafResources is PdfNull)
            {
                continue;
            }

            if (leafResources is PdfReference leafReference)
            {
                if (!walk.VisitResource(leafReference.ObjectNumber, cancellationToken))
                {
                    return true;
                }
            }

            QpdfObject resourcesTarget = leafResources is PdfReference leafLink
                ? ResolveEngine(engine, leafLink, governor, cancellationToken) ?? leafResources
                : leafResources;
            if (resourcesTarget is not PdfDictionary resourcesDict)
            {
                continue;
            }

            if (!PdfDictionaryLookup.TryGet(resourcesDict, "XObject"u8, governor, cancellationToken, out QpdfObject xobjectsRaw) || xobjectsRaw is null || xobjectsRaw is PdfNull)
            {
                continue;
            }

            if (xobjectsRaw is PdfReference xobjectsReference)
            {
                if (!walk.VisitResource(xobjectsReference.ObjectNumber, cancellationToken))
                {
                    return true;
                }
            }

            QpdfObject xobjectsTarget = xobjectsRaw is PdfReference xobjectsLink
                ? ResolveEngine(engine, xobjectsLink, governor, cancellationToken) ?? xobjectsRaw
                : xobjectsRaw;
            if (xobjectsTarget is not PdfDictionary xobjects)
            {
                continue;
            }

            using PdfDictionary.EntryEnumerator entries = xobjects.GetEntryEnumerator();
            while (entries.MoveNext(governor, cancellationToken))
            {
                QpdfObject entry = entries.Current.Value;
                if (entry is null || entry is PdfNull)
                {
                    continue;
                }

                QpdfObject target = entry is PdfReference entryReference
                    ? ResolveEngine(engine, entryReference, governor, cancellationToken) ?? entry
                    : entry;
                if (target is PdfStream stream && IsFormXObject(stream, governor, cancellationToken))
                {
                    walk.Push(entry, cancellationToken);
                }
            }
        }

        return false;
    }

    private static QpdfObject? ResolveEngine(DocumentEngine engine, PdfReference reference, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (PdfObjectLookup.TryResolve(reference, (IIndirectResolver)engine, governor, cancellationToken, out QpdfObject? found) && found is not null && found is not PdfNull)
        {
            return found;
        }

        return null;
    }
}
