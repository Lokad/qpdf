using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Content;

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
        List<QpdfCheckOutcome> outcomes = new()
        {
            new QpdfCheckOutcome("header", QpdfCheckStatus.Passed, $"Version {document.Version}."),
            new QpdfCheckOutcome("cross-reference", QpdfCheckStatus.Passed, $"{engine.XRefEntries.Count} entries cover the effective size."),
            new QpdfCheckOutcome("trailer", QpdfCheckStatus.Passed, "Catalog root is reachable and input is unencrypted."),
            document.PageCount == 0
                ? new QpdfCheckOutcome("page-tree", QpdfCheckStatus.Failed, "File does not contain any pages.")
                : new QpdfCheckOutcome("page-tree", QpdfCheckStatus.Passed, $"{document.PageCount} pages traversed."),
        };

        List<(int Number, PdfStream Stream)> streams = new();
        QpdfCheckOutcome objectGraph = CheckObjectGraph(engine, governor, streams, cancellationToken);
        outcomes.Add(objectGraph);
        if (nativeStreamTraversal && objectGraph.Status != QpdfCheckStatus.Failed)
        {
            // Native checking writes the reachable graph to a discard sink;
            // unreferenced streams are not decoded by that writer traversal.
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            HashSet<int> reachable = GraphReachability.Find(engine, engine.Trailer, memory, governor);
            streams.RemoveAll(stream => !reachable.Contains(stream.Number));
        }
        outcomes.Add(CheckStreams(engine, options.DecodeLevel, limits, governor, diagnostics, streams, cancellationToken));
        outcomes.Add(CheckPageContent(document, options, limits, governor, diagnostics, cancellationToken));
        outcomes.Add(CheckFormTree(document, governor, diagnostics, cancellationToken));
        outcomes.Add(LinearizationCheck.Check(engine, document.PageCount, limits, governor, cancellationToken));
        foreach (QpdfCheckOutcome outcome in outcomes)
        {
            if (outcome.Status == QpdfCheckStatus.Failed)
            {
                diagnostics.Add(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, $"Check '{outcome.Name}' failed: {outcome.Detail}");
            }
        }

        return outcomes;
    }

    private static QpdfCheckOutcome CheckObjectGraph(
        DocumentEngine engine,
        ResourceGovernor governor,
        List<(int Number, PdfStream Stream)> streams,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<int> numbers = ParserContainers.CreateList<int>(memory);
        foreach (int number in engine.XRefEntries.Keys)
        {
            governor.ChargeWork(1L);
            ParserContainers.AddItem(memory, numbers, number, sizeof(int));
        }
        foreach (int number in engine.InspectionObjectNumbers)
        {
            governor.ChargeWork(1L);
            if (!engine.XRefEntries.ContainsKey(number)) { ParserContainers.AddItem(memory, numbers, number, sizeof(int)); }
        }
        numbers.Sort();
        int resolved = 0;
        List<string> failures = new();
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            try
            {
                QpdfObject value = engine.Load(number);
                resolved += 1;
                if (value is PdfStream stream)
                {
                    streams.Add((number, stream));
                }
            }
            catch (QpdfLimitException ex)
            {
                failures.Add($"object {number}: {ex.Message}");
                break;
            }
            catch (QpdfException ex)
            {
                failures.Add($"object {number}: {ex.Message}");
                if (failures.Count >= MaxObjectFailures)
                {
                    failures.Add("Further object failures are not recorded.");
                    break;
                }
            }
        }

        if (failures.Count > 0)
        {
            return new QpdfCheckOutcome("object-graph", QpdfCheckStatus.Failed, string.Join(" ", failures));
        }

        return new QpdfCheckOutcome("object-graph", QpdfCheckStatus.Passed, $"{resolved} objects resolved with {streams.Count} streams.");
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
            return new QpdfCheckOutcome("streams", QpdfCheckStatus.Skipped, "Decode level none disables stream decoding.");
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        int decoded = 0;
        int unfiltered = 0;
        int aboveLevel = 0;
        int skippedUnavailable = 0;
        List<string> unavailable = new();
        List<string> failures = new();
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
                failures.Add($"stream {number}: {ex.Message}");
                if (failures.Count >= MaxStreamFailures)
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
                FilterChain.Decode(stream.Data, stages, limits, governor);
                decoded += 1;
            }
            catch (QpdfFilterException ex) when (ex.Reason == QpdfFilterFailureReason.UnsupportedFilter)
            {
                skippedUnavailable += 1;
                foreach (FilterStage stage in stages)
                {
                    if (!IsDecodable(stage.Name) && !unavailable.Contains(stage.Name, StringComparer.Ordinal))
                    {
                        unavailable.Add(stage.Name);
                    }
                }
            }
            catch (QpdfLimitException ex)
            {
                failures.Add($"stream {number}: {ex.Message}");
                break;
            }
            catch (QpdfException ex)
            {
                failures.Add($"stream {number}: {ex.Message}");
                if (failures.Count >= MaxStreamFailures)
                {
                    break;
                }
            }
        }

        if (failures.Count > 0)
        {
            return new QpdfCheckOutcome("streams", QpdfCheckStatus.Failed, string.Join(" ", failures));
        }

        string detail = $"{decoded} decoded, {unfiltered} unfiltered, {aboveLevel} above level {level}.";
        if (skippedUnavailable > 0)
        {
            detail += $" {skippedUnavailable} skipped ({string.Join(", ", unavailable)} unavailable).";
            diagnostics.Add(
                QpdfDiagnosticCode.CheckSkipped,
                QpdfDiagnosticSeverity.Warning,
                $"Skipped check 'streams': {detail} Decoding unavailable filters arrives in a later milestone.");
            return new QpdfCheckOutcome("streams", QpdfCheckStatus.Skipped, detail);
        }

        if (aboveLevel > 0)
        {
            diagnostics.Add(
                QpdfDiagnosticCode.CheckSkipped,
                QpdfDiagnosticSeverity.Info,
                $"Left {aboveLevel} stream(s) above decode level {level} for a higher level.");
        }

        return new QpdfCheckOutcome("streams", QpdfCheckStatus.Passed, detail);
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
            return new QpdfCheckOutcome("page-content", QpdfCheckStatus.Skipped, "Decode level none disables stream decoding.");
        }

        DocumentEngine engine = document.Engine;
        IIndirectResolver resolver = (IIndirectResolver)engine;
        int examined = 0;
        int skipped = 0;
        List<string> failures = new();
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
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
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
                byte[]? bytes = DecodeCheckStream(stream, resolver, options.DecodeLevel, limits, governor);
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
                catch (QpdfException ex) when (ex is not QpdfLimitException)
                {
                    failures.Add(string.Concat("Page ", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), " content: ", ex.Message));
                    if (failures.Count >= MaxStreamFailures)
                    {
                        break;
                    }
                }
            }

            if (failures.Count >= MaxStreamFailures)
            {
                break;
            }
        }

        if (failures.Count > 0)
        {
            return new QpdfCheckOutcome("page-content", QpdfCheckStatus.Failed, string.Join(" ", failures));
        }

        return new QpdfCheckOutcome("page-content", QpdfCheckStatus.Passed, string.Concat(examined.ToString(System.Globalization.CultureInfo.InvariantCulture), " content streams tokenized, ", skipped.ToString(System.Globalization.CultureInfo.InvariantCulture), " skipped."));
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
            return new QpdfCheckOutcome("forms", QpdfCheckStatus.Passed, "No interactive form.");
        }

        if (!TryResolveCheckArray(form, "Fields"u8, resolver, governor, cancellationToken, out PdfArray? fields) || fields is null)
        {
            state.Dispose();
            return new QpdfCheckOutcome("forms", QpdfCheckStatus.Passed, "Interactive form carries no field registrations.");
        }

        try
        {
            for (int i = 0; i < fields.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.CheckCancellation();
                governor.ChargeWork(1L);
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
            return new QpdfCheckOutcome("forms", QpdfCheckStatus.Failed, $"Form traversal exceeded its budget: {ex.Message}");
        }

        int widgets = state.WidgetCount;
        state.Dispose();
        string detail = $"{state.Nodes} field nodes traversed, {widgets} widgets reached.";
        if (state.Warnings > 0)
        {
            detail += $" {state.Warnings} warnings recorded.";
        }

        if (state.Truncated)
        {
            detail += " Further findings are not recorded.";
        }

        return new QpdfCheckOutcome("forms", QpdfCheckStatus.Passed, detail);
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
        governor.ChargeWork(1L);
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
            governor.ChargeWork(1L);
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
            AddFormWarning(diagnostics, state, "Encountered a direct object as a field or annotation while traversing /AcroForm; ignoring.");
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

        AddFormWarning(diagnostics, state, $"Field node {kidNumber} has an invalid /Parent entry while traversing /AcroForm.");
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
            governor.ChargeWork(1L);
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
                governor.ChargeWork(1L);
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

                AddFormWarning(diagnostics, state, $"Widget annotation {itemReference.ObjectNumber} is not reachable from /AcroForm.");
            }
        }
    }

    private static void AddFormWarning(DiagnosticBag diagnostics, FormCheckState state, string message)
    {
        state.Warnings += 1;
        if (state.Warnings > MaxFormWarnings)
        {
            state.Truncated = true;
            return;
        }

        diagnostics.Add(QpdfDiagnosticCode.FormStructureWarning, QpdfDiagnosticSeverity.Warning, message);
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
        ResourceGovernor governor)
    {
        FilterPlan stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, resolver, governor, governor.CancellationToken);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
        {
            return null;
        }
        using FilterPlan stageOwner = stages;

        if (stages.Count == 0)
        {
            return stream.Data;
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
            return FilterChain.Decode(stream.Data, stages, limits, governor);
        }
        catch (QpdfException ex) when (ex is not QpdfLimitException)
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
            governor.ChargeWork(1L);
            if (token.Kind == ContentTokenKind.Operator && token.Data.Span.SequenceEqual(idMarker))
            {
                tokenizer.TryReadInlineImageData(out ContentToken _);
            }
        }
    }
}
