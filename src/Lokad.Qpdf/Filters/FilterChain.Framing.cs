using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Filters;

internal static partial class FilterChain
{
    internal static bool TryGetEncodedLength(PdfName filter, PdfDictionary? parms, IIndirectResolver? resolver,
        ReadOnlySpan<byte> data, ResourceGovernor governor, CancellationToken token, out int length)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation(); length = 0;
        string? name = CanonicalName(filter, governor, token);
        if (name == FlateFilter.FilterName) { return FlateFilter.TryGetEncodedLength(data, governor, token, out length); }
        if (name == DctFilter.FilterName) { return DctFilter.TryGetEncodedLength(data, governor, token, out length); }
        if (name == LzwFilter.FilterName) { return LzwFilter.TryGetEncodedLength(data, ReadEarlyChange(parms, resolver, governor, token), governor, token, out length); }
        if (name is not (AsciiHexFilter.FilterName or Ascii85Filter.FilterName or RunLengthFilter.FilterName))
        { throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter, name, "Inline packet framing is unavailable for this filter."); }
        int position = 0;
        while (position < data.Length)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            byte value = data[position++];
            if (name == AsciiHexFilter.FilterName && value == '>') { length = position; return true; }
            if (name == Ascii85Filter.FilterName && value == '~' && position < data.Length && data[position] == '>') { length = position + 1; return true; }
            if (name == RunLengthFilter.FilterName)
            {
                if (value == 128) { length = position; return true; }
                int count = value < 128 ? value + 1 : 1;
                if (count > data.Length - position) { return false; }
                position += count;
            }
        }
        return false;
    }
}
