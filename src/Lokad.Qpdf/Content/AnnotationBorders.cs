using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Projects border dimensions into an annotation's effective page frame.</summary>
internal static class AnnotationBorders
{
    private static readonly PdfName BorderKey = PdfName.FromAscii("Border");
    private static readonly PdfName StyleKey = PdfName.FromAscii("BS");
    private static readonly PdfName WidthKey = PdfName.FromAscii("W");
    private static readonly PdfName DashKey = PdfName.FromAscii("D");

    internal static void Apply(PdfDictionary source, PdfNameTable<QpdfObject> entries,
        double[] matrix, PoolResolver resolver, OwnedAllocationBatch graph, GraphCopier copier,
        Dictionary<(int Id, AnnotationCopyKind Kind, bool Frame), PdfReference> copies,
        ParserMemoryScope traversal, ref int allocator)
    {
        double scale = double.Hypot(matrix[0], matrix[1]);
        bool swap = matrix[0] == 0 && matrix[3] == 0;
        if (scale == 1 && !swap) { return; }
        // Callers lend the existing memo for one placement/page frame. NoRotate
        // changes its orientation, while the scalar scale remains shared.
        traversal.CheckOwner(graph.Governor);
        QpdfObject? subtype = Resolve(Read(source, "Subtype"u8, graph), resolver, graph);
        if (subtype is not PdfName name) { return; }
        ReadOnlySpan<byte> type = name.RawSpan;
        bool styled = type.SequenceEqual("Link"u8) || type.SequenceEqual("FreeText"u8)
            || type.SequenceEqual("Line"u8) || type.SequenceEqual("Square"u8) || type.SequenceEqual("Circle"u8)
            || type.SequenceEqual("Polygon"u8) || type.SequenceEqual("PolyLine"u8) || type.SequenceEqual("Ink"u8)
            || type.SequenceEqual("Widget"u8);
        if (styled)
        {
            QpdfObject? rawStyle = Read(source, "BS"u8, graph);
            QpdfObject? style = Resolve(rawStyle, resolver, graph);
            if (style is PdfDictionary dictionary)
            {
                // BS overrides Border, including an unusable unused Border value.
                if (scale != 1)
                {
                    if (rawStyle is PdfReference reference
                        && copies.TryGetValue((reference.ObjectNumber, AnnotationCopyKind.BorderStyle, false), out PdfReference? cached))
                    { Assign(entries, StyleKey, cached, graph); return; }
                    PdfDictionary replacement = ScaleStyle(dictionary, scale, resolver, graph, copier, copies, traversal, ref allocator);
                    if (!ReferenceEquals(dictionary, replacement))
                    {
                        QpdfObject value = Store(rawStyle, replacement, AnnotationCopyKind.BorderStyle, false,
                            copier, copies, traversal, ref allocator);
                        Assign(entries, StyleKey, value, graph);
                    }
                }
                return;
            }
            if (style is not (null or PdfNull)) { throw Malformed(); }
        }
        QpdfObject? raw = Read(source, "Border"u8, graph);
        QpdfObject? border = Resolve(raw, resolver, graph);
        if (border is null or PdfNull)
        {
            // Default fields are not visited by the ordinary entry-copy loop.
            if (styled && scale != 1)
            {
                QpdfObject value = DefaultArray([0, 0, scale], AnnotationCopyKind.Border,
                    graph, copier, copies, traversal, ref allocator);
                Assign(entries, BorderKey, value, graph);
            }
            return;
        }
        if (border is not PdfArray array || array.Count is < 3 or > 4) { throw Malformed(); }
        if (raw is PdfReference borderReference
            && copies.TryGetValue((borderReference.ObjectNumber, AnnotationCopyKind.Border, swap), out PdfReference? existing))
        { Assign(entries, BorderKey, existing, graph); return; }
        double width = Number(array[2], resolver, graph);
        if (width == 0) { return; }
        double horizontal = Number(array[0], resolver, graph), vertical = Number(array[1], resolver, graph);
        double x = Math.Abs(matrix[0]) * horizontal + Math.Abs(matrix[2]) * vertical;
        double y = Math.Abs(matrix[1]) * horizontal + Math.Abs(matrix[3]) * vertical;
        QpdfObject? dash = array.Count == 4 ? array[3] : null;
        QpdfObject? nextDash = scale != 1 && dash is not null
            ? ScaleDash(dash, scale, resolver, graph, copier, copies, traversal, ref allocator) : dash;
        if (x == horizontal && y == vertical && width * scale == width && ReferenceEquals(dash, nextDash)) { return; }
        long bytes = checked(32L + IntPtr.Size * (long)array.Count);
        graph.Memory.Reserve(bytes); QpdfObject[] items = new QpdfObject[array.Count]; graph.Add(bytes);
        items[0] = x == horizontal ? array[0] : AnnotationCoordinates.Coordinate(x, graph);
        items[1] = y == vertical ? array[1] : AnnotationCoordinates.Coordinate(y, graph);
        items[2] = scale == 1 ? array[2] : AnnotationCoordinates.Coordinate(width * scale, graph);
        if (items.Length == 4 && nextDash is not null) { items[3] = nextDash; }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L);
        Assign(entries, BorderKey, Store(raw, result, AnnotationCopyKind.Border, swap,
            copier, copies, traversal, ref allocator), graph);
    }

    private static PdfDictionary ScaleStyle(PdfDictionary source, double scale, PoolResolver resolver,
        OwnedAllocationBatch graph, GraphCopier copier,
        Dictionary<(int Id, AnnotationCopyKind Kind, bool Frame), PdfReference> copies,
        ParserMemoryScope traversal, ref int allocator)
    {
        QpdfObject? rawWidth = Read(source, "W"u8, graph);
        QpdfObject? widthValue = Resolve(rawWidth, resolver, graph);
        double width = widthValue is null or PdfNull ? 1 : Number(widthValue, resolver, graph);
        // A zero width disables the border; its other presentation fields are unused.
        if (width == 0) { return source; }
        QpdfObject? style = Resolve(Read(source, "S"u8, graph), resolver, graph);
        bool dashed = style is PdfName name && name.RawSpan.SequenceEqual("D"u8);
        QpdfObject? dash = null;
        if (dashed)
        {
            QpdfObject? rawDash = Read(source, "D"u8, graph);
            dash = Resolve(rawDash, resolver, graph) is null or PdfNull
                ? DefaultArray([3 * scale], AnnotationCopyKind.Dash, graph, copier, copies, traversal, ref allocator)
                : ScaleDash(rawDash, scale, resolver, graph, copier, copies, traversal, ref allocator);
        }
        QpdfObject scaledWidth = AnnotationCoordinates.Coordinate(width * scale, graph);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        bool sawWidth = false, sawDash = false;
        using PdfDictionary.EntryEnumerator iterator = source.GetEntryEnumerator();
        while (iterator.MoveNext(graph.Governor, graph.Token))
        {
            var entry = iterator.Current; QpdfObject value = entry.Value;
            if (entry.Key.RawSpan.SequenceEqual("W"u8)) { sawWidth = true; value = scaledWidth; }
            else if (dashed && entry.Key.RawSpan.SequenceEqual("D"u8) && dash is not null) { sawDash = true; value = dash; }
            entries.Assign(graph.Memory, graph.Governor, entry.Key, value, graph.Token);
        }
        if (!sawWidth) { Assign(entries, WidthKey, scaledWidth, graph); }
        if (dashed && !sawDash && dash is not null) { Assign(entries, DashKey, dash, graph); }
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }

    private static QpdfObject ScaleDash(QpdfObject? raw, double scale, PoolResolver resolver, OwnedAllocationBatch graph,
        GraphCopier copier, Dictionary<(int Id, AnnotationCopyKind Kind, bool Frame), PdfReference> copies,
        ParserMemoryScope traversal, ref int allocator)
    {
        QpdfObject? value = Resolve(raw, resolver, graph);
        if (value is null or PdfNull) { return raw ?? PdfNull.Instance; }
        if (value is not PdfArray array) { throw Malformed(); }
        if (array.Count == 0) { return raw ?? value; }
        if (raw is PdfReference reference
            && copies.TryGetValue((reference.ObjectNumber, AnnotationCopyKind.Dash, false), out PdfReference? cached))
        { return cached; }
        bool nonzero = false;
        for (int index = 0; index < array.Count; index++) { nonzero |= Number(array[index], resolver, graph) != 0; }
        if (!nonzero) { throw Malformed(); }
        long bytes = checked(32L + IntPtr.Size * (long)array.Count);
        graph.Memory.Reserve(bytes); QpdfObject[] items = new QpdfObject[array.Count]; graph.Add(bytes);
        for (int index = 0; index < items.Length; index++)
        {
            double number = Number(array[index], resolver, graph);
            items[index] = number == 0 ? array[index] : AnnotationCoordinates.Coordinate(number * scale, graph);
        }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L);
        return Store(raw, result, AnnotationCopyKind.Dash, false, copier, copies, traversal, ref allocator);
    }

    private static PdfReference Store(QpdfObject? source, QpdfObject value, AnnotationCopyKind kind, bool frame,
        GraphCopier copier, Dictionary<(int Id, AnnotationCopyKind Kind, bool Frame), PdfReference> copies,
        ParserMemoryScope traversal, ref int allocator)
    {
        if (source is null or PdfReference)
        { ParserContainers.EnsureEntryCapacity(traversal, copies, 2L * sizeof(int) + IntPtr.Size); }
        PdfReference result = copier.AddIndirectObjectWithId(value, allocator); allocator = checked(allocator + 1);
        if (source is null or PdfReference)
        {
            copies.Add((source is PdfReference reference ? reference.ObjectNumber : 0, kind, frame), result);
        }
        return result;
    }

    private static PdfReference DefaultArray(ReadOnlySpan<double> values, AnnotationCopyKind kind,
        OwnedAllocationBatch graph, GraphCopier copier,
        Dictionary<(int Id, AnnotationCopyKind Kind, bool Frame), PdfReference> copies,
        ParserMemoryScope traversal, ref int allocator)
    {
        // PDF object zero is free. Each memo belongs to one uniform scale, so
        // implicit defaults can share storage across ordinary/NoRotate frames.
        if (copies.TryGetValue((0, kind, false), out PdfReference? cached)) { return cached; }
        return Store(null, Array(values, graph), kind, false, copier, copies, traversal, ref allocator);
    }

    private static PdfArray Array(ReadOnlySpan<double> values, OwnedAllocationBatch graph)
    {
        long bytes = checked(32L + IntPtr.Size * (long)values.Length);
        graph.Memory.Reserve(bytes); QpdfObject[] items = new QpdfObject[values.Length]; graph.Add(bytes);
        for (int index = 0; index < values.Length; index++) { items[index] = AnnotationCoordinates.Coordinate(values[index], graph); }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L); return result;
    }

    private static QpdfObject? Read(PdfDictionary source, ReadOnlySpan<byte> key, OwnedAllocationBatch graph)
        => PdfDictionaryLookup.TryGet(source, key, graph.Governor, graph.Token, out QpdfObject value) ? value : null;
    private static QpdfObject? Resolve(QpdfObject? value, PoolResolver resolver, OwnedAllocationBatch graph)
        => CatalogCopy.ResolveValue(value, resolver, graph.Governor, graph.Token);
    private static double Number(QpdfObject? raw, PoolResolver resolver, OwnedAllocationBatch graph)
    {
        double value = AnnotationCoordinates.Number(raw, resolver, graph);
        if (value < 0) { throw Malformed(); }
        return value;
    }
    private static void Assign(PdfNameTable<QpdfObject> entries, PdfName key, QpdfObject value, OwnedAllocationBatch graph)
        => entries.Assign(graph.Memory, graph.Governor, key, value, graph.Token);
    private static QpdfParseException Malformed()
        => new("Malformed annotation border cannot be transformed.", null, null);
}
