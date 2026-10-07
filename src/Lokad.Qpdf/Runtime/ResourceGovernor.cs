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
        owners.Drain();
        DisposeEmptyValueOwners(owners);
    }

    private void DisposeEmptyValueOwners(RetainedValueOwners owners)
    {
        if (!owners.Empty || owners.Busy) { return; }
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
    }

    internal long RetainValues(object wrapper, long wrapperBytes, IReadOnlyList<QpdfObject> roots, long reservedBytes)
    {
        RetainedValueOwners owners = _valueOwners ??= RetainedValueOwners.Create(this);
        try { return owners.Retain(wrapper, wrapperBytes, roots, reservedBytes); }
        catch { DisposeEmptyValueOwners(owners); throw; }
    }

    internal void ChargeWork(long units)
    {
        if (units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(units), "Charge must not be negative.");
        }

        checked
        {
            long next = _totalWork + units;
            if (next > _limits.MaxWork)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), next, _limits.MaxWork);
            }

            _totalWork = next;
        }

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

    /// <summary>Checks a prospective message without charging it a second time.</summary>
    internal void CheckDiagnostic(long messageBytes)
    {
        if (messageBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(messageBytes), "Reservation must not be negative.");
        }

        checked
        {
            long nextCount = _diagnosticCount + 1L;
            if (nextCount > _limits.MaxDiagnostics)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxDiagnostics), nextCount, _limits.MaxDiagnostics);
            }

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
