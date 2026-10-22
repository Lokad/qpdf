using System.Globalization;
using Lokad.Qpdf.Runtime;

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
            throw new QpdfParseException($"Invalid rotation '{specification}': the angle is missing.", offset, null);
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

        if (colon >= 0 && colon + 1 < specification.Length)
        {
            range = ConfigurationText.Slice(textStorage, specification, colon + 1, specification.Length - colon - 1);
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

    internal static OutputRotation FromPages(PdfPageRotation rotation, ResourceGovernor governor, ParserMemoryScope memory)
    {
        if (rotation is null)
        {
            throw new ArgumentNullException(nameof(rotation));
        }

        governor.CheckCancellation();
        string? range = null;
        if (rotation.Pages is IReadOnlyList<int> pages)
        {
            int length = pages.Count == 0 ? 0 : pages.Count - 1;
            Span<char> digits = stackalloc char[11];
            foreach (int page in pages)
            {
                governor.CheckCancellation();
                if (!page.TryFormat(digits, out int written, provider: CultureInfo.InvariantCulture))
                { throw new InvalidOperationException("Typed page exceeds its bounded number storage."); }
                length = checked(length + written);
                if (length > Pages.PageRanges.MaxRangeTextLength)
                { throw new QpdfParseException($"Invalid page range: text exceeds {Pages.PageRanges.MaxRangeTextLength} characters.", 0L, null); }
            }
            memory.Reserve(GovernedText.StringBytes(length));
            range = string.Create(length, (pages, governor), static (target, state) =>
            {
                int offset = 0;
                for (int i = 0; i < state.pages.Count; i++)
                {
                    state.governor.CheckCancellation(); state.governor.NoteOperation();
                    if (i > 0) { target[offset++] = ','; }
                    if (!state.pages[i].TryFormat(target[offset..], out int written, provider: CultureInfo.InvariantCulture))
                    { throw new InvalidOperationException("Typed page changed its admitted number storage."); }
                    offset += written;
                }
            });
            if (range.Length > 0)
            {
                // Reuse the existing bounded grammar and its established lexical envelope.
                using ParserMemoryScope syntax = ParserMemoryScope.Create(governor);
                syntax.Reserve(checked(256L + 128L * range.Length));
                governor.CheckCancellation();
                Pages.PageRanges.CheckSyntax(range);
            }
        }
        memory.Reserve(32L);
        return new OutputRotation(rotation.Mode, Normalize(rotation.Angle), range);
    }

    private static int Normalize(int angle)
    {
        return ((angle % 360) + 360) % 360;
    }
}
