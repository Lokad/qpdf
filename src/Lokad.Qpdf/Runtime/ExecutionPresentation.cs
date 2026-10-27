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
            memory.Reserve(72L + 40L);
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
        bool hasAccepted = failure.Freeze(accepted, diagnostics, (_flags & 8) != 0, Bag.HasNonWarningEntries, (_flags & 15) == 0);
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
