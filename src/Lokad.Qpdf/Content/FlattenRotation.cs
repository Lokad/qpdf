using System.Globalization;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class FlattenRotation
{
    private static readonly string[] Boxes =
    [
        "MediaBox",
        "CropBox",
        "BleedBox",
        "TrimBox",
        "ArtBox",
    ];

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
        HashSet<int> transformedAnnots = new();
        HashSet<int> matricizedAnnots = new();
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

            PoolResolver resolver = new(copier.CopiedObjects);
            double[]? media = ReadBox(page, "MediaBox", resolver);
            if (media is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten rotation without a media box; keeping the page as is.");
                continue;
            }

            Dictionary<string, double[]> rotated = new(StringComparer.Ordinal);
            foreach (string box in Boxes)
            {
                double[]? rect = ReadBox(page, box, resolver);
                if (rect is null)
                {
                    continue;
                }

                rotated[box] = RotateBox(rect, media, (int)angle);
            }

            double[] matrix = RotationMatrix((int)angle, media);
            List<QpdfObject>? wrapped = ReadContentEntries(copier, page, resolver, diagnostics);
            if (wrapped is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten rotation with unreadable content; keeping the page as is.");
                continue;
            }

            byte[] prefix = Encoding.ASCII.GetBytes("q\n" + FormatMatrix(matrix) + " cm\n");
            byte[] suffix = Encoding.ASCII.GetBytes("\nQ\n");
            PdfStream before = EncodeContent(prefix, compress, level, limits, governor);
            PdfStream after = EncodeContent(suffix, compress, level, limits, governor);
            PdfReference beforeLink = copier.AddIndirectObjectWithId(before, allocator);
            allocator = checked(allocator + 1);
            PdfReference afterLink = copier.AddIndirectObjectWithId(after, allocator);
            allocator = checked(allocator + 1);
            List<QpdfObject> entries = new(wrapped.Count + 2)
            {
                beforeLink,
            };
            entries.AddRange(wrapped);
            entries.Add(afterLink);
            Dictionary<PdfName, QpdfObject> rebuilt = new();
            foreach (PdfName key in page.Keys)
            {
                if (key.DisplayText == "Rotate" || key.DisplayText == "Contents" || key.DisplayText == "Annots")
                {
                    continue;
                }

                if (rotated.TryGetValue(key.DisplayText, out double[]? box))
                {
                    governor.ReserveMemory(64L);
                    rebuilt[key] = BoxArray(box, governor);
                    continue;
                }

                if (page.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    rebuilt[key] = found;
                }
            }

            governor.ReserveMemory(64L);
            rebuilt[PdfName.FromAscii("Contents")] = new PdfArray(entries);
            if (page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw) && annotsRaw is not null && annotsRaw is not PdfNull)
            {
                PdfArray? rewritten = TransformAnnots(copier, annotsRaw, matrix, transformedAnnots, matricizedAnnots, limits, governor, diagnostics, cancellationToken);
                if (rewritten is not null)
                {
                    rebuilt[PdfName.FromAscii("Annots")] = rewritten;
                }
                else
                {
                    rebuilt[PdfName.FromAscii("Annots")] = annotsRaw;
                }
            }

            governor.ReserveMemory(128L);
            copier.ReplaceCopiedObject(pageId, new PdfDictionary(rebuilt));
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

    private static double[]? ReadBox(PdfDictionary page, string key, PoolResolver resolver)
    {
        if (!page.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
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

    private static List<QpdfObject>? ReadContentEntries(
        GraphCopier copier,
        PdfDictionary page,
        PoolResolver resolver,
        DiagnosticBag diagnostics)
    {
        if (!page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null || contents is PdfNull)
        {
            return [];
        }

        if (contents is PdfArray array)
        {
            List<QpdfObject> entries = new(array.Count);
            foreach (QpdfObject item in array.Items)
            {
                if (item is PdfReference)
                {
                    QpdfObject? target = ResolveOrNull(resolver, (PdfReference)item);
                    if (target is not PdfStream)
                    {
                        return null;
                    }
                }
                else if (item is not PdfStream)
                {
                    return null;
                }

                entries.Add(item);
            }

            return entries;
        }

        if (contents is PdfReference singleReference)
        {
            QpdfObject? target = ResolveOrNull(resolver, singleReference);
            return target is PdfStream ? [contents] : null;
        }

        return contents is PdfStream ? [contents] : null;
    }

    private static PdfStream EncodeContent(
        byte[] content,
        bool compress,
        int? level,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        if (compress && content.Length > 0)
        {
            byte[] encoded = FlateFilter.Encode(content, level, limits, governor);
            governor.ReserveMemory(64L);
            return new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"),
            }), encoded);
        }

        return new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), content);
    }

    private static void RotateAppearances(
        GraphCopier copier,
        PdfDictionary annot,
        double[] matrix,
        HashSet<int> matricized,
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

        foreach (PdfName key in appearanceDict.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!appearanceDict.TryGet(key, out QpdfObject? entry) || entry is null || entry is PdfNull)
            {
                continue;
            }

            if (entry is PdfReference entryReference)
            {
                RotateAppearanceTarget(copier, entryReference, resolver, matrix, matricized, governor);
                continue;
            }

            if (entry is not PdfDictionary states)
            {
                continue;
            }

            foreach (PdfName state in states.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (!states.TryGet(state, out QpdfObject? stateRaw) || stateRaw is null || stateRaw is PdfNull)
                {
                    continue;
                }

                if (stateRaw is PdfReference stateReference)
                {
                    RotateAppearanceTarget(copier, stateReference, resolver, matrix, matricized, governor);
                }
            }
        }
    }

    private static void RotateAppearanceTarget(
        GraphCopier copier,
        PdfReference reference,
        PoolResolver resolver,
        double[] matrix,
        HashSet<int> matricized,
        ResourceGovernor governor)
    {
        if (!matricized.Add(reference.ObjectNumber))
        {
            return;
        }

        governor.NoteOperation();
        if (ResolveOrNull(resolver, reference) is not PdfStream stream)
        {
            return;
        }

        double[] composed = matrix;
        if (stream.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject? existing) && existing is PdfArray existingArray)
        {
            double[]? old = ReadMatrix(existingArray);
            if (old is not null)
            {
                composed = MultiplyMatrices(matrix, old);
            }
        }

        governor.ReserveMemory(64L);
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in stream.Dictionary.Keys)
        {
            if (stream.Dictionary.TryGet(key, out QpdfObject? found) && found is not null)
            {
                entries[key] = found;
            }
        }

        entries[PdfName.FromAscii("Matrix")] = BoxArray(composed, governor);
        copier.ReplaceCopiedObject(reference.ObjectNumber, new PdfStream(new PdfDictionary(entries), stream.Data));
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
        GraphCopier copier,
        QpdfObject annotsRaw,
        double[] matrix,
        HashSet<int> transformed,
        HashSet<int> matricized,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        PoolResolver resolver = new(copier.CopiedObjects);
        QpdfObject target = annotsRaw is PdfReference annotsReference
            ? ResolveOrNull(resolver, annotsReference) ?? annotsRaw
            : annotsRaw;
        if (target is not PdfArray array)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten rotation with a damaged annotation list; keeping annotations as is.");
            return null;
        }

        bool skipped = false;
        List<QpdfObject> entries = new(array.Count);
        foreach (QpdfObject item in array.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            QpdfObject? entryTarget = item is PdfReference itemReference
                ? ResolveOrNull(resolver, itemReference) ?? item
                : item;
            if (entryTarget is not PdfDictionary annot)
            {
                skipped = true;
                entries.Add(item);
                continue;
            }

            if (item is PdfReference indirect && !transformed.Add(indirect.ObjectNumber))
            {
                entries.Add(item);
                continue;
            }

            double[]? rect = ReadBox(annot, "Rect", resolver);
            if (rect is null)
            {
                skipped = true;
                entries.Add(item);
                continue;
            }

            double[] moved = TransformRect(rect, matrix);
            Dictionary<PdfName, QpdfObject> rebuilt = new();
            foreach (PdfName key in annot.Keys)
            {
                if (annot.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    rebuilt[key] = found;
                }
            }

            governor.ReserveMemory(64L);
            rebuilt[PdfName.FromAscii("Rect")] = BoxArray(moved, governor);
            RotateAppearances(copier, annot, matrix, matricized, resolver, governor, cancellationToken);
            PdfDictionary updated = new(rebuilt);
            if (item is PdfReference)
            {
                copier.ReplaceCopiedObject(((PdfReference)item).ObjectNumber, updated);
                entries.Add(item);
            }
            else
            {
                entries.Add(updated);
            }
        }

        if (skipped)
        {
            diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Some annotations could not be rotated; keeping them as is.");
        }

        return new PdfArray(entries);
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

    private static PdfArray BoxArray(double[] values, ResourceGovernor governor)
    {
        List<QpdfObject> items = new(values.Length);
        foreach (double value in values)
        {
            governor.ReserveMemory(32L);
            items.Add(new PdfReal(FormatDouble(value), value));
        }

        return new PdfArray(items);
    }

    private static string FormatMatrix(double[] values)
    {
        StringBuilder builder = new();
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(" ");
            }

            builder.Append(FormatDouble(values[i]));
        }

        return builder.ToString();
    }

    private static string FormatDouble(double value)
    {
        if (value > -0.00001 && value < 0.00001)
        {
            value = 0.0;
        }

        double rounded = Math.Round(value, 5, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.#####", CultureInfo.InvariantCulture);
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

