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
    internal bool PreserveUnreferenced { get; init; }
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
    private static readonly string[] ResourceOperators =
    [
        "CS", "cs", "gs", "Tf", "SCN", "scn", "BDC", "DP", "sh", "Do",
    ];

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

        foreach (int pageId in pageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary)
            {
                continue;
            }

            PdfDictionary page = (PdfDictionary)raw;
            if (request.Externalize)
            {
                page = ExternalizePageWithForms(copier, pageId, page, request, limits, governor, diagnostics, cancellationToken);
            }

            if (request.Coalesce)
            {
                page = CoalescePage(copier, pageId, page, request, limits, governor, diagnostics, cancellationToken);
            }

            if (request.Normalize)
            {
                page = NormalizePage(copier, pageId, page, request, limits, governor, diagnostics, cancellationToken);
            }

            if (prune)
            {
                page = PrunePage(copier, pageId, page, limits, governor, diagnostics, cancellationToken);
            }
        }
    }
    private static PdfDictionary ExternalizePageWithForms(GraphCopier copier, int pageId, PdfDictionary page, ContentTransformRequest request, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        PdfDictionary current = InlineImages.ExternalizePage(copier, pageId, page, request.MinBytes, request.Compress, request.Level, limits, governor, diagnostics, cancellationToken, request.PreserveUnreferenced, request.FilterOptions);
        if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? refreshed) || refreshed is not PdfDictionary freshPage)
        {
            return current;
        }

        current = freshPage;
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        if (!PdfDictionaryLookup.TryGet(current, "Resources"u8, governor, cancellationToken, out QpdfObject resourcesRaw) || resourcesRaw is PdfNull)
        {
            return current;
        }

        QpdfObject resourcesTarget = resourcesRaw is PdfReference resourcesReference ? ResolvePool(resolver, resourcesReference, governor, cancellationToken) ?? resourcesRaw : resourcesRaw;
        if (resourcesTarget is not PdfDictionary resources)
        {
            return current;
        }

        using FormResourceWalk forms = CollectFormStreams(copier, resources, governor, cancellationToken);
        for (int index = 0; index < forms.Count; index++)
        {
            int? formId = forms.Get(index).Id;
            if (!formId.HasValue)
            {
                continue;
            }

            if (!copier.CopiedObjects.TryGetValue(formId.Value, out QpdfObject? formRaw) || formRaw is not PdfStream form)
            {
                continue;
            }

            InlineImages.ExternalizeForm(copier, formId.Value, form, request.MinBytes, request.Compress, request.Level, limits, governor, diagnostics, cancellationToken, request.PreserveUnreferenced, request.FilterOptions);
        }

        if (copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? finalRaw) && finalRaw is PdfDictionary finalPage)
        {
            return finalPage;
        }

        return current;
    }

    private static ContentEntries? ReadPageContents(
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
        ContentEntries entries = ContentEntries.Create(governor, cancellationToken);
        try
        {
            PdfArray? array = contents as PdfArray;
            int count = array?.Count ?? 1;
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                QpdfObject item = array is null ? contents : array[index];
                if (item is PdfReference reference)
                {
                    if (!PdfObjectLookup.TryResolve(reference, copier.CopiedObjects, governor, cancellationToken, out QpdfObject? resolved) || resolved is not PdfStream resolvedStream)
                    {
                        return SkipContentEntries(entries, diagnostics, "Skipped content operation on an unresolvable content stream.");
                    }
                    entries.Add(new(reference.ObjectNumber, index, resolvedStream));
                    continue;
                }
                if (item is PdfStream direct)
                {
                    entries.Add(new(null, index, direct));
                    continue;
                }
                return SkipContentEntries(entries, diagnostics, "Skipped content operation on a non-stream content entry.");
            }
            return entries;
        }
        catch { entries.Dispose(); throw; }
    }

    private static ContentEntries? SkipContentEntries(ContentEntries entries, DiagnosticBag diagnostics, string message)
    {
        entries.Dispose();
        diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, message);
        return null;
    }

    private static (byte[] Data, bool Decoded) DecodeForContent(
        PdfStream stream,
        IIndirectResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
        => AnalysisChunks.DecodeForContent(stream, resolver, limits, governor, memory, cancellationToken);

    private static PdfDictionary StrippedContentDictionary(PdfDictionary dictionary, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 2, cancellationToken);
        PdfDictionary result = PdfDictionaryRebuild.StripStreamEncoding(graph, dictionary);
        graph.Retain(); return result;
    }

    private static PdfDictionary ReplaceEntry(PdfDictionary dictionary, ReadOnlySpan<byte> key, QpdfObject value, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 4, cancellationToken);
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, dictionary, key, value);
        graph.Retain(); return result;
    }

    internal static List<int> ReplacedContentIds(QpdfObject? value, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        List<int> ids = ParserContainers.CreateList<int>(memory);
        if (value is PdfReference reference)
        {
            AddContentId(ids, reference.ObjectNumber, memory, governor, token);
        }
        else if (value is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (array[i] is PdfReference item)
                {
                    AddContentId(ids, item.ObjectNumber, memory, governor, token);
                }
            }
        }

        return ids;
    }

    private static void AddContentId(List<int> ids, int id, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (ids.Count == ids.Capacity)
        {
            long capacity = Math.Min(Array.MaxLength, Math.Max(4L, ids.Capacity * 2L));
            governor.NoteOperation();
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        }
        ParserContainers.AddItem(memory, ids, id, sizeof(int));
    }

    private static PdfDictionary CoalescePage(
        GraphCopier copier,
        int pageId,
        PdfDictionary page,
        ContentTransformRequest request,
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
        PdfStream coalesced = BuildCoalescedContent(entries, resolver, request, limits, governor, cancellationToken);

        PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contentsRaw);
        PdfDictionary replaced;
        if (contentsRaw is PdfReference contentsRef)
        {
            copier.ReplaceCopiedObject(contentsRef.ObjectNumber, coalesced);
            replaced = page;
        }
        else
        {
            PdfReference? link = copier.AddIndirectObject(coalesced);
            if (link is null)
            {
                return page;
            }

            replaced = ReplaceEntry(page, "Contents"u8, link, governor, cancellationToken);
            using ParserMemoryScope orphanMemory = ParserMemoryScope.Create(governor);
            List<int>? orphaned = ReplacedContentIds(contentsRaw, orphanMemory, governor, cancellationToken);
            try
            {
                if (!request.PreserveUnreferenced && orphaned.Count > 0) { copier.DropPoolObjectsIfUnreferenced(orphaned, cancellationToken); }
            }
            finally { orphaned = null; }
        }

        copier.ReplaceCopiedObject(pageId, replaced);
        return replaced;
    }

    private static PdfStream BuildCoalescedContent(ContentEntries entries, PoolResolver resolver, ContentTransformRequest request,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, token);
        for (int index = 0; index < entries.Count; index++) { chunks.AddForContent(entries.Get(index).Stream, resolver, limits); }
        using JoinedContent joined = JoinedContent.Create(chunks.Parts, governor, token);
        chunks.Dispose();
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 8, token);
        bool compress = request.Compress && joined.RawBytes.Length > 0;
        byte[] data = compress ? FlateFilter.Encode(joined.RawBytes, request.Level, limits, governor, graph.Memory, token) : joined.TakeBytes(graph);
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

    private static void AddContentBuffer(OwnedAllocationBatch graph, byte[] data)
    { if (data.Length != 0) { graph.Add(checked(data.Length + GovernedBufferStream.ArrayOverheadBytes)); } }

    private static PdfStream CreateContentStream(OwnedAllocationBatch graph, PdfDictionary dictionary, byte[] data)
    {
        graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation(); graph.Memory.Reserve(32L);
        PdfStream stream = PdfStream.FromOwnedData(dictionary, data); graph.Add(32L); return stream;
    }
    private static PdfDictionary NormalizePage(
        GraphCopier copier,
        int pageId,
        PdfDictionary page,
        ContentTransformRequest request,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ContentEntries? entries = ReadPageContents(copier, page, governor, diagnostics, cancellationToken);
        if (entries is null || entries.Count == 0)
        {
            return page;
        }

        using ParserMemoryScope rewriteMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(rewriteMemory, copier.CopiedObjects);
        GovernedIntDictionary<PdfStream>? rewritten = GovernedIntDictionary<PdfStream>.Create(rewriteMemory, governor, IntPtr.Size, cancellationToken);
        try
        {
            for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                ContentEntries.Entry entry = entries.Get(entryIndex);
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                PdfStream? replacement = BuildNormalizedContent(entry.Stream, resolver, limits, governor, diagnostics, cancellationToken);
                if (replacement is null) { continue; }
                if (entry.ObjectId.HasValue)
                {
                    copier.ReplaceCopiedObject(entry.ObjectId.Value, replacement);
                }
                else
                {
                    rewritten.Assign(rewriteMemory, entry.ArrayIndex, replacement);
                }
            }

            if (rewritten.Count == 0)
            {
                if (copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? untouched) && untouched is PdfDictionary untouchedPage)
                {
                    return untouchedPage;
                }

                return page;
            }

            PdfDictionary current = page;
            if (copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? latest) && latest is PdfDictionary latestPage)
            {
                current = latestPage;
            }

            current = ReplaceContentEntries(current, rewritten, governor, cancellationToken);
            copier.ReplaceCopiedObject(pageId, current);
            return current;
        }
        finally { rewritten = null; }
    }

    private static PdfStream? BuildNormalizedContent(PdfStream original, PoolResolver resolver, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool contentError = false;
        using (ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor))
        {
            byte[] data = Array.Empty<byte>(); bool decoded;
            try
            {
                try { (data, decoded) = DecodeForContent(original, resolver, limits, governor, decodedMemory, token); }
                catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException))
                { data = original.ArrayData(decodedMemory, governor, token); decoded = false; }
                using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 4, token);
                byte[]? normalized = null;
                try
                {
                    try { normalized = NormalizeContentBytesOwned(data, limits, governor, token, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), graph.Memory); }
                    catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException)) { contentError = true; }
                    if (normalized is not null)
                    {
                        data = Array.Empty<byte>(); decodedMemory.Dispose(); AddContentBuffer(graph, normalized);
                        PdfDictionary dictionary = decoded ? PdfDictionaryRebuild.StripStreamEncoding(graph, original.Dictionary) : original.Dictionary;
                        PdfStream replacement = CreateContentStream(graph, dictionary, normalized); graph.Retain(); return replacement;
                    }
                }
                finally { normalized = null; }
            }
            finally { data = Array.Empty<byte>(); }
        }
        if (contentError) { diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Left a content stream unnormalized after a content error."); }
        return null;
    }

    private static PdfDictionary ReplaceContentEntries(
        PdfDictionary page,
        GovernedIntDictionary<PdfStream> rewritten,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contents) || contents is null)
        {
            return page;
        }

        if (contents is PdfStream)
        {
            foreach (KeyValuePair<int, PdfStream> entry in rewritten)
            {
                return ReplaceEntry(page, "Contents"u8, entry.Value, governor, cancellationToken);
            }

            return page;
        }

        if (contents is PdfArray array)
        {
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 6, cancellationToken);
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
                if (rewritten.TryGetValue(i, out PdfStream? replacement))
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
    internal static byte[] NormalizeContentBytes(
        byte[] data,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        long maximumBytes, string limitName)
        => NormalizeContentBytesCore(data, limits, governor, cancellationToken, maximumBytes, limitName, null);

    internal static byte[] NormalizeContentBytesOwned(byte[] data, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken cancellationToken, long maximumBytes, string limitName, ParserMemoryScope memory)
    {
        memory.CheckOwner(governor);
        return NormalizeContentBytesCore(data, limits, governor, cancellationToken, maximumBytes, limitName, memory);
    }

    private static byte[] NormalizeContentBytesCore(byte[] data, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken cancellationToken, long maximumBytes, string limitName, ParserMemoryScope? memory)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor); scratch.Reserve(192L);
        ContentTokenizer tokenizer = new(data, limits, governor, cancellationToken)
        {
            IncludeComments = true,
            IncludeWhitespace = true,
        };
        using GovernedBufferStream output = new(governor, maximumBytes, limitName, cancellationToken, Math.Max(1, Math.Min(8192, data.Length)));
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            switch (token.Kind)
            {
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
                case ContentTokenKind.InlineImage:
                    AppendRaw(output, token);
                    break;
                default:
                    AppendRaw(output, token);
                    break;
            }
        }

        return memory is null ? output.ToArray() : output.ToArray(memory, cancellationToken);
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

            decoded.WriteByte(span[i]);
            i += 1;
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

    private static bool IsHexDigit(byte b)
    {
        return (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'f')
            || (b >= (byte)'A' && b <= (byte)'F');
    }

    private static int HexValue(byte b)
    {
        if (b >= (byte)'0' && b <= (byte)'9')
        {
            return b - (byte)'0';
        }

        if (b >= (byte)'a' && b <= (byte)'f')
        {
            return b - (byte)'a' + 10;
        }

        return b - (byte)'A' + 10;
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

    private static PdfDictionary PrunePage(
        GraphCopier copier,
        int pageId,
        PdfDictionary page,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        if (!PdfDictionaryLookup.TryGet(page, "Resources"u8, governor, cancellationToken, out QpdfObject resourcesRaw) || resourcesRaw is PdfNull)
        {
            return page;
        }

        QpdfObject resourcesTarget = resourcesRaw is PdfReference resourcesReference
            ? ResolvePool(resolver, resourcesReference, governor, cancellationToken) ?? resourcesRaw
            : resourcesRaw;
        if (resourcesTarget is not PdfDictionary resources)
        {
            return page;
        }

        using ResourceNameSet unresolved = ResourceNameSet.Create(governor, cancellationToken);
        using (FormResourceWalk forms = CollectFormStreams(copier, resources, governor, cancellationToken))
        {
            for (int index = 0; index < forms.Count; index++)
            {
                FormResourceWalk.Form form = forms.Get(index);
                ScanForm(copier, form.Id, form.Stream, unresolved, limits, governor, diagnostics, cancellationToken);
            }
        }

        using AnalysisChunks? pageChunks = CollectPageChunks(copier, page, resolver, limits, governor, cancellationToken);
        if (pageChunks is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped resource pruning for content that could not be read.");
            return page;
        }

        using JoinedContent content = JoinContentBytes(pageChunks.Parts, governor, cancellationToken);
        pageChunks.Dispose();
        ScanAndPrune(copier, pageId, page, content.Bytes, resources, true, unresolved, limits, governor, diagnostics, cancellationToken);
        if (copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? refreshed) && refreshed is PdfDictionary refreshedPage)
        {
            return refreshedPage;
        }

        return page;
    }

    private static FormResourceWalk CollectFormStreams(
        GraphCopier copier,
        PdfDictionary resources,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        FormResourceWalk forms = FormResourceWalk.Create(governor, cancellationToken);
        try
        {
            PoolResolver resolver = forms.CreateResolver(copier.CopiedObjects);
            forms.AddScope(resources);
            for (int scope = 0; scope < forms.ScopeCount; scope++)
            {
                PdfDictionary current = forms.GetScope(scope);
                if (!PdfDictionaryLookup.TryGet(current, "XObject"u8, governor, cancellationToken, out QpdfObject xobjectsRaw) || xobjectsRaw is PdfNull)
                { continue; }
                QpdfObject xobjectsTarget = xobjectsRaw is PdfReference xobjectsReference
                    ? ResolvePool(resolver, xobjectsReference, governor, cancellationToken) ?? xobjectsRaw : xobjectsRaw;
                if (xobjectsTarget is not PdfDictionary xobjects) { continue; }
                using var entries = xobjects.GetEntryEnumerator();
                while (entries.MoveNext(governor, cancellationToken))
                {
                    QpdfObject entry = entries.Current.Value;
                    if (entry is null || entry is PdfNull) { continue; }
                    int? formId = null; QpdfObject target = entry;
                    if (entry is PdfReference entryReference)
                    {
                        formId = entryReference.ObjectNumber;
                        target = ResolvePool(resolver, entryReference, governor, cancellationToken) ?? entry;
                    }
                    if (target is not PdfStream stream || !IsFormXObject(stream, governor, cancellationToken)) { continue; }
                    if (formId.HasValue && !forms.VisitNumber(formId.Value)) { continue; }
                    forms.Add(formId, stream);
                    if (PdfDictionaryLookup.TryGet(stream.Dictionary, "Resources"u8, governor, cancellationToken, out QpdfObject nestedRaw) && nestedRaw is not PdfNull)
                    {
                        QpdfObject nestedTarget = nestedRaw is PdfReference nestedReference
                            ? ResolvePool(resolver, nestedReference, governor, cancellationToken) ?? nestedRaw : nestedRaw;
                        if (nestedTarget is PdfDictionary nested) { forms.AddScope(nested); }
                    }
                }
            }
            return forms;
        }
        catch { forms.Dispose(); throw; }
    }

    private static bool IsFormXObject(PdfStream stream, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (PdfDictionaryLookup.TryGet(stream.Dictionary, "Subtype"u8, governor, cancellationToken, out QpdfObject subtype) && subtype is PdfName name)
        {
            return PdfDictionaryLookup.NameEquals(name, "Form"u8, governor, cancellationToken);
        }

        return false;
    }

    private static void ScanForm(
        GraphCopier copier,
        int? formId,
        PdfStream form,
        ResourceNameSet unresolved,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        PdfDictionary? resources = null;
        if (PdfDictionaryLookup.TryGet(form.Dictionary, "Resources"u8, governor, cancellationToken, out QpdfObject ownRaw) && ownRaw is not PdfNull)
        {
            QpdfObject ownTarget = ownRaw is PdfReference ownReference
                ? ResolvePool(resolver, ownReference, governor, cancellationToken) ?? ownRaw
                : ownRaw;
            resources = ownTarget as PdfDictionary;
        }

        using AnalysisChunks? chunks = CollectFormContent(form, resolver, limits, governor, cancellationToken);
        if (chunks is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped resource pruning for content that could not be read.");
            return;
        }

        using JoinedContent content = JoinContentBytes(chunks.Parts, governor, cancellationToken);
        chunks.Dispose();
        ScanAndPrune(copier, formId, null, content.Bytes, resources, false, unresolved, limits, governor, diagnostics, cancellationToken);
    }

    private static void ScanAndPrune(
        GraphCopier copier,
        int? nodeId,
        PdfDictionary? page,
        ReadOnlyMemory<byte> content,
        PdfDictionary? resources,
        bool isPage,
        ResourceNameSet unresolved,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        using ResourceNameSet names = ResourceNameSet.Create(governor, cancellationToken);
        using ResourceNameSet scoped = ResourceNameSet.Create(governor, cancellationToken);
        if (!ScanNames(content, names, scoped, limits, governor, diagnostics, cancellationToken))
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped resource pruning for content that could not be parsed.");
            return;
        }


        using ResourceNameSet known = ResourceNameSet.Create(governor, cancellationToken);
        if (resources is not null)
        {
            for (int section = 0; section < 2; section++)
            {
                ReadOnlySpan<byte> key = section == 0 ? "Font"u8 : "XObject"u8;
                if (PdfDictionaryLookup.TryGet(resources, key, governor, cancellationToken, out QpdfObject sectionRaw) && sectionRaw is not PdfNull)
                {
                    QpdfObject sectionTarget = sectionRaw is PdfReference sectionReference
                        ? ResolvePool(resolver, sectionReference, governor, cancellationToken) ?? sectionRaw
                        : sectionRaw;
                    if (sectionTarget is PdfDictionary sectionDict)
                    {
                        using var entries = sectionDict.GetEntryEnumerator();
                        while (entries.MoveNext(governor, cancellationToken))
                        {
                            known.AddDecoded(entries.Current.Key.RawSpan);
                        }
                    }
                }
            }
        }

        using var scopedEntries = scoped.GetEnumerator();
        while (scopedEntries.MoveNext(governor, cancellationToken))
        {
            ReadOnlySpan<byte> name = scopedEntries.Current.Key.RawSpan;
            if (!known.ContainsDecoded(name))
            {
                unresolved.AddDecoded(name);
            }
        }

        if (resources is null)
        {
            return;
        }

        bool hasUnknown = false;
        using var unknownEntries = scoped.GetEnumerator();
        while (unknownEntries.MoveNext(governor, cancellationToken))
        {
            if (!known.ContainsDecoded(unknownEntries.Current.Key.RawSpan))
            {
                hasUnknown = true;
                break;
            }
        }

        if (hasUnknown)
        {
            // An explicit local form dictionary cannot resolve its usage.
            // Preserve the page scope conservatively, rather than assuming
            // only the unresolved spelling needs to survive there. An absent
            // form dictionary still uses the existing inherited-name policy.
            if (!isPage) { unresolved.MarkUnresolvedLocalScope(); }
            return;
        }

        if (isPage && unresolved.HasUnresolvedLocalScope) { return; }

        // Two sections, resources and the node header each own a table/wrapper.
        // A form adds one stream wrapper; a synthetic header can add a name/buffer.
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 11, cancellationToken);
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

            PdfNameTable<QpdfObject> kept = ResourceRebuild.CreateEntries(graph, out long allocation);
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

                if (isPage && unresolved.ContainsDecoded(key.RawSpan))
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

        if (!changed || !nodeId.HasValue)
        {
            return;
        }

        PdfNameTable<QpdfObject> resourcesEntries = ResourceRebuild.CreateEntries(graph, out long resourceAllocation);
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
            PdfDictionary kept = ResourceRebuild.Complete(graph, fonts, fontAllocation);
            resourcesEntries.Assign(graph.Memory, governor, fontKey ?? throw new InvalidOperationException("Font resource key is missing."), kept, cancellationToken);
        }
        if (xobjects is not null)
        {
            PdfDictionary kept = ResourceRebuild.Complete(graph, xobjects, xobjectAllocation);
            resourcesEntries.Assign(graph.Memory, governor, xobjectKey ?? throw new InvalidOperationException("XObject resource key is missing."), kept, cancellationToken);
        }
        PdfDictionary rebuilt = ResourceRebuild.Complete(graph, resourcesEntries, resourceAllocation);
        if (isPage)
        {
            if (page is null)
            {
                return;
            }

            PdfDictionary replacement = ResourceRebuild.ReplaceResources(graph, page, rebuilt);
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            copier.ValidateReplacement(nodeId.Value);
            graph.Retain();
            copier.ReplaceCopiedObject(nodeId.Value, replacement);
            return;
        }

        QpdfObject? current = null;
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (copier.CopiedObjects.TryGetValue(nodeId.Value, out QpdfObject? stored))
        {
            current = stored;
        }

        if (current is PdfStream stream)
        {
            PdfStream replacement = ResourceRebuild.ReplaceResources(graph, stream, rebuilt);
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            copier.ValidateReplacement(nodeId.Value);
            graph.Retain();
            copier.ReplaceCopiedObject(nodeId.Value, replacement);
        }
    }
    private static JoinedContent JoinContentBytes(IReadOnlyList<byte[]> parts, ResourceGovernor governor, CancellationToken token)
        => JoinedContent.Create(parts, governor, token);

    private static AnalysisChunks? CollectPageChunks(
        GraphCopier copier,
        PdfDictionary page,
        PoolResolver resolver,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contents) || contents is PdfNull)
        {
            return AnalysisChunks.Create(governor, cancellationToken);
        }

        using CheckStreamList streams = CheckStreamList.Create(governor);
        if (contents is PdfArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.CheckCancellation();
                QpdfObject item = array[index];
                QpdfObject target = item is PdfReference reference ? ResolvePool(resolver, reference, governor, cancellationToken) ?? item : item;
                if (target is not PdfStream stream)
                {
                    return null;
                }

                streams.Add(stream, cancellationToken);
            }
        }
        else if (contents is PdfReference singleReference)
        {
            QpdfObject? target = ResolvePool(resolver, singleReference, governor, cancellationToken);
            if (target is not PdfStream stream)
            {
                return null;
            }

            streams.Add(stream, cancellationToken);
        }
        else if (contents is PdfStream single)
        {
            streams.Add(single, cancellationToken);
        }
        else
        {
            return AnalysisChunks.Create(governor, cancellationToken);
        }

        AnalysisChunks chunks = AnalysisChunks.Create(governor, cancellationToken);
        try
        {
            for (int index = 0; index < streams.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                PdfStream stream = streams.Get(index, cancellationToken);
                if (!chunks.TryAdd(stream, resolver, limits))
                {
                    chunks.Dispose();
                    return null;
                }
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

    private static bool ScanNames(
        ReadOnlyMemory<byte> content,
        ResourceNameSet names,
        ResourceNameSet scoped,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope tokenizerMemory = ParserMemoryScope.Create(governor);
        tokenizerMemory.Reserve(192L);
        ContentTokenizer tokenizer = new(content, limits, governor, cancellationToken);
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
                        tokenizer.TryReadInlineImageData(out ContentToken _);
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
                            scoped.AddRaw(lastName.Span);
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
