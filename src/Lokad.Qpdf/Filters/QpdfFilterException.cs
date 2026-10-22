namespace Lokad.Qpdf.Filters;

/// <summary>Why a governed filter decode failed. Messages never embed stream content.</summary>
internal enum QpdfFilterFailureReason
{
    /// <summary>The filter or frame cannot be decoded with the available host capabilities.</summary>
    UnsupportedFilter = 0,

    /// <summary>Encoded bytes end prematurely or fail integrity checks.</summary>
    TruncatedInput = 1,

    /// <summary>Present bytes or parameters are not valid for the filter.</summary>
    InvalidData = 2,

    /// <summary>The filter chain exceeds the stream-filter budget.</summary>
    TooManyFilters = 3,
}

/// <summary>Explicit filter-pipeline failure. Promoted to public API when a public surface decodes.</summary>
internal sealed class QpdfFilterException : QpdfException
{
    internal QpdfFilterException(QpdfFilterFailureReason reason, string? filterName, string detail)
        : base(Format(reason, filterName, detail))
    {
        Reason = reason;
        FilterName = filterName;
    }

    internal QpdfFilterException(QpdfFilterFailureReason reason, string? filterName, string detail, Exception? innerException)
        : base(Format(reason, filterName, detail), innerException)
    {
        Reason = reason;
        FilterName = filterName;
    }

    internal QpdfFilterFailureReason Reason { get; }

    internal string? FilterName { get; }

    private static string Format(QpdfFilterFailureReason reason, string? filterName, string detail)
    {
        // Dynamic names already have an owner. Do not duplicate an arbitrarily
        // large name in an otherwise small error; keep the full alias below.
        string? displayName = filterName is { Length: > 256 } ? filterName[..256] + "..." : filterName;
        return filterName is null
            ? $"Filter decode failed ({reason}): {detail}."
            : $"Filter decode failed ({reason}) for '{displayName}': {detail}.";
    }
}
