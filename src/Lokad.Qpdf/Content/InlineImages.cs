using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>
/// Inline image externalization: images at or above a byte threshold become
/// regular XObjects referenced by generated Do operators. Mirrors the
/// upstream tracker including abbreviation expansion, colorspace lookup,
/// unique naming, and warn-and-keep failure policy.
/// </summary>
internal static class InlineImages
{
    internal static PdfDictionary ExternalizePage(
        GraphCopier copier,
        int pageId,
        PdfDictionary page,
        long minBytes,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using AnalysisChunks? chunks = CollectDecodedContents(copier, page, limits, governor, cancellationToken);
        if (chunks is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Unable to filter content stream; not attempting to externalize inline images.");
            return page;
        }

        using JoinedContent joined = JoinedContent.Create(chunks.Parts, governor, cancellationToken);
        chunks.Dispose();
        RewriteResult rewritten = RewriteContent(copier, page, joined.RawBytes, minBytes, compress, level, limits, governor, diagnostics, cancellationToken);
        if (!rewritten.Changed || rewritten.Stream is null || rewritten.Resources is null)
        {
            return page;
        }

        PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contentsRaw);
        using ParserMemoryScope orphanMemory = ParserMemoryScope.Create(governor);
        List<int> orphaned = ContentTransforms.ReplacedContentIds(contentsRaw, orphanMemory, governor, cancellationToken);
        PdfReference link = copier.AddIndirectObjectWithId(rewritten.Stream, copier.NextId);
        PdfDictionary replaced = ReplaceEntry(page, "Contents"u8, link, governor, cancellationToken);
        replaced = ReplaceEntry(replaced, "Resources"u8, rewritten.Resources, governor, cancellationToken);

        copier.ReplaceCopiedObject(pageId, replaced);
        if (orphaned is { Count: > 0 })
        {
            copier.DropPoolObjectsIfUnreferenced(orphaned, cancellationToken);
        }

