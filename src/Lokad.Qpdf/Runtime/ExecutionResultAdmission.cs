namespace Lokad.Qpdf.Runtime;

/// <summary>Immutable rejection outcomes borrowed before any job is admitted.</summary>
internal static class ExecutionResultAdmission
{
    // Coarse capacity for the result and counters, admitted before processing.
    internal const long MemoryBytes = 168L;
    internal const string InvalidZlibModeMessage = "The zlib-flate mode is not supported.";
    internal const string InvalidZlibLevelMessage = "The zlib-flate compression level must be 1..9 while compressing.";

    private static readonly QpdfExecutionCounters Zero = new(0L, 0L, 0L, 0L, 0L, 0L, 0L);
    internal static QpdfExecutionCounters EmptyCounters => Zero;
    private static readonly QpdfExecutionResult MemoryFailure = Failure(QpdfDiagnosticCode.BudgetExceeded,
        "MaxMemoryBytes cannot admit the execution outcome.");
    internal static readonly QpdfExecutionResult SharedMemoryFailure = Failure(QpdfDiagnosticCode.BudgetExceeded,
        "The host's shared memory allowance refused admission.");
    private static readonly QpdfExecutionResult Cancellation = new(QpdfExecutionStatus.Canceled,
        QpdfExitCode.Error, Array.Empty<QpdfDiagnostic>(), Zero, true);

    internal static readonly QpdfExecutionResult InvalidZlibMode = Failure(QpdfDiagnosticCode.InvalidArguments, InvalidZlibModeMessage);
    internal static readonly QpdfExecutionResult InvalidZlibLevel = Failure(QpdfDiagnosticCode.InvalidArguments, InvalidZlibLevelMessage);

    private static QpdfExecutionResult Failure(QpdfDiagnosticCode code, string message)
        => new(QpdfExecutionStatus.Failed, QpdfExitCode.Error,
            Array.AsReadOnly(new[] { new QpdfDiagnostic(code, QpdfDiagnosticSeverity.Error, message) }), Zero, true);

    internal static QpdfExecutionResult? Reject(QpdfLimits limits, CancellationToken token)
    {
        if (token.IsCancellationRequested) { return Cancellation; }
        if (limits.MaxMemoryBytes < MemoryBytes) { return MemoryFailure; }
        return null;
    }
}
