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
    internal static PageLabelSpec Parse(string specification, long offset, ConfigurationText? textStorage)
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

        CheckFirstPageSyntax(specification.AsSpan(0, colon), specification, offset);
        ReadOnlySpan<char> rest = specification.AsSpan(colon + 1);
        int firstSlash = rest.IndexOf('/');
        int secondSlash = firstSlash < 0 ? -1 : rest[(firstSlash + 1)..].IndexOf('/');
        if (secondSlash >= 0) { secondSlash += firstSlash + 1; }
        ReadOnlySpan<char> styleText = firstSlash < 0 ? rest : rest[..firstSlash];

        PdfPageLabelStyle? style = null;
        if (styleText.Length > 0)
        {
            style = styleText switch
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
        if (firstSlash >= 0)
        {
            ReadOnlySpan<char> startText = secondSlash < 0 ? rest[(firstSlash + 1)..] : rest[(firstSlash + 1)..secondSlash];
            start = ParseStart(startText, specification, offset);
        }

        string firstPage = ConfigurationText.Slice(textStorage, specification, 0, colon);
        int prefixAt = secondSlash < 0 ? specification.Length : colon + secondSlash + 2;
        string? prefix = prefixAt < specification.Length
            ? ConfigurationText.Slice(textStorage, specification, prefixAt, specification.Length - prefixAt) : null;
        return new PageLabelSpec(firstPage, style, start, prefix);
    }

    internal static PageLabelSpec FromLabel(PdfPageLabel label, Runtime.ResourceGovernor governor, Runtime.ParserMemoryScope memory)
    {
        if (label is null)
        {
            throw new ArgumentNullException(nameof(label));
        }

        governor.CheckCancellation();
        Span<char> digits = stackalloc char[10];
        if (!label.FirstPage.TryFormat(digits, out int length, provider: System.Globalization.CultureInfo.InvariantCulture))
        { throw new InvalidOperationException("Typed label exceeds its bounded page storage."); }
        memory.Reserve(Runtime.GovernedText.StringBytes(length));
        string firstPage = digits[..length].ToString();
        memory.Reserve(48L);
        return new PageLabelSpec(
            firstPage,
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
            long fromEnd = ParseDigits(FirstPage.AsSpan(1));
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

    internal PdfDictionary ToDictionary(Runtime.ResourceGovernor governor, CancellationToken token)
        => Content.PageLabelStorage.Explicit(this, governor, token);

    private static void CheckFirstPageSyntax(ReadOnlySpan<char> firstPage, string specification, long offset)
    {
        if (firstPage.Length == 0)
        {
            throw new QpdfParseException($"Invalid page label '{specification}': the first page is missing.", offset, null);
        }

        if (firstPage.SequenceEqual("z"))
        {
            return;
        }

        ReadOnlySpan<char> digits = firstPage;
        if (digits[0] == 'r')
        {
            digits = digits[1..];
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

    private static int? ParseStart(ReadOnlySpan<char> text, string specification, long offset)
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

    private static long ParseDigits(ReadOnlySpan<char> text)
    {
        long value = 0L;
        foreach (char digit in text)
        {
            value = checked(value * 10L + (digit - '0'));
        }

        return value;
    }
}
