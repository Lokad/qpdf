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
    internal enum AppearanceFit { Invalid, Empty, Painted }

    private const int FlagInvisible = 1;

    private const int FlagHidden = 2;

    private const int FlagPrint = 4;

    private const int FlagNoRotate = 16;

    private const int FlagNoView = 32;

    internal static bool NeedsAppearances(IReadOnlyList<AcroFormCopy.FieldImport> imports,
        ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        foreach (AcroFormCopy.FieldImport import in imports)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (import.Part.NeedAppearances) { return true; }
        }
        return false;
    }

    internal static bool NeedsAppearances(DocumentEngine engine, ResourceGovernor governor, CancellationToken token)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (!engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) || root is null || root is PdfNull)
        {
            return false;
        }

        if (CatalogCopy.ResolveDictionary(root, engine, governor, token) is not PdfDictionary catalog)
        {
            return false;
        }

        if (!catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? form) || form is null || form is PdfNull)
        {
            return false;
        }

        if (CatalogCopy.ResolveDictionary(form, engine, governor, token) is not PdfDictionary formDict)
        {
            return false;
        }

        return PdfDictionaryLookup.TryGet(formDict, "NeedAppearances"u8, governor, token, out QpdfObject need)
            && CatalogCopy.ResolveValue(need, engine, governor, token) is PdfBoolean flag && flag.Value;
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
        int forbidden = FlagInvisible | FlagHidden | (mode == "screen" ? FlagNoView : 0);
        bool warned = false;
        bool warnedOptionalContent = false;
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
            page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject? rotateRaw);
            if (rotateRaw is PdfReference rotateReference)
            { PdfObjectLookup.TryResolve(rotateReference, copier.CopiedObjects, governor, cancellationToken, out rotateRaw); }
            if (rotateRaw is PdfInteger rotateInteger)
            {
                rotate = ((rotateInteger.Value % 360L) + 360L) % 360L;
            }

            PdfDictionary? resources = EnsureResources(page, resolver);
            if (resources is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten annotations without page resources; keeping annotations as is.");
                continue;
            }

            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
            using ContentEntries? entries = page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject rawContents) && rawContents is not PdfNull
                ? ContentEntries.TryRead(rawContents, resolver, governor, cancellationToken) : ContentEntries.Create(governor, cancellationToken);
            if (entries is null)
            {
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Cannot flatten annotations with unreadable content; keeping the page as is.");
                continue;
            }
            // One live set of fixed-size matrices and one formatted scalar at a
            // time. Growing content and resource containers are admitted separately.
            graph.Memory.Reserve(2048L);
            PdfNameTable<QpdfObject> xobjects = PdfDictionaryRebuild.CreateEntries(graph, out long resourceAllocation);
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
            for (int index = 0; index < annots.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                QpdfObject item = annots[index];
                QpdfObject entryTarget = item is PdfReference itemReference ? ResolveOrNull(resolver, itemReference) ?? item : item;
                if (entryTarget is not PdfDictionary annot)
                { ParserContainers.AddItem(graph.Memory, kept, item, IntPtr.Size); continue; }
                bool isWidget = PdfDictionaryLookup.HasName(annot, "Subtype"u8, "Widget"u8, resolver, governor, cancellationToken);
                if (needAppearances && isWidget)
                { ParserContainers.AddItem(graph.Memory, kept, item, IntPtr.Size); continue; }
                if (PdfDictionaryLookup.TryGet(annot, "OC"u8, governor, cancellationToken, out QpdfObject condition)
                    && CatalogCopy.ResolveValue(condition, resolver, governor, cancellationToken) is not (null or PdfNull))
                {
                    ParserContainers.AddItem(graph.Memory, kept, item, IntPtr.Size);
                    if (!warnedOptionalContent)
                    {
                        warnedOptionalContent = true;
                        diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning,
                            "Cannot flatten annotations with optional-content conditions; keeping those annotations.");
                    }
                    continue;
                }

                PdfStream? appearance = FindAppearanceStream(annot, resolver, governor, cancellationToken, out PdfReference? appearanceRaw);
                if (appearance is null)
                {
                    if (HasAppearanceDictionary(annot)) { changed = true; }
                    else { ParserContainers.AddItem(graph.Memory, kept, item, IntPtr.Size); }
                    continue;
                }
                string name = UniqueFxoName(xobjects, ref suffix, governor, cancellationToken);
                changed = true;
                if (AppendAppearance(name, annot, appearance.Dictionary, (int)rotate, required, forbidden, resolver, stamped) != AppearanceFit.Painted) { continue; }
                PdfReference appearanceLink = EnsureAppearanceObject(copier, appearance, appearanceRaw, resolver, governor, cancellationToken, ref allocator);
                int length = Encoding.ASCII.GetBytes(name.AsSpan(), nameBytes);
                PdfName key = PdfDictionaryRebuild.CreateName(graph, nameBytes[..length]);
                xobjects.Assign(graph.Memory, governor, key, appearanceLink, cancellationToken);
            }
            if (!changed) { continue; }

            PdfDictionary outputResources = resources;
            if (stamped.Length > 3)
            {
                PdfDictionary xobjectDictionary = PdfDictionaryRebuild.Complete(graph, xobjects, resourceAllocation);
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
            int count = checked(entries.Count + 2);
            long arrayBytes = checked(32L + IntPtr.Size * (long)count);
            graph.Memory.Reserve(arrayBytes); QpdfObject[] stacked = new QpdfObject[count]; graph.Add(arrayBytes);
            stacked[0] = beforeLink; stacked[^1] = afterLink;
            for (int index = 0; index < entries.Count; index++) { stacked[index + 1] = entries.Get(index).Value; }
            graph.Memory.Reserve(24L); PdfArray contents = PdfArray.FromOwnedItems(stacked); graph.Add(24L);
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


    internal static PdfStream? FindAppearanceStream(PdfDictionary annot, PoolResolver resolver, ResourceGovernor governor, CancellationToken token, out PdfReference? link)
    {
        link = null;
        if (!annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject? ap) || ap is null || ap is PdfNull)
        {
            return null;
        }

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

        if (!PdfDictionaryLookup.TryGet(annot, "AS"u8, governor, token, out QpdfObject stateRaw)
            || CatalogCopy.ResolveValue(stateRaw, resolver, governor, token) is not PdfName state
            || !PdfDictionaryLookup.TryGet(states, state.RawSpan, governor, token, out QpdfObject found)) { return null; }
        QpdfObject target = found is PdfReference reference ? ResolveOrNull(resolver, reference) ?? found : found;
        if (target is not PdfStream stream) { return null; }
        link = found as PdfReference; return stream;
    }

    internal static PdfReference EnsureAppearanceObject(
        GraphCopier copier,
        PdfStream stream,
        PdfReference? link,
        PoolResolver resolver,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        ref int allocator)
    {
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool isForm = PdfDictionaryLookup.HasName(stream.Dictionary, "Subtype"u8, "Form"u8, resolver, governor, cancellationToken);
        if (isForm && link is not null) { return link; }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
        PdfDictionary header = isForm ? stream.Dictionary
            : PdfDictionaryRebuild.ReplaceEntry(graph, stream.Dictionary, "Subtype"u8, PdfName.FromAscii("Form"));
        PdfStream result = stream;
        if (!isForm)
        { graph.Memory.Reserve(32L); result = stream.WithDictionary(header); graph.Add(32L); }
        // Normalization belongs to this appearance edge. Other roots can retain
        // the original stream header, including an indirect Subtype value.
        PdfReference normalized = copier.AddIndirectObjectWithId(result, allocator); allocator = checked(allocator + 1);
        graph.Retain(); return normalized;
    }

    internal static AppearanceFit AppendAppearance(
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
            return AppearanceFit.Invalid;
        }

        if ((flags & required) != required)
        {
            return AppearanceFit.Invalid;
        }

        double[]? box = ReadBox(appearance, "BBox", resolver);
        double[]? rect = ReadBox(annot, "Rect", resolver);
        if (box is null || rect is null)
        {
            return AppearanceFit.Invalid;
        }
        if (rect[0] > rect[2]) { (rect[0], rect[2]) = (rect[2], rect[0]); }
        if (rect[1] > rect[3]) { (rect[1], rect[3]) = (rect[3], rect[1]); }

        double[] matrix = IdentityMatrix();
        if (appearance.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject? matrixRaw) && matrixRaw is not null && matrixRaw is not PdfNull)
        {
            QpdfObject matrixTarget = matrixRaw is PdfReference matrixReference
                ? ResolveOrNull(resolver, matrixReference) ?? matrixRaw
                : matrixRaw;
            double[]? parsed = PdfGeometry.ReadFiniteNumbers(matrixTarget, 6, resolver);
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

        Span<double> moved = stackalloc double[4];
        PdfGeometry.TransformRect(box, matrix, moved);
        double movedWidth = moved[2] - moved[0];
        double movedHeight = moved[3] - moved[1];
        if (movedWidth == 0.0 || movedHeight == 0.0)
        {
            return AppearanceFit.Empty;
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
        return AppearanceFit.Painted;
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
        double[] result = new double[6];
        PdfGeometry.Concat(first, second, result);
        return result;
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

        return PdfGeometry.ReadFiniteNumbers(array, 4, resolver);
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
        Span<char> text = stackalloc char[384];
        int length = WriterNumber.FormatGeometry(value, text);
        return new string(text[..length]);
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

