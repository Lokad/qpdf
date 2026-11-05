using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class AnnotationCoordinates
{
    // Returns true when NoRotate removes a rotation. The caller's fixed
    // geometry scratch funds the optional six-value frame.
    internal static bool SelectNoRotateFrame(PdfDictionary annotation, double[] rectangle, double[] matrix,
        PoolResolver resolver, ResourceGovernor governor, CancellationToken token, out double[] frame)
    {
        frame = matrix;
        if (!PdfDictionaryLookup.TryGet(annotation, "F"u8, governor, token, out QpdfObject raw)
            || CatalogCopy.ResolveValue(raw, resolver, governor, token) is not PdfInteger flags
            || (flags.Value & 16L) == 0L) { return false; }
        double scale = double.Hypot(matrix[0], matrix[1]);
        // Equivalent frames keep the existing appearance-sharing path.
        if (matrix[0] == scale && matrix[1] == 0 && matrix[2] == 0 && matrix[3] == scale) { return false; }
        double left = Math.Min(rectangle[0], rectangle[2]), top = Math.Max(rectangle[1], rectangle[3]);
        frame = [scale, 0, 0, scale,
            matrix[0] * left + matrix[2] * top + matrix[4] - scale * left,
            matrix[1] * left + matrix[3] * top + matrix[5] - scale * top];
        return true;
    }

    internal static QpdfObject TransformEntry(PdfDictionary annotation, PdfName key,
        QpdfObject raw, double[] matrix, PoolResolver resolver, OwnedAllocationBatch graph)
    {
        ReadOnlySpan<byte> slot = key.RawSpan;
        if (!slot.SequenceEqual("L"u8) && !slot.SequenceEqual("QuadPoints"u8)
            && !slot.SequenceEqual("Vertices"u8) && !slot.SequenceEqual("InkList"u8)
            && !slot.SequenceEqual("CL"u8) && !slot.SequenceEqual("RD"u8)
            && !slot.SequenceEqual("LL"u8) && !slot.SequenceEqual("LLE"u8)
            && !slot.SequenceEqual("LLO"u8) && !slot.SequenceEqual("CO"u8)) { return raw; }
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (!PdfDictionaryLookup.TryGet(annotation, "Subtype"u8, governor, token, out QpdfObject subtypeRaw)
            || CatalogCopy.ResolveValue(subtypeRaw, resolver, governor, token) is not PdfName subtype) { return raw; }
        ReadOnlySpan<byte> type = subtype.RawSpan;
        bool lineType = type.SequenceEqual("Line"u8);
        bool line = slot.SequenceEqual("L"u8) && lineType;
        bool distance = lineType && (slot.SequenceEqual("LL"u8) || slot.SequenceEqual("LLE"u8) || slot.SequenceEqual("LLO"u8));
        bool caption = lineType && slot.SequenceEqual("CO"u8);
        bool callout = slot.SequenceEqual("CL"u8) && type.SequenceEqual("FreeText"u8);
        bool vertices = slot.SequenceEqual("Vertices"u8) && (type.SequenceEqual("Polygon"u8) || type.SequenceEqual("PolyLine"u8));
        bool ink = slot.SequenceEqual("InkList"u8) && type.SequenceEqual("Ink"u8);
        bool insets = slot.SequenceEqual("RD"u8) && (type.SequenceEqual("FreeText"u8)
            || type.SequenceEqual("Square"u8) || type.SequenceEqual("Circle"u8) || type.SequenceEqual("Caret"u8));
        bool quads = slot.SequenceEqual("QuadPoints"u8) && (type.SequenceEqual("Link"u8)
            || type.SequenceEqual("Highlight"u8) || type.SequenceEqual("Underline"u8)
            || type.SequenceEqual("Squiggly"u8) || type.SequenceEqual("StrikeOut"u8));
        if (!line && !callout && !vertices && !ink && !quads && !insets && !distance && !caption) { return raw; }
        // An identity operation borrows the original value without interpreting it.
        if (matrix[0] == 1 && matrix[1] == 0 && matrix[2] == 0 && matrix[3] == 1 && matrix[4] == 0 && matrix[5] == 0) { return raw; }
        if (distance || caption)
        {
            // Page frames use uniform scale and rotation. CO's axes follow the
            // line itself; neither these offsets nor leader lengths are points.
            double scale = double.Hypot(matrix[0], matrix[1]);
            if (scale == 1) { return raw; }
            if (caption && (!PdfDictionaryLookup.TryGet(annotation, "Cap"u8, governor, token, out QpdfObject cap)
                || CatalogCopy.ResolveValue(cap, resolver, governor, token) is not PdfBoolean { Value: true })) { return raw; }
            return TransformDistances(raw, scale, caption, resolver, graph);
        }
        // Insets are distances between rectangle edges; translation changes neither.
        if (insets && matrix[0] == 1 && matrix[1] == 0 && matrix[2] == 0 && matrix[3] == 1) { return raw; }
        QpdfObject? target = CatalogCopy.ResolveValue(raw, resolver, governor, token);
        if ((quads || callout || insets) && target is PdfNull) { return raw; }
        if (target is not PdfArray array || (line && array.Count != 4)
            || (callout && array.Count != 4 && array.Count != 6)
            || (insets && array.Count != 4)
            || (quads && array.Count % 8 != 0)) { throw Malformed(); }
        if (array.Count == 0) { return raw; }
        if (insets) { return TransformInsets(array, matrix, resolver, graph); }
        if (!ink) { return TransformPoints(array, matrix, resolver, graph); }
        long bytes = checked(32L + IntPtr.Size * (long)array.Count);
        graph.Memory.Reserve(bytes); QpdfObject[] paths = new QpdfObject[array.Count]; graph.Add(bytes);
        for (int index = 0; index < paths.Length; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (CatalogCopy.ResolveValue(array[index], resolver, governor, token) is not PdfArray path) { throw Malformed(); }
            paths[index] = path.Count == 0 ? array[index] : TransformPoints(path, matrix, resolver, graph);
        }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(paths); graph.Add(24L); return result;
    }

    private static QpdfObject TransformDistances(QpdfObject raw, double scale, bool caption, PoolResolver resolver, OwnedAllocationBatch graph)
    {
        QpdfObject? value = CatalogCopy.ResolveValue(raw, resolver, graph.Governor, graph.Token);
        if (value is PdfNull) { return raw; }
        if (!caption)
        {
            double number = Number(value, resolver, graph);
            return number == 0 ? raw : Coordinate(number * scale, graph);
        }
        if (value is not PdfArray array || array.Count != 2) { throw Malformed(); }
        double along = Number(array[0], resolver, graph), across = Number(array[1], resolver, graph);
        if (along == 0 && across == 0) { return raw; }
        long bytes = 32L + 2L * IntPtr.Size;
        graph.Memory.Reserve(bytes); QpdfObject[] items = new QpdfObject[2]; graph.Add(bytes);
        items[0] = Coordinate(along * scale, graph);
        graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
        items[1] = Coordinate(across * scale, graph);
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L); return result;
    }

    private static PdfArray TransformInsets(PdfArray source, double[] matrix, PoolResolver resolver, OwnedAllocationBatch graph)
    {
        double left = Number(source[0], resolver, graph), top = Number(source[1], resolver, graph);
        double right = Number(source[2], resolver, graph), bottom = Number(source[3], resolver, graph);
        double a = matrix[0], b = matrix[1], c = matrix[2], d = matrix[3];
        // RD is left/top/right/bottom, unlike PDF rectangle coordinate arrays.
        Span<double> values = stackalloc double[4]
        {
            Math.Abs(a) * (a >= 0 ? left : right) + Math.Abs(c) * (c >= 0 ? bottom : top),
            Math.Abs(b) * (b >= 0 ? right : left) + Math.Abs(d) * (d >= 0 ? top : bottom),
            Math.Abs(a) * (a >= 0 ? right : left) + Math.Abs(c) * (c >= 0 ? top : bottom),
            Math.Abs(b) * (b >= 0 ? left : right) + Math.Abs(d) * (d >= 0 ? bottom : top),
        };
        long bytes = 32L + 4L * IntPtr.Size;
        graph.Memory.Reserve(bytes); QpdfObject[] items = new QpdfObject[4]; graph.Add(bytes);
        for (int index = 0; index < items.Length; index++)
        { graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation(); items[index] = Coordinate(values[index], graph); }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L); return result;
    }

    private static PdfArray TransformPoints(PdfArray source, double[] matrix, PoolResolver resolver, OwnedAllocationBatch graph)
    {
        if (source.Count % 2 != 0) { throw Malformed(); }
        long bytes = checked(32L + IntPtr.Size * (long)source.Count);
        graph.Memory.Reserve(bytes); QpdfObject[] points = new QpdfObject[source.Count]; graph.Add(bytes);
        for (int index = 0; index < points.Length; index += 2)
        {
            graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
            double x = Number(source[index], resolver, graph), y = Number(source[index + 1], resolver, graph);
            points[index] = Coordinate(matrix[0] * x + matrix[2] * y + matrix[4], graph);
            points[index + 1] = Coordinate(matrix[1] * x + matrix[3] * y + matrix[5], graph);
        }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(points); graph.Add(24L); return result;
    }

    private static double Number(QpdfObject? raw, PoolResolver resolver, OwnedAllocationBatch graph)
    {
        QpdfObject? value = CatalogCopy.ResolveValue(raw, resolver, graph.Governor, graph.Token);
        double number = value switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => double.NaN };
        if (!double.IsFinite(number)) { throw Malformed(); }
        return number;
    }

    private static PdfReal Coordinate(double value, OwnedAllocationBatch graph)
    {
        Span<char> text = stackalloc char[384];
        int count = WriterNumber.FormatGeometry(value, text);
        long bytes = checked(32L + GovernedText.StringBytes(count));
        graph.Memory.Reserve(bytes); PdfReal result = new(new string(text[..count]), value); graph.Add(bytes); return result;
    }

    private static QpdfParseException Malformed()
        => new("Malformed annotation coordinates cannot be transformed.", null, null);
}
