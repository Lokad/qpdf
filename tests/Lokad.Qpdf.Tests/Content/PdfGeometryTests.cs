using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed class PdfGeometryTests
{
    public static IEnumerable<object[]> Rectangles()
    {
        yield return [new double[] { 10, 20, 30, 40 }, new double[] { 1, 0, 0, 1, 0, 0 }, new double[] { 10, 20, 30, 40 }];
        yield return [new double[] { 10, 20, 30, 40 }, new double[] { 0, 1, -1, 0, 0, 0 }, new double[] { -40, 10, -20, 30 }];
        yield return [new double[] { 10, 20, 30, 40 }, new double[] { -1, 0, 0, -1, 0, 0 }, new double[] { -30, -40, -10, -20 }];
        yield return [new double[] { 10, 20, 30, 40 }, new double[] { 0, -1, 1, 0, 0, 0 }, new double[] { 20, -30, 40, -10 }];
        yield return [new double[] { 10, 20, 30, 40 }, new double[] { 1, 0, 0, 1, 5, -7 }, new double[] { 15, 13, 35, 33 }];
        yield return [new double[] { 10, 20, 30, 40 }, new double[] { 2, 0, 0, 2, 0, 0 }, new double[] { 20, 40, 60, 80 }];
        yield return [new double[] { 10, 20, 30, 40 }, new double[] { 1, 2, 3, 1, 5, -7 }, new double[] { 75, 33, 155, 93 }];
        yield return [new double[] { 30, 40, 10, 20 }, new double[] { 1, 0, 0, 1, 0, 0 }, new double[] { 10, 20, 30, 40 }];
        yield return [new double[] { 1, 2, 1, 2 }, new double[] { 0, 1, -1, 0, 3, 4 }, new double[] { 1, 5, 1, 5 }];
    }

    [Theory]
    [MemberData(nameof(Rectangles))]
    public void BoundsCoverAllCornersAndPermitAnOverlappingDestination(double[] rect, double[] matrix, double[] expected)
    {
        Span<double> result = stackalloc double[4];
        PdfGeometry.TransformRect(rect, matrix, result);
        Assert.Equal(expected, result.ToArray());
        double[] alias = (double[])rect.Clone();
        PdfGeometry.TransformRect(alias, matrix, alias);
        Assert.Equal(expected, alias);
    }

    [Theory]
    [InlineData(false, 10, 21)]
    [InlineData(true, 5, 7)]
    public void CompositionKeepsTranslationOrderAndAllowsEitherOperandToBeTheDestination(bool reverse, double x, double y)
    {
        double[] scale = [2, 0, 0, 3, 0, 0], translate = [1, 0, 0, 1, 5, 7];
        double[] first = reverse ? translate : scale, second = reverse ? scale : translate;
        double[] expected = [2, 0, 0, 3, x, y];
        double[] left = (double[])first.Clone(), right = (double[])second.Clone();
        PdfGeometry.Concat(left, second, left);
        PdfGeometry.Concat(first, right, right);
        Assert.Equal(expected, left); Assert.Equal(expected, right);
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("reference", true)]
    [InlineData("missing", false)]
    [InlineData("alias", false)]
    [InlineData("null", false)]
    [InlineData("name", false)]
    [InlineData("nan", false)]
    [InlineData("infinity", false)]
    [InlineData("length", false)]
    public void NumericArraysAcceptFiniteNumbersAndOneResolvedMember(string shape, bool valid)
    {
        PdfInteger integer = new("2", 2);
        PoolResolver resolver = new(new Dictionary<int, QpdfObject> { [7] = integer, [8] = new PdfReference(7, 0) });
        QpdfObject member = shape switch
        {
            "direct" => integer,
            "reference" => new PdfReference(7, 0),
            "missing" => new PdfReference(99, 0),
            "alias" => new PdfReference(8, 0),
            "null" => PdfNull.Instance,
            "name" => PdfName.FromAscii("Number"),
            "nan" => new PdfReal("0", double.NaN),
            "infinity" => new PdfReal("0", double.PositiveInfinity),
            _ => integer,
        };
        PdfArray array = shape == "length" ? new([integer]) : new([integer, member, new PdfReal("0.5", 0.5), integer]);
        Span<double> numbers = stackalloc double[4];
        Assert.Equal(valid, PdfGeometry.TryReadFiniteNumbers(array, resolver, numbers));
        Assert.Equal(valid, PdfGeometry.ReadFiniteNumbers(array, 4, resolver) is not null);
        if (valid) { Assert.Equal(new double[] { 2, 2, 0.5, 2 }, numbers.ToArray()); }
    }
}
