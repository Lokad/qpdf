
namespace Lokad.Qpdf.Runtime;

/// <summary>
/// Single per-job resource governor. Every subsystem reserves budget before
/// allocating and checks cooperative cancellation; breaches fail explicitly without
/// allocating. One owner per job; codec workers share synchronization for memory
/// admission and output-buffer accounting. Other job state remains single-owner.
/// </summary>
internal sealed class ResourceGovernor
{
    private readonly QpdfLimits _limits;
    private readonly CancellationToken _cancellationToken;
    private readonly SharedMemoryOwner? _sharedMemory;
    private object? _codecMemoryGate;
    private long _liveMemoryBytes;
    private long _hostMemoryBytes;
    private long _callerMetadataBytes;
    private long _peakMemoryBytes;
    private long _totalDecodedBytes;
    private long _totalOutputBytes;
    private long _hostOperations;
    private long _openHandles;
    private long _diagnosticCount;
    private long _diagnosticBytes;
    private long _operationsSinceCheck;
    private RetainedStorage? _valueOwners;
    private ExecutionPreparation? _executionPresentation;
    private PublicationReservation? _publication;
    private bool _returnsExecutionResult;
    private bool _finishingExecution;
    private QpdfExecutionResult? _preparedExecutionResult;
    private QpdfExecutionCounters? _preparedTypedCounters;
    private long _preparedTypedResultBytes;
    internal bool OwnsDocumentCounters { get; set; }
    internal bool DocumentAccepted { get; private set; }
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

    // Installed by the job owner before a codec call can publish work to threads.
    // Jobs without a codec keep the existing path without a lock or gate object.
    internal object CodecMemoryGate => _codecMemoryGate ??= new();

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

