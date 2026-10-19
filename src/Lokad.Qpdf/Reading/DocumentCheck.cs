using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Reading;

/// <summary>
/// Partial structural inspection over an opened document. Stages that ran report
/// passed or failed; stages whose prerequisites are unimplemented report skipped
/// with an explicit reason, never silently passed.
/// </summary>
internal static class DocumentCheck
{
    private const int MaxObjectFailures = 16;
    private const int MaxStreamFailures = 8;
    private const int MaxFormWarnings = 8;
    private const int MaxFormDepth = 32;

    internal static List<QpdfCheckOutcome> Run(
        QpdfDocument document,
        QpdfCheckOptions options,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        bool nativeStreamTraversal,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        DocumentEngine engine = document.Engine;
        using ParserMemoryScope outcomeMemory = ParserMemoryScope.Create(governor);
        List<QpdfCheckOutcome> outcomes = ParserContainers.CreateList<QpdfCheckOutcome>(outcomeMemory);
        AddItem(outcomeMemory, governor, outcomes, CheckOutcome.Create(governor, "header", QpdfCheckStatus.Passed,
            ["Version ", document.EffectiveVersionParts.Major, ".", document.EffectiveVersionParts.Minor, "."]), IntPtr.Size);
        AddItem(outcomeMemory, governor, outcomes, CheckOutcome.Create(governor, "cross-reference", QpdfCheckStatus.Passed,
            [engine.XRefEntries.Count, " entries cover the effective size."]), IntPtr.Size);
        AddItem(outcomeMemory, governor, outcomes, CheckOutcome.Literal(governor, "trailer", QpdfCheckStatus.Passed,
            "Catalog root is reachable and input is unencrypted."), IntPtr.Size);
        AddItem(outcomeMemory, governor, outcomes, document.PageCount == 0
            ? CheckOutcome.Literal(governor, "page-tree", QpdfCheckStatus.Failed, "File does not contain any pages.")
            : CheckOutcome.Create(governor, "page-tree", QpdfCheckStatus.Passed, [document.PageCount, " pages traversed."]), IntPtr.Size);

        using ParserMemoryScope streamMemory = ParserMemoryScope.Create(governor);
        List<(int Number, PdfStream Stream)> streams = ParserContainers.CreateList<(int, PdfStream)>(streamMemory);
        QpdfCheckOutcome objectGraph = CheckObjectGraph(engine, governor, streams, streamMemory, cancellationToken);
        AddItem(outcomeMemory, governor, outcomes, objectGraph, IntPtr.Size);
        if (nativeStreamTraversal && objectGraph.Status != QpdfCheckStatus.Failed)
        {
            // Native checking writes the reachable graph to a discard sink;
            // unreferenced streams are not decoded by that writer traversal.
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            HashSet<int> reachable = GraphReachability.Find(engine, engine.Trailer, memory, governor);
            streams.RemoveAll(stream => !reachable.Contains(stream.Number));
        }
        AddItem(outcomeMemory, governor, outcomes, CheckStreams(engine, options.DecodeLevel, limits, governor, diagnostics, streams, cancellationToken), IntPtr.Size);
        AddItem(outcomeMemory, governor, outcomes, CheckPageContent(document, options, limits, governor, diagnostics, cancellationToken), IntPtr.Size);
        AddItem(outcomeMemory, governor, outcomes, CheckFormTree(document, governor, diagnostics, cancellationToken), IntPtr.Size);
        AddItem(outcomeMemory, governor, outcomes, LinearizationCheck.Check(engine, document.PageCount, limits, governor, cancellationToken), IntPtr.Size);
        foreach (QpdfCheckOutcome outcome in outcomes)
        {
            if (outcome.Status == QpdfCheckStatus.Failed)
            {
                diagnostics.AddParts(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, ["Check '", outcome.Name, "' failed: ", outcome.Detail]);
            }
        }

        outcomeMemory.RetainGraph();
        return outcomes;
    }

    private static void AddItem<T>(ParserMemoryScope memory, ResourceGovernor governor, List<T> items, T value, long itemBytes)
    {
        governor.NoteOperation();
        if (items.Count == items.Capacity)
        {
            long capacity = Math.Min(Array.MaxLength, Math.Max(4L, items.Capacity * 2L));
            governor.CheckCancellation();
        }
        ParserContainers.AddItem(memory, items, value, itemBytes);
    }

