using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

/// <summary>Status of one declared inspection check.</summary>
public enum QpdfCheckStatus
{
    /// <summary>The check ran and held.</summary>
    Passed = 0,

    /// <summary>The check ran and found a defect.</summary>
    Failed = 1,

    /// <summary>The check did not run; its prerequisite is unimplemented.</summary>
    Skipped = 2,
}

/// <summary>Stream decode depth for inspection, mirroring upstream decode levels.</summary>
public enum QpdfStreamDecodeLevel
{
    /// <summary>Stream bytes are never decoded.</summary>
    None = 0,

    /// <summary>Flate, LZW, ASCIIHex, and ASCII85 decode.</summary>
    Generalized = 1,

    /// <summary>Generalized plus RunLength.</summary>
    Specialized = 2,

    /// <summary>Every filter is attempted; undecodable filters are reported, never silent.</summary>
    All = 3,
}

/// <summary>Outcome of one declared check. Skipped checks stay visible; never passed.</summary>
public sealed record QpdfCheckOutcome
{
    /// <summary>Creates a check outcome.</summary>
    /// <exception cref="ArgumentNullException">Name or detail is null.</exception>
    public QpdfCheckOutcome(string name, QpdfCheckStatus status, string detail)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Status = status;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }

    /// <summary>Gets the stable check name.</summary>
    public string Name { get; }

    /// <summary>Gets the check status.</summary>
    public QpdfCheckStatus Status { get; }

    /// <summary>Gets the sanitized detail.</summary>
    public string Detail { get; }
}

/// <summary>Options for structural inspection.</summary>
public sealed record QpdfCheckOptions
{
    /// <summary>Gets the stream decode depth.</summary>
    public QpdfStreamDecodeLevel DecodeLevel { get; init; } = QpdfStreamDecodeLevel.All;
}

/// <summary>
/// Outcome of structural inspection. This is a partial check with an explicit
/// contract: skipped stages are reported, never silently passed. It is not a
/// claim of full PDF conformance, safe content, or rendering correctness.
/// </summary>
public sealed record QpdfCheckResult
{
    internal const long StorageBytes = 64L;

    /// <summary>Creates an inspection result.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public QpdfCheckResult(
        int pageCount,
        PdfVersion version,
        IReadOnlyList<QpdfCheckOutcome> checks,
        IReadOnlyList<QpdfDiagnostic> diagnostics,
        QpdfExecutionCounters counters)
        : this(pageCount, version, checks, diagnostics, counters, false)
    { }

    internal QpdfCheckResult(
        int pageCount,
        PdfVersion version,
        IReadOnlyList<QpdfCheckOutcome> checks,
        IReadOnlyList<QpdfDiagnostic> diagnostics,
        QpdfExecutionCounters counters,
        bool adoptBacking)
    {
        PageCount = pageCount;
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Checks = checks is null ? throw new ArgumentNullException(nameof(checks)) : adoptBacking ? checks : [.. checks];
        Diagnostics = diagnostics is null ? throw new ArgumentNullException(nameof(diagnostics)) : adoptBacking ? diagnostics : [.. diagnostics];
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
    }

    /// <summary>Gets the traversed page count.</summary>
    public int PageCount { get; }

    /// <summary>Gets the effective document version.</summary>
    public PdfVersion Version { get; }

    /// <summary>Gets the per-check outcomes in run order.</summary>
    public IReadOnlyList<QpdfCheckOutcome> Checks { get; }

    /// <summary>Gets the recorded diagnostics.</summary>
    public IReadOnlyList<QpdfDiagnostic> Diagnostics { get; }

    /// <summary>Gets the final job counters.</summary>
    public QpdfExecutionCounters Counters { get; }

    /// <summary>Gets whether every executed check passed.</summary>
    public bool Passed
    {
        get
        {
            foreach (QpdfCheckOutcome check in Checks)
            {
                if (check.Status == QpdfCheckStatus.Failed)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Gets whether any declared check was skipped.</summary>
    public bool HasSkipped
    {
        get
        {
            foreach (QpdfCheckOutcome check in Checks)
            {
                if (check.Status == QpdfCheckStatus.Skipped)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private QpdfCheckResult(QpdfCheckResult source)
    {
        PageCount = source.PageCount;
        Version = source.Version;
        Checks = source.Checks;
        Diagnostics = source.Diagnostics;
        Counters = source.Counters;
    }

    /// <summary>Compares exposed values and aliases.</summary>
    public bool Equals(QpdfCheckResult? other) => other is not null
        && PageCount == other.PageCount
        && EqualityComparer<PdfVersion>.Default.Equals(Version, other.Version)
        && EqualityComparer<IReadOnlyList<QpdfCheckOutcome>>.Default.Equals(Checks, other.Checks)
        && EqualityComparer<IReadOnlyList<QpdfDiagnostic>>.Default.Equals(Diagnostics, other.Diagnostics)
        && EqualityComparer<QpdfExecutionCounters>.Default.Equals(Counters, other.Counters);

    /// <summary>Hashes exposed values and aliases.</summary>
    public override int GetHashCode() => HashCode.Combine(EqualityContract, PageCount, Version, Checks, Diagnostics, Counters);

}
