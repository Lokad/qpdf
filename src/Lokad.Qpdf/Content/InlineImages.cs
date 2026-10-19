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
        => ExternalizePage(copier, pageId, page, minBytes, compress, level, limits, governor, diagnostics, cancellationToken, false);

    internal static PdfDictionary ExternalizePage(
        GraphCopier copier, int pageId, PdfDictionary page, long minBytes, bool compress, int? level,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        CancellationToken cancellationToken, bool preserveUnreferenced)
        => ExternalizePage(copier, pageId, page, minBytes, compress, level, limits,
            governor, diagnostics, cancellationToken, preserveUnreferenced, 0);

    internal static PdfDictionary ExternalizePage(
        GraphCopier copier, int pageId, PdfDictionary page, long minBytes, bool compress, int? level,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        CancellationToken cancellationToken, bool preserveUnreferenced, byte filterOptions)
    {
        using AnalysisChunks? chunks = CollectDecodedContents(copier, page, limits, governor, cancellationToken);
        if (chunks is null)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Unable to filter content stream; not attempting to externalize inline images.");
            return page;
        }

        using JoinedContent joined = JoinedContent.Create(chunks.Parts, governor, cancellationToken);
        chunks.Dispose();
        RewriteResult rewritten = RewriteContentCore(copier, page, joined.RawBytes, minBytes, compress, level, limits, governor, diagnostics, cancellationToken, false, preserveUnreferenced, filterOptions);
        CompleteResourceWalk(page, rewritten.Resources, pageId, governor, diagnostics, cancellationToken);
        PdfDictionary replaced = page;
        if (rewritten.Resources is QpdfObject resources)
        { replaced = ReplaceEntry(replaced, "Resources"u8, resources, governor, cancellationToken); }
        if (!rewritten.Changed || rewritten.Stream is null)
        {
            if (!ReferenceEquals(page, replaced)) { copier.ReplaceCopiedObject(pageId, replaced); }
            return replaced;
        }

        PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, cancellationToken, out QpdfObject contentsRaw);
        using ParserMemoryScope orphanMemory = ParserMemoryScope.Create(governor);
        List<int> orphaned = ContentTransforms.ReplacedContentIds(contentsRaw, orphanMemory, governor, cancellationToken);
        PdfReference link = copier.AddIndirectObjectWithId(rewritten.Stream, copier.NextId);
        replaced = ReplaceEntry(replaced, "Contents"u8, link, governor, cancellationToken);

        copier.ReplaceCopiedObject(pageId, replaced);
        if (!preserveUnreferenced && orphaned is { Count: > 0 })
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
        => ExternalizeForm(copier, formId, form, minBytes, compress, level, limits, governor, diagnostics, cancellationToken, false);

    internal static void ExternalizeForm(
        GraphCopier copier, int formId, PdfStream form, long minBytes, bool compress, int? level,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        CancellationToken cancellationToken, bool preserveUnreferenced)
        => ExternalizeForm(copier, formId, form, minBytes, compress, level, limits,
            governor, diagnostics, cancellationToken, preserveUnreferenced, 0);

    internal static void ExternalizeForm(
        GraphCopier copier, int formId, PdfStream form, long minBytes, bool compress, int? level,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        CancellationToken cancellationToken, bool preserveUnreferenced, byte filterOptions)
    {
        using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(resolverMemory, copier.CopiedObjects);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, cancellationToken);
        if (!chunks.TryAddForInlineImages(form, resolver, limits))
        {
            return;
        }

        RewriteResult rewritten = RewriteContentCore(copier, form.Dictionary, chunks.Parts[0], minBytes, compress, level, limits, governor, diagnostics, cancellationToken, true, preserveUnreferenced, filterOptions);
        chunks.Dispose();
        resolverMemory.Dispose();
        CompleteResourceWalk(form.Dictionary, rewritten.Resources, formId, governor, diagnostics, cancellationToken);
        using OwnedAllocationBatch headers = OwnedAllocationBatch.Create(governor, 10, cancellationToken);
        PdfDictionary formDict = rewritten.Changed && rewritten.Stream is not null
            ? PdfDictionaryRebuild.StripStreamEncoding(headers, form.Dictionary) : form.Dictionary;
        if (rewritten.Resources is PdfNull)
        { formDict = PdfDictionaryRebuild.RemoveEntry(headers, formDict, "Resources"u8); }
        else if (rewritten.Resources is QpdfObject resources)
        { formDict = PdfDictionaryRebuild.ReplaceEntry(headers, formDict, "Resources"u8, resources); }
        if (rewritten.Changed && rewritten.Stream is not null && PdfDictionaryLookup.TryGet(rewritten.Stream.Dictionary, "Filter"u8, governor, cancellationToken, out QpdfObject filter))
        {
            formDict = PdfDictionaryRebuild.ReplaceEntry(headers, formDict, "Filter"u8, filter);
        }
        if (ReferenceEquals(formDict, form.Dictionary)) { return; }
        headers.Retain();
        using OwnedAllocationBatch final = OwnedAllocationBatch.Create(governor, 1, cancellationToken);
        final.Memory.Reserve(32L);
        PdfStream replaced = (rewritten.Stream ?? form).WithDictionary(formDict);
        final.Add(32L); final.Retain(); copier.ReplaceCopiedObject(formId, replaced);
    }

    private readonly record struct RewriteResult(bool Changed, PdfStream? Stream, QpdfObject? Resources);

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
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
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
        => RewriteContentCore(copier, page, joined, minBytes, compress, level, limits, governor, diagnostics, cancellationToken, false, false, 0);

    private static RewriteResult RewriteContentCore(
        GraphCopier copier, PdfDictionary? page, byte[] joined, long minBytes, bool compress, int? level,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken,
        bool form, bool preserveUnreferenced, byte filterOptions)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(scratch, copier.CopiedObjects);
        QpdfObject? resources = EnsureResources(copier, page, governor, cancellationToken);
        bool canAttach = resources is PdfDictionary prepared &&
            PdfDictionaryLookup.TryGet(prepared, "XObject"u8, governor, cancellationToken, out QpdfObject objects) && objects is PdfDictionary;
        scratch.Reserve(160L);
        ContentTokenizer tokenizer = new(joined, limits, governor, cancellationToken) { IncludeComments = true, IncludeWhitespace = true };
        using InlineImageBuffer output = InlineImageBuffer.Create(governor, limits, cancellationToken);
        bool inImage = false; long imageStart = 0, dictStart = 0; int suffix = 1;
        try
        {
            while (tokenizer.TryReadToken(out ContentToken token))
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (!inImage)
                {
                    if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("BI"u8))
                    { inImage = true; imageStart = token.Offset; dictStart = checked(token.Offset + token.Data.Length); continue; }
                    if (token.Kind == ContentTokenKind.Name) { output.Write("/"u8); }
                    output.Write(token.Data.Span); continue;
                }
                if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("ID"u8))
                {
                    if (!tokenizer.TryReadInlineImageData(out ContentToken image)) { inImage = false; continue; }
                    int dictionaryLength = checked((int)(token.Offset - dictStart));
                    InlineImageEntry? converted = InlineImageStorage.CreateWithResources(joined.AsMemory(checked((int)dictStart), dictionaryLength), image.Data,
                        minBytes, token.Offset, resources, ref suffix, limits, governor, diagnostics, resolver, cancellationToken);
                    if (converted is InlineImageEntry entry)
                    {
                        PdfStream candidate = StreamTransformer.Transform(entry.Stream, copier.NextId, resolver, compress,
                            StreamTransformer.DecodeLevel(filterOptions), (filterOptions & 4) != 0, level,
                            limits, governor, diagnostics, cancellationToken) ?? entry.Stream;
                        output.Add(new(entry.Name, candidate));
                        if (!canAttach)
                        {
                            suffix = checked(suffix - 1);
                            if (resources is not PdfDictionary) { WarnDictionaryOperation(resources, false, diagnostics); }
                            if (resources is PdfNull)
                            { return new(false, null, AddXObjects(copier, resources, output, preserveUnreferenced, governor, cancellationToken)); }
                            QpdfObject? target = resources is PdfDictionary resourceDictionary &&
                                PdfDictionaryLookup.TryGet(resourceDictionary, "XObject"u8, governor, cancellationToken, out QpdfObject value) ? value : null;
                            if (target is PdfReference reference)
                            { PdfObjectLookup.TryResolve(reference, copier.CopiedObjects, governor, cancellationToken, out target); }
                            WarnDictionaryOperation(target, true, diagnostics);
                        }
                        output.WriteImage(entry.Name);
                    }
                    else { output.Write(joined.AsSpan(checked((int)imageStart), checked((int)(tokenizer.Position - imageStart)))); }
                    inImage = false; continue;
                }
                if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual("EI"u8)) { inImage = false; }
            }
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            if (form && resources is not PdfNull) { throw; }
            if (resources is PdfNull) { return new(false, null, resources); }
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Unable to filter content stream; not attempting to externalize inline images.");
            resources = AddXObjects(copier, resources, output, preserveUnreferenced, governor, cancellationToken);
            return page is null && output.Count == 0 ? default : new(false, null, resources);
        }
        if (output.Count == 0) { return page is null ? default : new(false, null, resources); }
        resources = AddXObjects(copier, resources, output, preserveUnreferenced, governor, cancellationToken);
        PdfStream stream = output.Complete(compress, level, limits);
        return new(true, stream, resources);
    }

    private static QpdfObject? EnsureResources(GraphCopier copier, PdfDictionary? page, ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary? existing = null;
        if (page is not null)
        {
            if (!PdfDictionaryLookup.TryGet(page, "Resources"u8, governor, token, out QpdfObject raw)) { return null; }
            QpdfObject? target = raw;
            if (raw is PdfReference reference) { PdfObjectLookup.TryResolve(reference, copier.CopiedObjects, governor, token, out target); }
            existing = target as PdfDictionary;
            if (existing is null) { return target; }
        }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 6, token);
        PdfDictionary? resolvedXObjects = null;
        QpdfObject? existingXObjects = null;
        if (existing is not null && PdfDictionaryLookup.TryGet(existing, "XObject"u8, governor, token, out QpdfObject rawXObjects))
        {
            QpdfObject? target = rawXObjects;
            if (rawXObjects is PdfReference reference)
            { PdfObjectLookup.TryResolve(reference, copier.CopiedObjects, governor, token, out target); }
            resolvedXObjects = target as PdfDictionary;
            existingXObjects = target;
        }
        if (existingXObjects is not null and not (PdfDictionary or PdfNull)) { return existing; }
        if (resolvedXObjects is null)
        {
            PdfNameTable<QpdfObject> blank = PdfDictionaryRebuild.CreateEntries(graph, out long blankAllocation);
            resolvedXObjects = PdfDictionaryRebuild.Complete(graph, blank, blankAllocation);
        }
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
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

    private static QpdfObject? AddXObjects(GraphCopier copier, QpdfObject? resources, InlineImageBuffer output,
        bool preserveUnreferenced, ResourceGovernor governor, CancellationToken token)
    {
        if (output.Count == 0) { return resources; }
        if (resources is not PdfDictionary dictionary ||
            !PdfDictionaryLookup.TryGet(dictionary, "XObject"u8, governor, token, out QpdfObject raw) || raw is not PdfDictionary xobjects)
        {
            if (preserveUnreferenced)
            { for (int index = 0; index < output.Count; index++) { copier.AddIndirectObjectWithId(output.Get(index).Stream, copier.NextId); } }
            return resources;
        }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 4, token);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
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
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, dictionary, "XObject"u8, images); graph.Retain(); return result;
    }

    private static void CompleteResourceWalk(PdfDictionary original, QpdfObject? resources, int objectId,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        if (resources is null or PdfDictionary or PdfNull) { return; }
        if (PdfDictionaryLookup.TryGet(original, "Resources"u8, governor, token, out QpdfObject raw) && raw is PdfReference)
        { throw new QpdfParseException("Resources reference does not resolve to a dictionary.", null, objectId); }
        WarnDictionaryOperation(resources, false, diagnostics);
    }

    internal static void WarnDictionaryOperation(QpdfObject? value, bool replacement, DiagnosticBag diagnostics)
    {
        string message = value switch
        {
            null or PdfNull => replacement
                ? "operation for dictionary attempted on object of type null: ignoring key replacement request"
                : "operation for dictionary attempted on object of type null: returning null for attempted key retrieval",
            PdfInteger => replacement
                ? "operation for dictionary attempted on object of type integer: ignoring key replacement request"
                : "operation for dictionary attempted on object of type integer: returning null for attempted key retrieval",
            PdfReal => replacement
                ? "operation for dictionary attempted on object of type real: ignoring key replacement request"
                : "operation for dictionary attempted on object of type real: returning null for attempted key retrieval",
            PdfArray => replacement
                ? "operation for dictionary attempted on object of type array: ignoring key replacement request"
                : "operation for dictionary attempted on object of type array: returning null for attempted key retrieval",
            PdfBoolean => replacement
                ? "operation for dictionary attempted on object of type boolean: ignoring key replacement request"
                : "operation for dictionary attempted on object of type boolean: returning null for attempted key retrieval",
            PdfName => replacement
                ? "operation for dictionary attempted on object of type name: ignoring key replacement request"
                : "operation for dictionary attempted on object of type name: returning null for attempted key retrieval",
            PdfByteString => replacement
                ? "operation for dictionary attempted on object of type string: ignoring key replacement request"
                : "operation for dictionary attempted on object of type string: returning null for attempted key retrieval",
            PdfStream => replacement
                ? "operation for dictionary attempted on object of type stream: ignoring key replacement request"
                : "operation for dictionary attempted on object of type stream: returning null for attempted key retrieval",
            _ => replacement
                ? "operation for dictionary attempted on object of unknown type: ignoring key replacement request"
                : "operation for dictionary attempted on object of unknown type: returning null for attempted key retrieval"
        };
        diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, message);
    }

    private static PdfDictionary ConvertImageDict(
        PdfDictionary raw,
        PdfDictionary resources,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
        => InlineImageDictionary.Convert(raw, resources, governor, diagnostics, cancellationToken);
}
