namespace Lokad.Qpdf;

/// <summary>Numbering style for explicit page labels.</summary>
public enum PdfPageLabelStyle
{
    /// <summary>Arabic numerals.</summary>
    Decimal = 0,

    /// <summary>Upper-case Roman numerals.</summary>
    UpperRoman = 1,

    /// <summary>Lower-case Roman numerals.</summary>
    LowerRoman = 2,

    /// <summary>Upper-case alphabetic characters.</summary>
    UpperAlpha = 3,

    /// <summary>Lower-case alphabetic characters.</summary>
    LowerAlpha = 4,

    /// <summary>No number; the prefix still appears.</summary>
    None = 5,
}

/// <summary>An explicit page label starting at a 1-based output page.</summary>
public sealed record PdfPageLabel
{
    /// <summary>Creates a page label.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The first page or start is not positive.</exception>
    public PdfPageLabel(int firstPage, PdfPageLabelStyle? style, int? start, string? prefix)
    {
        if (firstPage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPage), "Label pages are 1-based and must be positive.");
        }

        if (start.HasValue && start.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Label start values must be positive.");
        }

        FirstPage = firstPage;
        Style = style;
        Start = start;
        Prefix = prefix;
    }

    /// <summary>Gets the 1-based first output page using this label.</summary>
    public int FirstPage { get; }

    /// <summary>Gets the numbering style, or null for no number.</summary>
    public PdfPageLabelStyle? Style { get; }

    /// <summary>Gets the starting value, or null for the default of 1.</summary>
    public int? Start { get; }

    /// <summary>Gets the prepended prefix, or null for none.</summary>
    public string? Prefix { get; }
}
