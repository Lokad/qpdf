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

    /// <summary>A declared page count disagrees with traversal.</summary>
    PageCountMismatch = 4,

    /// <summary>An excluded cryptographic operation was requested.</summary>
    UnsupportedCryptoOperation = 5,

    /// <summary>Invocation arguments or job fields are invalid.</summary>
    InvalidArguments = 6,

    /// <summary>Valid input needs an unimplemented feature.</summary>
    UnsupportedFeature = 7,

    /// <summary>A declared check did not run; its prerequisite is unimplemented.</summary>
    CheckSkipped = 8,

    /// <summary>A declared check ran and found a defect.</summary>
    CheckFailed = 9,

    /// <summary>Bounded repair rebuilt cross-references after strict parsing failed.</summary>
    RecoveryEngaged = 10,

    /// <summary>Help or version text for an identity request.</summary>
    HelpText = 11,

    /// <summary>Attachment listing line.</summary>
    AttachmentListing = 12,

    /// <summary>Linearization inspection line.</summary>
    LinearizationReport = 13,

    /// <summary>Page-count inspection line.</summary>
    PageCount = 14,

    /// <summary>Page-dictionary inspection line.</summary>
    PageListing = 15,

    /// <summary>Object inspection line.</summary>
    ObjectListing = 16,

    /// <summary>Cross-reference inspection line.</summary>
    XrefListing = 17,

    /// <summary>Peak memory usage note.</summary>
    MemoryUsage = 18,

    /// <summary>JSON schema consistency note.</summary>
    JsonSchemaCheck = 19,

    /// <summary>Operation summary note.</summary>
    VerboseSummary = 20,

    /// <summary>Write progress note.</summary>
    ProgressReport = 21,
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

