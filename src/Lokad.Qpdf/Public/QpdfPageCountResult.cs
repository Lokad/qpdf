namespace Lokad.Qpdf;

/// <summary>A validated page-tree traversal count after its input lease closes.</summary>
public sealed record QpdfPageCountResult
{
    internal const long StorageBytes = 40L;

    internal QpdfPageCountResult(int pageCount, IReadOnlyList<QpdfDiagnostic> warnings, QpdfExecutionCounters counters)
    { PageCount = pageCount; Warnings = warnings; Counters = counters; }

    /// <summary>Gets the number of traversed pages, independently of malformed declared counts.</summary>
    public int PageCount { get; }

    /// <summary>Gets immutable reader and recovery warnings.</summary>
    public IReadOnlyList<QpdfDiagnostic> Warnings { get; }

    /// <summary>Gets the final counters, including closed input handles.</summary>
    public QpdfExecutionCounters Counters { get; }
}
