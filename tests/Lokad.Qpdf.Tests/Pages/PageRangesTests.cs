using Lokad.Qpdf.Pages;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Pages;

public sealed class PageRangesTests
{
    private static void Matches(int total, string? range, int[] expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        IReadOnlyList<int> actual = PageRanges.Expand(range, total, long.MaxValue, governor, memory);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyDocumentAndAllPagesRangeSelectNothing(string? range)
    {
        Matches(0, range, []);
    }

    [Fact]
    public void EmptyRangeSelectsEveryPage()
    {
        Matches(6, "", [1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public void NullRangeSelectsEveryPage()
    {
        Matches(6, null, [1, 2, 3, 4, 5, 6]);
    }

    [Theory]
    [InlineData("1,6,4", new int[] { 1, 6, 4 })]
    [InlineData("3-7", new int[] { 3, 4, 5, 6, 7 })]
    [InlineData("7-3", new int[] { 7, 6, 5, 4, 3 })]
    [InlineData("1,3,5-9,15-12", new int[] { 1, 3, 5, 6, 7, 8, 9, 15, 14, 13, 12 })]
    [InlineData("r3-r1", new int[] { 13, 14, 15 })]
    [InlineData("r1-r3", new int[] { 15, 14, 13 })]
    [InlineData("5,7-9,12", new int[] { 5, 7, 8, 9, 12 })]
    public void ManualExamplesExpandInOrder(string range, int[] expected)
    {
        Matches(15, range, expected);
    }

    [Fact]
    public void FullSpanFormsExpand()
    {
        Matches(15, "1-z", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);
        Matches(6, "z-1", [6, 5, 4, 3, 2, 1]);
    }

    [Theory]
    [InlineData("1-20:even", new int[] { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20 })]
    [InlineData("5,7-9,12:odd", new int[] { 5, 8, 12 })]
    [InlineData("5,7-9,12:even", new int[] { 7, 9 })]
    public void PositionFiltersApplyToFinalSet(string range, int[] expected)
    {
        Matches(20, range, expected);
    }

    [Fact]
    public void SuffixAloneSelectsNothing()
    {
        Matches(6, ":even", []);
        Matches(6, ":odd", []);
    }

    [Theory]
    [InlineData("1-10,x3-4", new int[] { 1, 2, 5, 6, 7, 8, 9, 10 })]
    [InlineData("4-10,x7-9,12-8,xr5", new int[] { 4, 5, 6, 10, 12, 10, 9, 8 })]
    [InlineData("1-6,x2,x3", new int[] { 1, 4, 5, 6 })]
    [InlineData("6-1,x5", new int[] { 6, 4, 3, 2, 1 })]
    [InlineData("1-6,x2:odd", new int[] { 1, 4, 6 })]
    public void ExclusionsRemoveAccumulatedMembers(string range, int[] expected)
    {
        Matches(15, range, expected);
    }

    [Theory]
    [InlineData("1,1", new int[] { 1, 1 })]
    [InlineData("z,z", new int[] { 6, 6 })]
    [InlineData("01-3", new int[] { 1, 2, 3 })]
    [InlineData("r2", new int[] { 5 })]
    [InlineData("z", new int[] { 6 })]
    [InlineData("r6", new int[] { 1 })]
    public void OracleProbedFormsAgree(string range, int[] expected)
    {
        Matches(6, range, expected);
    }

    [Theory]
    [InlineData("x1-3")]
    [InlineData("1-")]
    [InlineData(",1")]
    [InlineData("1,")]
    [InlineData("r")]
    [InlineData("x")]
    [InlineData("1-6:EVEN")]
    [InlineData("1::even")]
    [InlineData("1-6:odd:even")]
    [InlineData("1 2")]
    [InlineData("1-2-3")]
    [InlineData("xx2")]
    [InlineData("1-3,x")]
    [InlineData("99999999999999999999")]
    public void OracleProbedRejectionsFail(string range)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.Throws<QpdfParseException>(() => PageRanges.Expand(range, 6, long.MaxValue, governor, memory));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("r0")]
    [InlineData("r99")]
    [InlineData("7-3")]
    [InlineData("4-10,x7-9")]
    public void OutOfRangeEndpointsFailCleanly(string range)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRanges.Expand(range, 6, long.MaxValue, governor, memory));
    }

    [Fact]
    public void ExcludedEndpointsStillValidate()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRanges.Expand("4-10,x7-9", 6, long.MaxValue, governor, memory));
    }

    [Fact]
    public void ZeroTotalFails()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRanges.Expand("1", 0, long.MaxValue, governor, memory));
    }

    [Fact]
    public void OversizedTextFailsBeforeAllocating()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        string range = new string('1', PageRanges.MaxRangeTextLength + 1);
        Assert.Throws<QpdfParseException>(() => PageRanges.Expand(range, 6, long.MaxValue, governor, memory));
    }

    [Fact]
    public void ExpansionHonorsTheEntryBudget()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.Throws<QpdfLimitException>(() => PageRanges.Expand("1-6", 6, 5L, governor, memory));
        Matches(6, "1-6", [1, 2, 3, 4, 5, 6]);
    }
    [Fact]
    public void EntireSelectionHonorsTheEntryBudget()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.Throws<QpdfLimitException>(() => PageRanges.Expand(null, 10000, 1L, governor, memory));
        Assert.Throws<QpdfLimitException>(() => PageRanges.Expand("", 10000, 1L, governor, memory));
    }
}
