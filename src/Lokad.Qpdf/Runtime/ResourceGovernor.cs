using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Runtime;

/// <summary>
/// Single per-job resource governor. Every subsystem reserves budget before
/// allocating and charges cumulative work; breaches fail explicitly without
/// allocating. Not safe for concurrent use by design: one governor per job.
/// </summary>
internal sealed class ResourceGovernor
{
    private readonly QpdfLimits _limits;
    private readonly CancellationToken _cancellationToken;
    private long _liveMemoryBytes;
    private long _peakMemoryBytes;
    private long _totalWork;
    private long _totalDecodedBytes;
    private long _totalOutputBytes;
    private long _hostOperations;
    private long _openHandles;
    private long _diagnosticCount;
    private long _diagnosticBytes;
    private long _operationsSinceCheck;
    private RetainedValueOwners? _valueOwners;
    private DiagnosticStage? _diagnosticStage;
    private PresentationCountReservation? _presentationCount;
    private ExecutionPresentation? _executionPresentation;
    private PublicationWorkReservation? _publicationWork;
    private long _reservedWork;
    private long _reservedResultWork;
    private bool _presentingDiagnostics;
    private bool _finishingDiagnosticFailure;
    private bool _returnsExecutionResult;
    private bool _finishingExecution;
    private bool _executionFinished;
    private QpdfExecutionResult? _preparedExecutionResult;
    internal bool DeferredPlainHistory { get; set; }
    internal ExecutionPresentation? ExecutionPresentation => _executionPresentation;