    internal long RemainingMemoryBytes
    {
        get
        {
            if (_codecMemoryGate is object gate)
            { lock (gate) { return Math.Max(0L, _limits.MaxMemoryBytes - LiveMemoryBytes); } }
            return Math.Max(0L, _limits.MaxMemoryBytes - LiveMemoryBytes);
        }
    }
    internal long RemainingDecodedBytes => _limits.MaxDecodedBytes - _totalDecodedBytes;

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
        if (_codecMemoryGate is object gate)
        { lock (gate) { ReserveMemoryCore(bytes, hostStorage); } }
        else { ReserveMemoryCore(bytes, hostStorage); }
    }

    private void ReserveMemoryCore(long bytes, bool hostStorage)
    {
        checked
        {
            long next = LiveMemoryBytes + bytes;
            if (next > _limits.MaxMemoryBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxMemoryBytes), next, _limits.MaxMemoryBytes);
            }

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

        if (_codecMemoryGate is object gate)
        { lock (gate) { ReleaseMemoryCore(bytes); } }
        else { ReleaseMemoryCore(bytes); }
    }

    private void ReleaseMemoryCore(long bytes)
    {
        _liveMemoryBytes = Math.Max(0L, _liveMemoryBytes - bytes);
        _sharedMemory?.ReleaseTo(Math.Max(0L, LiveMemoryBytes - _hostMemoryBytes));
    }

    internal void ReleaseHostMemory(long bytes)
    {
        if (_codecMemoryGate is object gate)
        { lock (gate) { ReleaseHostMemoryCore(bytes); } }
        else { ReleaseHostMemoryCore(bytes); }
    }

    private void ReleaseHostMemoryCore(long bytes)
    {
        if (bytes < 0L || bytes > _hostMemoryBytes)
        { throw new InvalidOperationException("Host storage release exceeds its local policy estimate."); }
        _hostMemoryBytes -= bytes;
        ReleaseMemoryCore(bytes);
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

    internal long RetainGraph(long reservedBytes)
    {
        if (reservedBytes == 0L) { return 0L; }
        return ValueOwners.RetainGraph(reservedBytes);
    }

    internal long RetainAllocations(OwnedAllocationBatch allocations, long reservedBytes)
    {
        RetainedStorage owners = ValueOwners;
        return owners.Retain(allocations, reservedBytes);
    }

    internal RetainedStorage.Receipt RetainAllocation(long reservedBytes)
    {
        RetainedStorage owners = ValueOwners;
        return owners.RetainAllocation(reservedBytes);
    }

    internal void DropAllocation(RetainedStorage.Receipt receipt)
    {
        RetainedStorage owners = _valueOwners ?? throw new InvalidOperationException("Allocation owner is unavailable.");
        ReleaseMemory(owners.DropAllocation(receipt));
    }

    internal long RetainCheckOutcome(long reservedBytes)
    {
        RetainedStorage owners = ValueOwners;
        return owners.RetainCheckOutcome(reservedBytes);
    }


    internal void ChargeDecodedBytes(long bytes)
    {
        CheckDecodedBytes(bytes);
        long next = checked(_totalDecodedBytes + bytes);
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

            _totalOutputBytes = next;
        }
    }

    internal void NoteHostOperation()
    {
        if (_publication is PublicationReservation held && !held.Publishing)
        {
            if (held.HostOperations == long.MaxValue)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxHostOperations), long.MaxValue, _limits.MaxHostOperations); }
            CheckHostOperationCapacity(held.HostOperations + 1L);
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

    internal PublicationReservation? ReserveOutputWrapper()
    {
        if (_publication is PublicationReservation publication && publication.Publishing)
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

    internal long PresentationOutputBytes => _totalOutputBytes;
    internal long PresentationDecodedBytes => _totalDecodedBytes;
    internal long PresentationPeakBytes => _peakMemoryBytes;

    internal void AttachExecutionPresentation(ExecutionPresentation presentation)
    {
        if (_executionPresentation is not null) { throw new InvalidOperationException("A job already has an execution presentation."); }
        _executionPresentation = presentation;
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
        ReserveCallerMetadata(ExecutionResultAdmission.MemoryBytes);
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
        long bytes = checked(resultBytes + 88L);
        ReserveCallerMetadata(bytes);
        try
        {
            QpdfExecutionCounters counters = new(0L, 0L, 0L, 0L, 0L, 0L, 0L);
            _preparedTypedCounters = counters;
            _preparedTypedResultBytes = resultBytes;
            counters.CaptureBeforeReturn(CounterValues);
        }
        catch { ReleaseCallerMetadata(bytes); throw; }
    }

    internal void AcceptTypedResult()
    {
        if (_preparedTypedResultBytes == 0L)
        { throw new InvalidOperationException("Missing admitted typed result storage."); }
        // The bounded wrapper constructor has executed. No ordinary work or
        // cancellation poll can follow completed publication at this boundary.
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

    internal void CompleteStorage()
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
        ReserveCallerMetadata(PdfVersion.StorageBytes);
        try { return new(major, minor); }
        catch { ReleaseCallerMetadata(PdfVersion.StorageBytes); throw; }
    }

    internal QpdfExecutionCounters OwnedSnapshot()
    {
        CheckCancellation();
        ReserveCallerMetadata(88L);
        try
        {
            QpdfExecutionCounters counters = new(0L, 0L, 0L, 0L, 0L, 0L, 0L);
            counters.CaptureBeforeReturn(CounterValues);
            return counters;
        }
        catch { ReleaseCallerMetadata(88L); throw; }
    }

    // The fixed reserve was admitted before any processing. Construction has
    // no graph/index traversal or further publication/presentation admission.
    internal void PrepareExecutionResult()
    {
        if (!_returnsExecutionResult || _preparedExecutionResult is not null) { return; }
        CheckCancellation();
        try
        {
            QpdfExecutionCounters counters = new(0L, 0L, 0L, 0L, 0L, 0L, 0L);
            QpdfExecutionResult result = new(QpdfExecutionStatus.Completed, QpdfExitCode.Success,
                Array.Empty<QpdfDiagnostic>(), counters, true);
            _preparedExecutionResult = result;
        }
        catch
        {
            _returnsExecutionResult = false;
            ReleaseCallerMetadata(ExecutionResultAdmission.MemoryBytes);
            throw;
        }
        CheckCancellation();
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
        CompletePrivateStorage();
        _sharedMemory?.Complete(Math.Max(0L, LiveMemoryBytes - _hostMemoryBytes));
    }

    internal PresentedDiagnostics CompleteExecutionPresentation()
    {
        PrepareExecutionResult();
        return (ExecutionPresentation ?? throw new InvalidOperationException("Missing execution presentation.")).Complete();
    }

    internal void AttachPublication(PublicationReservation reservation)
    {
        if (_publication is not null) { throw new InvalidOperationException("Publication storage is already held."); }
        _publication = reservation;
    }

    internal void DetachPublication(PublicationReservation reservation)
    {
        if (!ReferenceEquals(_publication, reservation)) { throw new InvalidOperationException("Publication storage is unavailable."); }
        _publication = null;
    }

    private void CheckDiagnosticBytes(long bytes)
    {
        long next = checked(_diagnosticBytes + bytes);
        if (next > _limits.MaxDiagnosticBytes)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxDiagnosticBytes), next, _limits.MaxDiagnosticBytes); }
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
            CheckDiagnosticCount(1L);

            CheckDiagnosticBytes(messageBytes);
        }
    }

    internal void CheckCancellation()
    {
        if (!_finishingExecution) { _cancellationToken.ThrowIfCancellationRequested(); }
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
            _totalDecodedBytes,
            _totalOutputBytes,
            _hostOperations,
            _openHandles,
            _diagnosticCount);
    }

    internal ExecutionCounterValues CounterValues => new(
        LiveMemoryBytes, _peakMemoryBytes, _totalDecodedBytes,
        _totalOutputBytes, _hostOperations, _openHandles, _diagnosticCount);
}
