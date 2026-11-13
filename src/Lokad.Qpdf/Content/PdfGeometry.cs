using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal static class PdfGeometry
{
    internal static void TransformRect(ReadOnlySpan<double> rect, ReadOnlySpan<double> matrix, Span<double> result)
    {
        double x0 = (matrix[0] * rect[0]) + (matrix[2] * rect[1]) + matrix[4];
        double y0 = (matrix[1] * rect[0]) + (matrix[3] * rect[1]) + matrix[5];
        double x1 = (matrix[0] * rect[0]) + (matrix[2] * rect[3]) + matrix[4];
        double y1 = (matrix[1] * rect[0]) + (matrix[3] * rect[3]) + matrix[5];
        double x2 = (matrix[0] * rect[2]) + (matrix[2] * rect[1]) + matrix[4];
        double y2 = (matrix[1] * rect[2]) + (matrix[3] * rect[1]) + matrix[5];
        double x3 = (matrix[0] * rect[2]) + (matrix[2] * rect[3]) + matrix[4];
        double y3 = (matrix[1] * rect[2]) + (matrix[3] * rect[3]) + matrix[5];
        double llx = x0;
        if (x1 < llx) llx = x1;
        if (x2 < llx) llx = x2;
        if (x3 < llx) llx = x3;
        double lly = y0;
        if (y1 < lly) lly = y1;
        if (y2 < lly) lly = y2;
        if (y3 < lly) lly = y3;
        double urx = x0;
        if (x1 > urx) urx = x1;
        if (x2 > urx) urx = x2;
        if (x3 > urx) urx = x3;
        double ury = y0;
        if (y1 > ury) ury = y1;
        if (y2 > ury) ury = y2;
        if (y3 > ury) ury = y3;
        result[0] = llx; result[1] = lly; result[2] = urx; result[3] = ury;
    }

    internal static void Concat(ReadOnlySpan<double> first, ReadOnlySpan<double> second, Span<double> result)
    {
        double a = (first[0] * second[0]) + (first[2] * second[1]);
        double b = (first[1] * second[0]) + (first[3] * second[1]);
        double c = (first[0] * second[2]) + (first[2] * second[3]);
        double d = (first[1] * second[2]) + (first[3] * second[3]);
        double e = (first[0] * second[4]) + (first[2] * second[5]) + first[4];
        double f = (first[1] * second[4]) + (first[3] * second[5]) + first[5];
        result[0] = a; result[1] = b; result[2] = c; result[3] = d; result[4] = e; result[5] = f;
    }

    // The enclosing feature decides how to handle malformed/missing geometry.
    // Resolve each array member once, matching the copied pool's numeric rules.
    internal static bool TryReadFiniteNumbers(QpdfObject? value, IIndirectResolver resolver, Span<double> numbers)
    {
        if (value is not PdfArray array || array.Count != numbers.Length) { return false; }
        for (int index = 0; index < numbers.Length; index++)
        {
            QpdfObject? item = array[index];
            if (item is PdfReference reference && !resolver.TryResolve(reference, out item)) { return false; }
            double number;
            if (item is PdfInteger integer) { number = integer.Value; }
            else if (item is PdfReal real) { number = real.Value; }
            else { return false; }
            if (!double.IsFinite(number)) { return false; }
            numbers[index] = number;
        }
        return true;
    }

    internal static double[]? ReadFiniteNumbers(QpdfObject? value, int count, IIndirectResolver resolver)
    {
        if (value is not PdfArray array || array.Count != count) { return null; }
        double[] numbers = new double[count];
        return TryReadFiniteNumbers(array, resolver, numbers) ? numbers : null;
    }
}