    private static void AddFailure(DisplayBuffer detail, ref int count, ReadOnlySpan<DiagnosticPart> parts)
    {
        if (count != 0) { detail.AppendText(" "); }
        detail.AppendParts(parts);
        count++;
    }

    private static QpdfCheckOutcome FinishOutcome(ResourceGovernor governor, string name, QpdfCheckStatus status, DisplayBuffer detail)
    {
        using GovernedText text = detail.Finish();
        return CheckOutcome.Create(governor, name, status, text);
    }

    private static QpdfCheckOutcome CheckObjectGraph(
        DocumentEngine engine,
        ResourceGovernor governor,
        List<(int Number, PdfStream Stream)> streams,
        ParserMemoryScope streamMemory,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<int> numbers = ParserContainers.CreateList<int>(memory);
        foreach (int number in engine.XRefEntries.Keys)
        {
            governor.NoteOperation();
            ParserContainers.AddItem(memory, numbers, number, sizeof(int));
        }
        foreach (int number in engine.InspectionObjectNumbers)
        {
            governor.NoteOperation();
            if (!engine.XRefEntries.ContainsKey(number)) { ParserContainers.AddItem(memory, numbers, number, sizeof(int)); }
        }
        numbers.Sort();
        int resolved = 0;
        using DisplayBuffer failures = DisplayBuffer.Create(governor, false);
        int failureCount = 0;
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            try
            {
                QpdfObject value = engine.Load(number);
                resolved += 1;
                if (value is PdfStream stream)
                {
                    AddItem(streamMemory, governor, streams, (number, stream), 16L);
                }
            }
            catch (QpdfLimitException ex)
            {
                AddFailure(failures, ref failureCount, ["object ", number, ": ", ex.Message]);
                break;
            }
            catch (QpdfException ex) when (ex is not QpdfMemoryAdmissionException)
            {
                AddFailure(failures, ref failureCount, ["object ", number, ": ", ex.Message]);
                if (failureCount >= MaxObjectFailures)
                {
                    AddFailure(failures, ref failureCount, ["Further object failures are not recorded."]);
                    break;
                }
            }
        }

        if (failureCount > 0)
        {
            return FinishOutcome(governor, "object-graph", QpdfCheckStatus.Failed, failures);
        }

