namespace Lokad.Qpdf.Runtime;

/// <summary>A required transformation has no representation in the supported profile.</summary>
internal sealed class QpdfUnsupportedFeatureException(string message) : QpdfException(message);
