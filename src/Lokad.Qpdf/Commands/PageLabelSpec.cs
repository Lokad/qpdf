using System.Text;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Commands;

/// <summary>
/// An explicit page label with a lazily resolved first page. The text form mirrors
/// upstream `first-page:[type][/start[/prefix]]`: the first page is a number, `rN`
/// counting from the end, or `z` for the last page; the type is `D`, `R`, `r`, `A`,
/// `a`, or omitted for no number. Resolution validates coverage at execution time.
/// </summary>
internal sealed record PageLabelSpec(string FirstPage, PdfPageLabelStyle? Style, int? Start, string? Prefix)
{
    internal static PageLabelSpec Parse(string specification, long offset)
    {
        if (specification is null)
        {
            throw new QpdfParseException("Null page label specification.", offset, null);
        }

        int colon = specification.IndexOf(':');
        if (colon < 0)
        {
            throw new QpdfParseException($"Invalid page label '{specification}': expected first-page:[type][/start[/prefix]].", offset, null);
        }

        string firstPage = specification.Substring(0, colon);
        CheckFirstPageSyntax(firstPage, specification, offset);
        string rest = specification.Substring(colon + 1);
        string[] parts = rest.Split('/', 3);
        if (parts.Length > 3)
        {
            throw new QpdfParseException($"Invalid page label '{specification}'.", offset, null);
        }

        PdfPageLabelStyle? style = null;
        if (parts.Length >= 1 && parts[0].Length > 0)
        {
            style = parts[0] switch
            {
                "D" => PdfPageLabelStyle.Decimal,
                "R" => PdfPageLabelStyle.UpperRoman,
                "r" => PdfPageLabelStyle.LowerRoman,
                "A" => PdfPageLabelStyle.UpperAlpha,
                "a" => PdfPageLabelStyle.LowerAlpha,
                _ => throw new QpdfParseException($"Invalid page label '{specification}': unknown numbering type.", offset, null),
            };
        }

        int? start = null;
        if (parts.Length >= 2)
        {
            start = ParseStart(parts[1], specification, offset);
        }

        string? prefix = parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : null;
        return new PageLabelSpec(firstPage, style, start, prefix);
    }

    internal static PageLabelSpec FromLabel(PdfPageLabel label)
    {
        if (label is null)
        {
            throw new ArgumentNullException(nameof(label));
        }

        return new PageLabelSpec(
            label.FirstPage.ToString(System.Globalization.CultureInfo.InvariantCulture),
            label.Style,
            label.Start,
            label.Prefix);
    }

    internal int ResolveFirstPage(int total)
    {
        if (FirstPage == "z")
        {
            return total;
        }

        if (FirstPage.Length > 1 && FirstPage[0] == 'r')
        {
            long fromEnd = ParseDigits(FirstPage.Substring(1));
            long resolved = (long)total - fromEnd + 1L;
            if (resolved < 1L || resolved > total)
            {
                throw new ArgumentOutOfRangeException(nameof(FirstPage), $"Label page '{FirstPage}' is outside 1..{total}.");
            }

            return (int)resolved;
        }

        long page = ParseDigits(FirstPage);
        if (page < 1L || page > total)
        {
            throw new ArgumentOutOfRangeException(nameof(FirstPage), $"Label page '{FirstPage}' is outside 1..{total}.");
        }

        return (int)page;
    }

    internal PdfDictionary ToDictionary()
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        if (Style.HasValue && Style.Value != PdfPageLabelStyle.None)
        {
            string name = Style.Value switch
            {
                PdfPageLabelStyle.Decimal => "D",
                PdfPageLabelStyle.UpperRoman => "R",
                PdfPageLabelStyle.LowerRoman => "r",
                PdfPageLabelStyle.UpperAlpha => "A",
                PdfPageLabelStyle.LowerAlpha => "a",
                _ => throw new QpdfException($"Unknown page label style {(int)Style.Value}."),
            };
            entries[PdfName.FromAscii("S")] = PdfName.FromAscii(name);
        }

        if (Start.HasValue)
        {
            entries[PdfName.FromAscii("St")] = new PdfInteger(
                Start.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Start.Value);
        }

        if (Prefix is not null)
        {
            entries[PdfName.FromAscii("P")] = new PdfByteString(Encoding.UTF8.GetBytes(Prefix), hex: false);
        }

        return new PdfDictionary(entries);
    }

    private static void CheckFirstPageSyntax(string firstPage, string specification, long offset)
    {
        if (firstPage.Length == 0)
        {
            throw new QpdfParseException($"Invalid page label '{specification}': the first page is missing.", offset, null);
        }

        if (firstPage == "z")
        {
            return;
        }

        string digits = firstPage;
        if (digits[0] == 'r')
        {
            digits = digits.Substring(1);
        }

        if (digits.Length == 0 || digits.Length > 10)
        {
            throw new QpdfParseException($"Invalid page label '{specification}': first pages are n, rN, or z.", offset, null);
        }

        foreach (char digit in digits)
        {
            if (digit < '0' || digit > '9')
            {
                throw new QpdfParseException($"Invalid page label '{specification}': first pages are n, rN, or z.", offset, null);
            }
        }

        if (ParseDigits(digits) < 1L)
        {
            throw new QpdfParseException($"Invalid page label '{specification}': the first page must be positive.", offset, null);
        }
    }

    private static int? ParseStart(string text, string specification, long offset)
    {
        if (text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException($"Invalid page label '{specification}': the start must be a positive number.", offset, null);
        }

        long value = ParseDigits(text);
        if (value < 1L || value > int.MaxValue)
        {
            throw new QpdfParseException($"Invalid page label '{specification}': the start must be a positive number.", offset, null);
        }

        foreach (char digit in text)
        {
            if (digit < '0' || digit > '9')
            {
                throw new QpdfParseException($"Invalid page label '{specification}': the start must be a positive number.", offset, null);
            }
        }

        return (int)value;
    }

    private static long ParseDigits(string text)
    {
        long value = 0L;
        foreach (char digit in text)
        {
            value = checked(value * 10L + (digit - '0'));
        }

        return value;
    }
}
