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
    private long _reservedWork;
    private bool _presentingDiagnostics;

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
        try { owners.Drain(); DisposeEmptyValueOwners(owners); }
        finally { ShrinkDiagnosticWork(); }
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
        CheckWorkCapacity(capacity);
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

    internal void EndDiagnosticPresentation(DiagnosticStage stage)
    {
        if (ReferenceEquals(_diagnosticStage, stage)) { _presentingDiagnostics = false; }
    }

    internal void DetachDiagnosticStage(DiagnosticStage stage)
    {
        if (!ReferenceEquals(_diagnosticStage, stage)) { return; }
        _diagnosticStage = null;
        _presentingDiagnostics = false;
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
        else
        {
            if (units > long.MaxValue - _reservedWork) { throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), long.MaxValue, _limits.MaxWork); }
            long capacity = units + _reservedWork;
            CheckWorkCapacity(capacity);
        }
        _totalWork = checked(_totalWork + units);

        NoteOperation();
    }

    internal void ChargeDecodedBytes(long bytes)
    {
        CheckDecodedBytes(bytes);
        _totalDecodedBytes = checked(_totalDecodedBytes + bytes);
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

            if (bytes > 0L) { _presentationCount?.BeginOutput(); }
            _totalOutputBytes = next;
        }
    }

    internal void NoteHostOperation()
    {
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
        _diagnosticCount = checked(_diagnosticCount + pending);
    }

    internal void PresentDiagnosticCount() => _presentationCount?.Present();

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

            long nextBytes = _diagnosticBytes + messageBytes;
            if (nextBytes > _limits.MaxDiagnosticBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxDiagnosticBytes), nextBytes, _limits.MaxDiagnosticBytes);
            }
        }
    }

    internal void CheckCancellation()
    {
        _cancellationToken.ThrowIfCancellationRequested();
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
            _cancellationToken.ThrowIfCancellationRequested();
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
}
