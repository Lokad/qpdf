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
    private readonly SharedMemoryOwner? _sharedMemory;
    private long _liveMemoryBytes;
    private long _hostMemoryBytes;
    private long _callerMetadataBytes;
    private long _peakMemoryBytes;
    private long _totalWork;
    private long _totalDecodedBytes;
    private long _totalOutputBytes;
    private long _hostOperations;
    private long _openHandles;
    private long _diagnosticCount;
    private long _diagnosticBytes;
    private long _operationsSinceCheck;
    private RetainedStorage? _valueOwners;
    private DiagnosticStage? _diagnosticStage;
    private PresentationCountReservation? _presentationCount;
    private ExecutionPreparation? _executionPresentation;
    private PublicationWorkReservation? _publicationWork;
    private long _reservedWork;
    private long _reservedResultWork;
    private bool _presentingDiagnostics;
    private bool _finishingDiagnosticFailure;
    private bool _returnsExecutionResult;
    private bool _finishingExecution;
    private QpdfExecutionResult? _preparedExecutionResult;
    private bool _buildingExecutionResult;
    private QpdfExecutionCounters? _preparedTypedCounters;
    private long _preparedTypedResultBytes;
    internal bool OwnsDocumentCounters { get; set; }
    internal bool DocumentAccepted { get; private set; }
    internal bool DeferredPlainHistory { get; set; }
    internal ExecutionPresentation? ExecutionPresentation => _executionPresentation as ExecutionPresentation;

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

    internal ResourceGovernor(QpdfLimits limits, CancellationToken cancellationToken, IQpdfHost host)
        : this(limits, cancellationToken)
    {
        JpegCodec = (host as IQpdfJpegCodecHost)?.JpegCodec;
        if (host is IQpdfMemoryHost memoryHost && memoryHost.MemoryBudget is IMemoryBudget budget)
        { _sharedMemory = new(budget, limits.MaxMemoryBytes); }
    }

    internal IQpdfJpegCodec? JpegCodec { get; init; }

    internal CancellationToken CancellationToken => _cancellationToken;

    internal long MaxNestingDepth => _limits.MaxNestingDepth;

    private long LiveMemoryBytes => _liveMemoryBytes;

    // Small escaping values share a coarse caller owner. This category stays
    // charged independently of private graph/cache cleanup and CLR collection.
    internal long CallerMetadataBytes => _callerMetadataBytes;
    internal long CallerStorageBytes => _valueOwners?.CallerBytes ?? 0L;
    internal long PrivateRetainedBytes => _valueOwners?.PrivateBytes ?? 0L;

    private void ReserveCallerMetadata(long bytes)
    {
        long next = checked(_callerMetadataBytes + bytes);
        ReserveMemory(bytes);
        _callerMetadataBytes = next;
    }

    private void ReleaseCallerMetadata(long bytes)
    {
        _callerMetadataBytes -= bytes;
        ReleaseMemory(bytes);
    }

    internal long RemainingMemoryBytes => Math.Max(0L, _limits.MaxMemoryBytes - LiveMemoryBytes);
    internal long RemainingWorkUnits => Math.Max(0L, _limits.MaxWork - _totalWork - _reservedWork
        - (_publicationWork?.Remaining ?? 0L) - _reservedResultWork);

    internal void ReserveMemory(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Reservation must not be negative.");
        }

        ReserveReconciledMemory(bytes);
    }

    private void ReserveReconciledMemory(long bytes)
        => ReserveReconciledMemory(bytes, false);

    internal void ReserveHostMemory(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ReserveReconciledMemory(bytes, true);
    }

    private void ReserveReconciledMemory(long bytes, bool hostStorage)
    {
        checked
        {
            long next = LiveMemoryBytes + bytes;
            if (next > _limits.MaxMemoryBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxMemoryBytes), next, _limits.MaxMemoryBytes);
            }

            CheckPresentationBytes(_totalOutputBytes, _totalDecodedBytes, Math.Max(next, _peakMemoryBytes));
            long rawNext = _liveMemoryBytes + bytes;
            long hostNext = hostStorage ? _hostMemoryBytes + bytes : _hostMemoryBytes;
            _sharedMemory?.Ensure(Math.Max(0L, next - hostNext));
            _liveMemoryBytes = rawNext;
            _hostMemoryBytes = hostNext;
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

        _liveMemoryBytes = Math.Max(0L, _liveMemoryBytes - bytes);
        _sharedMemory?.ReleaseTo(Math.Max(0L, LiveMemoryBytes - _hostMemoryBytes));
    }

    internal void ReleaseHostMemory(long bytes)
    {
        if (bytes < 0L || bytes > _hostMemoryBytes)
        { throw new InvalidOperationException("Host storage release exceeds its local policy estimate."); }
        _hostMemoryBytes -= bytes;
        ReleaseMemory(bytes);
    }

    internal void CompletePrivateStorage()
    {
        RetainedStorage? storage = _valueOwners;
        storage?.Complete();
        if (storage?.CallerBytes == 0L) { _valueOwners = null; }
    }

    private RetainedStorage ValueOwners => _valueOwners ??= new(this);

    internal void RetainPrivateStorage(long bytes) => ValueOwners.RetainPrivate(bytes);

    internal void RetainCallerStorage(long bytes) => ValueOwners.RetainCaller(bytes);

    internal void PrepareResultStorage(long bytes) => ValueOwners.RetainResult(bytes);
    internal void PromoteAllocation(RetainedStorage.Receipt receipt) => ValueOwners.PromoteAllocation(receipt);
    internal void GrowAllocation(RetainedStorage.Receipt receipt, long bytes) => ValueOwners.GrowAllocation(receipt, bytes);

    internal long RetainValue(QpdfObject value, long reservedBytes)
    {
        if (reservedBytes == 0L) { return 0L; }
        RetainedStorage owners = ValueOwners;
        try { return owners.Retain(value, reservedBytes); }
        finally { ShrinkDiagnosticWork(); }
    }

    internal long RetainValues(object wrapper, long wrapperBytes, IReadOnlyList<QpdfObject> roots, long reservedBytes)
    {
        RetainedStorage owners = ValueOwners;
        try { return owners.Retain(wrapper, wrapperBytes, roots, reservedBytes); }
        finally { ShrinkDiagnosticWork(); }
    }

    internal long RetainAllocations(OwnedAllocationBatch allocations, long reservedBytes)
    {
        RetainedStorage owners = ValueOwners;
        return owners.Retain(allocations, reservedBytes);
    }

    internal long RetainAllocations(OwnedAllocationBatch allocations, QpdfObject parsed, long reservedBytes)
    {
        RetainedStorage owners = ValueOwners;
        return owners.Retain(allocations, parsed, reservedBytes);
    }

    internal RetainedStorage.Receipt RetainAllocation(object carrier, long reservedBytes)
    {
        RetainedStorage owners = ValueOwners;
        try { return owners.RetainAllocation(carrier, reservedBytes); }
        finally { ShrinkDiagnosticWork(); }
    }

    internal void DropAllocation(object carrier, RetainedStorage.Receipt receipt)
    {
        RetainedStorage owners = _valueOwners ?? throw new InvalidOperationException("Allocation owner is unavailable.");
        ReleaseMemory(owners.DropAllocation(carrier, receipt));
    }

    internal long RetainDiagnostic(QpdfDiagnostic diagnostic, long reservedBytes)
    {
        RetainedStorage owners = ValueOwners;
        try { return owners.RetainDiagnostic(diagnostic, reservedBytes); }
        finally { ShrinkDiagnosticWork(); }
    }

    internal long RetainCheckOutcome(QpdfCheckOutcome outcome, long detailBytes, long reservedBytes)
    {
        RetainedStorage owners = ValueOwners;
        try { return owners.RetainCheckOutcome(outcome, detailBytes, reservedBytes); }
        finally { ShrinkDiagnosticWork(); }
    }

    internal string? FindDiagnosticMessage(string message, int hash)
        => _valueOwners?.FindDiagnosticMessage(message, hash);

    internal string? FindDiagnosticMessage(ReadOnlySpan<DiagnosticPart> parts, DiagnosticTextInfo info)
        => _valueOwners?.FindDiagnosticMessage(parts, info);

    internal long RetainCachedDiagnostic(QpdfDiagnostic diagnostic, long reservedBytes, int messageHash)
    {
        RetainedStorage owners = ValueOwners;
        try { return owners.RetainCachedDiagnostic(diagnostic, reservedBytes, messageHash); }
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
        // Memo growth preflights the future presentation bound. Shrinking this
        // credit cannot fail after a successful ownership transfer.
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
            // Each reservation is admitted against the same remaining budget.
            // Subtracting those nonnegative bounds cannot overflow; construct
            // the potentially overflowing requested total only on failure.
            long available = _limits.MaxWork - _totalWork - _reservedWork - heldPublicationUnits - _reservedResultWork;
            if (units > available) { CheckReservedWorkCapacity(units, heldPublicationUnits); }
        }
        _totalWork = checked(_totalWork + units);

        NoteOperation();
    }

    private void CheckReservedWorkCapacity(long units, long heldPublicationUnits)
    {
        if (units > long.MaxValue - _reservedWork || units + _reservedWork > long.MaxValue - heldPublicationUnits
            || units + _reservedWork + heldPublicationUnits > long.MaxValue - _reservedResultWork)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), long.MaxValue, _limits.MaxWork); }
        CheckWorkCapacity(units + _reservedWork + heldPublicationUnits + _reservedResultWork);
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

    internal void AttachResultWarnings(ResultWarningSnapshot snapshot)
    {
        if (_executionPresentation is not null) { throw new InvalidOperationException("Result preparation is already attached."); }
        _executionPresentation = snapshot;
    }

    internal void DetachResultWarnings(ResultWarningSnapshot snapshot)
    {
        if (!ReferenceEquals(_executionPresentation, snapshot)) { throw new InvalidOperationException("Result warnings are unavailable."); }
        _executionPresentation = null;
    }

    internal void ExpectExecutionResult()
    {
        CheckWorkCapacity(ExecutionResultAdmission.WorkUnits);
        ReserveCallerMetadata(ExecutionResultAdmission.MemoryBytes);
        _reservedResultWork = ExecutionResultAdmission.WorkUnits;
        _returnsExecutionResult = true;
    }

    internal bool TryExpectExecutionResult()
    {
        try { ExpectExecutionResult(); return true; }
        catch (QpdfMemoryAdmissionException) { _sharedMemory?.Complete(Math.Max(0L, LiveMemoryBytes - _hostMemoryBytes)); return false; }
    }

    // Counter and wrapper capacity is admitted together beneath the caller's
    // owner. Escaping values carry no finalizer, weak link or governor reference.
    internal QpdfExecutionCounters TypedCounters => _preparedTypedCounters ?? ExecutionResultAdmission.EmptyCounters;
    internal bool HasTypedResult => _preparedTypedCounters is not null;

    internal void PrepareTypedResult(long resultBytes)
    {
        if (resultBytes <= 0L) { throw new ArgumentOutOfRangeException(nameof(resultBytes)); }
        if (_preparedTypedCounters is not null || _preparedTypedResultBytes != 0L || _returnsExecutionResult)
        { throw new InvalidOperationException("Typed result storage is already prepared."); }
        CheckCancellation();
        long held = checked(_reservedWork + (_publicationWork?.Remaining ?? 0L) + _reservedResultWork);
        if (held > long.MaxValue - 2L)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), long.MaxValue, _limits.MaxWork); }
        CheckWorkCapacity(held + 2L);
        long bytes = checked(resultBytes + 88L);
        ReserveCallerMetadata(bytes);
        _buildingExecutionResult = true;
        try
        {
            QpdfExecutionCounters counters = new(0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L);
            _reservedResultWork += 2L;
            ChargeExecutionResultWork(1L);
            _preparedTypedCounters = counters;
            _preparedTypedResultBytes = resultBytes;
            counters.CaptureBeforeReturn(CounterValues);
        }
        catch { ReleaseCallerMetadata(bytes); throw; }
        finally { _buildingExecutionResult = false; }
    }

    internal void AcceptTypedResult()
    {
        if (_preparedTypedResultBytes == 0L)
        { throw new InvalidOperationException("Missing admitted typed result storage."); }
        // The bounded wrapper constructor has executed. No ordinary work or
        // cancellation poll can follow completed publication at this boundary.
        _buildingExecutionResult = true;
        try { ChargeExecutionResultWork(1L); }
        finally { _buildingExecutionResult = false; }
        _valueOwners?.AcceptResults();
        _preparedTypedResultBytes = 0L;
    }

    internal void FinishTypedCounters()
    {
        if (_preparedTypedResultBytes != 0L)
        { throw new InvalidOperationException("Typed result storage has not transferred."); }
        QpdfExecutionCounters counters = _preparedTypedCounters
            ?? throw new InvalidOperationException("Missing admitted typed counters.");
        CompletePrivateStorage();
        counters.CaptureBeforeReturn(CounterValues);
        _preparedTypedCounters = null;
        _sharedMemory?.Complete(Math.Max(0L, LiveMemoryBytes - _hostMemoryBytes));
    }

    internal void AbandonTypedCounters()
    {
        if (_preparedTypedResultBytes != 0L)
        {
            // The prepared counters and unused wrapper never escaped.
            ReleaseCallerMetadata(checked(_preparedTypedResultBytes + 88L));
            _preparedTypedResultBytes = 0L;
            _reservedResultWork -= 1L;
        }
        _preparedTypedCounters = null;
        CompletePrivateStorage();
        _sharedMemory?.Complete(Math.Max(0L, LiveMemoryBytes - _hostMemoryBytes));
    }

    internal void AcceptDocument()
    {
        _valueOwners?.AcceptResults();
        DocumentAccepted = true;
    }

    internal void AbandonDocument()
    {
        CompletePrivateStorage();
        _sharedMemory?.Complete(Math.Max(0L, LiveMemoryBytes - _hostMemoryBytes));
    }

    internal void FinishDocumentCounters()
    {
        if (OwnsDocumentCounters)
        {
            CompletePrivateStorage();
            _sharedMemory?.TrimTo(Math.Max(0L, LiveMemoryBytes - _hostMemoryBytes));
            // Counter snapshots remain public after document disposal. Keep
            // admission usable; no GC callback can refund earlier aliases.
        }
    }

    // Small public metadata is covered by coarse caller-owned estimates;
    // collection cannot refund its allowance while other aliases remain usable.
    internal PdfVersion OwnVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        CheckCancellation();
        ChargeWork(1L);
        ReserveCallerMetadata(PdfVersion.StorageBytes);
        try { return new(major, minor); }
        catch { ReleaseCallerMetadata(PdfVersion.StorageBytes); throw; }
    }

    internal QpdfExecutionCounters OwnedSnapshot()
    {
        CheckCancellation();
        ChargeWork(1L);
        ReserveCallerMetadata(88L);
        try
        {
            QpdfExecutionCounters counters = new(0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L);
            counters.CaptureBeforeReturn(CounterValues);
            return counters;
        }
        catch { ReleaseCallerMetadata(88L); throw; }
    }

    // The fixed reserve was admitted before any processing. Construction has
    // no graph/index work and cannot consume publication or diagnostic credit.
    internal void PrepareExecutionResult()
    {
        if (!_returnsExecutionResult || _preparedExecutionResult is not null) { return; }
        CheckCancellation();
        _buildingExecutionResult = true;
        try
        {
            QpdfExecutionCounters counters = new(0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L);
            QpdfExecutionResult result = new(QpdfExecutionStatus.Completed, QpdfExitCode.Success,
                Array.Empty<QpdfDiagnostic>(), counters, true);
            ChargeExecutionResultWork(2L);
            _preparedExecutionResult = result;
        }
        catch
        {
            _returnsExecutionResult = false;
            ReleaseCallerMetadata(ExecutionResultAdmission.MemoryBytes);
            throw;
        }
        finally { _buildingExecutionResult = false; }
        CheckCancellation();
    }

    private void ChargeExecutionResultWork(long units)
    {
        if (units > _reservedResultWork) { throw new InvalidOperationException("Execution result work is unavailable."); }
        _reservedResultWork -= units;
        _totalWork = checked(_totalWork + units);
        NoteOperation();
    }

    internal QpdfExecutionResult FinishExecutionResult(ExecutionOutcome outcome)
    {
        _finishingExecution = true;
        try
        {
            PrepareExecutionResult();
            QpdfExecutionResult result = _preparedExecutionResult
                ?? throw new InvalidOperationException("Missing admitted execution result.");
            result.CompleteBeforeReturn(outcome);
            _valueOwners?.AcceptResults();
            CompletePrivateStorage();
            result.Counters.CaptureBeforeReturn(CounterValues);
            _preparedExecutionResult = null; _returnsExecutionResult = false;
            return result;
        }
        catch (QpdfMemoryAdmissionException) { return ExecutionResultAdmission.SharedMemoryFailure; }
        finally { AbandonExecutionResult(); _finishingExecution = false; }
    }

    // Every command boundary invokes this after its component/async cleanup,
    // including when an unexpected exception bypasses result construction.
    internal void AbandonExecutionResult()
    {
        _preparedExecutionResult = null;
        if (_returnsExecutionResult)
        {
            _returnsExecutionResult = false;
            ReleaseCallerMetadata(ExecutionResultAdmission.MemoryBytes);
        }
        _reservedResultWork = 0L;
        CompletePrivateStorage();
        _sharedMemory?.Complete(Math.Max(0L, LiveMemoryBytes - _hostMemoryBytes));
    }

    internal PresentedDiagnostics? CompleteExecutionPresentation()
    {
        PrepareExecutionResult();
        return ExecutionPresentation?.Complete();
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
        if (_cancellationToken.IsCancellationRequested
            && !_finishingDiagnosticFailure && !_finishingExecution && !_buildingExecutionResult)
        { _cancellationToken.ThrowIfCancellationRequested(); }
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
            CheckCancellation();
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
