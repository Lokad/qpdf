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
        LiveMemoryBytes = liveMemoryBytes;
        PeakMemoryBytes = peakMemoryBytes;
        TotalWork = totalWork;
        TotalDecodedBytes = totalDecodedBytes;
        TotalOutputBytes = totalOutputBytes;
        HostOperations = hostOperations;
        OpenHandles = openHandles;
        DiagnosticCount = diagnosticCount;
    }

    /// <summary>Gets the currently retained managed bytes.</summary>
    public long LiveMemoryBytes { get; }

    /// <summary>Gets the high-water mark of retained managed bytes.</summary>
    public long PeakMemoryBytes { get; }

    /// <summary>Gets the charged units of bounded work.</summary>
    public long TotalWork { get; }

    /// <summary>Gets the decoded stream payload bytes.</summary>
    public long TotalDecodedBytes { get; }

    /// <summary>Gets charged output bytes, including staged bytes discarded on failure.</summary>
    public long TotalOutputBytes { get; }

    /// <summary>Gets the host storage calls.</summary>
    public long HostOperations { get; }

    /// <summary>Gets the currently open host handles.</summary>
    public long OpenHandles { get; }

    /// <summary>Gets the recorded diagnostics.</summary>
    public long DiagnosticCount { get; }
}/// <summary>Explicit outcome of an executed operation.</summary>
public sealed record QpdfExecutionResult
{
    /// <summary>Gets the execution status.</summary>
    public QpdfExecutionStatus Status { get; }

    /// <summary>Gets the command-adapter exit code.</summary>
    public QpdfExitCode ExitCode { get; }

    /// <summary>Gets the recorded diagnostics.</summary>
    public IReadOnlyList<QpdfDiagnostic> Diagnostics { get; }

    /// <summary>Gets the final resource counters.</summary>
    public QpdfExecutionCounters Counters { get; private init; }

    /// <summary>
    /// Gets JSON inspection text when no output file was requested, or null
    /// for operations that publish to a host path or have no text payload.
    /// The caller owns the returned immutable text; no ambient stdout is used.
    /// </summary>
    public string? OutputText { get; private init; }

    internal QpdfExecutionResult WithOutputText(string? text) => this with { OutputText = text };

    /// <summary>
    /// Gets caller-owned binary inspection bytes when no host output path was
    /// requested, or null when no binary payload was produced. An empty payload
    /// has a value with length zero. Diagnostic text is recorded separately.
    /// The library does not mutate these bytes after returning the result.
    /// Failed or canceled execution returns no binary payload, even if an
    /// earlier inspection produced staged bytes. No ambient stdout is used.
    /// </summary>
    public ReadOnlyMemory<byte>? OutputBytes { get; private init; }

    internal QpdfExecutionResult WithOutputBytes(ReadOnlyMemory<byte>? bytes) => this with { OutputBytes = bytes };

    internal QpdfExecutionResult WithCounters(QpdfExecutionCounters counters) => this with
    {
        Counters = counters ?? throw new ArgumentNullException(nameof(counters)),
    };

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
        Status = status;
        ExitCode = exitCode;
        Diagnostics = diagnostics is null
            ? throw new ArgumentNullException(nameof(diagnostics))
            : adoptDiagnostics ? diagnostics : [.. diagnostics];
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
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
