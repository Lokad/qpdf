using System.Text;

namespace Lokad.Qpdf.Commands;

/// <summary>
/// A rotation over output positions with a lazily expanded range. The text form
/// mirrors upstream `[+|-]angle[:range]`: the angle is any multiple of 90
/// degrees normalized modulo 360, and the range holds comma-separated `n`,
/// `n-m`, and `z` (last page) items. An absent range selects every page; later
/// specifications win per page. exotic range forms (`rN`, `:odd`, `:even`,
/// exclusions) stay explicitly unsupported.
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
            CheckRangeSyntax(range, specification, offset);
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

        CheckRangeSyntax(range, range, 0L);
        return new OutputRotation(rotation.Mode, Normalize(rotation.Angle), range);
    }

    internal List<int> Expand(int total)
    {
        List<int> positions = new();
        HashSet<int> seen = new();
        if (Range is null)
        {
            for (int page = 1; page <= total; page++)
            {
                positions.Add(page);
            }

            return positions;
        }

        if (Range.Length == 0)
        {
            return positions;
        }

        foreach (string item in Range.Split(','))
        {
            if (item.Length == 0)
            {
                throw new QpdfParseException($"Invalid rotation range '{Range}'.", 0L, null);
            }

            int dash = item.IndexOf('-');
            if (dash < 0)
            {
                AddPosition(positions, seen, ParsePosition(item, total), total);
                continue;
            }

            int first = ParsePosition(item.Substring(0, dash), total);
            int last = ParsePosition(item.Substring(dash + 1), total);
            if (first > last)
            {
                throw new QpdfParseException($"Invalid rotation range '{Range}': start exceeds end.", 0L, null);
            }

            for (int page = first; page <= last; page++)
            {
                AddPosition(positions, seen, page, total);
            }
        }

        return positions;
    }

    private static void AddPosition(List<int> positions, HashSet<int> seen, int page, int total)
    {
        if (page < 1 || page > total)
        {
            throw new ArgumentOutOfRangeException(nameof(page), $"Rotation page {page} is outside 1..{total}.");
        }

        if (seen.Add(page))
        {
            positions.Add(page);
        }
    }

    private static int ParsePosition(string text, int total)
    {
        if (text == "z")
        {
            return total;
        }

        if (text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException($"Invalid rotation range '{text}'.", 0L, null);
        }

        long value = 0L;
        foreach (char digit in text)
        {
            if (digit < '0' || digit > '9')
            {
                throw new QpdfParseException($"Invalid rotation range '{text}': only n, n-m, comma lists, and z are supported.", 0L, null);
            }

            value = checked(value * 10L + (digit - '0'));
        }

        if (value < 1L || value > int.MaxValue)
        {
            throw new QpdfParseException($"Invalid rotation range '{text}'.", 0L, null);
        }

        return (int)value;
    }

    private static void CheckRangeSyntax(string range, string specification, long offset)
    {
        foreach (string item in range.Split(','))
        {
            if (item.Length == 0)
            {
                throw new QpdfParseException($"Invalid rotation '{specification}'.", offset, null);
            }

            int dash = item.IndexOf('-');
            if (dash < 0)
            {
                CheckPositionSyntax(item, specification, offset);
                continue;
            }

            CheckPositionSyntax(item.Substring(0, dash), specification, offset);
            CheckPositionSyntax(item.Substring(dash + 1), specification, offset);
        }
    }

    private static void CheckPositionSyntax(string text, string specification, long offset)
    {
        if (text == "z")
        {
            return;
        }

        if (text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException($"Invalid rotation '{specification}'.", offset, null);
        }

        foreach (char digit in text)
        {
            if (digit < '0' || digit > '9')
            {
                throw new QpdfParseException($"Invalid rotation '{specification}': only n, n-m, comma lists, and z are supported.", offset, null);
            }
        }
    }

    private static int Normalize(int angle)
    {
        return ((angle % 360) + 360) % 360;
    }
}
