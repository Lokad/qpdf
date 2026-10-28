using System.Collections;

namespace Lokad.Qpdf.Runtime;

internal readonly record struct PresentedDiagnostics(IReadOnlyList<QpdfDiagnostic> Diagnostics, QpdfExitCode ExitCode);

/// <summary>Prepares owned diagnostics before publication and retains accepted failure history.</summary>
internal sealed class ExecutionPresentation : ExecutionPreparation, IDisposable
{
    private ResourceGovernor? _governor;
    private DiagnosticBag? _bag;
    private ParserMemoryScope? _memory;
    private DiagnosticProjection? _prepared;
    private FailureProjection? _failure;
    private readonly byte _flags;

    private sealed class FailureProjection : IReadOnlyList<QpdfDiagnostic>
    {
        private IReadOnlyList<QpdfDiagnostic> _accepted = Array.Empty<QpdfDiagnostic>();
        private IReadOnlyList<QpdfDiagnostic> _terminal = Array.Empty<QpdfDiagnostic>();
        private bool _noWarn, _frozen, _warningHistoryOnly;
        private int _acceptedCount, _prefixCount, _terminalStart, _count;

        internal bool Freeze(IReadOnlyList<QpdfDiagnostic> accepted, IReadOnlyList<QpdfDiagnostic> terminal, bool noWarn, bool warningHistoryOnly)
        {
            if (_frozen) { throw new InvalidOperationException("Failure diagnostics were already frozen."); }
            _noWarn = noWarn; _warningHistoryOnly = warningHistoryOnly;
            for (int index = 0; index < accepted.Count; index++)
            { if (AcceptedVisible(accepted[index])) { _acceptedCount++; } }
            _accepted = _acceptedCount == 0 ? Array.Empty<QpdfDiagnostic>() : accepted;

            // Producers return fresh terminal records or an accepted prefix
            // followed by fresh records. Presentation may omit hidden warnings,
            // and accepted history may have grown since that prefix was captured.
            for (int index = 0; index < accepted.Count; index++)
            {
                QpdfDiagnostic item = accepted[index];
                if (_terminalStart < terminal.Count && ReferenceEquals(item, terminal[_terminalStart])) { _terminalStart++; }
                else if (Visible(item)) { break; }
            }
            for (int index = 0; index < _terminalStart; index++)
            { if (Visible(terminal[index]) && !AcceptedVisible(terminal[index])) { _prefixCount++; } }
            _count = _acceptedCount + _prefixCount;
            for (int index = _terminalStart; index < terminal.Count; index++)
            { if (Visible(terminal[index])) { _count++; } }
            _terminal = terminal; _frozen = true;
            return _acceptedCount > 0;
        }

        private bool Visible(QpdfDiagnostic item) => !_noWarn || item.Severity != QpdfDiagnosticSeverity.Warning;
        private bool AcceptedVisible(QpdfDiagnostic item) => Visible(item) && (!_warningHistoryOnly || item.Severity == QpdfDiagnosticSeverity.Warning);
        // Returned DTO traversal belongs to its caller. The immutable view
        // borrows already accepted storage, avoiding a failure-time allocation.
        public int Count => _count;

