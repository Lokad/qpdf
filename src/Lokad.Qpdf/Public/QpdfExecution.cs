using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

/// <summary>Library-level outcome of an executed operation.</summary>
public enum QpdfExecutionStatus
{
    /// <summary>The operation ran to completion.</summary>
    Completed = 0,

    /// <summary>The operation failed explicitly.</summary>
    Failed = 1,

    /// <summary>The operation observed cancellation.</summary>
    Canceled = 2,
}

/// <summary>
/// Process exit code for command adapters. The library never uses 1 as an
/// ordinary processing result; cancellation stays a distinct status.
/// </summary>
public enum QpdfExitCode
{
    /// <summary>Success.</summary>
    Success = 0,

    /// <summary>Error.</summary>
    Error = 2,

    /// <summary>Warnings without errors.</summary>
    Warning = 3,
}

/// <summary>A point-in-time snapshot of per-job resource counters.</summary>
public sealed record QpdfExecutionCounters
{
    private long _liveMemoryBytes;
    private long _peakMemoryBytes;
    private long _totalWork;
    private long _totalDecodedBytes;
    private long _totalOutputBytes;
    private long _hostOperations;
    private long _openHandles;
    private long _diagnosticCount;
    private ExecutionResultMemory? _storage;

    /// <summary>Creates a counter snapshot.</summary>
    public QpdfExecutionCounters(
        long liveMemoryBytes,
        long peakMemoryBytes,
        long totalWork,
        long totalDecodedBytes,
        long totalOutputBytes,
        long hostOperations,
        long openHandles,
        long diagnosticCount)
    {
        _liveMemoryBytes = liveMemoryBytes;
        _peakMemoryBytes = peakMemoryBytes;
        _totalWork = totalWork;
        _totalDecodedBytes = totalDecodedBytes;
        _totalOutputBytes = totalOutputBytes;
        _hostOperations = hostOperations;
        _openHandles = openHandles;
        _diagnosticCount = diagnosticCount;
    }

    private QpdfExecutionCounters(QpdfExecutionCounters source)
    {
        _liveMemoryBytes = source._liveMemoryBytes;
        _peakMemoryBytes = source._peakMemoryBytes;
        _totalWork = source._totalWork;
        _totalDecodedBytes = source._totalDecodedBytes;
        _totalOutputBytes = source._totalOutputBytes;
        _hostOperations = source._hostOperations;
        _openHandles = source._openHandles;
        _diagnosticCount = source._diagnosticCount;
    }

    /// <summary>Compares the eight observed counter values.</summary>
    public bool Equals(QpdfExecutionCounters? other) => other is not null
        && LiveMemoryBytes == other.LiveMemoryBytes && PeakMemoryBytes == other.PeakMemoryBytes
        && TotalWork == other.TotalWork && TotalDecodedBytes == other.TotalDecodedBytes
        && TotalOutputBytes == other.TotalOutputBytes && HostOperations == other.HostOperations
        && OpenHandles == other.OpenHandles && DiagnosticCount == other.DiagnosticCount;

    /// <summary>Hashes the eight observed counter values.</summary>
    public override int GetHashCode() => HashCode.Combine(LiveMemoryBytes, PeakMemoryBytes,
        TotalWork, TotalDecodedBytes, TotalOutputBytes, HostOperations, OpenHandles, DiagnosticCount);

    internal void OwnStorage(ExecutionResultMemory storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (_storage is not null) { throw new InvalidOperationException("Counter storage is already owned."); }
        _storage = storage;
    }

    /// <summary>Gets the estimated job-memory charge at snapshot time.</summary>
    /// <remarks>This immutable accounting value is not a live heap measurement.</remarks>
    public long LiveMemoryBytes => _liveMemoryBytes;

    /// <summary>Gets the high-water mark of the estimated job-memory charge.</summary>
    /// <remarks>Unreleased accounting charges do not establish retained memory.</remarks>
    public long PeakMemoryBytes => _peakMemoryBytes;

    /// <summary>Gets the charged units of bounded work.</summary>
    public long TotalWork => _totalWork;

    /// <summary>Gets the decoded stream payload bytes.</summary>
    public long TotalDecodedBytes => _totalDecodedBytes;

    /// <summary>Gets charged output bytes, including staged bytes discarded on failure.</summary>
    public long TotalOutputBytes => _totalOutputBytes;

    /// <summary>Gets the host storage calls.</summary>
    public long HostOperations => _hostOperations;

    /// <summary>Gets the currently open host handles.</summary>
    public long OpenHandles => _openHandles;

    /// <summary>Gets the recorded diagnostics.</summary>
    public long DiagnosticCount => _diagnosticCount;

    // Only a governor's private, prepaid snapshot is refreshed. No exposed
    // counter or caller-created record enters this preparation path.
    internal void CaptureBeforeReturn(ExecutionCounterValues counters)
    {
        _liveMemoryBytes = counters.LiveMemoryBytes;
        _peakMemoryBytes = counters.PeakMemoryBytes;
        _totalWork = counters.TotalWork;
        _totalDecodedBytes = counters.TotalDecodedBytes;
        _totalOutputBytes = counters.TotalOutputBytes;
        _hostOperations = counters.HostOperations;
        _openHandles = counters.OpenHandles;
        _diagnosticCount = counters.DiagnosticCount;
    }
}

/// <summary>Explicit outcome of an executed operation.</summary>
public sealed record QpdfExecutionResult
{
    private QpdfExecutionStatus _status;
    private QpdfExitCode _exitCode;
    private IReadOnlyList<QpdfDiagnostic> _diagnostics;
    private string? _outputText;
    private ReadOnlyMemory<byte>? _outputBytes;
    private ExecutionResultMemory? _storage;
    /// <summary>Gets the execution status.</summary>
    public QpdfExecutionStatus Status => _status;

