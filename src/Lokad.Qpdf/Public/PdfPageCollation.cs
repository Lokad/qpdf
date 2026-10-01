namespace Lokad.Qpdf;

/// <summary>Interleaving policy for multi-source page order.</summary>
public sealed record PdfPageCollation
{
    /// <summary>Creates a collation with per-source group sizes.</summary>
    /// <exception cref="ArgumentNullException">The group sizes are null.</exception>
    /// <exception cref="ArgumentException">No group size was supplied.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A group size is not positive.</exception>
    public PdfPageCollation(IReadOnlyList<int> groupSizes)
    {
        if (groupSizes is null)
        {
            throw new ArgumentNullException(nameof(groupSizes));
        }

        int[] sizes = [.. groupSizes];
        if (sizes.Length == 0)
        {
            throw new ArgumentException("At least one collation group size is required.", nameof(groupSizes));
        }

        foreach (int size in sizes)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(groupSizes), "Collation group sizes must be positive.");
            }
        }

        GroupSizes = sizes;
    }

    /// <summary>Creates a uniform interleaving policy taking the same count from every source.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The group size is not positive.</exception>
    public static PdfPageCollation Interleave(int groupSize)
    {
        return new PdfPageCollation([groupSize]);
    }

    /// <summary>Gets the group sizes: one uniform value, or one per source.</summary>
    public IReadOnlyList<int> GroupSizes { get; }
}

/// <summary>Combined options for multi-source page selection.</summary>
public sealed record PdfPageMergeOptions
{
    /// <summary>Gets the collation policy, or null to concatenate sources in order.</summary>
    public PdfPageCollation? Collation { get; init; }

    /// <summary>Gets the rotations applied to output pages, in order.</summary>
    public IReadOnlyList<PdfPageRotation> Rotations { get; init; } = [];

    /// <summary>Gets the explicit page labels for the output document.</summary>
    public IReadOnlyList<PdfPageLabel> Labels { get; init; } = [];
}
