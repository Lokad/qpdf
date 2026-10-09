using System.Collections;

namespace Lokad.Qpdf.Runtime;

internal readonly record struct PresentedDiagnostics(IReadOnlyList<QpdfDiagnostic> Diagnostics, QpdfExitCode ExitCode);

/// <summary>Prepares owned diagnostics before publication and retains accepted failure history.</summary>
internal sealed class ExecutionPresentation : IDisposable
{
    private ResourceGovernor? _governor;
    private DiagnosticBag? _bag;
    private ParserMemoryScope? _memory;
    private DiagnosticProjection? _prepared;
    private FailureProjection? _failure;
    private RetainedValueOwners.Receipt? _failureReceipt;
    private readonly byte _flags;

    private sealed class FailureProjection : IReadOnlyList<QpdfDiagnostic>
    {
        private IReadOnlyList<QpdfDiagnostic> _accepted = Array.Empty<QpdfDiagnostic>();
        private IReadOnlyList<QpdfDiagnostic> _terminal = Array.Empty<QpdfDiagnostic>();
        private bool _noWarn, _frozen, _warningHistoryOnly;

        internal bool Freeze(IReadOnlyList<QpdfDiagnostic> accepted, IReadOnlyList<QpdfDiagnostic> terminal, bool noWarn, bool hasNonWarning, bool warningHistoryOnly)
        {
            if (_frozen) { throw new InvalidOperationException("Failure diagnostics were already frozen."); }
            _accepted = noWarn && !hasNonWarning ? Array.Empty<QpdfDiagnostic>() : accepted;
            if (warningHistoryOnly)
            {
                bool hasWarning = false;
                foreach (QpdfDiagnostic item in accepted) { if (item.Severity == QpdfDiagnosticSeverity.Warning) { hasWarning = true; break; } }
                if (!hasWarning) { _accepted = Array.Empty<QpdfDiagnostic>(); }
            }
            _terminal = terminal; _noWarn = noWarn; _warningHistoryOnly = warningHistoryOnly; _frozen = true;
            return _accepted.Count > 0;
        }

        private bool Visible(QpdfDiagnostic item) => !_noWarn || item.Severity != QpdfDiagnosticSeverity.Warning;
        private bool AcceptedVisible(QpdfDiagnostic item) => Visible(item) && (!_warningHistoryOnly || item.Severity == QpdfDiagnosticSeverity.Warning);
        private bool Prior(QpdfDiagnostic item)
        {
            foreach (QpdfDiagnostic accepted in _accepted)
            { if (AcceptedVisible(accepted) && ReferenceEquals(item, accepted)) { return true; } }
            return false;
        }

        // Returned DTO traversal belongs to its caller. The immutable view
        // borrows already accepted storage, avoiding a failure-time allocation.
        public int Count
        {
            get
            {
                int count = 0;
                foreach (QpdfDiagnostic item in _accepted) { if (AcceptedVisible(item)) { count++; } }
                foreach (QpdfDiagnostic item in _terminal) { if (Visible(item) && !Prior(item)) { count++; } }
                return count;
            }
        }

        public QpdfDiagnostic this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                foreach (QpdfDiagnostic item in _accepted) { if (AcceptedVisible(item) && index-- == 0) { return item; } }
                foreach (QpdfDiagnostic item in _terminal) { if (Visible(item) && !Prior(item) && index-- == 0) { return item; } }
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public IEnumerator<QpdfDiagnostic> GetEnumerator()
        {
            foreach (QpdfDiagnostic item in _accepted) { if (AcceptedVisible(item)) { yield return item; } }
            foreach (QpdfDiagnostic item in _terminal) { if (Visible(item) && !Prior(item)) { yield return item; } }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private ExecutionPresentation(ResourceGovernor governor, DiagnosticBag bag, ParserMemoryScope memory,
        FailureProjection failure, RetainedValueOwners.Receipt failureReceipt, byte flags)
    { _governor = governor; _bag = bag; _memory = memory; _failure = failure; _failureReceipt = failureReceipt; _flags = flags; }

    internal static ExecutionPresentation? Create(ResourceGovernor governor, DiagnosticBag bag,
        bool verbose, bool memoryUsage, bool progress, bool noWarn, bool warningExitZero, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        FailureProjection? failure = null;
        RetainedValueOwners.Receipt? receipt = null;
        try
        {
            memory.Reserve(72L + 40L);
            failure = new();
            receipt = memory.RetainAllocationReceipt(failure, 40L);
            byte flags = (byte)((verbose ? 1 : 0) | (memoryUsage ? 2 : 0) | (progress ? 4 : 0)
                | (noWarn ? 8 : 0) | (warningExitZero ? 16 : 0));
            ExecutionPresentation presentation = new(governor, bag, memory, failure, receipt, flags);
            governor.AttachExecutionPresentation(presentation);
            bag.SuppressWarningStorage = noWarn;
            return presentation;
        }
        catch
        {
            if (failure is not null && receipt is not null) { governor.DropAllocation(failure, receipt); }
            memory.Dispose(); throw;
        }
    }

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(ExecutionPresentation));
    private DiagnosticBag Bag => _bag ?? throw new ObjectDisposedException(nameof(ExecutionPresentation));

