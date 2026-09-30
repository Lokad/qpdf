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
public sealed record QpdfExecutionCounters(
    long LiveMemoryBytes,
    long PeakMemoryBytes,
    long TotalWork,
    long TotalDecodedBytes,
    long TotalOutputBytes,
    long HostOperations,
    long OpenHandles,
    long DiagnosticCount);

/// <summary>Explicit outcome of an executed operation.</summary>
public sealed record QpdfExecutionResult
{
    /// <summary>Gets the execution status.</summary>
    public QpdfExecutionStatus Status { get; }

    /// <summary>Gets the command-adapter exit code.</summary>
    public QpdfExitCode ExitCode { get; }

    /// <summary>Gets the recorded diagnostics.</summary>
    public IReadOnlyList<QpdfDiagnostic> Diagnostics { get; }

    /// <summary>Gets the final resource counters.</summary>
    public QpdfExecutionCounters Counters { get; }

    /// <summary>Creates an execution result.</summary>
    /// <exception cref="ArgumentNullException">Diagnostics or counters are null.</exception>
    public QpdfExecutionResult(
        QpdfExecutionStatus status,
        QpdfExitCode exitCode,
        IReadOnlyList<QpdfDiagnostic> diagnostics,
        QpdfExecutionCounters counters)
    {
        Status = status;
        ExitCode = exitCode;
        Diagnostics = diagnostics is null
            ? throw new ArgumentNullException(nameof(diagnostics))
            : [.. diagnostics];
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
