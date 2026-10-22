using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class FlattenRotation
{
    private static readonly PdfName[] Boxes =
    [
        PdfName.FromAscii("MediaBox"), PdfName.FromAscii("CropBox"),
        PdfName.FromAscii("BleedBox"), PdfName.FromAscii("TrimBox"), PdfName.FromAscii("ArtBox"),
    ];
    private static readonly PdfName Rect = PdfName.FromAscii("Rect");

    internal static int Apply(
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        int allocator)
    {
        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
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
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        HashSet<int> transformedAnnots = ParserContainers.CreateSet<int>(traversal);
        HashSet<int> matricizedAnnots = ParserContainers.CreateSet<int>(traversal);
        foreach ((GraphCopier copier, int pageId) in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary page)
            {
                continue;
            }

            long rotate = 0L;
            bool hasRotate = false;
            if (page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject? rotateRaw) && rotateRaw is PdfInteger rotateInteger)
            {
                rotate = rotateInteger.Value;
                hasRotate = true;
            }

            if (!hasRotate)
            {
                continue;
            }

            long angle = rotate % 360L;
            if (angle < 0L)
            {
                angle += 360L;
            }

            if (angle != 90L && angle != 180L && angle != 270L)
            {
                continue;
            }

            using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
            scratch.Reserve(2048L); // Fixed matrices, boxes, resolver and formatting state.
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
            PoolResolver resolver = new(copier.CopiedObjects);
            double[]? media = ReadBox(page, Boxes[0], resolver);
            if (media is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten rotation without a media box; keeping the page as is.");
                continue;
            }
            double[]?[] rotated = new double[Boxes.Length][];
            for (int box = 0; box < Boxes.Length; box++)
            {
                double[]? rect = ReadBox(page, Boxes[box], resolver);
                if (rect is not null) { rotated[box] = RotateBox(rect, media, (int)angle); }
            }
            double[] matrix = RotationMatrix((int)angle, media);
            using ContentEntries? wrapped = page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents) && contents is not PdfNull
                ? ContentEntries.TryRead(contents, resolver, governor, cancellationToken) : ContentEntries.Create(governor, cancellationToken);
            if (wrapped is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten rotation with unreadable content; keeping the page as is.");
                continue;
            }
            int count = checked(wrapped.Count + 2);
            long arrayBytes = checked(32L + IntPtr.Size * (long)count);
            graph.Memory.Reserve(arrayBytes);
            QpdfObject[] items = new QpdfObject[count]; graph.Add(arrayBytes);
            using (DisplayBuffer prefix = DisplayBuffer.Create(governor, false))
            {
                prefix.Append("q\n");
                for (int i = 0; i < matrix.Length; i++)
                {
                    if (i > 0) { prefix.Append(' '); }
                    prefix.Append(FormatDouble(matrix[i], scratch));
                }
                prefix.Append(" cm\n");
                using GovernedText text = prefix.Finish();
                graph.Memory.Reserve(32L + text.Value.Length);
                byte[] bytes = Encoding.ASCII.GetBytes(text.Value); graph.Add(32L + bytes.Length);
                PdfStream before = ContentTransforms.EncodeContent(bytes, compress, level, limits, graph);
                items[0] = copier.AddIndirectObjectWithId(before, allocator); allocator = checked(allocator + 1);
            }
            graph.Memory.Reserve(35L); byte[] suffix = "\nQ\n"u8.ToArray(); graph.Add(35L);
            PdfStream after = ContentTransforms.EncodeContent(suffix, compress, level, limits, graph);
            items[^1] = copier.AddIndirectObjectWithId(after, allocator); allocator = checked(allocator + 1);
            for (int i = 0; i < wrapped.Count; i++) { items[i + 1] = wrapped.Get(i).Value; }
            graph.Memory.Reserve(24L); PdfArray stacked = PdfArray.FromOwnedItems(items); graph.Add(24L);
            PdfNameTable<QpdfObject> rebuilt = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
            using (PdfDictionary.EntryEnumerator source = page.GetEntryEnumerator())
            {
                while (source.MoveNext(governor, cancellationToken))
                {
                    PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
                    if (key.RawSpan.SequenceEqual("Rotate"u8) || key.RawSpan.SequenceEqual("Contents"u8) || key.RawSpan.SequenceEqual("Annots"u8)) { continue; }
                    for (int box = 0; box < Boxes.Length; box++)
                    {
                        if (key.Equals(Boxes[box]) && rotated[box] is double[] rect) { value = NumberArray(rect, graph); break; }
                    }
                    rebuilt.Assign(graph.Memory, governor, key, value, cancellationToken);
                }
            }
            rebuilt.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Contents"u8), stacked, cancellationToken);
            if (page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotsRaw) && annotsRaw is not PdfNull)
            {
                PdfArray? annots = TransformAnnots(copier, annotsRaw, matrix, transformedAnnots, matricizedAnnots, traversal, graph, diagnostics);
                rebuilt.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Annots"u8), annots ?? annotsRaw, cancellationToken);
            }
            PdfDictionary result = PdfDictionaryRebuild.Complete(graph, rebuilt, allocation);
            copier.ReplaceCopiedObject(pageId, result); graph.Retain();
        }

        return allocator;
    }

    private static double[] RotationMatrix(int angle, double[] media)
    {
        if (angle == 90)
        {
            return [0.0, -1.0, 1.0, 0.0, 0.0, media[2] + media[0]];
        }

        if (angle == 180)
        {
            return [-1.0, 0.0, 0.0, -1.0, media[2] + media[0], media[3] + media[1]];
        }

        return [0.0, 1.0, -1.0, 0.0, media[3] + media[1], 0.0];
    }

    private static double[] RotateBox(double[] rect, double[] media, int angle)
    {
        double left = rect[0] - media[0];
        double right = media[2] - rect[2];
        double bottom = rect[1] - media[1];
        double top = media[3] - rect[3];
        if (angle == 90)
        {
            return [media[1] + bottom, media[0] + right, media[3] - top, media[2] - left];
        }

        if (angle == 180)
        {
            return [media[0] + right, media[1] + top, media[2] - left, media[3] - bottom];
        }

        return [media[1] + top, media[0] + left, media[3] - bottom, media[2] - right];
    }

    private static double[]? ReadBox(PdfDictionary page, PdfName key, PoolResolver resolver)
    {
        if (!page.TryGet(key, out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        QpdfObject target = raw is PdfReference reference
            ? ResolveOrNull(resolver, reference) ?? raw
            : raw;
        if (target is not PdfArray array || array.Count != 4)
        {
            return null;
        }

        double[] numbers = new double[4];
        for (int i = 0; i < 4; i++)
        {
            QpdfObject item = array[i];
            if (item is PdfReference itemReference)
            {
                QpdfObject? resolved = ResolveOrNull(resolver, itemReference);
                if (resolved is null)
                {
                    return null;
                }

                item = resolved;
            }

            if (item is PdfInteger integer)
            {
                numbers[i] = (double)integer.Value;
            }
            else if (item is PdfReal real)
            {
                numbers[i] = real.Value;
            }
            else
            {
                return null;
            }

            if (!double.IsFinite(numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    private static void RotateAppearances(
        GraphCopier copier,
        PdfDictionary annot,
        double[] matrix,
        HashSet<int> matricized,
        ParserMemoryScope traversal,
        PoolResolver resolver,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? appearance) || appearance is null || appearance is PdfNull)
        {
            return;
        }

        QpdfObject appearanceTarget = appearance is PdfReference appearanceReference
            ? ResolveOrNull(resolver, appearanceReference) ?? appearance
            : appearance;
        if (appearanceTarget is not PdfDictionary appearanceDict)
        {
            return;
        }

        using PdfDictionary.EntryEnumerator appearances = appearanceDict.GetEntryEnumerator();
        while (appearances.MoveNext(governor, cancellationToken))
        {
            QpdfObject entry = appearances.Current.Value;
            if (entry is PdfNull) { continue; }

            if (entry is PdfReference entryReference)
            {
                RotateAppearanceTarget(copier, entryReference, resolver, matrix, matricized, traversal, governor, cancellationToken);
                continue;
            }

            if (entry is not PdfDictionary states)
            {
                continue;
            }

            using PdfDictionary.EntryEnumerator stateEntries = states.GetEntryEnumerator();
            while (stateEntries.MoveNext(governor, cancellationToken))
            {
                QpdfObject stateRaw = stateEntries.Current.Value;
                if (stateRaw is PdfNull) { continue; }

                if (stateRaw is PdfReference stateReference)
                {
                    RotateAppearanceTarget(copier, stateReference, resolver, matrix, matricized, traversal, governor, cancellationToken);
                }
            }
        }
    }

    internal static void RotateAppearanceTarget(
        GraphCopier copier,
        PdfReference reference,
        PoolResolver resolver,
        double[] matrix,
        HashSet<int> matricized,
        ParserMemoryScope traversal,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (!ParserContainers.AddSetItem(traversal, matricized, reference.ObjectNumber, sizeof(int)))
        {
            return;
        }

        governor.NoteOperation();
        if (ResolveOrNull(resolver, reference) is not PdfStream stream)
        {
            return;
        }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor); scratch.Reserve(256L);
        double[] composed = matrix;
        if (stream.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject? existing) && existing is PdfArray existingArray)
        {
            double[]? old = ReadMatrix(existingArray);
            if (old is not null)
            {
                composed = MultiplyMatrices(matrix, old);
            }
        }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
        PdfArray values = NumberArray(composed, graph);
        PdfDictionary header = PdfDictionaryRebuild.ReplaceEntry(graph, stream.Dictionary, "Matrix"u8, values);
        graph.Memory.Reserve(32L); PdfStream result = stream.WithDictionary(header); graph.Add(32L);
        copier.ReplaceCopiedObject(reference.ObjectNumber, result); graph.Retain();
    }

    private static double[]? ReadMatrix(PdfArray array)
    {
        if (array.Count != 6)
        {
            return null;
        }

        double[] numbers = new double[6];
        for (int i = 0; i < 6; i++)
        {
            if (array[i] is PdfInteger integer)
            {
                numbers[i] = (double)integer.Value;
            }
            else if (array[i] is PdfReal real)
            {
                numbers[i] = real.Value;
            }
            else
            {
                return null;
            }

            if (!double.IsFinite(numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    private static double[] MultiplyMatrices(double[] first, double[] second)
    {
        return
        [
            (first[0] * second[0]) + (first[1] * second[2]),
            (first[0] * second[1]) + (first[1] * second[3]),
            (first[2] * second[0]) + (first[3] * second[2]),
            (first[2] * second[1]) + (first[3] * second[3]),
            (first[4] * second[0]) + (first[5] * second[2]) + second[4],
            (first[4] * second[1]) + (first[5] * second[3]) + second[5],
        ];
    }

    private static PdfArray? TransformAnnots(
        GraphCopier copier, QpdfObject annotsRaw, double[] matrix,
        HashSet<int> transformed, HashSet<int> matricized, ParserMemoryScope traversal,
        OwnedAllocationBatch graph, DiagnosticBag diagnostics)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        PoolResolver resolver = new(copier.CopiedObjects);
        QpdfObject target = annotsRaw is PdfReference link ? ResolveOrNull(resolver, link) ?? annotsRaw : annotsRaw;
        if (target is not PdfArray array)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten rotation with a damaged annotation list; keeping annotations as is.");
            return null;
        }
        bool skipped = false;
        long bytes = checked(32L + IntPtr.Size * (long)array.Count);
        graph.Memory.Reserve(bytes);
        QpdfObject[] items = new QpdfObject[array.Count]; graph.Add(bytes);
        for (int index = 0; index < array.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            QpdfObject item = array[index]; items[index] = item;
            QpdfObject? targetAnnot = item is PdfReference reference ? ResolveOrNull(resolver, reference) ?? item : item;
            if (targetAnnot is not PdfDictionary annot) { skipped = true; continue; }
            if (item is PdfReference indirect && !ParserContainers.AddSetItem(traversal, transformed, indirect.ObjectNumber, sizeof(int))) { continue; }
            double[]? rect = ReadBox(annot, Rect, resolver);
            if (rect is null) { skipped = true; continue; }
            PdfArray moved = NumberArray(TransformRect(rect, matrix), graph);
            PdfDictionary updated = PdfDictionaryRebuild.ReplaceEntry(graph, annot, "Rect"u8, moved);
            RotateAppearances(copier, annot, matrix, matricized, traversal, resolver, governor, token);
            if (item is PdfReference annotReference) { copier.ReplaceCopiedObject(annotReference.ObjectNumber, updated); }
            else { items[index] = updated; }
        }
        if (skipped)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Some annotations could not be rotated; keeping them as is.");
        }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L); return result;
    }

    private static double[] TransformRect(double[] rect, double[] matrix)
    {
        double x0 = (matrix[0] * rect[0]) + (matrix[2] * rect[1]) + matrix[4];
        double y0 = (matrix[1] * rect[0]) + (matrix[3] * rect[1]) + matrix[5];
        double x1 = (matrix[0] * rect[0]) + (matrix[2] * rect[3]) + matrix[4];
        double y1 = (matrix[1] * rect[0]) + (matrix[3] * rect[3]) + matrix[5];
        double x2 = (matrix[0] * rect[2]) + (matrix[2] * rect[1]) + matrix[4];
        double y2 = (matrix[1] * rect[2]) + (matrix[3] * rect[1]) + matrix[5];
        double x3 = (matrix[0] * rect[2]) + (matrix[2] * rect[3]) + matrix[4];
        double y3 = (matrix[1] * rect[2]) + (matrix[3] * rect[3]) + matrix[5];
        double llx = x0;
        if (x1 < llx) llx = x1;
        if (x2 < llx) llx = x2;
        if (x3 < llx) llx = x3;
        double lly = y0;
        if (y1 < lly) lly = y1;
        if (y2 < lly) lly = y2;
        if (y3 < lly) lly = y3;
        double urx = x0;
        if (x1 > urx) urx = x1;
        if (x2 > urx) urx = x2;
        if (x3 > urx) urx = x3;
        double ury = y0;
        if (y1 > ury) ury = y1;
        if (y2 > ury) ury = y2;
        if (y3 > ury) ury = y3;
        return [llx, lly, urx, ury];
    }

    private static PdfArray NumberArray(double[] values, OwnedAllocationBatch graph)
    {
        long bytes = checked(32L + IntPtr.Size * (long)values.Length);
        graph.Memory.Reserve(bytes); QpdfObject[] items = new QpdfObject[values.Length]; graph.Add(bytes);
        for (int i = 0; i < values.Length; i++)
        {
            string text = FormatDouble(values[i], graph.Memory);
            graph.Memory.Reserve(32L); items[i] = new PdfReal(text, values[i]);
            graph.Add(checked(32L + GovernedText.StringBytes(text.Length)));
        }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L); return result;
    }

    private static string FormatDouble(double value, ParserMemoryScope memory)
    {
        Span<char> text = stackalloc char[384];
        int length = WriterNumber.FormatGeometry(value, text);
        memory.Reserve(GovernedText.StringBytes(length));
        return new string(text[..length]);
    }

    private static QpdfObject? ResolveOrNull(PoolResolver resolver, PdfReference reference)
    {
        if (((IIndirectResolver)resolver).TryResolve(reference, out QpdfObject? found) && found is not null && found is not PdfNull)
        {
            return found;
        }

        return null;
    }
}

