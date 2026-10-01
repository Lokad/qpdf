namespace Lokad.Qpdf;

/// <summary>Ordered page selection within the strict profile.</summary>
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

        Pages = [pageNumber];
        FirstAndLastCount = null;
    }

    private PdfPageSelectionOptions(IReadOnlyList<int> pages, int? firstAndLastCount)
    {
        Pages = pages;
        FirstAndLastCount = firstAndLastCount;
    }

    /// <summary>Gets the explicit ordered selection; duplicates and order are preserved.</summary>
    public IReadOnlyList<int> Pages { get; }

    internal int? FirstAndLastCount { get; }

    /// <summary>Creates an explicit ordered selection; order and duplicates are preserved.</summary>
    /// <exception cref="ArgumentNullException">The pages are null.</exception>
    /// <exception cref="ArgumentException">The selection is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page number is not positive.</exception>
    public static PdfPageSelectionOptions FromPages(IEnumerable<int> pages)
    {
        if (pages is null)
        {
            throw new ArgumentNullException(nameof(pages));
        }

        int[] selected = [.. pages];
        if (selected.Length == 0)
        {
            throw new ArgumentException("The page selection must not be empty.", nameof(pages));
        }

        foreach (int page in selected)
        {
            if (page <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pages), "Page numbers are 1-based and must be positive.");
            }
        }

        return new PdfPageSelectionOptions(selected, null);
    }

    /// <summary>Creates a first-and-last preview selection for a positive window size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The window size is not positive.</exception>
    public static PdfPageSelectionOptions FirstAndLast(int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The preview size must be positive.");
        }

        return new PdfPageSelectionOptions([], count);
    }

    /// <summary>Creates a sorted, deduplicated ascending selection.</summary>
    /// <exception cref="ArgumentNullException">The pages are null.</exception>
    /// <exception cref="ArgumentException">The selection is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page number is not positive.</exception>
    public static PdfPageSelectionOptions Normalized(IEnumerable<int> pages)
    {
        if (pages is null)
        {
            throw new ArgumentNullException(nameof(pages));
        }

        SortedSet<int> ordered = new();
        foreach (int page in pages)
        {
            if (page <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pages), "Page numbers are 1-based and must be positive.");
            }

            ordered.Add(page);
        }

        if (ordered.Count == 0)
        {
            throw new ArgumentException("The page selection must not be empty.", nameof(pages));
        }

        return new PdfPageSelectionOptions([.. ordered], null);
    }

    internal IReadOnlyList<int> Resolve(int totalPages)
    {
        if (!FirstAndLastCount.HasValue)
        {
            return Pages;
        }

        int window = FirstAndLastCount.Value;
        if (totalPages <= 0)
        {
            return [];
        }

        if ((long)window * 2L >= totalPages)
        {
            int[] all = new int[totalPages];
            for (int i = 0; i < totalPages; i++)
            {
                all[i] = i + 1;
            }

            return all;
        }

        int[] selected = new int[checked(window * 2)];
        for (int i = 0; i < window; i++)
        {
            selected[i] = i + 1;
            selected[window + i] = totalPages - window + 1 + i;
        }

        return selected;
    }
}

/// <summary>Outcome of a page selection.</summary>
public sealed record PdfPageSelectionResult
{
    /// <summary>Creates a selection result.</summary>
    /// <exception cref="ArgumentNullException">Warnings or counters are null.</exception>
    public PdfPageSelectionResult(
        int sourcePageCount,
        IReadOnlyList<int> sourcePages,
        int pageCount,
        IReadOnlyList<QpdfDiagnostic> warnings,
        QpdfExecutionCounters counters)
    {
        SourcePageCount = sourcePageCount;
        SourcePages = sourcePages is null ? throw new ArgumentNullException(nameof(sourcePages)) : [.. sourcePages];
        PageCount = pageCount;
        Warnings = warnings is null ? throw new ArgumentNullException(nameof(warnings)) : [.. warnings];
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
    }

    /// <summary>Gets the source document page count.</summary>
    public int SourcePageCount { get; }

    /// <summary>Gets the exact source page number for every output page, in order.</summary>
    public IReadOnlyList<int> SourcePages { get; }

    /// <summary>Gets the output page count.</summary>
    public int PageCount { get; }

    /// <summary>Gets the warnings recorded by the job.</summary>
    public IReadOnlyList<QpdfDiagnostic> Warnings { get; }

    /// <summary>Gets the final job counters.</summary>
    public QpdfExecutionCounters Counters { get; }
}
