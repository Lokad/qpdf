namespace Lokad.Qpdf;

/// <summary>Severity of a recorded diagnostic.</summary>
public enum QpdfDiagnosticSeverity
{
    /// <summary>Informational note; processing continues.</summary>
    Info = 0,

    /// <summary>Unusual but tolerated condition; processing continues.</summary>
    Warning = 1,

    /// <summary>Failure detail for a failed operation.</summary>
    Error = 2,
}

/// <summary>
/// Stable diagnostic codes. Codes never leak input content, physical paths,
/// host exception text, or stack traces; messages carry only safe summaries.
/// </summary>
public enum QpdfDiagnosticCode
{
    /// <summary>A resource budget was exceeded.</summary>
    BudgetExceeded = 0,

    /// <summary>A host operation failed and was reported explicitly.</summary>
    HostOperationFailed = 1,

    /// <summary>Planned outputs conflict with each other or with inputs.</summary>
    OutputConflict = 2,

    /// <summary>A dictionary repeats a key; the last value wins.</summary>
    DuplicateKey = 3,
}

/// <summary>A single structured diagnostic entry.</summary>
public sealed record QpdfDiagnostic
{
    /// <summary>Gets the stable code.</summary>
    public QpdfDiagnosticCode Code { get; }

    /// <summary>Gets the severity.</summary>
    public QpdfDiagnosticSeverity Severity { get; }

    /// <summary>Gets the sanitized message.</summary>
    public string Message { get; }

    /// <summary>Creates a diagnostic entry.</summary>
    /// <exception cref="ArgumentNullException">The message is null.</exception>
    public QpdfDiagnostic(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, string message)
    {
        Code = code;
        Severity = severity;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }
}

