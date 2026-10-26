using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

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
/// <c>CheckSyntax</c> validates structure without a page count so plans can
/// fail fast at parse time; bounds still resolve at execution.
/// </summary>
internal static class PageRanges
{
    internal const int MaxRangeTextLength = 4096;

    private sealed record Endpoint(bool Relative, long Value);

    private sealed record Group(bool Exclude, Endpoint First, Endpoint? Last);

    internal static void CheckSyntax(string? range)
    {
        if (range is null || range.Length == 0)
        {
            return;
        }

        if (range.Length > MaxRangeTextLength)
        {
            throw new QpdfParseException($"Invalid page range: text exceeds {MaxRangeTextLength} characters.", 0L, null);
        }

        Parse(range);
    }

    internal static IReadOnlyList<int> Expand(string? range, int total, long maxEntries,
        ResourceGovernor governor, ParserMemoryScope memory)
    {
        memory.CheckOwner(governor); governor.CheckCancellation();
        if (total == 0 && string.IsNullOrEmpty(range)) { return []; }
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
            if ((long)total > maxEntries)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), (long)total, maxEntries);
            }

            List<int> all = ParserContainers.CreateList<int>(memory);
            for (int index = 0; index < total; index++)
            {
                AddMember(all, index + 1, governor, memory);
            }

            return all;
        }

        if (range.Length > MaxRangeTextLength)
        {
            throw new QpdfParseException($"Invalid page range: text exceeds {MaxRangeTextLength} characters.", 0L, null);
        }

        // Range text is capped at 4096 characters. This envelope covers its
        // bounded lexical strings, split array and endpoint/group records.
        memory.Reserve(checked(256L + 128L * range.Length));
        governor.NoteOperation();
        (List<Group> groups, bool oddOnly, bool evenOnly) = Parse(range);
        List<int> selected = ParserContainers.CreateList<int>(memory);
        foreach (Group group in groups)
        {
            int first = ResolveEndpoint(group.First, range, total);
            int last = group.Last is null ? first : ResolveEndpoint(group.Last, range, total);
            if (!group.Exclude)
            {
                long span = Math.Abs((long)last - first) + 1L;
                if (span > maxEntries - selected.Count)
                {
                    throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), span, maxEntries);
                }
                int direction = first <= last ? 1 : -1;
                for (int index = 0; index < span; index++)
                { AddMember(selected, first + direction * index, governor, memory); }
                continue;
            }

            int lower = Math.Min(first, last), upper = Math.Max(first, last);
            int kept = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                governor.NoteOperation();
                if (selected[i] >= lower && selected[i] <= upper)
                {
                    continue;
                }

                selected[kept] = selected[i];
                kept += 1;
            }

            selected.RemoveRange(kept, selected.Count - kept);
        }

        if (!oddOnly && !evenOnly)
        {
            return selected;
        }

        int filtered = 0;
        for (int i = 0; i < selected.Count; i++)
        {
            governor.NoteOperation();
            bool oddPosition = (i % 2) == 0;
            if (oddPosition == oddOnly)
            {
                selected[filtered++] = selected[i];
            }
        }

        selected.RemoveRange(filtered, selected.Count - filtered);
        return selected;
    }

    private static (List<Group> Groups, bool OddOnly, bool EvenOnly) Parse(string range)
    {
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

        List<Group> parsed = new();
        if (groups.Length > 0)
        {
            bool first = true;
            foreach (string text in groups.Split(','))
            {
                parsed.Add(ParseGroup(text, range, first));
                first = false;
            }
        }

        return (parsed, oddOnly, evenOnly);
    }

    private static Group ParseGroup(string text, string range, bool first)
    {
        if (text.Length == 0)
        {
            throw new QpdfParseException($"Invalid page range '{range}': trailing comma.", 0L, null);
        }

        bool exclude = false;
        string body = text;
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

        if (dash < 0)
        {
            return new Group(exclude, ParseEndpoint(body, range), null);
        }

        return new Group(exclude, ParseEndpoint(body.Substring(0, dash), range), ParseEndpoint(body.Substring(dash + 1), range));
    }

    private static Endpoint ParseEndpoint(string text, string range)
    {
        if (text.Length == 0)
        {
            throw new QpdfParseException($"Invalid page range '{range}': invalid range syntax.", 0L, null);
        }

        if (text == "z")
        {
            return new Endpoint(true, 1L);
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

        return new Endpoint(relative, value);
    }

    private static void AddMember(List<int> items, int value, ResourceGovernor governor, ParserMemoryScope memory)
    {
        governor.NoteOperation();
        ParserContainers.AddItem(memory, items, value, sizeof(int));
    }

    private static int ResolveEndpoint(Endpoint endpoint, string range, int total)
    {
        long page = endpoint.Relative ? (long)total - endpoint.Value + 1L : endpoint.Value;
        if (page < 1L || page > (long)total)
        {
            throw new ArgumentOutOfRangeException(nameof(range), $"Invalid page range '{range}': number {endpoint.Value} out of range.");
        }

        return (int)page;
    }
}
