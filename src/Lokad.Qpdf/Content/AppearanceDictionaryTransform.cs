using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Rebuilds only changed appearance roles and keeps other dictionary edges intact.</summary>
internal static class AppearanceDictionaryTransform
{
    internal interface IStreamTransform
    {
        PdfReference? Transform(PdfStream stream, PdfReference? source, ref int allocator);
        void ValidateNonStream(QpdfObject? value);
    }

    internal static QpdfObject Apply<TTransform>(GraphCopier copier, PoolResolver resolver, QpdfObject appearance,
        bool frame, Dictionary<(int Id, AnnotationCopyKind Kind, bool Frame), PdfReference> copies,
        ParserMemoryScope traversal, OwnedAllocationBatch graph, scoped TTransform transform, ref int allocator)
        where TTransform : IStreamTransform, allows ref struct
        => Rewrite(copier, resolver, appearance, false, frame, copies, traversal, graph, transform, ref allocator);

    private static QpdfObject Rewrite<TTransform>(GraphCopier copier, PoolResolver resolver, QpdfObject raw, bool states,
        bool frame, Dictionary<(int Id, AnnotationCopyKind Kind, bool Frame), PdfReference> copies,
        ParserMemoryScope traversal, OwnedAllocationBatch graph, scoped TTransform transform, ref int allocator)
        where TTransform : IStreamTransform, allows ref struct
    {
        ResourceGovernor governor = graph.Governor;
        CancellationToken token = graph.Token;
        if (CatalogCopy.ResolveValue(raw, resolver, governor, token) is not PdfDictionary dictionary) { return raw; }

        PdfReference? reference = raw as PdfReference;
        AnnotationCopyKind kind = states ? AnnotationCopyKind.AppearanceStates : AnnotationCopyKind.Appearance;
        if (reference is not null && copies.TryGetValue((reference.ObjectNumber, kind, frame), out PdfReference? cached))
        { return cached; }

        PdfNameTable<QpdfObject>? entries = null;
        long allocation = 0L;
        using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
        while (source.MoveNext(governor, token))
        {
            (PdfName key, QpdfObject value) = source.Current;
            ReadOnlySpan<byte> slot = key.RawSpan;
            if (!states && !slot.SequenceEqual("N"u8) && !slot.SequenceEqual("R"u8) && !slot.SequenceEqual("D"u8)) { continue; }
            QpdfObject? target = CatalogCopy.ResolveValue(value, resolver, governor, token);
            QpdfObject replacement = value;
            if (target is PdfStream stream)
            {
                PdfReference? original = value as PdfReference;
                if (original is null)
                { replacement = transform.Transform(stream, null, ref allocator) ?? value; }
                else if (copies.TryGetValue((original.ObjectNumber, AnnotationCopyKind.Appearance, frame), out PdfReference? placed))
                { replacement = placed; }
                else
                {
                    placed = transform.Transform(stream, original, ref allocator) ?? original;
                    ParserContainers.EnsureEntryCapacity(traversal, copies, 2L * sizeof(int) + IntPtr.Size);
                    copies.Add((original.ObjectNumber, AnnotationCopyKind.Appearance, frame), placed);
                    replacement = placed;
                }
            }
            else if (!states && target is PdfDictionary)
            {
                replacement = Rewrite(copier, resolver, value, true, frame, copies, traversal, graph, transform, ref allocator);
            }
            else { transform.ValidateNonStream(target); }

            if (Equals(value, replacement)) { continue; }
            if (entries is null)
            {
                entries = PdfDictionaryRebuild.CreateEntries(graph, out allocation);
                using PdfDictionary.EntryEnumerator original = dictionary.GetEntryEnumerator();
                while (original.MoveNext(governor, token))
                { entries.Assign(graph.Memory, governor, original.Current.Key, original.Current.Value, token); }
            }
            entries.Assign(graph.Memory, governor, key, replacement, token);
        }

        if (reference is null)
        { return entries is null ? raw : PdfDictionaryRebuild.Complete(graph, entries, allocation); }

        PdfReference result = reference;
        if (entries is not null)
        {
            PdfDictionary rebuilt = PdfDictionaryRebuild.Complete(graph, entries, allocation);
            result = copier.AddIndirectObjectWithId(rebuilt, allocator);
            allocator = checked(allocator + 1);
        }
        ParserContainers.EnsureEntryCapacity(traversal, copies, 2L * sizeof(int) + IntPtr.Size);
        copies.Add((reference.ObjectNumber, kind, frame), result);
        return result;
    }
}
