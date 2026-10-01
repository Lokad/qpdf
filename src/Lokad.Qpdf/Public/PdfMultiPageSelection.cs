namespace Lokad.Qpdf;

/// <summary>One merge input: a document path plus its page selection.</summary>
public sealed record PdfPageSource
{
    /// <summary>Creates a page source.</summary>
    /// <exception cref="ArgumentNullException">The selection is null.</exception>
    public PdfPageSource(QpdfPath input, PdfPageSelectionOptions selection)
    {
        Input = input;
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }

    /// <summary>Gets the source document path.</summary>
    public QpdfPath Input { get; }

    /// <summary>Gets the source page selection.</summary>
    public PdfPageSelectionOptions Selection { get; }
}

/// <summary>Maps one output page back to its source document and page.</summary>
public sealed record PdfPageSelectionMapping(int SourceIndex, int SourcePage);

/// <summary>Outcome of a multi-source page selection.</summary>
public sealed record PdfMultiPageSelectionResult
{
    /// <summary>Creates a merge result.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PdfMultiPageSelectionResult(
        int pageCount,
        IReadOnlyList<PdfPageSelectionMapping> mapping,
        IReadOnlyList<int> sourcePageCounts,
        IReadOnlyList<QpdfPath> outputPaths,
        IReadOnlyList<QpdfDiagnostic> warnings,
        QpdfExecutionCounters counters)
    {
        PageCount = pageCount;
        Mapping = mapping is null ? throw new ArgumentNullException(nameof(mapping)) : [.. mapping];
        SourcePageCounts = sourcePageCounts is null ? throw new ArgumentNullException(nameof(sourcePageCounts)) : [.. sourcePageCounts];
        OutputPaths = outputPaths is null ? throw new ArgumentNullException(nameof(outputPaths)) : [.. outputPaths];
        Warnings = warnings is null ? throw new ArgumentNullException(nameof(warnings)) : [.. warnings];
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
    }

    /// <summary>Gets the output page count.</summary>
    public int PageCount { get; }

    /// <summary>Gets the source mapping for every output page, in order.</summary>
    public IReadOnlyList<PdfPageSelectionMapping> Mapping { get; }

    /// <summary>Gets the page count of every source document, in order.</summary>
    public IReadOnlyList<int> SourcePageCounts { get; }

    /// <summary>Gets every published output path: one entry, or one per split group.</summary>
    public IReadOnlyList<QpdfPath> OutputPaths { get; }

    /// <summary>Gets the warnings recorded by the job.</summary>
    public IReadOnlyList<QpdfDiagnostic> Warnings { get; }

    /// <summary>Gets the final job counters.</summary>
    public QpdfExecutionCounters Counters { get; }
}
