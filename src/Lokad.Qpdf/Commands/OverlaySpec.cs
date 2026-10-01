namespace Lokad.Qpdf.Commands;

internal sealed record OverlaySpec(string File, string? To, string? From, string? Repeat);

internal static class OverlayRanges
{
    private const char Comma = (char)44;
    private const char Dash = (char)45;
    private const char Zero = (char)48;

    internal static void ValidateSyntax(string text, string role, long index)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (text.Length == 0)
        {
            if (role == "--from")
            {
                return;
            }

            throw new QpdfParseException("Option " + role + " requires a non-empty page range.", index, null);
        }

        foreach (string part in text.Split(Comma))
        {
            ValidatePart(part.Trim(), text, role, index);
        }
    }

    internal static IReadOnlyList<int> Resolve(string? range, int count, string role)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (range is null)
        {
            List<int> all = new(count);
            for (int page = 1; page <= count; page++)
            {
                all.Add(page);
            }

            return all;
        }

        if (range.Length == 0)
        {
            return [];
        }

        List<int> selected = new();
        foreach (string part in range.Split(Comma))
        {
            AppendPart(part.Trim(), range, role, count, selected);
        }

        return selected;
    }

    private static void ValidatePart(string part, string text, string role, long index)
    {
        if (part.Length == 0 || part.Length > 21)
        {
            ThrowInvalid(text, role, index);
        }

        int dash = part.IndexOf(Dash);
        if (dash < 0)
        {
            ValidateNumber(part, text, role, index);
            return;
        }

        if (dash == 0 || dash == part.Length - 1 || part.IndexOf(Dash, dash + 1) >= 0)
        {
            ThrowInvalid(text, role, index);
        }

        ValidateNumber(part.Substring(0, dash), text, role, index);
        ValidateNumber(part.Substring(dash + 1), text, role, index);
    }

    private static void ValidateNumber(string token, string text, string role, long index)
    {
        if (token.Length == 0 || token.Length > 10)
        {
            ThrowInvalid(text, role, index);
        }

        foreach (char digit in token)
        {
            if (digit < Zero || digit > (char)57)
            {
                ThrowInvalid(text, role, index);
            }
        }

        if (token.Length > 1 && token[0] == Zero)
        {
            ThrowInvalid(text, role, index);
        }
    }

    private static void ThrowInvalid(string text, string role, long index)
    {
        throw new QpdfParseException("Invalid " + role + " range " + text + ": expected n, n-m, or comma lists without relative numbers, exclusions, or odd/even positions.", index, null);
    }

    private static void AppendPart(string part, string range, string role, int count, List<int> selected)
    {
        int dash = part.IndexOf(Dash);
        if (dash < 0)
        {
            selected.Add(CheckedPage(ParseDigits(part), range, role, count));
            return;
        }

        int first = CheckedPage(ParseDigits(part.Substring(0, dash)), range, role, count);
        int last = CheckedPage(ParseDigits(part.Substring(dash + 1)), range, role, count);
        if (first <= last)
        {
            for (int page = first; page <= last; page++)
            {
                selected.Add(page);
            }
        }
        else
        {
            for (int page = first; page >= last; page--)
            {
                selected.Add(page);
            }
        }
    }

    private static int ParseDigits(string token)
    {
        long value = 0L;
        foreach (char digit in token)
        {
            value = checked(value * 10L + (digit - Zero));
        }

        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static int CheckedPage(int page, string range, string role, int count)
    {
        if (page < 1 || page > count)
        {
            throw new QpdfParseException("Page number in " + role + " range is outside the document.", 0L, null);
        }

        return page;
    }
}
