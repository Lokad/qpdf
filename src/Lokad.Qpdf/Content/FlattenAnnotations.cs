using System.Globalization;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class FlattenAnnotations
{
    private const int FlagPrint = 4;

    private const int FlagNoRotate = 16;

    private const int FlagNoView = 32;

    internal static bool NeedsAppearances(DocumentEngine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (!engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) || root is null || root is PdfNull)
        {
            return false;
        }

        QpdfObject catalogTarget = root is PdfReference rootReference
            && ((IIndirectResolver)engine).TryResolve(rootReference, out QpdfObject? resolved)
            && resolved is not null
            ? resolved
            : root;
        if (catalogTarget is not PdfDictionary catalog)
        {
            return false;
        }

        if (!catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? form) || form is null || form is PdfNull)
        {
            return false;
        }

        QpdfObject formTarget = form is PdfReference formReference
            && ((IIndirectResolver)engine).TryResolve(formReference, out QpdfObject? formResolved)
            && formResolved is not null
            ? formResolved
            : form;
        if (formTarget is not PdfDictionary formDict)
        {
            return false;
        }

        return formDict.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject? need) && need is PdfBoolean flag && flag.Value;
    }

    internal static int Apply(
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        string mode,
        bool needAppearances,
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

        if (mode is null)
        {
            throw new ArgumentNullException(nameof(mode));
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
        int required = mode == "print" ? FlagPrint : 0;
        int forbidden = mode == "screen" ? FlagNoView : 0;
        bool warned = false;
        foreach ((GraphCopier copier, int pageId) in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary page)
            {
                continue;
            }

            if (!page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            PoolResolver resolver = new(copier.CopiedObjects);
            QpdfObject annotsTarget = annotsRaw is PdfReference annotsReference
                ? ResolveOrNull(resolver, annotsReference) ?? annotsRaw
                : annotsRaw;
            if (annotsTarget is not PdfArray annots)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten a damaged annotation list; keeping annotations as is.");
                continue;
            }

            if (annots.Count == 0)
            {
                continue;
            }

            if (needAppearances && !warned)
            {
                warned = true;
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Document needs updated appearance streams, so form fields will not be flattened.");
            }

            long rotate = 0L;
            if (page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject? rotateRaw) && rotateRaw is PdfInteger rotateInteger)
            {
                rotate = rotateInteger.Value;
            }

            PdfDictionary? resources = EnsureResources(copier, page, resolver, governor);
            if (resources is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten annotations without page resources; keeping annotations as is.");
                continue;
            }

            HashSet<string> taken = CollectTakenNames(resources, resolver);
            int suffix = 1;
            List<QpdfObject> kept = new(annots.Count);
            List<byte> stamped = new(256);
            bool changed = false;
            foreach (QpdfObject item in annots.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                QpdfObject entryTarget = item is PdfReference itemReference
                    ? ResolveOrNull(resolver, itemReference) ?? item
                    : item;
                if (entryTarget is not PdfDictionary annot)
                {
                    kept.Add(item);
                    continue;
                }

                string subtype = string.Empty;
                if (annot.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtypeRaw) && subtypeRaw is PdfName subtypeName)
                {
                    subtype = subtypeName.DisplayText;
                }

                bool isWidget = subtype == "Widget";
                if (needAppearances && isWidget)
                {
                    kept.Add(item);
                    continue;
                }

                PdfStream? appearance = FindAppearanceStream(annot, resolver, out PdfReference? appearanceRaw);
                if (appearance is null)
                {
                    if (HasAppearanceDictionary(annot))
                    {
                        changed = true;
                        continue;
                    }

                    kept.Add(item);
                    continue;
                }

                (PdfReference appearanceLink, PdfDictionary appearanceDict) = EnsureAppearanceObject(copier, appearance, appearanceRaw, governor, ref allocator);
                string name = UniqueFxoName(taken, ref suffix);
                string content = AppearanceContent(name, annot, appearanceDict, (int)rotate, required, forbidden, resolver);
                if (content.Length == 0)
                {
                    changed = true;
                    continue;
                }

                resources = AddXObjectEntry(resources, name, appearanceLink, resolver, governor);
                AppendAscii(stamped, content);
                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            byte[] prefix = Encoding.ASCII.GetBytes("q\n");
            byte[] suffixBytes = stamped.Count == 0
                ? Encoding.ASCII.GetBytes("\nQ\n")
                : JoinTwo(Encoding.ASCII.GetBytes("\nQ\n"), [.. stamped], governor);
            PdfStream before = EncodeContent(prefix, compress, level, limits, governor);
            PdfStream after = EncodeContent(suffixBytes, compress, level, limits, governor);
            PdfReference beforeLink = copier.AddIndirectObjectWithId(before, allocator);
            allocator = checked(allocator + 1);
            PdfReference afterLink = copier.AddIndirectObjectWithId(after, allocator);
            allocator = checked(allocator + 1);
            List<QpdfObject>? entries = ReadContentEntries(page, resolver);
            if (entries is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten annotations with unreadable content; keeping the page as is.");
                continue;
            }

            List<QpdfObject> stacked = new(entries.Count + 2)
            {
                beforeLink,
            };
            stacked.AddRange(entries);
            stacked.Add(afterLink);
            Dictionary<PdfName, QpdfObject> rebuilt = new();
            foreach (PdfName key in page.Keys)
            {
                if (key.DisplayText == "Contents" || key.DisplayText == "Annots" || key.DisplayText == "Resources")
                {
                    continue;
                }

                if (page.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    rebuilt[key] = found;
                }
            }

            governor.ReserveMemory(192L);
            rebuilt[PdfName.FromAscii("Contents")] = new PdfArray(stacked);
            rebuilt[PdfName.FromAscii("Resources")] = resources;
            if (kept.Count == 0)
            {
                copier.ReplaceCopiedObject(pageId, new PdfDictionary(rebuilt));
                continue;
            }

            PdfArray keptArray = new(kept);
            if (annotsTarget is PdfReference annotsReference2 && annotsRaw is PdfReference annotsOriginal)
            {
                copier.ReplaceCopiedObject(annotsOriginal.ObjectNumber, keptArray);
                rebuilt[PdfName.FromAscii("Annots")] = annotsOriginal;
            }
            else
            {
                rebuilt[PdfName.FromAscii("Annots")] = keptArray;
            }

            copier.ReplaceCopiedObject(pageId, new PdfDictionary(rebuilt));
        }

        return allocator;
    }

    private static PdfDictionary? EnsureResources(
        GraphCopier copier,
        PdfDictionary page,
        PoolResolver resolver,
        ResourceGovernor governor)
    {
        if (page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject? raw) && raw is not null && raw is not PdfNull)
        {
            QpdfObject target = raw is PdfReference reference
                ? ResolveOrNull(resolver, reference) ?? raw
                : raw;
            if (target is PdfDictionary dictionary)
            {
                if (target is PdfReference)
                {
                    return dictionary;
                }

                return dictionary;
            }

            return null;
        }

        governor.ReserveMemory(64L);
        return new PdfDictionary(new Dictionary<PdfName, QpdfObject>());
    }

    private static HashSet<string> CollectTakenNames(PdfDictionary resources, PoolResolver resolver)
    {
        HashSet<string> taken = new(StringComparer.Ordinal);
        if (ResolveXObjects(resources, resolver) is PdfDictionary xobjects)
        {
            foreach (PdfName key in xobjects.Keys)
            {
                taken.Add(key.DisplayText);
            }
        }

        return taken;
    }

    private static string UniqueFxoName(HashSet<string> taken, ref int suffix)
    {
        while (true)
        {
            string candidate = "Fxo" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix += 1;
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static PdfDictionary AddXObjectEntry(
        PdfDictionary resources,
        string name,
        PdfReference link,
        PoolResolver resolver,
        ResourceGovernor governor)
    {
        Dictionary<PdfName, QpdfObject> xobjects = new();
        if (ResolveXObjects(resources, resolver) is PdfDictionary existing)
        {
            foreach (PdfName key in existing.Keys)
            {
                if (existing.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    xobjects[key] = found;
                }
            }
        }

        governor.ReserveMemory(64L);
        xobjects[new PdfName(Encoding.ASCII.GetBytes(name))] = link;
        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in resources.Keys)
        {
            if (resources.TryGet(key, out QpdfObject? found) && found is not null)
            {
                entries[key] = found;
            }
        }

        governor.ReserveMemory(64L);
        entries[PdfName.FromAscii("XObject")] = new PdfDictionary(xobjects);
        return new PdfDictionary(entries);
    }

    private static bool HasAppearanceDictionary(PdfDictionary annot)
    {
        return annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? ap) && ap is not null && ap is not PdfNull;
    }


    private static PdfStream? FindAppearanceStream(PdfDictionary annot, PoolResolver resolver, out PdfReference? link)
    {
        link = null;
        if (!annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? ap) || ap is null || ap is PdfNull)
        {
            return null;
        }

        PdfReference? apLink = ap as PdfReference;
        QpdfObject apTarget = ap is PdfReference apReference
            ? ResolveOrNull(resolver, apReference) ?? ap
            : ap;
        if (apTarget is not PdfDictionary apDict)
        {
            return null;
        }

        if (!apDict.TryGet(PdfName.FromAscii("N"), out QpdfObject? normal) || normal is null || normal is PdfNull)
        {
            return null;
        }

        PdfReference? normalLink = normal as PdfReference;
        QpdfObject normalTarget = normal is PdfReference normalReference
            ? ResolveOrNull(resolver, normalReference) ?? normal
            : normal;
        if (normalTarget is PdfStream direct)
        {
            link = normalLink;
            return direct;
        }

        if (normalTarget is not PdfDictionary states)
        {
            return null;
        }

        string state = string.Empty;
        if (annot.TryGet(PdfName.FromAscii("AS"), out QpdfObject? stateRaw) && stateRaw is PdfName stateName)
        {
            state = stateName.DisplayText;
        }

        if (state.Length == 0)
        {
            return null;
        }

        foreach (PdfName key in states.Keys)
        {
            if (key.DisplayText != state)
            {
                continue;
            }

            if (!states.TryGet(key, out QpdfObject? found) || found is null)
            {
                return null;
            }

            QpdfObject stateTarget = found is PdfReference foundReference
                ? ResolveOrNull(resolver, foundReference) ?? found
                : found;
            if (stateTarget is PdfStream stateStream)
            {
                link = found as PdfReference;
                return stateStream;
            }

            return null;
        }

        return null;
    }

    private static (PdfReference Link, PdfDictionary Dictionary) EnsureAppearanceObject(
        GraphCopier copier,
        PdfStream stream,
        PdfReference? link,
        ResourceGovernor governor,
        ref int allocator)
    {
        bool isForm = stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtype)
            && subtype is PdfName subtypeName
            && subtypeName.DisplayText == "Form";
        if (link is not null)
        {
            if (!isForm)
            {
                Dictionary<PdfName, QpdfObject> entries = new();
                foreach (PdfName key in stream.Dictionary.Keys)
                {
                    if (stream.Dictionary.TryGet(key, out QpdfObject? found) && found is not null)
                    {
                        entries[key] = found;
                    }
                }

                governor.ReserveMemory(64L);
                entries[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form");
                copier.ReplaceCopiedObject(link.ObjectNumber, new PdfStream(new PdfDictionary(entries), stream.Data));
            }

            Dictionary<PdfName, QpdfObject> current = new();
            foreach (PdfName key in stream.Dictionary.Keys)
            {
                if (stream.Dictionary.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    current[key] = found;
                }
            }

            current[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form");
            return (link, new PdfDictionary(current));
        }

        Dictionary<PdfName, QpdfObject> fresh = new();
        foreach (PdfName key in stream.Dictionary.Keys)
        {
            if (stream.Dictionary.TryGet(key, out QpdfObject? found) && found is not null)
            {
                fresh[key] = found;
            }
        }

        governor.ReserveMemory(64L);
        fresh[PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Form");
        PdfDictionary dictionary = new(fresh);
        PdfReference freshLink = copier.AddIndirectObjectWithId(new PdfStream(dictionary, stream.Data), allocator);
        allocator = checked(allocator + 1);
        return (freshLink, dictionary);
    }

    private static string AppearanceContent(
        string name,
        PdfDictionary annot,
        PdfDictionary appearance,
        int rotate,
        int required,
        int forbidden,
        PoolResolver resolver)
    {
        long flags = 0L;
        if (annot.TryGet(PdfName.FromAscii("F"), out QpdfObject? flagsRaw))
        {
            QpdfObject flagsTarget = flagsRaw is PdfReference flagsReference
                ? ResolveOrNull(resolver, flagsReference) ?? flagsRaw
                : flagsRaw;
            if (flagsTarget is PdfInteger flagsInteger)
            {
                flags = flagsInteger.Value;
            }
        }

        if ((flags & forbidden) != 0L)
        {
            return string.Empty;
        }

        if ((flags & required) != required)
        {
            return string.Empty;
        }

        double[]? box = ReadBox(appearance, "BBox", resolver);
        double[]? rect = ReadBox(annot, "Rect", resolver);
        if (box is null || rect is null)
        {
            return string.Empty;
        }

        double[] matrix = IdentityMatrix();
        if (appearance.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject? matrixRaw) && matrixRaw is not null && matrixRaw is not PdfNull)
        {
            QpdfObject matrixTarget = matrixRaw is PdfReference matrixReference
                ? ResolveOrNull(resolver, matrixReference) ?? matrixRaw
                : matrixRaw;
            double[]? parsed = ReadMatrix(matrixTarget);
            if (parsed is not null)
            {
                matrix = parsed;
            }
        }

        bool doRotate = rotate != 0 && (flags & FlagNoRotate) != 0L;
        if (doRotate)
        {
            matrix = ConcatMatrix(Rotate90Matrix(rotate), matrix);
            rect = RotateRect(rect, rotate);
        }

        double[] moved = TransformRect(box, matrix);
        double movedWidth = moved[2] - moved[0];
        double movedHeight = moved[3] - moved[1];
        if (movedWidth == 0.0 || movedHeight == 0.0)
        {
            return string.Empty;
        }

        double rectWidth = rect[2] - rect[0];
        double rectHeight = rect[3] - rect[1];
        double[] final = TranslateMatrix(rect[0], rect[1]);
        final = ConcatMatrix(final, ScaleMatrix(rectWidth / movedWidth, rectHeight / movedHeight));
        final = ConcatMatrix(final, TranslateMatrix(-moved[0], -moved[1]));
        if (doRotate)
        {
            final = ConcatMatrix(final, Rotate90Matrix(rotate));
        }

        return string.Concat("q\n", FormatMatrix(final), " cm\n/", name, " Do\nQ\n");
    }

    private static double[] RotateRect(double[] rect, int rotate)
    {
        double width = rect[2] - rect[0];
        double height = rect[3] - rect[1];
        if (rotate == 90)
        {
            return [rect[0], rect[3], rect[0] + height, rect[3] + width];
        }

        if (rotate == 180)
        {
            return [rect[0] - width, rect[3], rect[0], rect[3] + height];
        }

        return [rect[0] - height, rect[3] - width, rect[0], rect[3]];
    }

    private static double[] IdentityMatrix()
    {
        return [1.0, 0.0, 0.0, 1.0, 0.0, 0.0];
    }

    private static double[] TranslateMatrix(double tx, double ty)
    {
        return [1.0, 0.0, 0.0, 1.0, tx, ty];
    }

    private static double[] ScaleMatrix(double sx, double sy)
    {
        return [sx, 0.0, 0.0, sy, 0.0, 0.0];
    }

    private static double[] Rotate90Matrix(int rotate)
    {
        if (rotate == 90)
        {
            return [0.0, 1.0, -1.0, 0.0, 0.0, 0.0];
        }

        if (rotate == 180)
        {
            return [-1.0, 0.0, 0.0, -1.0, 0.0, 0.0];
        }

        if (rotate == 270)
        {
            return [0.0, -1.0, 1.0, 0.0, 0.0, 0.0];
        }

        return IdentityMatrix();
    }

    private static double[] ConcatMatrix(double[] first, double[] second)
    {
        double a = (first[0] * second[0]) + (first[2] * second[1]);
        double b = (first[1] * second[0]) + (first[3] * second[1]);
        double c = (first[0] * second[2]) + (first[2] * second[3]);
        double d = (first[1] * second[2]) + (first[3] * second[3]);
        double e = (first[0] * second[4]) + (first[2] * second[5]) + first[4];
        double f = (first[1] * second[4]) + (first[3] * second[5]) + first[5];
        return [a, b, c, d, e, f];
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

    private static double[]? ReadMatrix(QpdfObject value)
    {
        if (value is not PdfArray array || array.Count != 6)
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

    private static double[]? ReadBox(PdfDictionary dictionary, string key, PoolResolver resolver)
    {
        if (!dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject? raw) || raw is null || raw is PdfNull)
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

    private static List<QpdfObject>? ReadContentEntries(PdfDictionary page, PoolResolver resolver)
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
                if (item is PdfReference reference)
                {
                    QpdfObject? target = ResolveOrNull(resolver, reference);
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

    private static byte[] JoinTwo(byte[] first, byte[] second, ResourceGovernor governor)
    {
        governor.ReserveMemory(first.Length + second.Length);
        byte[] output = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, output, 0, first.Length);
        Buffer.BlockCopy(second, 0, output, first.Length, second.Length);
        return output;
    }

    private static void AppendAscii(List<byte> output, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        foreach (byte b in bytes)
        {
            output.Add(b);
        }
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

    private static PdfDictionary? ResolveXObjects(PdfDictionary resources, PoolResolver resolver)
    {
        if (!resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }

        QpdfObject target = raw is PdfReference reference
            ? ResolveOrNull(resolver, reference) ?? raw
            : raw;
        return target as PdfDictionary;
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