        return replaced;
    }

    internal static void ExternalizeForm(
        GraphCopier copier,
        int formId,
        PdfStream form,
        long minBytes,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, cancellationToken);
        if (!chunks.TryAddForInlineImages(form, resolver, limits))
        {
            return;
        }

        RewriteResult rewritten = RewriteContent(copier, form.Dictionary, chunks.Parts[0], minBytes, compress, level, limits, governor, diagnostics, cancellationToken);
        chunks.Dispose();
        resolverMemory.Dispose();
        if (!rewritten.Changed || rewritten.Stream is null || rewritten.Resources is null)
        {
            return;
        }

        using OwnedAllocationBatch headers = OwnedAllocationBatch.Create(governor, 10, cancellationToken);
        PdfDictionary unencoded = PdfDictionaryRebuild.StripStreamEncoding(headers, form.Dictionary);
        PdfDictionary formDict = ResourceRebuild.ReplaceResources(headers, unencoded, rewritten.Resources);
        if (PdfDictionaryLookup.TryGet(rewritten.Stream.Dictionary, "Filter"u8, governor, cancellationToken, out QpdfObject filter))
        {
            formDict = PdfDictionaryRebuild.ReplaceEntry(headers, formDict, "Filter"u8, filter);
        }
        headers.Retain();
        using OwnedAllocationBatch final = OwnedAllocationBatch.Create(governor, 1, cancellationToken);
        final.Memory.Reserve(32L);
        PdfStream replaced = rewritten.Stream.WithDictionary(formDict);
        final.Add(replaced, 32L); final.Retain(); copier.ReplaceCopiedObject(formId, replaced);
    }

    private readonly record struct RewriteResult(bool Changed, PdfStream? Stream, PdfDictionary? Resources);

    private static AnalysisChunks? CollectDecodedContents(
        GraphCopier copier,
        PdfDictionary page,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contents) ||
            contents is PdfNull || contents is not (PdfArray or PdfReference or PdfStream))
        {
            return AnalysisChunks.Create(governor, cancellationToken);
        }

        using ContentEntries streams = ContentEntries.Create(governor, cancellationToken);
        PdfArray? array = contents as PdfArray;
        int count = array?.Count ?? 1;
        // Validate every entry before decoding any stream, preserving failure order.
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
            QpdfObject? item = array is null ? contents : array[index];
            int? objectId = null;
            if (item is PdfReference reference)
            {
                objectId = reference.ObjectNumber;
                PdfObjectLookup.TryResolve(reference, copier.CopiedObjects, governor, cancellationToken, out item);
            }
            if (item is not PdfStream stream) { return null; }
            streams.Add(new(objectId, index, stream));
        }
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        AnalysisChunks chunks = AnalysisChunks.Create(governor, cancellationToken);
        try
        {
            for (int index = 0; index < streams.Count; index++)
            {
                if (!chunks.TryAddForInlineImages(streams.Get(index).Stream, resolver, limits))
                { chunks.Dispose(); return null; }
            }
            return chunks;
        }
        catch { chunks.Dispose(); throw; }
    }

    private static PdfDictionary ReplaceEntry(PdfDictionary dictionary, ReadOnlySpan<byte> key, QpdfObject value,
        ResourceGovernor governor, CancellationToken token)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 4, token);
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, dictionary, key, value); graph.Retain(); return result;
    }

    private static RewriteResult RewriteContent(
        GraphCopier copier,
        PdfDictionary? page,
        byte[] joined,
        long minBytes,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(scratch, copier.CopiedObjects);
        PdfDictionary resources = EnsureResources(copier, page, governor, cancellationToken);
        scratch.Reserve(160L);
        ContentTokenizer tokenizer = new(joined, limits, governor, cancellationToken) { IncludeComments = true, IncludeWhitespace = true };
        using InlineImageBuffer output = InlineImageBuffer.Create(governor, limits, cancellationToken);
        bool inImage = false; long imageStart = 0, dictStart = 0; int suffix = 1;
        try
        {
            while (tokenizer.TryReadToken(out ContentToken token))
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
                if (!inImage)
                {
                    if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("BI"u8))
                    { inImage = true; imageStart = token.Offset; dictStart = checked(token.Offset + token.Data.Length); continue; }
                    output.Write(token.Data.Span); continue;
                }
                if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("ID"u8))
                {
                    if (!tokenizer.TryReadInlineImageData(out ContentToken image)) { inImage = false; continue; }
                    int dictionaryLength = checked((int)(token.Offset - dictStart));
                    InlineImageEntry? converted = InlineImageStorage.Create(joined.AsMemory(checked((int)dictStart), dictionaryLength), image.Data,
                        minBytes, token.Offset, resources, ref suffix, limits, governor, diagnostics, resolver, cancellationToken);
                    if (converted is InlineImageEntry entry) { output.Add(entry); output.WriteImage(entry.Name); }
                    else { output.Write(joined.AsSpan(checked((int)imageStart), checked((int)(tokenizer.Position - imageStart)))); }
                    inImage = false; continue;
                }
                if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("EI"u8)) { inImage = false; }
            }
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Unable to filter content stream; not attempting to externalize inline images.");
            return default;
        }
        if (output.Count == 0) { return default; }
        resources = AddXObjects(copier, resources, output, governor, cancellationToken);
        PdfStream stream = output.Complete(compress, level, limits);
        return new(true, stream, resources);
    }

    private static PdfDictionary EnsureResources(GraphCopier copier, PdfDictionary? page, ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary? existing = null;
        if (page is not null && PdfDictionaryLookup.TryGet(page, "Resources"u8, governor, token, out QpdfObject raw))
        {
            QpdfObject? target = raw;
            if (raw is PdfReference reference) { PdfObjectLookup.TryResolve(reference, copier.CopiedObjects, governor, token, out target); }
            existing = target as PdfDictionary;
        }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 6, token);
        PdfDictionary? resolvedXObjects = null;
        if (existing is not null && PdfDictionaryLookup.TryGet(existing, "XObject"u8, governor, token, out QpdfObject rawXObjects))
        {
            QpdfObject? target = rawXObjects;
            if (rawXObjects is PdfReference reference)
            { PdfObjectLookup.TryResolve(reference, copier.CopiedObjects, governor, token, out target); }
            resolvedXObjects = target as PdfDictionary;
        }
        if (resolvedXObjects is null)
        {
            PdfNameTable<QpdfObject> blank = PdfDictionaryRebuild.CreateEntries(graph, out int blankAllocation);
            resolvedXObjects = PdfDictionaryRebuild.Complete(graph, blank, blankAllocation);
        }
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out int allocation);
        bool hasKey = false;
        if (existing is not null)
        {
            using PdfDictionary.EntryEnumerator source = existing.GetEntryEnumerator();
            while (source.MoveNext(governor, token))
            {
                PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
                if (value is null) { continue; }
                if (PdfDictionaryLookup.NameEquals(key, "XObject"u8, governor, token)) { hasKey = true; value = resolvedXObjects; }
                entries.Assign(graph.Memory, governor, key, value, token);
            }
        }
        if (!hasKey) { entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "XObject"u8), resolvedXObjects, token); }
        PdfDictionary result = PdfDictionaryRebuild.Complete(graph, entries, allocation); graph.Retain(); return result;
    }

    private static PdfDictionary AddXObjects(GraphCopier copier, PdfDictionary resources, InlineImageBuffer output, ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(resources, "XObject"u8, governor, token, out QpdfObject raw) || raw is not PdfDictionary xobjects) { return resources; }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 4, token);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out int allocation);
        using PdfDictionary.EntryEnumerator source = xobjects.GetEntryEnumerator();
        while (source.MoveNext(governor, token))
        { if (source.Current.Value is QpdfObject value) { entries.Assign(graph.Memory, governor, source.Current.Key, value, token); } }
        for (int index = 0; index < output.Count; index++)
        {
            InlineImageEntry image = output.Get(index);
            // Parsing rejects source references. Resource values already belong
            // to this pool and must retain their destination identities.
            PdfReference link = copier.AddIndirectObjectWithId(image.Stream, copier.NextId);
            entries.Assign(graph.Memory, governor, image.Name, link, token);
        }
        PdfDictionary images = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, resources, "XObject"u8, images); graph.Retain(); return result;
    }

    private static PdfDictionary ConvertImageDict(
        PdfDictionary raw,
        PdfDictionary resources,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
        => InlineImageDictionary.Convert(raw, resources, governor, diagnostics, cancellationToken);
}