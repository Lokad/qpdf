namespace Lokad.Qpdf.Reading;

/// <summary>Physical boundaries observed by the shared indirect-object parser.</summary>
internal readonly record struct PdfObjectExtent(long Start, long End, long WhitespaceEnd);
