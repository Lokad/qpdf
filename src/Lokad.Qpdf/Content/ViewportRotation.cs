using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class ViewportRotation
{
    internal static QpdfObject Project(QpdfObject raw, ReadOnlySpan<double> matrix, GraphCopier copier,
        PoolResolver resolver, OwnedAllocationBatch graph, ParserMemoryScope scratch, ref int allocator)
    {
        if (raw is PdfNull) { return raw; }
        ResourceGovernor governor = graph.Governor;
        CancellationToken token = graph.Token;
        if (CatalogCopy.ResolveValue(raw, resolver, governor, token) is not PdfArray source)
        { throw new QpdfUnsupportedFeatureException("Rotation baking requires a readable page viewport array."); }
        if (source.Count == 0) { return raw; }

        long bytes = checked(56L + IntPtr.Size * (long)source.Count);
        graph.Memory.Reserve(bytes);
        QpdfObject[] viewports = new QpdfObject[source.Count];
        graph.Add(bytes);
        Dictionary<PdfReference, PdfReference>? headers = null, measures = null;
        for (int index = 0; index < source.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            QpdfObject entry = source[index];
            if (entry is PdfReference reference && headers?.TryGetValue(reference, out PdfReference? projected) == true)
            { viewports[index] = projected; continue; }
            if (CatalogCopy.ResolveDictionary(entry, resolver, governor, token) is not PdfDictionary viewport)
            { throw new QpdfUnsupportedFeatureException("Rotation baking requires a readable viewport dictionary."); }
            PdfDictionary edited = ProjectViewport(viewport, matrix, copier, resolver, graph, scratch, ref measures, ref allocator);
            viewports[index] = entry is PdfReference original
                ? PublishHeader(original, edited, copier, scratch, ref headers, ref allocator) : edited;
        }
        return PdfArray.FromOwnedItems(viewports);
    }

    private static PdfDictionary ProjectViewport(PdfDictionary viewport, ReadOnlySpan<double> matrix,
        GraphCopier copier, PoolResolver resolver, OwnedAllocationBatch graph, ParserMemoryScope scratch,
        ref Dictionary<PdfReference, PdfReference>? measures, ref int allocator)
    {
        ResourceGovernor governor = graph.Governor;
        CancellationToken token = graph.Token;
        Span<double> source = stackalloc double[4], bounds = stackalloc double[4];
        if (!PdfDictionaryLookup.TryGet(viewport, "BBox"u8, governor, token, out QpdfObject rawBox)
            || !PdfGeometry.TryReadFiniteNumbers(CatalogCopy.ResolveValue(rawBox, resolver, governor, token), resolver, source))
        { throw new QpdfUnsupportedFeatureException("Rotation baking requires finite viewport bounds."); }

        if (!PdfDictionaryLookup.TryGet(viewport, "Measure"u8, governor, token, out QpdfObject rawMeasure) || rawMeasure is PdfNull)
        {
            PdfGeometry.TransformRect(source, matrix, bounds);
            CheckFinite(bounds);
            return PdfDictionaryRebuild.ReplaceEntry(graph, viewport, "BBox"u8, FlattenRotation.NumberArray(bounds, graph));
        }

        // The paired corners specify the rectilinear axes and the default
        // measuring origin. Their ordering survives the change of frame.
        MovePoint(source[..2], matrix, bounds[..2]);
        MovePoint(source[2..], matrix, bounds[2..]);
        PdfDictionary result = PdfDictionaryRebuild.ReplaceEntry(graph, viewport, "BBox"u8, FlattenRotation.NumberArray(bounds, graph));
        QpdfObject measure = ProjectMeasure(rawMeasure, matrix, copier, resolver, graph, scratch, ref measures, ref allocator);
        return ReferenceEquals(measure, rawMeasure) ? result : PdfDictionaryRebuild.ReplaceEntry(graph, result, "Measure"u8, measure);
    }

    private static QpdfObject ProjectMeasure(QpdfObject raw, ReadOnlySpan<double> matrix, GraphCopier copier,
        PoolResolver resolver, OwnedAllocationBatch graph, ParserMemoryScope scratch,
        ref Dictionary<PdfReference, PdfReference>? measures, ref int allocator)
    {
        if (raw is PdfReference reference && measures?.TryGetValue(reference, out PdfReference? projected) == true)
        { return projected; }
        ResourceGovernor governor = graph.Governor;
        CancellationToken token = graph.Token;
        if (CatalogCopy.ResolveDictionary(raw, resolver, governor, token) is not PdfDictionary measure)
        { throw new QpdfUnsupportedFeatureException("Rotation baking requires a readable viewport measurement dictionary."); }
        if (PdfDictionaryLookup.TryGet(measure, "Subtype"u8, governor, token, out QpdfObject subtype))
        {
            QpdfObject? resolved = CatalogCopy.ResolveValue(subtype, resolver, governor, token);
            if (resolved is not PdfNull
                && (resolved is not PdfName name || !PdfDictionaryLookup.NameEquals(name, "RL"u8, governor, token)))
            { throw new QpdfUnsupportedFeatureException("Rotation baking supports rectilinear viewport measurements."); }
        }
        if (!PdfDictionaryLookup.TryGet(measure, "O"u8, governor, token, out QpdfObject rawOrigin) || rawOrigin is PdfNull)
        { return raw; }
        Span<double> source = stackalloc double[2], origin = stackalloc double[2];
        if (!PdfGeometry.TryReadFiniteNumbers(CatalogCopy.ResolveValue(rawOrigin, resolver, governor, token), resolver, source))
        { throw new QpdfUnsupportedFeatureException("Rotation baking requires a finite viewport measurement origin."); }
        MovePoint(source, matrix, origin);
        PdfDictionary edited = PdfDictionaryRebuild.ReplaceEntry(graph, measure, "O"u8, FlattenRotation.NumberArray(origin, graph));
        return raw is PdfReference original
            ? PublishHeader(original, edited, copier, scratch, ref measures, ref allocator) : edited;
    }

    private static PdfReference PublishHeader(PdfReference original, PdfDictionary value, GraphCopier copier,
        ParserMemoryScope scratch, ref Dictionary<PdfReference, PdfReference>? memo, ref int allocator)
    {
        memo ??= ParserContainers.CreateDictionary<PdfReference, PdfReference>(scratch);
        ParserContainers.EnsureEntryCapacity(scratch, memo, IntPtr.Size * 2L);
        PdfReference replacement = copier.AddIndirectObjectWithId(value, allocator);
        allocator = checked(allocator + 1);
        memo.Add(original, replacement);
        return replacement;
    }

    private static void MovePoint(ReadOnlySpan<double> point, ReadOnlySpan<double> matrix, Span<double> result)
    {
        result[0] = matrix[0] * point[0] + matrix[2] * point[1] + matrix[4];
        result[1] = matrix[1] * point[0] + matrix[3] * point[1] + matrix[5];
        CheckFinite(result);
    }

    private static void CheckFinite(ReadOnlySpan<double> values)
    {
        foreach (double value in values)
        {
            if (!double.IsFinite(value)) { throw new QpdfUnsupportedFeatureException("Rotation baking produced non-finite viewport coordinates."); }
        }
    }
}
