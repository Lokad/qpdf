namespace Lokad.Qpdf.Commands;

internal sealed record OverlaySpec(string File, string? To, string? From, string? Repeat);

internal static class OverlayRanges
{
    internal static void ValidateSyntax(string text, string role, long index)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Pages.PageRanges.CheckSyntax(text);
        }
        catch (QpdfParseException ex)
        {
            throw new QpdfParseException("Invalid " + role + " range " + text + ": expected upstream page-range syntax.", index, null, ex);
        }
    }

    internal static IReadOnlyList<int> Resolve(string? range, int count, long maxEntries)
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

        return Pages.PageRanges.Expand(range, count, maxEntries);
    }
}