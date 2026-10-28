using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Commands;

/// <summary>
/// A rotation over output positions, borrowed from immutable typed settings or
/// expanded lazily from a text range. The text form
/// mirrors upstream `[+|-]angle[:range]`: the angle is any multiple of 90
/// degrees normalized modulo 360, and the range uses the shared upstream
/// page-range grammar (spans, descending ranges, `rN`/`z`, exclusions,
/// `:odd`/`:even`, duplicates preserved). An absent range selects every page;
/// later specifications win per page.
/// </summary>
internal sealed record OutputRotation(PdfRotationMode Mode, int Angle, string? Range)
{
    private IReadOnlyList<int>? Positions { get; init; }

    internal static OutputRotation Parse(string specification, long offset, ConfigurationText? textStorage)
    {
        if (specification is null)
        {
            throw new QpdfParseException("Null rotation specification.", offset, null);
        }

        ReadOnlySpan<char> angleText = specification.AsSpan();
        string? range = null;
        int colon = specification.IndexOf(':');
        if (colon >= 0)
        {
            angleText = specification.AsSpan(0, colon);
        }

        if (angleText.Length == 0)
        {
            throw new QpdfParseException($"Invalid rotation '{ArgvParser.ErrorPreview(specification)}': the angle is missing.", offset, null);
        }

        PdfRotationMode mode = PdfRotationMode.Set;
        if (angleText[0] == '+')
        {
            mode = PdfRotationMode.Add;
            angleText = angleText[1..];
        }
        else if (angleText[0] == '-')
        {
            mode = PdfRotationMode.Subtract;
            angleText = angleText[1..];
        }

        if (angleText.Length == 0 || angleText.Length > 10)
        {
            throw new QpdfParseException($"Invalid rotation '{ArgvParser.ErrorPreview(specification)}'.", offset, null);
        }

        long angle = 0L;
        foreach (char digit in angleText)
        {
            if (digit < '0' || digit > '9')
            {
                throw new QpdfParseException($"Invalid rotation '{ArgvParser.ErrorPreview(specification)}'.", offset, null);
            }

            angle = checked(angle * 10L + (digit - '0'));
        }

        int normalized = (int)(angle % 360L);
        if (normalized % 90 != 0)
        {
            throw new QpdfParseException($"Invalid rotation '{ArgvParser.ErrorPreview(specification)}': the angle must be a multiple of 90.", offset, null);
        }

        if (colon >= 0 && colon + 1 < specification.Length)
        {
            range = ConfigurationText.Slice(textStorage, specification, colon + 1, specification.Length - colon - 1);
            try
            {
                Pages.PageRanges.CheckSyntax(range);
            }
            catch (QpdfParseException ex)
            {
                throw new QpdfParseException($"Invalid rotation '{ArgvParser.ErrorPreview(specification)}'.", offset, null, ex);
            }
        }

        return new OutputRotation(mode, normalized, range);
    }

    internal static OutputRotation FromPages(PdfPageRotation rotation, ResourceGovernor governor, ParserMemoryScope memory)
    {
        if (rotation is null)
        {
            throw new ArgumentNullException(nameof(rotation));
        }

        governor.CheckCancellation();
        memory.Reserve(40L);
        // Public settings own a defensive read-only snapshot. Borrow it through
        // preparation/publication rather than copying or formatting its numbers.
        return new OutputRotation(rotation.Mode, Normalize(rotation.Angle), null) { Positions = rotation.Pages };
    }

    internal IReadOnlyList<int> ResolvePositions(int total, long maxEntries,
        ResourceGovernor governor, ParserMemoryScope memory)
    {
        memory.CheckOwner(governor); governor.CheckCancellation();
        if (Positions is not { Count: > 0 } pages)
        { return Pages.PageRanges.Expand(Range, total, maxEntries, governor, memory); }
        if (pages.Count > maxEntries)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), pages.Count, maxEntries); }
        foreach (int page in pages)
        {
            governor.CheckCancellation();
            if (page < 1 || page > total)
            { throw new ArgumentOutOfRangeException(nameof(Positions), $"Output page {page} exceeds the page count of {total}."); }
        }
        return pages;
    }

    private static int Normalize(int angle)
    {
        return ((angle % 360) + 360) % 360;
    }
}
