using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Reading;

/// <summary>A page leaf with its resolved dictionary and source reference, if indirect.</summary>
internal readonly struct PageLocation
{
    internal PageLocation(PdfReference? source, PdfDictionary page)
    {
        Source = source;
        Page = page ?? throw new ArgumentNullException(nameof(page));
    }

    internal PdfReference? Source { get; }

    internal PdfDictionary Page { get; }
}
