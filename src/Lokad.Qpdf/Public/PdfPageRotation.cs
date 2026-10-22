namespace Lokad.Qpdf;

/// <summary>How a rotation angle combines with the page rotation.</summary>
public enum PdfRotationMode
{
    /// <summary>Set the rotation exactly, ignoring the page value.</summary>
    Set = 0,

    /// <summary>Add the angle clockwise to the page value.</summary>
    Add = 1,

    /// <summary>Subtract the angle from the page value.</summary>
    Subtract = 2,
}

/// <summary>A rotation applied to output pages, in upstream `[+|-]angle[:range]` spirit.</summary>
public sealed record PdfPageRotation
{
    /// <summary>Creates a rotation for output pages.</summary>
    /// <remarks>A null or empty page list selects every output page.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The angle is not a multiple of 90, or a page is not positive.</exception>
    public PdfPageRotation(PdfRotationMode mode, int angle, IReadOnlyList<int>? pages)
    {
        if (angle % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(angle), "Rotation angles must be multiples of 90 degrees.");
        }

        if (pages is not null)
        {
            int[] selected = [.. pages];
            foreach (int page in selected)
            {
                if (page <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(pages), "Output page numbers are 1-based and must be positive.");
                }
            }

            Pages = Array.AsReadOnly(selected);
        }
        else
        {
            Pages = null;
        }

        Mode = mode;
        Angle = angle;
    }

    /// <summary>Gets the rotation mode.</summary>
    public PdfRotationMode Mode { get; }

    /// <summary>Gets the rotation angle in degrees.</summary>
    public int Angle { get; }

    /// <summary>Gets a read-only snapshot of 1-based output pages; null or empty selects every output page.</summary>
    public IReadOnlyList<int>? Pages { get; }
}
