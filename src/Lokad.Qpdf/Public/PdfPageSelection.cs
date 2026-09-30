namespace Lokad.Qpdf;

/// <summary>Single-page selection within the initial strict profile.</summary>
public sealed record PdfPageSelectionOptions
{
    /// <summary>Creates a selection for a 1-based page number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The page number is not positive.</exception>
    public PdfPageSelectionOptions(int pageNumber)
    {
        if (pageNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page numbers are 1-based and must be positive.");
        }

        PageNumber = pageNumber;
    }

    /// <summary>Gets the 1-based page number.</summary>
    public int PageNumber { get; }
}

/// <summary>Outcome of a single-page selection.</summary>
public sealed record PdfPageSelectionResult
{
    /// <summary>Creates a selection result.</summary>
    /// <exception cref="ArgumentNullException">Warnings or counters are null.</exception>
    public PdfPageSelectionResult(
        int sourcePageNumber,
        int pageCount,
        IReadOnlyList<QpdfDiagnostic> warnings,
        QpdfExecutionCounters counters)
    {
        SourcePageNumber = sourcePageNumber;
        PageCount = pageCount;
        Warnings = warnings is null ? throw new ArgumentNullException(nameof(warnings)) : [.. warnings];
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
    }

    /// <summary>Gets the selected source page number.</summary>
    public int SourcePageNumber { get; }

    /// <summary>Gets the output page count.</summary>
    public int PageCount { get; }

    /// <summary>Gets the warnings recorded by the job.</summary>
    public IReadOnlyList<QpdfDiagnostic> Warnings { get; }

    /// <summary>Gets the final job counters.</summary>
    public QpdfExecutionCounters Counters { get; }
}
