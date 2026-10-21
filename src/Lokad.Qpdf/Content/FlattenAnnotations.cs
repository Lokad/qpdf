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
        Span<byte> nameBytes = stackalloc byte[16];
        foreach ((GraphCopier copier, int pageId) in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
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

            PdfDictionary? resources = EnsureResources(page, resolver);
            if (resources is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten annotations without page resources; keeping annotations as is.");
                continue;
            }

            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
            List<QpdfObject>? entries = ReadContentEntries(page, resolver, graph.Memory, governor, cancellationToken);
            if (entries is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten annotations with unreadable content; keeping the page as is.");
                continue;
            }
            // One live set of fixed-size matrices and one formatted scalar at a
            // time. Growing content and resource containers are admitted separately.
            graph.Memory.Reserve(2048L);
            PdfNameTable<QpdfObject> xobjects = ResourceRebuild.CreateEntries(graph, out long resourceAllocation);
            if (ResolveXObjects(resources, resolver) is PdfDictionary existing)
            {
                using PdfDictionary.EntryEnumerator source = existing.GetEntryEnumerator();
                while (source.MoveNext(governor, cancellationToken))
                { xobjects.Assign(graph.Memory, governor, source.Current.Key, source.Current.Value, cancellationToken); }
            }
            int suffix = 1;
            List<QpdfObject> kept = ParserContainers.CreateList(graph.Memory);
            using GovernedBufferStream stamped = new(governor, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes), cancellationToken, 256);
            stamped.Write("\nQ\n"u8);
            bool changed = false;
            foreach (QpdfObject item in annots.Items)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                QpdfObject entryTarget = item is PdfReference itemReference ? ResolveOrNull(resolver, itemReference) ?? item : item;
                if (entryTarget is not PdfDictionary annot)
                { ParserContainers.AddItem(graph.Memory, kept, item, IntPtr.Size); continue; }
                bool isWidget = annot.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtypeRaw)
                    && subtypeRaw is PdfName subtype && PdfDictionaryLookup.NameEquals(subtype, "Widget"u8, governor, cancellationToken);
                if (needAppearances && isWidget)
                { ParserContainers.AddItem(graph.Memory, kept, item, IntPtr.Size); continue; }

                PdfStream? appearance = FindAppearanceStream(annot, resolver, out PdfReference? appearanceRaw);
                if (appearance is null)
                {
                    if (HasAppearanceDictionary(annot)) { changed = true; }
                    else { ParserContainers.AddItem(graph.Memory, kept, item, IntPtr.Size); }
                    continue;
                }
                string name = UniqueFxoName(xobjects, ref suffix, governor, cancellationToken);
                changed = true;
                if (!AppendAppearance(name, annot, appearance.Dictionary, (int)rotate, required, forbidden, resolver, stamped)) { continue; }
                (PdfReference appearanceLink, _) = EnsureAppearanceObject(copier, appearance, appearanceRaw, governor, cancellationToken, ref allocator);
                int length = Encoding.ASCII.GetBytes(name.AsSpan(), nameBytes);
                PdfName key = PdfDictionaryRebuild.CreateName(graph, nameBytes[..length]);
                xobjects.Assign(graph.Memory, governor, key, appearanceLink, cancellationToken);
            }
            if (!changed) { continue; }

            PdfDictionary outputResources = resources;
            if (stamped.Length > 3)
            {
                PdfDictionary xobjectDictionary = ResourceRebuild.Complete(graph, xobjects, resourceAllocation);
                outputResources = PdfDictionaryRebuild.ReplaceEntry(graph, resources, "XObject"u8, xobjectDictionary);
            }
            else { graph.UpdateBytes(resourceAllocation, 0L); }
            // Both payloads are admitted once and transferred into immutable streams.
            graph.Memory.Reserve(34L); byte[] prefix = "q\n"u8.ToArray(); graph.Add(34L);
            byte[] suffixBytes = stamped.ToArray(graph.Memory, cancellationToken);
            graph.Add(32L + suffixBytes.Length);
            PdfStream before = ContentTransforms.EncodeContent(prefix, compress, level, limits, graph);
            PdfStream after = ContentTransforms.EncodeContent(suffixBytes, compress, level, limits, graph);
            PdfReference beforeLink = copier.AddIndirectObjectWithId(before, allocator); allocator = checked(allocator + 1);
            PdfReference afterLink = copier.AddIndirectObjectWithId(after, allocator); allocator = checked(allocator + 1);
            List<QpdfObject> stacked = ParserContainers.CreateList(graph.Memory);
            ParserContainers.AddItem(graph.Memory, stacked, beforeLink, IntPtr.Size);
            foreach (QpdfObject entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(graph.Memory, stacked, entry, IntPtr.Size);
            }
            ParserContainers.AddItem(graph.Memory, stacked, afterLink, IntPtr.Size);
            PdfArray contents = ParserContainers.CopyArray(graph.Memory, stacked); graph.Add(64L + IntPtr.Size * (long)stacked.Count);
            PdfNameTable<QpdfObject> rebuilt = PdfDictionaryRebuild.CreateEntries(graph, out long pageAllocation);
            using PdfDictionary.EntryEnumerator pageEntries = page.GetEntryEnumerator();
            while (pageEntries.MoveNext(governor, cancellationToken))
            {
                PdfName key = pageEntries.Current.Key;
                if (PdfDictionaryLookup.NameEquals(key, "Contents"u8, governor, cancellationToken)
                    || PdfDictionaryLookup.NameEquals(key, "Resources"u8, governor, cancellationToken)
                    || PdfDictionaryLookup.NameEquals(key, "Annots"u8, governor, cancellationToken)) { continue; }
                rebuilt.Assign(graph.Memory, governor, key, pageEntries.Current.Value, cancellationToken);
            }
            rebuilt.Assign(graph.Memory, governor, PdfName.FromAscii("Contents"), contents, cancellationToken);
            rebuilt.Assign(graph.Memory, governor, PdfName.FromAscii("Resources"), outputResources, cancellationToken);
            if (kept.Count > 0)
            {
                PdfArray keptArray = ParserContainers.CopyArray(graph.Memory, kept); graph.Add(64L + IntPtr.Size * (long)kept.Count);
                rebuilt.Assign(graph.Memory, governor, PdfName.FromAscii("Annots"), keptArray, cancellationToken);
            }
            PdfDictionary result = PdfDictionaryRebuild.Complete(graph, rebuilt, pageAllocation);
            copier.ReplaceCopiedObject(pageId, result); graph.Retain();
        }

        return allocator;
    }

    private static readonly PdfDictionary EmptyResources = new(new Dictionary<PdfName, QpdfObject>());

    private static PdfDictionary? EnsureResources(PdfDictionary page, PoolResolver resolver)
    {
        if (!page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        { return EmptyResources; }
        return (raw is PdfReference reference ? ResolveOrNull(resolver, reference) : raw) as PdfDictionary;
    }

    private static string UniqueFxoName(PdfNameTable<QpdfObject> taken, ref int suffix, ResourceGovernor governor, CancellationToken token)
    {
        Span<byte> bytes = stackalloc byte[16];
        while (true)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            string candidate = "Fxo" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix = checked(suffix + 1);
            int length = Encoding.ASCII.GetBytes(candidate.AsSpan(), bytes);
            if (!taken.TryGetValue(bytes[..length], governor, token, out _)) { return candidate; }
        }
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

    internal static (PdfReference Link, PdfDictionary Dictionary) EnsureAppearanceObject(
        GraphCopier copier,
        PdfStream stream,
        PdfReference? link,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool isForm = stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? subtype)
            && subtype is PdfName subtypeName
            && subtypeName.DisplayText == "Form";
        if (isForm && link is not null) { return (link, stream.Dictionary); }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
        PdfDictionary header = isForm ? stream.Dictionary
            : PdfDictionaryRebuild.ReplaceEntry(graph, stream.Dictionary, "Subtype"u8, PdfName.FromAscii("Form"));
        PdfStream result = stream;
        if (!isForm)
        { graph.Memory.Reserve(32L); result = stream.WithDictionary(header); graph.Add(32L); }
        if (link is null)
        { link = copier.AddIndirectObjectWithId(result, allocator); allocator = checked(allocator + 1); }
        else { copier.ReplaceCopiedObject(link.ObjectNumber, result); }
        graph.Retain(); return (link, header);
    }

    private static bool AppendAppearance(
        string name,
        PdfDictionary annot,
        PdfDictionary appearance,
        int rotate,
        int required,
        int forbidden,
        PoolResolver resolver,
        GovernedBufferStream output)
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
            return false;
        }

        if ((flags & required) != required)
        {
            return false;
        }

        double[]? box = ReadBox(appearance, "BBox", resolver);
        double[]? rect = ReadBox(annot, "Rect", resolver);
        if (box is null || rect is null)
        {
            return false;
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
            return false;
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

        output.Write("q\n"u8);
        for (int i = 0; i < final.Length; i++)
        {
            if (i > 0) { output.WriteByte((byte)' '); }
            AppendAscii(output, FormatDouble(final[i]));
        }
        output.Write(" cm\n/"u8); AppendAscii(output, name); output.Write(" Do\nQ\n"u8);
        return true;
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

    private static List<QpdfObject>? ReadContentEntries(PdfDictionary page, PoolResolver resolver, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (!page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null || contents is PdfNull)
        {
            return ParserContainers.CreateList(memory);
        }

        QpdfObject targetContents = contents is PdfReference contentsReference ? ResolveOrNull(resolver, contentsReference) ?? contents : contents;
        if (targetContents is PdfArray array)
        {
            List<QpdfObject> entries = ParserContainers.CreateList(memory);
            foreach (QpdfObject item in array.Items)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
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

                ParserContainers.AddItem(memory, entries, item, IntPtr.Size);
            }

            return entries;
        }

        if (contents is PdfReference singleReference)
        {
            QpdfObject? target = ResolveOrNull(resolver, singleReference);
            if (target is not PdfStream) { return null; }
            List<QpdfObject> entries = ParserContainers.CreateList(memory);
            ParserContainers.AddItem(memory, entries, contents, IntPtr.Size); return entries;
        }

        if (contents is not PdfStream) { return null; }
        List<QpdfObject> direct = ParserContainers.CreateList(memory);
        ParserContainers.AddItem(memory, direct, contents, IntPtr.Size); return direct;
    }

    private static void AppendAscii(GovernedBufferStream output, string text)
    {
        Span<byte> bytes = stackalloc byte[256];
        for (int at = 0; at < text.Length; at += Math.Min(bytes.Length, text.Length - at))
        {
            int length = Encoding.ASCII.GetBytes(text.AsSpan(at, Math.Min(bytes.Length, text.Length - at)), bytes);
            output.Write(bytes[..length]);
        }
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