    /// <summary>Gets the command-adapter exit code.</summary>
    public QpdfExitCode ExitCode => _exitCode;

    /// <summary>Gets the recorded diagnostics.</summary>
    public IReadOnlyList<QpdfDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Gets the final resource counters.</summary>
    public QpdfExecutionCounters Counters { get; private init; }

    /// <summary>
    /// Gets JSON inspection text when no output file was requested, or null
    /// for operations that publish to a host path or have no text payload.
    /// The caller owns the returned immutable text; no ambient stdout is used.
    /// </summary>
    public string? OutputText { get => _outputText; private init => _outputText = value; }

    /// <summary>
    /// Gets caller-owned binary inspection bytes when no host output path was
    /// requested, or null when no binary payload was produced. An empty payload
    /// has a value with length zero. Diagnostic text is recorded separately.
    /// The library does not mutate these bytes after returning the result.
    /// Failed or canceled execution returns no binary payload, even if an
    /// earlier inspection produced staged bytes. No ambient stdout is used.
    /// </summary>
    public ReadOnlyMemory<byte>? OutputBytes { get => _outputBytes; private init => _outputBytes = value; }

    /// <summary>Creates an execution result.</summary>
    /// <exception cref="ArgumentNullException">Diagnostics or counters are null.</exception>
    public QpdfExecutionResult(
        QpdfExecutionStatus status,
        QpdfExitCode exitCode,
        IReadOnlyList<QpdfDiagnostic> diagnostics,
        QpdfExecutionCounters counters)
        : this(status, exitCode, diagnostics, counters, false)
    { }

    internal QpdfExecutionResult(QpdfExecutionStatus status, QpdfExitCode exitCode,
        IReadOnlyList<QpdfDiagnostic> diagnostics, QpdfExecutionCounters counters, bool adoptDiagnostics)
    {
        _status = status;
        _exitCode = exitCode;
        _diagnostics = diagnostics is null
            ? throw new ArgumentNullException(nameof(diagnostics))
            : adoptDiagnostics ? diagnostics : [.. diagnostics];
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
    }

    private QpdfExecutionResult(QpdfExecutionResult source)
    {
        _status = source._status;
        _exitCode = source._exitCode;
        _diagnostics = source._diagnostics;
        _outputText = source._outputText;
        _outputBytes = source._outputBytes;
        Counters = source.Counters;
    }

    /// <summary>Compares the exposed outcome values and aliases.</summary>
    public bool Equals(QpdfExecutionResult? other) => other is not null
        && Status == other.Status && ExitCode == other.ExitCode
        && EqualityComparer<IReadOnlyList<QpdfDiagnostic>>.Default.Equals(Diagnostics, other.Diagnostics)
        && Counters.Equals(other.Counters) && OutputText == other.OutputText
        && Nullable.Equals(OutputBytes, other.OutputBytes);

    /// <summary>Hashes the exposed outcome values and aliases.</summary>
    public override int GetHashCode() => HashCode.Combine(Status, ExitCode, Diagnostics, Counters, OutputText, OutputBytes);

    internal void OwnStorage(ExecutionResultMemory storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (_storage is not null) { throw new InvalidOperationException("Result storage is already owned."); }
        _storage = storage;
    }

    internal void CompleteBeforeReturn(ExecutionOutcome outcome)
    {
        _status = outcome.Status;
        _exitCode = outcome.ExitCode;
        _diagnostics = outcome.Diagnostics;
        _outputText = outcome.OutputText;
        _outputBytes = outcome.OutputBytes;
    }

    /// <summary>Builds a completed result, mapping diagnostics to an exit code.</summary>
    public static QpdfExecutionResult Completed(
        IReadOnlyList<QpdfDiagnostic> diagnostics,
        QpdfExecutionCounters counters)
    {
        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        if (counters is null)
        {
            throw new ArgumentNullException(nameof(counters));
        }

        return new QpdfExecutionResult(QpdfExecutionStatus.Completed, MapCompletedExit(diagnostics), diagnostics, counters);
    }

    /// <summary>Builds a failed result.</summary>
    public static QpdfExecutionResult Failed(
        IReadOnlyList<QpdfDiagnostic> diagnostics,
        QpdfExecutionCounters counters)
    {
        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        if (counters is null)
        {
            throw new ArgumentNullException(nameof(counters));
        }

        return new QpdfExecutionResult(QpdfExecutionStatus.Failed, QpdfExitCode.Error, diagnostics, counters);
    }

    /// <summary>Builds a canceled result.</summary>
    public static QpdfExecutionResult Canceled(
        IReadOnlyList<QpdfDiagnostic> diagnostics,
        QpdfExecutionCounters counters)
    {
        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        if (counters is null)
        {
            throw new ArgumentNullException(nameof(counters));
        }

        return new QpdfExecutionResult(QpdfExecutionStatus.Canceled, QpdfExitCode.Error, diagnostics, counters);
    }

    private static QpdfExitCode MapCompletedExit(IReadOnlyList<QpdfDiagnostic> diagnostics)
    {
        bool warning = false;
        foreach (QpdfDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == QpdfDiagnosticSeverity.Error)
            {
                return QpdfExitCode.Error;
            }

            if (diagnostic.Severity == QpdfDiagnosticSeverity.Warning)
            {
                warning = true;
            }
        }

        return warning ? QpdfExitCode.Warning : QpdfExitCode.Success;
    }
}