        return CheckOutcome.Create(governor, "object-graph", QpdfCheckStatus.Passed, [resolved, " objects resolved with ", streams.Count, " streams."]);
    }

    private static bool InLevel(string filter, QpdfStreamDecodeLevel level)
    {
        return level switch
        {
            QpdfStreamDecodeLevel.None => false,
            QpdfStreamDecodeLevel.Generalized => filter == FlateFilter.FilterName || filter == LzwFilter.FilterName
                || filter == AsciiHexFilter.FilterName || filter == Ascii85Filter.FilterName,
            QpdfStreamDecodeLevel.Specialized => IsDecodable(filter),
            QpdfStreamDecodeLevel.All => true,
            _ => false,
        };
    }

    private static bool IsDecodable(string filter)
    {
        return filter == FlateFilter.FilterName || filter == LzwFilter.FilterName
            || filter == AsciiHexFilter.FilterName || filter == Ascii85Filter.FilterName
            || filter == RunLengthFilter.FilterName;
    }

    private static QpdfCheckOutcome CheckStreams(
        DocumentEngine engine,
        QpdfStreamDecodeLevel level,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        List<(int Number, PdfStream Stream)> streams,
        CancellationToken cancellationToken)
    {
        if (level == QpdfStreamDecodeLevel.None)
        {
            diagnostics.Add(QpdfDiagnosticCode.CheckSkipped, QpdfDiagnosticSeverity.Info, "Skipped check 'streams': decode level none disables stream decoding.");
            return CheckOutcome.Literal(governor, "streams", QpdfCheckStatus.Skipped, "Decode level none disables stream decoding.");
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        int decoded = 0;
        int unfiltered = 0;
        int aboveLevel = 0;
        int skippedUnavailable = 0;
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<string> unavailable = ParserContainers.CreateList<string>(memory);
        using DisplayBuffer failures = DisplayBuffer.Create(governor, false);
        int failureCount = 0;
        foreach ((int number, PdfStream stream) in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FilterPlan stages;
            try
            {
                stages = FilterChain.Resolve(stream.Dictionary, resolver, governor, cancellationToken);
            }
            catch (QpdfParseException ex)
            {
                AddFailure(failures, ref failureCount, ["stream ", number, ": ", ex.Message]);
                if (failureCount >= MaxStreamFailures)
                {
                    break;
                }

                continue;
            }
            using FilterPlan stageOwner = stages;

            if (stages.Count == 0)
            {
                unfiltered += 1;
                continue;
            }

            bool attempt = true;
            foreach (FilterStage stage in stages)
            {
                if (!InLevel(stage.Name, level))
                {
                    attempt = false;
                    break;
                }
            }

            if (!attempt)
            {
                aboveLevel += 1;
                continue;
            }

            try
            {
                using ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor);
                _ = FilterChain.Decode(stream.ArrayData(decodedMemory, governor, cancellationToken), stages, limits, governor, decodedMemory, cancellationToken);
                decoded += 1;
            }
            catch (QpdfFilterException ex) when (ex.Reason == QpdfFilterFailureReason.UnsupportedFilter)
            {
                skippedUnavailable += 1;
                foreach (FilterStage stage in stages)
                {
                    if (!IsDecodable(stage.Name) && !ContainsUnavailable(governor, unavailable, stage.Name))
                    {
                        AddItem(memory, governor, unavailable, stage.Name, IntPtr.Size);
                    }
                }
            }
            catch (QpdfLimitException ex)
            {
                AddFailure(failures, ref failureCount, ["stream ", number, ": ", ex.Message]);
                break;
            }
            catch (QpdfException ex) when (ex is not QpdfMemoryAdmissionException)
            {
                AddFailure(failures, ref failureCount, ["stream ", number, ": ", ex.Message]);
                if (failureCount >= MaxStreamFailures)
                {
                    break;
                }
            }
        }

        if (failureCount > 0)
        {
            return FinishOutcome(governor, "streams", QpdfCheckStatus.Failed, failures);
        }

        using DisplayBuffer detail = DisplayBuffer.Create(governor, false);
        detail.AppendParts([decoded, " decoded, ", unfiltered, " unfiltered, ", aboveLevel, " above level ", DiagnosticPart.DecodeLevel(level), "."]);
        if (skippedUnavailable > 0)
        {
            detail.AppendParts([" ", skippedUnavailable, " skipped ("]);
            for (int index = 0; index < unavailable.Count; index++)
            {
                governor.NoteOperation();
                if (index != 0) { detail.AppendText(", "); }
                detail.AppendText(unavailable[index]);
            }
            detail.AppendText(" unavailable).");
            QpdfCheckOutcome outcome = FinishOutcome(governor, "streams", QpdfCheckStatus.Skipped, detail);
            diagnostics.AddParts(
                QpdfDiagnosticCode.CheckSkipped,
                QpdfDiagnosticSeverity.Warning,
                ["Skipped check 'streams': ", outcome.Detail, " Decoding unavailable filters arrives in a later milestone."]);
            return outcome;
        }

        if (aboveLevel > 0)
        {
            diagnostics.AddParts(
                QpdfDiagnosticCode.CheckSkipped,
                QpdfDiagnosticSeverity.Info,
                ["Left ", aboveLevel, " stream(s) above decode level ", DiagnosticPart.DecodeLevel(level), " for a higher level."]);
        }

        return FinishOutcome(governor, "streams", QpdfCheckStatus.Passed, detail);
    }

    private static bool ContainsUnavailable(ResourceGovernor governor, List<string> names, string name)
    {
        foreach (string candidate in names)
        {
            governor.CheckCancellation();
            if (candidate.Length != name.Length) { continue; }
            bool equal = true;
            for (int offset = 0; offset < name.Length; offset += Math.Min(256, name.Length - offset))
            {
                int count = Math.Min(256, name.Length - offset);
                governor.CheckCancellation();
                if (!candidate.AsSpan(offset, count).SequenceEqual(name.AsSpan(offset, count))) { equal = false; break; }
            }
            if (equal) { return true; }
        }
        return false;
    }

    private static QpdfCheckOutcome CheckPageContent(
        QpdfDocument document,
        QpdfCheckOptions options,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (options.DecodeLevel == QpdfStreamDecodeLevel.None)
        {
            diagnostics.Add(QpdfDiagnosticCode.CheckSkipped, QpdfDiagnosticSeverity.Info, "Skipped check page-content: decode level none disables stream decoding.");
            return CheckOutcome.Literal(governor, "page-content", QpdfCheckStatus.Skipped, "Decode level none disables stream decoding.");
        }

        DocumentEngine engine = document.Engine;
        IIndirectResolver resolver = (IIndirectResolver)engine;
        int examined = 0;
        int skipped = 0;
        using DisplayBuffer failures = DisplayBuffer.Create(governor, false);
        int failureCount = 0;
        int pageNumber = 0;
        IReadOnlyList<PageLocation> pages = document.PageReferences;
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            PageLocation location = pages[pageIndex];
            cancellationToken.ThrowIfCancellationRequested();
            pageNumber += 1;
            if (!PdfDictionaryLookup.TryGet(location.Page, "Contents"u8, governor, cancellationToken, out QpdfObject contents) || contents is null || contents is PdfNull)
            {
                continue;
            }

            using CheckStreamList streams = CheckStreamList.Create(governor);
            bool unreadable = false;
            if (contents is PdfArray array)
            {
                for (int itemIndex = 0; itemIndex < array.Count; itemIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    QpdfObject item = array[itemIndex];
                    QpdfObject target = item;
                    if (item is PdfReference reference)
                    {
                        if (!PdfObjectLookup.TryResolve(reference, resolver, governor, cancellationToken, out QpdfObject? resolved) || resolved is null)
                        {
                            unreadable = true;
                            break;
                        }

                        target = resolved;
                    }

                    if (target is not PdfStream stream)
                    {
                        unreadable = true;
                        break;
                    }

                    streams.Add(stream, cancellationToken);
                }
            }
            else if (contents is PdfReference singleReference)
            {
                if (!PdfObjectLookup.TryResolve(singleReference, resolver, governor, cancellationToken, out QpdfObject? resolved) || resolved is not PdfStream resolvedStream)
                {
                    unreadable = true;
                }
                else
                {
                    streams.Add(resolvedStream, cancellationToken);
                }
            }
            else if (contents is PdfStream single)
            {
                streams.Add(single, cancellationToken);
            }
            else
            {
                unreadable = true;
            }

            if (unreadable)
            {
                skipped += 1;
                continue;
            }

            for (int streamIndex = 0; streamIndex < streams.Count; streamIndex++)
            {
                PdfStream stream = streams.Get(streamIndex, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor);
                byte[]? bytes = DecodeCheckStream(stream, resolver, options.DecodeLevel, limits, governor, decodedMemory, cancellationToken);
                if (bytes is null)
                {
                    skipped += 1;
                    continue;
                }

                try
                {
                    TokenizeCheckStream(bytes, limits, governor, cancellationToken);
                    examined += 1;
                }
                catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
                {
                    AddFailure(failures, ref failureCount, ["Page ", DiagnosticPart.Invariant(pageNumber), " content: ", ex.Message]);
                    if (failureCount >= MaxStreamFailures)
                    {
                        break;
                    }
                }
            }

            if (failureCount >= MaxStreamFailures)
            {
                break;
            }
        }

        if (failureCount > 0)
        {
            return FinishOutcome(governor, "page-content", QpdfCheckStatus.Failed, failures);
        }

        return CheckOutcome.Create(governor, "page-content", QpdfCheckStatus.Passed,
            [DiagnosticPart.Invariant(examined), " content streams tokenized, ", DiagnosticPart.Invariant(skipped), " skipped."]);
    }

    private sealed class FormCheckState : IDisposable
    {
        private ParserMemoryScope? _memory;
        private ResourceGovernor? _governor;
        private GovernedIntSet? _visited;
        private GovernedIntSet? _widgets;

        private FormCheckState(ParserMemoryScope memory, ResourceGovernor governor)
        {
            _memory = memory; _governor = governor;
            // The owner is admitted before initial lookup. Explicit caller
            // cancellation is observed by that lookup after construction;
            // the governor's own token also bounds these allocations.
            _visited = GovernedIntSet.Create(memory, governor, CancellationToken.None);
            _widgets = GovernedIntSet.Create(memory, governor, CancellationToken.None);
        }

        internal static FormCheckState Create(ResourceGovernor governor)
        {
            ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            try { memory.Reserve(64L); return new(memory, governor); }
            catch { memory.Dispose(); throw; }
        }

        public int Nodes { get; set; }
        public int Warnings { get; set; }
        public bool Truncated { get; set; }
        private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(FormCheckState));
        private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(FormCheckState));
        private GovernedIntSet Visited => _visited ?? throw new ObjectDisposedException(nameof(FormCheckState));
        private GovernedIntSet Widgets => _widgets ?? throw new ObjectDisposedException(nameof(FormCheckState));
        internal int WidgetCount => Widgets.Count;

        internal bool Visit(int number, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Visited.CheckOwner(Governor); return Visited.Add(Memory, number); }
        internal void ReachWidget(int number, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Widgets.CheckOwner(Governor); Widgets.Add(Memory, number); }
        internal bool HasWidget(int number, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Widgets.Contains(number); }

        public void Dispose()
        {
            ParserMemoryScope? memory = _memory;
            _visited = null; _widgets = null; _memory = null; _governor = null;
            memory?.Dispose();
        }
    }
    /// <summary>
    /// Warn-only form-tree validation, mirroring the reference checker's three
    /// field findings: kids whose /Parent back-link does not name the node
    /// reached through /AcroForm, page widgets no field registration reaches,
    /// and field-like direct objects met while traversing /AcroForm (warned
    /// and ignored like the reference). Findings warn (exit 3 through the
    /// shared exit mapping) and never fail the check; the copy path keeps
    /// preserving these shapes verbatim, so this stage corrects nothing.
    /// Unresolvable references stop only their own branch silently: no oracle
    /// evidence pins a warning for them, and damaged trees stay lenient
    /// elsewhere.
    /// </summary>
    private static QpdfCheckOutcome CheckFormTree(
        QpdfDocument document,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        DocumentEngine engine = document.Engine;
        IIndirectResolver resolver = (IIndirectResolver)engine;
        using FormCheckState state = FormCheckState.Create(governor);
        if (!TryGetCheckForm(engine, resolver, governor, cancellationToken, out PdfDictionary? form) || form is null)
        {
            state.Dispose();
            return CheckOutcome.Literal(governor, "forms", QpdfCheckStatus.Passed, "No interactive form.");
        }

        if (!TryResolveCheckArray(form, "Fields"u8, resolver, governor, cancellationToken, out PdfArray? fields) || fields is null)
        {
            state.Dispose();
            return CheckOutcome.Literal(governor, "forms", QpdfCheckStatus.Passed, "Interactive form carries no field registrations.");
        }

        try
        {
            for (int i = 0; i < fields.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.CheckCancellation();
                if (fields[i] is PdfReference topReference)
                {
                    if (PdfObjectLookup.TryResolve(topReference, resolver, governor, cancellationToken, out QpdfObject? top) && top is PdfDictionary topDictionary)
                    {
                        WalkFieldNode(topDictionary, topReference.ObjectNumber, 0, resolver, governor, diagnostics, state, cancellationToken);
                    }
                }
                else if (fields[i] is PdfDictionary topDirect)
                {
                    WalkDirectNode(topDirect, 0, resolver, governor, diagnostics, state, cancellationToken);
                }
            }

            CollectUnreachedWidgets(document, resolver, governor, diagnostics, state, cancellationToken);
        }
        catch (QpdfLimitException ex)
        {
            state.Dispose();
            return CheckOutcome.Create(governor, "forms", QpdfCheckStatus.Failed, ["Form traversal exceeded its budget: ", ex.Message]);
        }

        int widgets = state.WidgetCount;
        state.Dispose();
        using DisplayBuffer detail = DisplayBuffer.Create(governor, false);
        detail.AppendParts([state.Nodes, " field nodes traversed, ", widgets, " widgets reached."]);
        if (state.Warnings > 0)
        {
            detail.AppendParts([" ", state.Warnings, " warnings recorded."]);
        }

        if (state.Truncated)
        {
            detail.AppendText(" Further findings are not recorded.");
        }

        return FinishOutcome(governor, "forms", QpdfCheckStatus.Passed, detail);
    }

    private static void WalkFieldNode(
        PdfDictionary node,
        int? nodeId,
        int depth,
        IIndirectResolver resolver,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        FormCheckState state,
        CancellationToken cancellationToken)
    {
        if (depth > MaxFormDepth)
        {
            return;
        }

        if (nodeId.HasValue && !state.Visit(nodeId.Value, cancellationToken))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        state.Nodes += 1;
        if (nodeId.HasValue && IsCheckWidget(node, governor, cancellationToken))
        {
            state.ReachWidget(nodeId.Value, cancellationToken);
        }

        if (!TryResolveCheckArray(node, "Kids"u8, resolver, governor, cancellationToken, out PdfArray? kids) || kids is null)
        {
            return;
        }

        for (int i = 0; i < kids.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            if (kids[i] is PdfReference kidReference)
            {
                if (PdfObjectLookup.TryResolve(kidReference, resolver, governor, cancellationToken, out QpdfObject? kid) && kid is PdfDictionary kidDictionary)
                {
                    if (nodeId.HasValue)
                    {
                        CheckParentLink(kidDictionary, kidReference.ObjectNumber, nodeId.Value, governor, diagnostics, state, cancellationToken);
                    }

                    WalkFieldNode(kidDictionary, kidReference.ObjectNumber, depth + 1, resolver, governor, diagnostics, state, cancellationToken);
                }
            }
            else if (kids[i] is PdfDictionary kidDirect)
            {
                WalkDirectNode(kidDirect, depth + 1, resolver, governor, diagnostics, state, cancellationToken);
            }
        }
    }

    private static void WalkDirectNode(
        PdfDictionary node,
        int depth,
        IIndirectResolver resolver,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        FormCheckState state,
        CancellationToken cancellationToken)
    {
        if (IsFieldLike(node, governor, cancellationToken))
        {
            AddFormWarning(diagnostics, state, ["Encountered a direct object as a field or annotation while traversing /AcroForm; ignoring."]);
            return;
        }

        WalkFieldNode(node, null, depth, resolver, governor, diagnostics, state, cancellationToken);
    }

    private static bool IsFieldLike(PdfDictionary node, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        return PdfDictionaryLookup.TryGet(node, "T"u8, governor, cancellationToken, out QpdfObject title) && title is not PdfNull
            || PdfDictionaryLookup.TryGet(node, "Kids"u8, governor, cancellationToken, out QpdfObject kids) && kids is not PdfNull
            || PdfDictionaryLookup.TryGet(node, "FT"u8, governor, cancellationToken, out QpdfObject fieldType) && fieldType is not PdfNull;
    }

    private static void CheckParentLink(
        PdfDictionary kid,
        int kidNumber,
        int parentNumber,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        FormCheckState state,
        CancellationToken cancellationToken)
    {
        bool valid = PdfDictionaryLookup.TryGet(kid, "Parent"u8, governor, cancellationToken, out QpdfObject parentRaw)
            && parentRaw is PdfReference parentReference
            && parentReference.ObjectNumber == parentNumber;
        if (valid)
        {
            return;
        }

        AddFormWarning(diagnostics, state, ["Field node ", kidNumber, " has an invalid /Parent entry while traversing /AcroForm."]);
    }

    private static void CollectUnreachedWidgets(
        QpdfDocument document,
        IIndirectResolver resolver,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        FormCheckState state,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PageLocation> pages = document.PageReferences;
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            PageLocation location = pages[pageIndex];
            cancellationToken.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            if (!PdfDictionaryLookup.TryGet(location.Page, "Annots"u8, governor, cancellationToken, out QpdfObject annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            QpdfObject annotsTarget = annotsRaw is PdfReference annotsReference
                && PdfObjectLookup.TryResolve(annotsReference, resolver, governor, cancellationToken, out QpdfObject? resolved)
                ? resolved ?? annotsRaw
                : annotsRaw;
            if (annotsTarget is not PdfArray annots)
            {
                continue;
            }

            for (int i = 0; i < annots.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.CheckCancellation();
                if (annots[i] is not PdfReference itemReference)
                {
                    continue;
                }

                if (state.HasWidget(itemReference.ObjectNumber, cancellationToken))
                {
                    continue;
                }

                if (!PdfObjectLookup.TryResolve(itemReference, resolver, governor, cancellationToken, out QpdfObject? target) || target is not PdfDictionary annot || !IsCheckWidget(annot, governor, cancellationToken))
                {
                    continue;
                }

                AddFormWarning(diagnostics, state, ["Widget annotation ", itemReference.ObjectNumber, " is not reachable from /AcroForm."]);
            }
        }
    }

    private static void AddFormWarning(DiagnosticBag diagnostics, FormCheckState state, ReadOnlySpan<DiagnosticPart> parts)
    {
        state.Warnings += 1;
        if (state.Warnings > MaxFormWarnings)
        {
            state.Truncated = true;
            return;
        }

        diagnostics.AddParts(QpdfDiagnosticCode.FormStructureWarning, QpdfDiagnosticSeverity.Warning, parts);
    }

    private static bool TryGetCheckForm(DocumentEngine engine, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken cancellationToken, out PdfDictionary? form)
    {
        form = null;
        if (!PdfDictionaryLookup.TryGet(engine.Trailer, "Root"u8, governor, cancellationToken, out QpdfObject root) || root is null || root is PdfNull)
        {
            return false;
        }

        PdfDictionary? catalog = root is PdfReference rootReference
            && PdfObjectLookup.TryResolve(rootReference, resolver, governor, cancellationToken, out QpdfObject? resolved)
            ? resolved as PdfDictionary
            : root as PdfDictionary;
        if (catalog is null)
        {
            return false;
        }

        if (!PdfDictionaryLookup.TryGet(catalog, "AcroForm"u8, governor, cancellationToken, out QpdfObject raw) || raw is null || raw is PdfNull)
        {
            return false;
        }

        if (raw is PdfReference formReference)
        {
            if (PdfObjectLookup.TryResolve(formReference, resolver, governor, cancellationToken, out QpdfObject? target) && target is PdfDictionary dictionary)
            {
                form = dictionary;
                return true;
            }

            return false;
        }

        form = raw as PdfDictionary;
        return form is not null;
    }

    private static bool TryResolveCheckArray(PdfDictionary node, ReadOnlySpan<byte> key, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken cancellationToken, out PdfArray? array)
    {
        array = null;
        if (!PdfDictionaryLookup.TryGet(node, key, governor, cancellationToken, out QpdfObject raw) || raw is null || raw is PdfNull)
        {
            return false;
        }

        if (raw is PdfArray direct)
        {
            array = direct;
            return true;
        }

        if (raw is PdfReference reference
            && PdfObjectLookup.TryResolve(reference, resolver, governor, cancellationToken, out QpdfObject? resolved)
            && resolved is PdfArray indirect)
        {
            array = indirect;
            return true;
        }

        return false;
    }

    private static bool IsCheckWidget(PdfDictionary dictionary, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        return PdfDictionaryLookup.TryGet(dictionary, "Subtype"u8, governor, cancellationToken, out QpdfObject subtype)
            && subtype is PdfName name
            && PdfDictionaryLookup.NameEquals(name, "Widget"u8, governor, cancellationToken);
    }
    private static byte[]? DecodeCheckStream(
        PdfStream stream,
        IIndirectResolver resolver,
        QpdfStreamDecodeLevel level,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        FilterPlan stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, resolver, governor, cancellationToken);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            return null;
        }
        using FilterPlan stageOwner = stages;

        if (stages.Count == 0)
        {
            return stream.ArrayData(memory, governor, cancellationToken);
        }

        foreach (FilterStage stage in stages)
        {
            if (!InLevel(stage.Name, level))
            {
                return null;
            }
        }

        try
        {
            return FilterChain.Decode(stream.ArrayData(memory, governor, cancellationToken), stages, limits, governor, memory, cancellationToken);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            return null;
        }
    }

    private static void TokenizeCheckStream(
        byte[] bytes,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        ContentTokenizer tokenizer = new(bytes, limits, governor);
        byte[] idMarker = [(byte)73, (byte)68];
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual(idMarker))
            {
                tokenizer.TryReadInlineImageData(out ContentToken _);
            }
        }
    }
}