    internal ResourceGovernor(QpdfLimits limits, CancellationToken cancellationToken)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        _limits = limits;
        _cancellationToken = cancellationToken;
    }

    internal CancellationToken CancellationToken => _cancellationToken;

    internal long MaxNestingDepth => _limits.MaxNestingDepth;

    private long LiveMemoryBytes => Math.Max(0L, _liveMemoryBytes - (_valueOwners?.RetiredBytes ?? 0L));

    internal void ReserveMemory(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Reservation must not be negative.");
        }

        ReconcileValueOwners();
        checked
        {
            long next = LiveMemoryBytes + bytes;
            if (next > _limits.MaxMemoryBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxMemoryBytes), next, _limits.MaxMemoryBytes);
            }

            CheckPresentationBytes(_totalOutputBytes, _totalDecodedBytes, Math.Max(next, _peakMemoryBytes));
            _liveMemoryBytes += bytes;
            if (next > _peakMemoryBytes)
            {
                _peakMemoryBytes = next;
            }
        }
    }

    internal void ReleaseMemory(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Release must not be negative.");
        }

        _liveMemoryBytes = Math.Max(_valueOwners?.RetiredBytes ?? 0L, _liveMemoryBytes - bytes);
    }

    private void ReconcileValueOwners()
    {
        RetainedValueOwners? owners = _valueOwners;
        if (owners is null || owners.Busy) { return; }
        bool finishing = _finishingExecution;
        if (_executionFinished) { _finishingExecution = true; }
        try { owners.Drain(); DisposeEmptyValueOwners(owners); }
        finally { _finishingExecution = finishing; ShrinkDiagnosticWork(); }
    }

    private void DisposeEmptyValueOwners(RetainedValueOwners owners)
    {
        if (owners.Busy) { return; }
        owners.DisposeUnusedIndex();
        if (!owners.Empty) { return; }
        // Fold the atomic retirement total before detaching its storage. The
        // detached ledger has no live carrier or future collection notification.
        _liveMemoryBytes = Math.Max(0L, _liveMemoryBytes - owners.RetiredBytes);
        _valueOwners = null;
        owners.DisposeEmpty();
    }

    internal long RetainValue(QpdfObject value, long reservedBytes)
    {
        if (reservedBytes == 0L) { return 0L; }
        RetainedValueOwners owners = _valueOwners ??= RetainedValueOwners.Create(this);
        try { return owners.Retain(value, reservedBytes); }
        catch { DisposeEmptyValueOwners(owners); throw; }
        finally { ShrinkDiagnosticWork(); }
    }

    internal long RetainValues(object wrapper, long wrapperBytes, IReadOnlyList<QpdfObject> roots, long reservedBytes)
    {
        RetainedValueOwners owners = _valueOwners ??= RetainedValueOwners.Create(this);
        try { return owners.Retain(wrapper, wrapperBytes, roots, reservedBytes); }
        catch { DisposeEmptyValueOwners(owners); throw; }
        finally { ShrinkDiagnosticWork(); }
    }

    internal long RetainAllocations(OwnedAllocationBatch allocations, long reservedBytes)
    {
        RetainedValueOwners owners = _valueOwners ??= RetainedValueOwners.Create(this);
        try { return owners.Retain(allocations, reservedBytes); }
        catch { DisposeEmptyValueOwners(owners); throw; }
    }

    internal long RetainAllocations(OwnedAllocationBatch allocations, QpdfObject parsed, long reservedBytes)
    {
        RetainedValueOwners owners = _valueOwners ??= RetainedValueOwners.Create(this);
        try { return owners.Retain(allocations, parsed, reservedBytes); }
        catch { DisposeEmptyValueOwners(owners); throw; }
    }

    internal RetainedValueOwners.Receipt RetainAllocation(object carrier, long reservedBytes)
    {
        RetainedValueOwners owners = _valueOwners ??= RetainedValueOwners.Create(this);
        try { return owners.RetainAllocation(carrier, reservedBytes); }
        catch { DisposeEmptyValueOwners(owners); throw; }
        finally { ShrinkDiagnosticWork(); }
    }

    internal void DropAllocation(object carrier, RetainedValueOwners.Receipt receipt)
    {
        RetainedValueOwners owners = _valueOwners ?? throw new InvalidOperationException("Allocation owner is unavailable.");
        long bytes = receipt.Bytes;
        owners.DropAllocation(carrier, receipt);
        ReleaseMemory(bytes);
        DisposeEmptyValueOwners(owners);
        ShrinkDiagnosticWork();
    }

    internal long RetainDiagnostic(QpdfDiagnostic diagnostic, long reservedBytes)
    {
        RetainedValueOwners owners = _valueOwners ??= RetainedValueOwners.Create(this);
        try { return owners.RetainDiagnostic(diagnostic, reservedBytes); }
        catch { DisposeEmptyValueOwners(owners); throw; }
        finally { ShrinkDiagnosticWork(); }
    }

    internal long RetainCheckOutcome(QpdfCheckOutcome outcome, long detailBytes, long reservedBytes)
    {
        RetainedValueOwners owners = _valueOwners ??= RetainedValueOwners.Create(this);
        try { return owners.RetainCheckOutcome(outcome, detailBytes, reservedBytes); }
        catch { DisposeEmptyValueOwners(owners); throw; }
        finally { ShrinkDiagnosticWork(); }
    }

    internal string? FindDiagnosticMessage(string message, int hash)
        => _valueOwners?.FindDiagnosticMessage(message, hash);

    internal string? FindDiagnosticMessage(ReadOnlySpan<DiagnosticPart> parts, DiagnosticTextInfo info)
        => _valueOwners?.FindDiagnosticMessage(parts, info);

    internal long RetainCachedDiagnostic(QpdfDiagnostic diagnostic, long reservedBytes, int messageHash)
    {
        RetainedValueOwners owners = _valueOwners ??= RetainedValueOwners.Create(this);
        try { return owners.RetainCachedDiagnostic(diagnostic, reservedBytes, messageHash); }
        catch { DisposeEmptyValueOwners(owners); throw; }
        finally { ShrinkDiagnosticWork(); }
    }

    internal DiagnosticWorkState DiagnosticWorkState => _valueOwners?.WorkState ?? default;

    internal void AttachDiagnosticStage(DiagnosticStage stage)
    {
        if (_diagnosticStage is not null) { throw new InvalidOperationException("A job already has a diagnostic stage."); }
        _diagnosticStage = stage;
    }

    internal void SetDiagnosticWork(DiagnosticStage stage, long capacity)
    {
        if (!ReferenceEquals(_diagnosticStage, stage) || _presentingDiagnostics) { throw new InvalidOperationException("Diagnostic work cannot be changed in this phase."); }
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        long publication = _publicationWork?.Remaining ?? 0L;
        if (capacity > long.MaxValue - publication || capacity + publication > long.MaxValue - _reservedResultWork)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), long.MaxValue, _limits.MaxWork); }
        CheckWorkCapacity(capacity + publication + _reservedResultWork);
        _reservedWork = capacity;
    }

    internal void PreflightDiagnosticWork(DiagnosticWorkState state)
    {
        if (_diagnosticStage is null || _presentingDiagnostics) { return; }
        long needed = _diagnosticStage.PresentationWork(state);
        if (needed > _reservedWork) { SetDiagnosticWork(_diagnosticStage, needed); }
    }

    private void ShrinkDiagnosticWork()
    {
        if (_diagnosticStage is null || _presentingDiagnostics || _diagnosticStage.Admitting) { return; }
        long needed = _diagnosticStage.PresentationWork(DiagnosticWorkState);
        // Growth preflight funds an upper bound before any carrier activates.
        // After commit/rollback/retirement, reconciliation only returns excess;
        // it cannot fail after a successful ownership transfer.
        if (needed < _reservedWork) { _reservedWork = needed; }
    }

    internal void BeginDiagnosticPresentation(DiagnosticStage stage)
    {
        SetDiagnosticWork(stage, stage.PresentationWork(DiagnosticWorkState));
        _presentingDiagnostics = true;
    }

    internal void BeginDiagnosticFailurePresentation(DiagnosticStage stage)
    {
        BeginDiagnosticPresentation(stage);
        _finishingDiagnosticFailure = true;
    }

    internal void EndDiagnosticPresentation(DiagnosticStage stage)
    {
        if (ReferenceEquals(_diagnosticStage, stage)) { _presentingDiagnostics = false; _finishingDiagnosticFailure = false; }
    }

    internal void DetachDiagnosticStage(DiagnosticStage stage)
    {
        if (!ReferenceEquals(_diagnosticStage, stage)) { return; }
        _diagnosticStage = null;
        _presentingDiagnostics = false;
        _finishingDiagnosticFailure = false;
        _reservedWork = 0L;
    }

    private void CheckWorkCapacity(long units)
    {
        if (units <= _limits.MaxWork - _totalWork) { return; }
        long requested = units > long.MaxValue - _totalWork ? long.MaxValue : _totalWork + units;
        throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), requested, _limits.MaxWork);
    }

    internal void ChargeWork(long units)
    {
        if (units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(units), "Charge must not be negative.");
        }

        if (_presentingDiagnostics)
        {
            if (units > _reservedWork) { throw new InvalidOperationException("Diagnostic work exceeds its pre-admitted bound."); }
            _reservedWork -= units;
        }
        else if (_publicationWork is PublicationWorkReservation publication && publication.Publishing)
        {
            publication.Consume(units);
        }
        else
        {
            long heldPublicationUnits = _publicationWork?.Remaining ?? 0L;
            if (units > long.MaxValue - _reservedWork || units + _reservedWork > long.MaxValue - heldPublicationUnits
                || units + _reservedWork + heldPublicationUnits > long.MaxValue - _reservedResultWork)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), long.MaxValue, _limits.MaxWork); }
            long capacity = units + _reservedWork + heldPublicationUnits + _reservedResultWork;
            CheckWorkCapacity(capacity);
        }
        _totalWork = checked(_totalWork + units);

        NoteOperation();
    }

    internal void ChargeRetirementWork(bool funded)
    {
        if (!funded) { ChargeWork(1L); return; }
        if (_reservedResultWork == 0L) { throw new InvalidOperationException("Result retirement work is unavailable."); }
        _reservedResultWork--;
        _totalWork = checked(_totalWork + 1L);
        NoteOperation();
    }

    internal void ChargeDecodedBytes(long bytes)
    {
        CheckDecodedBytes(bytes);
        long next = checked(_totalDecodedBytes + bytes);
        CheckPresentationBytes(_totalOutputBytes, next, _peakMemoryBytes);
        _totalDecodedBytes = next;
    }

    /// <summary>Checks a prospective stage result without committing its byte charge.</summary>
    internal void CheckDecodedBytes(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Charge must not be negative.");
        }

        checked
        {
            long next = _totalDecodedBytes + bytes;
            if (next > _limits.MaxDecodedBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), next, _limits.MaxDecodedBytes);
            }
        }
    }

    internal void ChargeOutputBytes(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Charge must not be negative.");
        }

        checked
        {
            long next = _totalOutputBytes + bytes;
            if (next > _limits.MaxOutputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), next, _limits.MaxOutputBytes);
            }

            if (bytes > 0L) { _presentationCount?.BeginOutput(next, _totalDecodedBytes, _peakMemoryBytes); }
            CheckPresentationBytes(next, _totalDecodedBytes, _peakMemoryBytes);
            _totalOutputBytes = next;
        }
    }

    internal void NoteHostOperation()
    {
        if (_publicationWork is PublicationWorkReservation held && !held.Publishing)
        {
            if (held.Remaining == long.MaxValue)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxHostOperations), long.MaxValue, _limits.MaxHostOperations); }
            CheckHostOperationCapacity(held.Remaining + 1L);
        }
        checked
        {
            long next = _hostOperations + 1L;
            if (next > _limits.MaxHostOperations)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxHostOperations), next, _limits.MaxHostOperations);
            }

            _hostOperations = next;
        }

        NoteOperation();
    }

    internal void CheckHostOperationCapacity(long operations)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(operations);
        if (operations <= _limits.MaxHostOperations - _hostOperations) { return; }
        long requested = operations > long.MaxValue - _hostOperations ? long.MaxValue : _hostOperations + operations;
        throw new QpdfLimitException(nameof(QpdfLimits.MaxHostOperations), requested, _limits.MaxHostOperations);
    }

    internal void CheckHandleCapacity(long handles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(handles);
        if (handles <= _limits.MaxOpenHandles - _openHandles) { return; }
        long requested = handles > long.MaxValue - _openHandles ? long.MaxValue : _openHandles + handles;
        throw new QpdfLimitException(nameof(QpdfLimits.MaxOpenHandles), requested, _limits.MaxOpenHandles);
    }

    internal PublicationWorkReservation? ReserveOutputWrapper()
    {
        if (_publicationWork is PublicationWorkReservation publication && publication.Publishing)
        {
            publication.AcquireTransaction();
            return publication;
        }
        ReserveMemory(GovernedHost.OutputWrapperBytes);
        return null;
    }

    internal void AcquireHandle()
    {
        checked
        {
            long next = _openHandles + 1L;
            if (next > _limits.MaxOpenHandles)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOpenHandles), next, _limits.MaxOpenHandles);
            }

            _openHandles = next;
        }
    }

    internal void ReleaseHandle()
    {
        _openHandles = Math.Max(0L, _openHandles - 1L);
    }

    internal void ReserveDiagnostic(long messageBytes)
    {
        CheckDiagnostic(messageBytes);
        _diagnosticCount = checked(_diagnosticCount + 1L);
        _diagnosticBytes = checked(_diagnosticBytes + messageBytes);
    }

    internal void CheckDiagnosticCount(long pending)
    {
        if (pending < 0L) { throw new ArgumentOutOfRangeException(nameof(pending)); }
        long next = checked(_diagnosticCount + pending);
        if (next > _limits.MaxDiagnostics)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxDiagnostics), next, _limits.MaxDiagnostics); }
    }

    internal void AttachPresentationCount(PresentationCountReservation reservation)
    {
        if (_presentationCount is not null) { throw new InvalidOperationException("A job already has presentation slots."); }
        CheckDiagnosticBytes(reservation.MessageBytes(_totalOutputBytes, _totalDecodedBytes, _peakMemoryBytes));
        _presentationCount = reservation;
    }

    internal void DetachPresentationCount(PresentationCountReservation reservation)
    {
        if (!ReferenceEquals(_presentationCount, reservation)) { throw new InvalidOperationException("Presentation slots are unavailable."); }
        _presentationCount = null;
    }

    internal void AcceptPresentationCount(PresentationCountReservation reservation, long pending)
    {
        if (!ReferenceEquals(_presentationCount, reservation)) { throw new InvalidOperationException("Presentation slots are unavailable."); }
        CheckDiagnosticCount(pending);
        long messageBytes = reservation.MessageBytes(_totalOutputBytes, _totalDecodedBytes, _peakMemoryBytes);
        CheckDiagnosticBytes(messageBytes);
        _diagnosticCount = checked(_diagnosticCount + pending);
        _diagnosticBytes = checked(_diagnosticBytes + messageBytes);
    }

    internal void PresentDiagnosticCount() => _presentationCount?.Present();

    internal bool HasPresentation => _presentationCount is not null;
    internal void AbandonPresentationCount() => _presentationCount?.Dispose();

    internal long PresentationOutputBytes => Math.Max(_totalOutputBytes, _presentationCount?.AnticipatedOutput ?? 0L);
    internal long PresentationDecodedBytes => _totalDecodedBytes;
    internal long PresentationPeakBytes => _peakMemoryBytes;

    internal void AttachExecutionPresentation(ExecutionPresentation presentation)
    {
        if (_executionPresentation is not null) { throw new InvalidOperationException("A job already has an execution presentation."); }
        _executionPresentation = presentation;
        DeferredPlainHistory = false;
    }

    internal void DetachExecutionPresentation(ExecutionPresentation presentation)
    {
        if (!ReferenceEquals(_executionPresentation, presentation)) { throw new InvalidOperationException("Execution presentation is unavailable."); }
        _executionPresentation = null;
    }

    internal void PrepareExecutionPresentation()
    {
        PrepareExecutionResult();
        _executionPresentation?.Prepare();
    }

    internal void ExpectExecutionResult() => _returnsExecutionResult = true;

    // Admission precedes output creation. The private carriers are populated
    // once, after every operation/configuration scope has closed.
    internal void PrepareExecutionResult()
    {
        if (!_returnsExecutionResult || _preparedExecutionResult is not null) { return; }
        const long counterBytes = 80L;
        const long resultBytes = 72L;
        long publication = _publicationWork?.Remaining ?? 0L;
        if (_reservedWork > long.MaxValue - publication || _reservedWork + publication > long.MaxValue - 2L)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), long.MaxValue, _limits.MaxWork); }
        CheckWorkCapacity(_reservedWork + publication + 2L);
        _reservedResultWork = 2L;
        QpdfExecutionCounters? counters = null;
        RetainedValueOwners.Receipt? counterReceipt = null;
        bool counterReserved = false;
        bool resultReserved = false;
        try
        {
            ReserveMemory(counterBytes); counterReserved = true;
            counters = new(0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L);
            counterReceipt = RetainAllocation(counters, counterBytes);
            ReserveMemory(resultBytes); resultReserved = true;
            QpdfExecutionResult result = new(QpdfExecutionStatus.Completed, QpdfExitCode.Success,
                Array.Empty<QpdfDiagnostic>(), counters, true);
            RetainedValueOwners.Receipt resultReceipt = RetainAllocation(result, resultBytes);
            counterReceipt.RetirementFunded = true;
            resultReceipt.RetirementFunded = true;
            _preparedExecutionResult = result;
        }
        catch
        {
            if (resultReserved) { ReleaseMemory(resultBytes); }
            if (counters is not null && counterReceipt is not null) { DropAllocation(counters, counterReceipt); }
            else if (counterReserved) { ReleaseMemory(counterBytes); }
            _reservedResultWork = 0L;
            throw;
        }
    }

    internal QpdfExecutionResult FinishExecutionResult(ExecutionOutcome outcome)
    {
        _finishingExecution = true;
        try { return FinishExecutionResultCore(outcome); }
        finally { _finishingExecution = false; _executionFinished = true; }
    }

    private QpdfExecutionResult FinishExecutionResultCore(ExecutionOutcome outcome)
    {
        try { PrepareExecutionResult(); }
        catch (Exception ex) when (ex is QpdfException or OperationCanceledException)
        {
            // Generic fatal-result admission remains a separate audit gate.
            // Preserve an existing failure; a successful fileless outcome must
            // report failed admission rather than escape as a false success.
            if (outcome.Status == QpdfExecutionStatus.Completed)
            {
                outcome = ex is OperationCanceledException ? ExecutionOutcome.Canceled(outcome.Diagnostics)
                    : ExecutionOutcome.Failed([.. outcome.Diagnostics,
                        new QpdfDiagnostic(QpdfDiagnosticCode.BudgetExceeded, QpdfDiagnosticSeverity.Error, ex.Message)]);
            }
            _returnsExecutionResult = false;
            return new(outcome.Status, outcome.ExitCode, outcome.Diagnostics, Snapshot(), true);
        }
        QpdfExecutionResult result = _preparedExecutionResult
            ?? throw new InvalidOperationException("Missing admitted execution result.");
        _preparedExecutionResult = null; _returnsExecutionResult = false;
        result.CompleteBeforeReturn(outcome);
        result.Counters.CaptureBeforeReturn(CounterValues);
        return result;
    }

    internal PresentedDiagnostics? CompleteExecutionPresentation()
    {
        PrepareExecutionResult();
        return _executionPresentation?.Complete();
    }

    internal void AttachPublicationWork(PublicationWorkReservation reservation)
    {
        if (_publicationWork is not null) { throw new InvalidOperationException("Publication work is already held."); }
        if (_reservedWork > long.MaxValue - reservation.Remaining
            || _reservedWork + reservation.Remaining > long.MaxValue - _reservedResultWork)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), long.MaxValue, _limits.MaxWork); }
        CheckWorkCapacity(_reservedWork + reservation.Remaining + _reservedResultWork);
        _publicationWork = reservation;
    }

    internal void DetachPublicationWork(PublicationWorkReservation reservation)
    {
        if (!ReferenceEquals(_publicationWork, reservation)) { throw new InvalidOperationException("Publication work is unavailable."); }
        _publicationWork = null;
    }

    internal void AnticipatePresentationOutput(long bytes)
    {
        if (bytes < 0L) { throw new ArgumentOutOfRangeException(nameof(bytes)); }
        CheckCancellation();
        long output = checked(_totalOutputBytes + bytes);
        if (output > _limits.MaxOutputBytes)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), output, _limits.MaxOutputBytes); }
        _presentationCount?.AnticipateOutput(output, _totalDecodedBytes, _peakMemoryBytes);
    }

    internal void CheckPendingDiagnosticBytes(long bytes) => CheckDiagnosticBytes(bytes);

    private void CheckDiagnosticBytes(long bytes)
    {
        long next = checked(_diagnosticBytes + bytes);
        if (next > _limits.MaxDiagnosticBytes)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxDiagnosticBytes), next, _limits.MaxDiagnosticBytes); }
    }

    private void CheckPresentationBytes(long output, long decoded, long peak)
    {
        PresentationCountReservation? presentation = _presentationCount;
        if (presentation is null) { return; }
        CheckCancellation();
        CheckDiagnosticBytes(presentation.MessageBytes(output, decoded, peak));
    }

    /// <summary>Checks a prospective message without charging it a second time.</summary>
    internal void CheckDiagnostic(long messageBytes)
    {
        if (messageBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(messageBytes), "Reservation must not be negative.");
        }

        checked
        {
            CheckDiagnosticCount(1L + (_presentationCount?.Pending ?? 0L));

            long pendingBytes = _presentationCount?.MessageBytes(_totalOutputBytes, _totalDecodedBytes, _peakMemoryBytes) ?? 0L;
            CheckDiagnosticBytes(checked(messageBytes + pendingBytes));
        }
    }

    internal void CheckCancellation()
    {
        if (!_finishingDiagnosticFailure && !_finishingExecution) { _cancellationToken.ThrowIfCancellationRequested(); }
    }

    internal void NoteOperation()
    {
        checked
        {
            _operationsSinceCheck += 1L;
        }

        if (_operationsSinceCheck >= _limits.CancellationCheckInterval)
        {
            _operationsSinceCheck = 0L;
            if (!_finishingDiagnosticFailure && !_finishingExecution) { _cancellationToken.ThrowIfCancellationRequested(); }
        }
    }

    internal QpdfExecutionCounters Snapshot()
    {
        return new QpdfExecutionCounters(
            LiveMemoryBytes,
            _peakMemoryBytes,
            _totalWork,
            _totalDecodedBytes,
            _totalOutputBytes,
            _hostOperations,
            _openHandles,
            _diagnosticCount);
    }

    internal ExecutionCounterValues CounterValues => new(
        LiveMemoryBytes, _peakMemoryBytes, _totalWork, _totalDecodedBytes,
        _totalOutputBytes, _hostOperations, _openHandles, _diagnosticCount);
}
