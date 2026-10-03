namespace Lokad.Qpdf.Pages;

/// <summary>
/// Upstream page-range grammar shared by page selection, rotation, and
/// overlay ranges: comma-separated groups of numbers and dash ranges with
/// <c>rN</c>/<c>z</c> endpoints, descending ranges, <c>x</c> exclusions, and a
/// single trailing <c>:odd</c>/<c>:even</c> position filter. An empty range
/// selects every page; <c>:odd</c>/<c>:even</c> alone select nothing.
/// Groups expand in order with duplicates preserved; exclusions remove every
/// matching member accumulated so far, and the position filter applies last.
/// Out-of-range endpoints (including excluded ones) fail explicitly.
/// </summary>
internal static class PageRanges
{
    internal const int MaxRangeTextLength = 4096;

    internal static IReadOnlyList<int> Expand(string? range, int total, long maxEntries)
    {
        if (total < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "Page counts are 1-based and must be positive.");
        }

        if (maxEntries < 1L)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "The entry budget must be positive.");
        }

        if (range is null || range.Length == 0)
        {
            List<int> all = new(total);
            for (int page = 1; page <= total; page++)
            {
                all.Add(page);
            }

            return all;
        }

        if (range.Length > MaxRangeTextLength)
        {
            throw new QpdfParseException($"Invalid page range: text exceeds {MaxRangeTextLength} characters.", 0L, null);
        }

        string groups = range;
        bool oddOnly = false;
        bool evenOnly = false;
        int colon = range.IndexOf(':');
        if (colon >= 0)
        {
            string suffix = range.Substring(colon + 1);
            string head = range.Substring(0, colon);
            if (head.IndexOf(':') >= 0)
            {
                throw new QpdfParseException($"Invalid page range '{range}': expected :even or :odd.", 0L, null);
            }

            if (suffix == "odd")
            {
                oddOnly = true;
            }
            else if (suffix == "even")
            {
                evenOnly = true;
            }
            else
            {
                throw new QpdfParseException($"Invalid page range '{range}': expected :even or :odd.", 0L, null);
            }

            groups = head;
        }

        List<int> selected = new();
        if (groups.Length > 0)
        {
            bool first = true;
            foreach (string group in groups.Split(','))
            {
                AppendGroup(selected, group, range, total, maxEntries, first);
                first = false;
            }
        }

        if (!oddOnly && !evenOnly)
        {
            return selected;
        }

        List<int> filtered = new(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            bool oddPosition = (i % 2) == 0;
            if (oddPosition == oddOnly)
            {
                filtered.Add(selected[i]);
            }
        }

        return filtered;
    }

    private static void AppendGroup(List<int> selected, string group, string range, int total, long maxEntries, bool first)
    {
        if (group.Length == 0)
        {
            throw new QpdfParseException($"Invalid page range '{range}': trailing comma.", 0L, null);
        }

        bool exclude = false;
        string body = group;
        if (body[0] == 'x')
        {
            exclude = true;
            body = body.Substring(1);
            if (first)
            {
                throw new QpdfParseException($"Invalid page range '{range}': first range group may not be an exclusion.", 0L, null);
            }
        }

        if (body.Length == 0)
        {
            throw new QpdfParseException($"Invalid page range '{range}': invalid range syntax.", 0L, null);
        }

        int dash = body.IndexOf('-');
        if (dash >= 0 && body.IndexOf('-', dash + 1) >= 0)
        {
            throw new QpdfParseException($"Invalid page range '{range}': invalid range syntax.", 0L, null);
        }

        List<int> members = new();
        if (dash < 0)
        {
            members.Add(ResolveEndpoint(body, range, total));
        }
        else
        {
            int firstPage = ResolveEndpoint(body.Substring(0, dash), range, total);
            int lastPage = ResolveEndpoint(body.Substring(dash + 1), range, total);
            if (firstPage <= lastPage)
            {
                for (int page = firstPage; page <= lastPage; page++)
                {
                    members.Add(page);
                }
            }
            else
            {
                for (int page = firstPage; page >= lastPage; page--)
                {
                    members.Add(page);
                }
            }
        }

        if (!exclude)
        {
            foreach (int page in members)
            {
                if ((long)selected.Count >= maxEntries)
                {
                    throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), (long)selected.Count + 1L, maxEntries);
                }

                selected.Add(page);
            }

            return;
        }

        HashSet<int> excluded = new(members);
        int kept = 0;
        for (int i = 0; i < selected.Count; i++)
        {
            if (excluded.Contains(selected[i]))
            {
                continue;
            }

            selected[kept] = selected[i];
            kept += 1;
        }

        selected.RemoveRange(kept, selected.Count - kept);
    }

    private static int ResolveEndpoint(string text, string range, int total)
    {
        if (text.Length == 0)
        {
            throw new QpdfParseException($"Invalid page range '{range}': invalid range syntax.", 0L, null);
        }

        if (text == "z")
        {
            return total;
        }

        bool relative = false;
        string digits = text;
        if (text[0] == 'r')
        {
            relative = true;
            digits = text.Substring(1);
        }

        if (digits.Length == 0 || digits.Length > 19)
        {
            throw new QpdfParseException($"Invalid page range '{range}': invalid range syntax.", 0L, null);
        }

        long value = 0L;
        foreach (char digit in digits)
        {
            if (digit < '0' || digit > '9')
            {
                throw new QpdfParseException($"Invalid page range '{range}': invalid range syntax.", 0L, null);
            }

            try
            {
                value = checked(value * 10L + (digit - '0'));
            }
            catch (OverflowException ex)
            {
                throw new QpdfParseException($"Invalid page range '{range}': number overflows 64-bit range.", 0L, null, ex);
            }
        }

        long page = relative ? (long)total - value + 1L : value;
        if (page < 1L || page > (long)total)
        {
            throw new QpdfParseException($"Invalid page range '{range}': number {value} out of range.", 0L, null);
        }

        return (int)page;
    }
}
