using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Content;

/// <summary>Projects one explicit local destination through an already baked page frame.</summary>
internal static class DestinationCoordinates
{
    internal static PdfArray Transform(PdfArray source, double[] matrix, PoolResolver resolver, OwnedAllocationBatch graph)
    {
        QpdfObject? Resolve(QpdfObject raw) => CatalogCopy.ResolveValue(raw, resolver, graph.Governor, graph.Token);
        if (source.Count < 2 || Resolve(source[1]) is not PdfName name) { throw Malformed(); }
        ReadOnlySpan<byte> mode = name.RawSpan;
        if (mode.SequenceEqual("Fit"u8) || mode.SequenceEqual("FitB"u8))
        { if (source.Count != 2) { throw Malformed(); } return source; }
        bool xyz = mode.SequenceEqual("XYZ"u8), rectangle = mode.SequenceEqual("FitR"u8);
        bool horizontal = mode.SequenceEqual("FitH"u8) || mode.SequenceEqual("FitBH"u8);
        bool vertical = mode.SequenceEqual("FitV"u8) || mode.SequenceEqual("FitBV"u8);
        int count = xyz ? 2 : rectangle ? 4 : horizontal || vertical ? 1 : 0;
        if (count == 0 || source.Count != (xyz ? 5 : count + 2)) { throw Malformed(); }
        Span<double> original = stackalloc double[4], projected = stackalloc double[4];
        for (int index = 0; index < count; index++)
        {
            graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
            QpdfObject? value = Resolve(source[index + 2]);
            if (value is null or PdfNull && !rectangle)
            { throw new QpdfUnsupportedFeatureException("Rotation baking cannot preserve a destination position that depends on prior viewer state."); }
            original[index] = Number(value);
        }
        if (xyz)
        {
            QpdfObject? zoom = Resolve(source[4]);
            if (zoom is not (null or PdfNull) && Number(zoom) < 0) { throw Malformed(); }
            original[2] = original[0]; original[3] = original[1];
            FlattenRotation.TransformRect(original, matrix, projected);
        }
        else if (rectangle) { FlattenRotation.TransformRect(original, matrix, projected); }
        else
        {
            if (matrix[1] != 0 || matrix[2] != 0)
            { throw new QpdfUnsupportedFeatureException("Quarter-turn rotation baking cannot preserve both an axis-fit destination's position and its window-dependent magnification."); }
            projected[0] = horizontal ? matrix[3] * original[0] + matrix[5] : matrix[0] * original[0] + matrix[4];
        }
        bool changed = false;
        for (int index = 0; index < count; index++)
        {
            if (!double.IsFinite(projected[index])) { throw Malformed(); }
            changed |= projected[index] != original[index];
        }
        if (!changed) { return source; }
        long bytes = checked(56L + IntPtr.Size * (long)source.Count);
        graph.Memory.Reserve(bytes); QpdfObject[] items = new QpdfObject[source.Count]; graph.Add(bytes);
        for (int index = 0; index < items.Length; index++) { items[index] = source[index]; }
        for (int index = 0; index < count; index++)
        { if (projected[index] != original[index]) { items[index + 2] = AnnotationCoordinates.Coordinate(projected[index], graph); } }
        return PdfArray.FromOwnedItems(items);
    }

    private static double Number(QpdfObject? value)
    {
        double result = value switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => double.NaN };
        return double.IsFinite(result) ? result : throw Malformed();
    }
    private static QpdfParseException Malformed() => new("Malformed local destination cannot follow the baked page frame.", null, null);
}
