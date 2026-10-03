using System.Text;

namespace Lokad.Qpdf.Commands;

/// <summary>
/// A rotation over output positions with a lazily expanded range. The text form
/// mirrors upstream `[+|-]angle[:range]`: the angle is any multiple of 90
/// degrees normalized modulo 360, and the range uses the shared upstream
/// page-range grammar (spans, descending ranges, `rN`/`z`, exclusions,
/// `:odd`/`:even`, duplicates preserved). An absent range selects every page;
/// later specifications win per page.
/// </summary>
internal sealed record OutputRotation(PdfRotationMode Mode, int Angle, string? Range)
{
    internal static OutputRotation Parse(string specification, long offset)
    {
        if (specification is null)
        {
            throw new QpdfParseException("Null rotation specification.", offset, null);
        }

        string angleText = specification;
        string? range = null;
        int colon = specification.IndexOf(':');
        if (colon >= 0)
        {
            angleText = specification.Substring(0, colon);
            range = specification.Substring(colon + 1);
            if (range.Length == 0)
            {
                range = null;
            }
        }

        if (angleText.Length == 0)
        {
            throw new QpdfParseException($"Invalid rotation '{specification}': the angle is missing.", offset, null);
        }

        PdfRotationMode mode = PdfRotationMode.Set;
        if (angleText[0] == '+')
        {
            mode = PdfRotationMode.Add;
            angleText = angleText.Substring(1);
        }
        else if (angleText[0] == '-')
        {
            mode = PdfRotationMode.Subtract;
            angleText = angleText.Substring(1);
        }

        if (angleText.Length == 0 || angleText.Length > 10)
        {
            throw new QpdfParseException($"Invalid rotation '{specification}'.", offset, null);
        }

        long angle = 0L;
        foreach (char digit in angleText)
        {
            if (digit < '0' || digit > '9')
            {
                throw new QpdfParseException($"Invalid rotation '{specification}'.", offset, null);
            }

            angle = checked(angle * 10L + (digit - '0'));
        }

        int normalized = (int)(angle % 360L);
        if (normalized % 90 != 0)
        {
            throw new QpdfParseException($"Invalid rotation '{specification}': the angle must be a multiple of 90.", offset, null);
        }

        if (range is not null)
        {
            try
            {
                Pages.PageRanges.CheckSyntax(range);
            }
            catch (QpdfParseException ex)
            {
                throw new QpdfParseException($"Invalid rotation '{specification}'.", offset, null, ex);
            }
        }

        return new OutputRotation(mode, normalized, range);
    }

    internal static OutputRotation FromPages(PdfPageRotation rotation)
    {
        if (rotation is null)
        {
            throw new ArgumentNullException(nameof(rotation));
        }

        if (rotation.Pages is null)
        {
            return new OutputRotation(rotation.Mode, Normalize(rotation.Angle), null);
        }

        StringBuilder joined = new();
        bool first = true;
        foreach (int page in rotation.Pages)
        {
            if (!first)
            {
                joined.Append(',');
            }

            joined.Append(page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            first = false;
        }

        string range = joined.ToString();
        if (range.Length == 0)
        {
            return new OutputRotation(rotation.Mode, Normalize(rotation.Angle), string.Empty);
        }

        Pages.PageRanges.CheckSyntax(range);
        return new OutputRotation(rotation.Mode, Normalize(rotation.Angle), range);
    }

    internal List<int> Expand(int total, long maxEntries)
    {
        return [.. Pages.PageRanges.Expand(Range, total, maxEntries)];
    }

    private static int Normalize(int angle)
    {
        return ((angle % 360) + 360) % 360;
    }
}