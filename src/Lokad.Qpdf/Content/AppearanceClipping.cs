using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class AppearanceClipping
{
    private static readonly PdfName Original = PdfName.FromAscii("Original");
    private static readonly PdfName XObject = PdfName.FromAscii("XObject");
    private static readonly PdfName Type = PdfName.FromAscii("Type");
    private static readonly PdfName Subtype = PdfName.FromAscii("Subtype");
    private static readonly PdfName Form = PdfName.FromAscii("Form");
    private static readonly PdfName BBox = PdfName.FromAscii("BBox");
    private static readonly PdfName Resources = PdfName.FromAscii("Resources");

    internal static void Apply(GraphCopier copier, int annotationId, ReadOnlySpan<double> sourceClip,
        int rotation, QpdfLimits limits, OwnedAllocationBatch graph, ref int allocator)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (!copier.CopiedObjects.TryGetValue(annotationId, out QpdfObject? raw) || raw is not PdfDictionary annotation) { return; }
        PoolResolver resolver = new(copier.CopiedObjects);
        // Popup windows are viewer UI rather than artwork in the page frame.
        if (PdfDictionaryLookup.HasName(annotation, "Subtype"u8, "Popup"u8, resolver, governor, token)) { return; }
        long flags = PdfDictionaryLookup.TryGet(annotation, "F"u8, governor, token, out QpdfObject flagsRaw)
            && CatalogCopy.ResolveValue(flagsRaw, resolver, governor, token) is PdfInteger integer ? integer.Value : 0;
        if ((flags & 2L) != 0) { return; }
        if (!PdfDictionaryLookup.TryGet(annotation, "Rect"u8, governor, token, out QpdfObject rectangle)) { return; }
        Span<double> rect = stackalloc double[4];
        if (!Overlays.TryReadRectangle(rectangle, resolver, rect)) { return; }
        if (rect[0] > rect[2]) { (rect[0], rect[2]) = (rect[2], rect[0]); }
        if (rect[1] > rect[3]) { (rect[1], rect[3]) = (rect[3], rect[1]); }
        if (rect[0] == rect[2] || rect[1] == rect[3]) { return; }
        Span<double> clip = stackalloc double[4]; sourceClip.CopyTo(clip);
        if (rotation != 0 && (flags & 16L) != 0)
        {
            double left = rect[0], top = rect[3];
            (double a, double b, double c, double d) = rotation switch
            { 90 => (0, -1, 1, 0), 180 => (-1, 0, 0, -1), 270 => (0, 1, -1, 0), _ => (1, 0, 0, 1) };
            double x0 = a * clip[0] + c * clip[1] + left - a * left - c * top;
            double y0 = b * clip[0] + d * clip[1] + top - b * left - d * top;
            double x1 = a * clip[2] + c * clip[3] + left - a * left - c * top;
            double y1 = b * clip[2] + d * clip[3] + top - b * left - d * top;
            clip[0] = Math.Min(x0, x1); clip[1] = Math.Min(y0, y1); clip[2] = Math.Max(x0, x1); clip[3] = Math.Max(y0, y1);
        }
        if (rect[0] >= clip[0] && rect[1] >= clip[1] && rect[2] <= clip[2] && rect[3] <= clip[3]) { return; }
        if (FlattenAnnotations.FindAppearanceStream(annotation, resolver, governor, token, out _) is null
            || !PdfDictionaryLookup.TryGet(annotation, "AP"u8, governor, token, out QpdfObject appearance))
        {
            if (HasNoStaticArtwork(annotation, resolver, governor, token)) { return; }
            throw new QpdfUnsupportedFeatureException("Source clipping requires a usable normal annotation appearance.");
        }
        clip[0] = Math.Max(rect[0], clip[0]); clip[1] = Math.Max(rect[1], clip[1]);
        clip[2] = Math.Min(rect[2], clip[2]); clip[3] = Math.Min(rect[3], clip[3]);
        if (clip[0] >= clip[2] || clip[1] >= clip[3]) { clip.Clear(); }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        Dictionary<(int Number, bool States), PdfReference> memo = ParserContainers.CreateDictionary<(int, bool), PdfReference>(scratch);
        QpdfObject replacement = CopyDictionary(copier, annotation, rectangle, clip, appearance, false,
            resolver, limits, graph, scratch, memo, ref allocator);
        if (Equals(appearance, replacement)) { return; }
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, annotation, "AP"u8, replacement);
        copier.ValidateReplacement(annotationId); copier.ReplaceCopiedObject(annotationId, result);
    }

    private static bool HasNoStaticArtwork(PdfDictionary annotation, PoolResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (PdfDictionaryLookup.TryGet(annotation, "AP"u8, governor, token, out QpdfObject ap)
            && CatalogCopy.ResolveValue(ap, resolver, governor, token) is not (null or PdfNull)) { return false; }
        if (PdfDictionaryLookup.HasName(annotation, "Subtype"u8, "Screen"u8, resolver, governor, token)) { return true; }
        if (!PdfDictionaryLookup.HasName(annotation, "Subtype"u8, "Link"u8, resolver, governor, token)) { return false; }
        QpdfObject? style = PdfDictionaryLookup.TryGet(annotation, "BS"u8, governor, token, out QpdfObject raw)
            ? CatalogCopy.ResolveValue(raw, resolver, governor, token) : null;
        QpdfObject? width;
        if (style is PdfDictionary dictionary)
        {
            width = PdfDictionaryLookup.TryGet(dictionary, "W"u8, governor, token, out raw) ? raw : null;
        }
        else
        {
            if (style is not (null or PdfNull)) { return false; }
            if (!PdfDictionaryLookup.TryGet(annotation, "Border"u8, governor, token, out raw)
                || CatalogCopy.ResolveValue(raw, resolver, governor, token) is not PdfArray { Count: 3 or 4 } border) { return false; }
            width = border[2];
        }
        return CatalogCopy.ResolveValue(width, resolver, governor, token) is PdfInteger { Value: 0 } or PdfReal { Value: 0 };
    }

    private static QpdfObject CopyDictionary(GraphCopier copier, PdfDictionary annotation, QpdfObject rectangle,
        ReadOnlySpan<double> clip, QpdfObject raw, bool states, PoolResolver resolver, QpdfLimits limits,
        OwnedAllocationBatch graph, ParserMemoryScope scratch,
        Dictionary<(int Number, bool States), PdfReference> memo, ref int allocator)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        if (CatalogCopy.ResolveValue(raw, resolver, governor, token) is not PdfDictionary dictionary) { return raw; }
        PdfReference? reference = raw as PdfReference;
        if (reference is not null && memo.TryGetValue((reference.ObjectNumber, states), out PdfReference? prior)) { return prior; }
        PdfNameTable<QpdfObject>? entries = null; long allocation = 0;
        using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
        while (source.MoveNext(governor, token))
        {
            PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
            ReadOnlySpan<byte> slot = key.RawSpan;
            if (!states && !slot.SequenceEqual("N"u8) && !slot.SequenceEqual("R"u8) && !slot.SequenceEqual("D"u8)) { continue; }
            QpdfObject? target = CatalogCopy.ResolveValue(value, resolver, governor, token);
            QpdfObject changed = value;
            if (target is PdfStream stream)
            {
                if (value is PdfReference streamReference && memo.TryGetValue((streamReference.ObjectNumber, false), out PdfReference? cached)) { changed = cached; }
                else
                {
                    changed = Wrap(copier, annotation, rectangle, clip, stream, value as PdfReference, resolver, limits, graph, ref allocator);
                    if (value is PdfReference original)
                    {
                        ParserContainers.EnsureEntryCapacity(scratch, memo, sizeof(int) * 2L + IntPtr.Size);
                        memo.Add((original.ObjectNumber, false), (PdfReference)changed);
                    }
                }
            }
            else if (!states && target is PdfDictionary)
            { changed = CopyDictionary(copier, annotation, rectangle, clip, value, true, resolver, limits, graph, scratch, memo, ref allocator); }
            else if (target is not (null or PdfNull))
            { throw new QpdfUnsupportedFeatureException("Source clipping requires stream-backed annotation appearance states."); }
            if (Equals(value, changed)) { continue; }
            if (entries is null)
            {
                entries = PdfDictionaryRebuild.CreateEntries(graph, out allocation);
                using PdfDictionary.EntryEnumerator original = dictionary.GetEntryEnumerator();
                while (original.MoveNext(governor, token)) { entries.Assign(graph.Memory, governor, original.Current.Key, original.Current.Value, token); }
            }
            entries.Assign(graph.Memory, governor, key, changed, token);
        }
        QpdfObject result = raw;
        if (entries is not null)
        {
            PdfDictionary rebuilt = PdfDictionaryRebuild.Complete(graph, entries, allocation);
            if (reference is null) { return rebuilt; }
            result = copier.AddIndirectObjectWithId(rebuilt, allocator); allocator = checked(allocator + 1);
        }
        if (reference is not null)
        {
            ParserContainers.EnsureEntryCapacity(scratch, memo, sizeof(int) * 2L + IntPtr.Size);
            memo.Add((reference.ObjectNumber, states), (PdfReference)result);
        }
        return result;
    }

    private static PdfReference Wrap(GraphCopier copier, PdfDictionary annotation, QpdfObject rectangle,
        ReadOnlySpan<double> clip, PdfStream stream, PdfReference? reference, PoolResolver resolver,
        QpdfLimits limits, OwnedAllocationBatch graph, ref int allocator)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        using ParserMemoryScope fitMemory = ParserMemoryScope.Create(governor);
        fitMemory.Reserve(2048L); // One live set of shared fit matrices and formatted scalars.
        if (PdfDictionaryLookup.TryGet(stream.Dictionary, "Resources"u8, governor, token, out QpdfObject resourcesRaw)
            && CatalogCopy.ResolveValue(resourcesRaw, resolver, governor, token) is not (null or PdfNull or PdfDictionary))
        { throw new QpdfUnsupportedFeatureException("Source clipping cannot preserve unusable annotation appearance resources."); }
        if (PdfDictionaryLookup.TryGet(stream.Dictionary, "Matrix"u8, governor, token, out QpdfObject matrix)
            && CatalogCopy.ResolveValue(matrix, resolver, governor, token) is QpdfObject resolved && resolved is not PdfNull
            && (resolved is not PdfArray array || Overlays.ReadOverlayMatrix(array, resolver) is null))
        { throw new QpdfUnsupportedFeatureException("Source clipping cannot preserve an unusable annotation appearance matrix."); }
        using GovernedBufferStream output = new(governor, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), token, 256);
        output.Write("q\n"u8, token);
        for (int index = 0; index < 4; index++)
        {
            if (index != 0) { output.WriteByte((byte)' '); }
            WriterNumber.AppendGeometry(output, index < 2 ? clip[index] : clip[index] - clip[index - 2]);
        }
        output.Write(" re W n\n"u8, token);
        FlattenAnnotations.AppearanceFit fit = FlattenAnnotations.AppendAppearance("Original", annotation, stream.Dictionary, 0, 0, 0, resolver, output);
        if (fit == FlattenAnnotations.AppearanceFit.Invalid)
        { throw new QpdfUnsupportedFeatureException("Source clipping cannot preserve an unusable annotation appearance frame."); }
        output.Write("Q\n"u8, token);
        PdfReference original = FlattenAnnotations.EnsureAppearanceObject(copier, stream, reference, resolver, governor, token, ref allocator);
        PdfDictionary resources = Dictionary(graph, [(XObject, Dictionary(graph, [(Original, original)]))]);
        PdfDictionary header = Dictionary(graph, [(Type, XObject), (Subtype, Form), (BBox, rectangle), (Resources, resources)]);
        byte[] data = output.ToArray(graph.Memory, token); graph.Add(data.LongLength + GovernedBufferStream.ArrayOverheadBytes);
        graph.Memory.Reserve(32L); PdfStream wrapper = PdfStream.FromOwnedData(header, data); graph.Add(32L);
        PdfReference result = copier.AddIndirectObjectWithId(wrapper, allocator); allocator = checked(allocator + 1); return result;
    }

    private static PdfDictionary Dictionary(OwnedAllocationBatch graph, ReadOnlySpan<(PdfName Key, QpdfObject Value)> values)
    {
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        foreach (var entry in values) { entries.Assign(graph.Memory, graph.Governor, entry.Key, entry.Value, graph.Token); }
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }
}