        public QpdfDiagnostic this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                if (index >= _count) { throw new ArgumentOutOfRangeException(nameof(index)); }
                if (index < _acceptedCount)
                {
                    if (_acceptedCount == _accepted.Count) { return _accepted[index]; }
                    for (int at = 0; at < _accepted.Count; at++)
                    { if (AcceptedVisible(_accepted[at]) && index-- == 0) { return _accepted[at]; } }
                }
                index -= _acceptedCount;
                if (index < _prefixCount)
                {
                    for (int at = 0; at < _terminalStart; at++)
                    { if (Visible(_terminal[at]) && !AcceptedVisible(_terminal[at]) && index-- == 0) { return _terminal[at]; } }
                }
                index -= _prefixCount;
                if (_count - _acceptedCount - _prefixCount == _terminal.Count - _terminalStart)
                { return _terminal[_terminalStart + index]; }
                for (int at = _terminalStart; at < _terminal.Count; at++)
                { if (Visible(_terminal[at]) && index-- == 0) { return _terminal[at]; } }
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public IEnumerator<QpdfDiagnostic> GetEnumerator()
        {
            for (int index = 0; index < _accepted.Count; index++)
            { QpdfDiagnostic item = _accepted[index]; if (AcceptedVisible(item)) { yield return item; } }
            for (int index = _prefixCount == 0 ? _terminalStart : 0; index < _terminal.Count; index++)
            {
                QpdfDiagnostic item = _terminal[index];
                if (Visible(item) && (index >= _terminalStart || !AcceptedVisible(item))) { yield return item; }
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private ExecutionPresentation(ResourceGovernor governor, DiagnosticBag bag, ParserMemoryScope memory,
        FailureProjection failure, byte flags)
    { _governor = governor; _bag = bag; _memory = memory; _failure = failure; _flags = flags; }

    internal static ExecutionPresentation? Create(ResourceGovernor governor, DiagnosticBag bag,
        bool verbose, bool memoryUsage, bool progress, bool noWarn, bool warningExitZero, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            // Precreate failure metadata under the operation allowance. Its
            // referenced diagnostic arrays/text retain their existing owners;
            // the small view needs no independently retiring receipt.
            memory.Reserve(72L + 56L);
            FailureProjection failure = new();
            byte flags = (byte)((verbose ? 1 : 0) | (memoryUsage ? 2 : 0) | (progress ? 4 : 0)
                | (noWarn ? 8 : 0) | (warningExitZero ? 16 : 0));
            ExecutionPresentation presentation = new(governor, bag, memory, failure, flags);
            governor.AttachExecutionPresentation(presentation);
            bag.SuppressWarningStorage = noWarn;
            return presentation;
        }
        catch
        {
            memory.Dispose(); throw;
        }
    }

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(ExecutionPresentation));
    private DiagnosticBag Bag => _bag ?? throw new ObjectDisposedException(nameof(ExecutionPresentation));

    internal override void Prepare()
    {
        // Freeze actual stored messages and summaries before publication. Completion
        // reuses this preparation and cannot allocate or reject it after commit.
        if ((_flags & 15) == 0 || _prepared is not null) { return; }
        ResourceGovernor governor = Governor;
        governor.CheckCancellation();
        _prepared = DiagnosticProjection.Create(governor, Bag.Entries, (_flags & 8) != 0,
            (_flags & 1) != 0, (_flags & 2) != 0, (_flags & 4) != 0,
            governor.PresentationOutputBytes, governor.PresentationDecodedBytes, governor.PresentationPeakBytes);
    }

    internal PresentedDiagnostics Complete()
    {
        if ((_flags & 15) == 0)
        {
            IReadOnlyList<QpdfDiagnostic> entries = Bag.Entries;
            bool error = false;
            for (int index = 0; index < entries.Count; index++)
            { error |= entries[index].Severity == QpdfDiagnosticSeverity.Error; }
            return new(entries, error ? QpdfExitCode.Error
                : Bag.HasAcceptedWarnings && (_flags & 16) == 0 ? QpdfExitCode.Warning : QpdfExitCode.Success);
        }
        Prepare();
        DiagnosticProjection prepared = _prepared ?? throw new InvalidOperationException("Missing prepared diagnostics.");
        QpdfExitCode exit = prepared.HasError ? QpdfExitCode.Error
            : (prepared.HasWarning || Bag.HasAcceptedWarnings) && (_flags & 16) == 0 ? QpdfExitCode.Warning : QpdfExitCode.Success;
        IReadOnlyList<QpdfDiagnostic> diagnostics = prepared.Transfer(); _prepared = null;
        return new(diagnostics, exit);
    }

    internal ExecutionOutcome Failure(ExecutionOutcome result)
    {
        IReadOnlyList<QpdfDiagnostic>? failure = FreezeFailure(result.Diagnostics);
        return failure is null ? result : new ExecutionOutcome(result.Status, result.ExitCode, failure);
    }

    private IReadOnlyList<QpdfDiagnostic>? FreezeFailure(IReadOnlyList<QpdfDiagnostic> diagnostics)
    {
        FailureProjection failure = _failure ?? throw new InvalidOperationException("Missing failure diagnostics.");
        IReadOnlyList<QpdfDiagnostic> accepted = Bag.Entries;
        bool hasAccepted = failure.Freeze(accepted, diagnostics, (_flags & 8) != 0, (_flags & 15) == 0);
        if ((_flags & 15) == 0 && !hasAccepted) { return null; }
        _failure = null;
        return failure;
    }

    public void Dispose()
    {
        ResourceGovernor? governor = _governor;
        if (governor is null) { return; }
        governor.DetachExecutionPresentation(this);
        _prepared?.Dispose(); _prepared = null;
        _failure = null; _bag = null; _governor = null;
        ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
    }
}