    internal void PreparePlainFailureHistory()
    {
        if ((_flags & 15) != 0 || !Bag.HasNonWarningEntries || !Bag.HasAcceptedWarnings) { return; }
        IReadOnlyList<QpdfDiagnostic> source = Bag.PromotedEntries;
        DiagnosticProjection history = DiagnosticProjection.CreateWarningHistory(Governor, source);
        _prepared?.Dispose(); _prepared = history;
    }

    internal void Prepare()
    {
        // Plain jobs keep their existing success projection. Only the prepaid
        // warning-history view is needed if their execution later fails.
        if ((_flags & 15) == 0) { return; }
        ResourceGovernor governor = Governor;
        governor.CheckCancellation();
        IReadOnlyList<QpdfDiagnostic> source = Bag.Entries;
        long output = (_flags & 5) != 0 ? governor.PresentationOutputBytes : 0L;
        long decoded = (_flags & 1) != 0 ? governor.PresentationDecodedBytes : 0L;
        bool usesPeak = (_flags & 3) != 0;
        long peak = usesPeak ? governor.PresentationPeakBytes : 0L;
        if (_prepared is DiagnosticProjection previous && previous.SourceCount == source.Count
            && previous.OutputBytes == output && previous.DecodedBytes == decoded && previous.PeakBytes == peak) { return; }
        while (true)
        {
            governor.CheckCancellation(); governor.ChargeWork(1L);
            peak = usesPeak ? governor.PresentationPeakBytes : 0L;
            DiagnosticProjection candidate = DiagnosticProjection.Create(governor, source, (_flags & 8) != 0,
                (_flags & 1) != 0, (_flags & 2) != 0, (_flags & 4) != 0, output, decoded, peak);
            if (!usesPeak || candidate.PeakBytes == governor.PresentationPeakBytes)
            {
                _prepared?.Dispose(); _prepared = candidate;
                return;
            }
            candidate.Dispose();
        }
    }

    internal PresentedDiagnostics? Complete()
    {
        if ((_flags & 15) == 0) { return null; }
        Prepare();
        DiagnosticProjection prepared = _prepared ?? throw new InvalidOperationException("Missing prepared diagnostics.");
        QpdfExitCode exit = prepared.HasError ? QpdfExitCode.Error
            : (prepared.HasWarning || Bag.HasAcceptedWarnings) && (_flags & 16) == 0 ? QpdfExitCode.Warning : QpdfExitCode.Success;
        Governor.PresentDiagnosticCount();
        IReadOnlyList<QpdfDiagnostic> diagnostics = prepared.Transfer(); _prepared = null;
        return new(diagnostics, exit);
    }

    internal QpdfExecutionResult Failure(QpdfExecutionResult result)
    {
        FailureProjection failure = _failure ?? throw new InvalidOperationException("Missing failure diagnostics.");
        IReadOnlyList<QpdfDiagnostic> accepted = Bag.Entries;
        if ((_flags & 15) == 0 && _prepared is DiagnosticProjection history)
        { accepted = history.Transfer(); _prepared = null; }
        bool hasAccepted = failure.Freeze(accepted, result.Diagnostics, (_flags & 8) != 0, Bag.HasNonWarningEntries, (_flags & 15) == 0);
        if ((_flags & 15) == 0 && !hasAccepted) { return result; }
        _failure = null; _failureReceipt = null;
        return new QpdfExecutionResult(result.Status, result.ExitCode, failure, result.Counters, true);
    }

    public void Dispose()
    {
        ResourceGovernor? governor = _governor;
        if (governor is null) { return; }
        governor.DetachExecutionPresentation(this);
        _prepared?.Dispose(); _prepared = null;
        if (_failure is not null && _failureReceipt is not null) { governor.DropAllocation(_failure, _failureReceipt); }
        _failure = null; _failureReceipt = null; _bag = null; _governor = null;
        ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
    }
}
