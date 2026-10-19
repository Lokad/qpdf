namespace Lokad.Qpdf.Runtime;

/// <summary>A private outcome until execution and configuration scopes close.</summary>
// Intermediate outcomes are values, not escaping DTOs or counter snapshots.
internal readonly record struct ExecutionOutcome(
    QpdfExecutionStatus Status,
    QpdfExitCode ExitCode,
    IReadOnlyList<QpdfDiagnostic> Diagnostics)
{
    internal string? OutputText { get; init; }
    internal ReadOnlyMemory<byte>? OutputBytes { get; init; }

    internal ExecutionOutcome WithOutputText(string? text) => this with { OutputText = text };
    internal ExecutionOutcome WithOutputBytes(ReadOnlyMemory<byte>? bytes) => this with { OutputBytes = bytes };

    internal static ExecutionOutcome Completed(IReadOnlyList<QpdfDiagnostic> diagnostics)
    {
        bool warning = false;
        foreach (QpdfDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == QpdfDiagnosticSeverity.Error)
            { return new(QpdfExecutionStatus.Completed, QpdfExitCode.Error, diagnostics); }
            warning |= diagnostic.Severity == QpdfDiagnosticSeverity.Warning;
        }
        return new(QpdfExecutionStatus.Completed, warning ? QpdfExitCode.Warning : QpdfExitCode.Success, diagnostics);
    }

    internal static ExecutionOutcome Failed(IReadOnlyList<QpdfDiagnostic> diagnostics)
        => new(QpdfExecutionStatus.Failed, QpdfExitCode.Error, diagnostics);

    internal static ExecutionOutcome Canceled(IReadOnlyList<QpdfDiagnostic> diagnostics)
        => new(QpdfExecutionStatus.Canceled, QpdfExitCode.Error, diagnostics);
}

internal readonly record struct ExecutionCounterValues(
    long LiveMemoryBytes,
    long PeakMemoryBytes,
    long TotalDecodedBytes,
    long TotalOutputBytes,
    long HostOperations,
    long OpenHandles,
    long DiagnosticCount);
