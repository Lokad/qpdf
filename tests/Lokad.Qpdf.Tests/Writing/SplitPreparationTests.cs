using Lokad.Qpdf.Operations;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SplitPreparationTests
{
    [Theory]
    [InlineData("out.pdf", 0, 1, "", "")]
    [InlineData("out", 1, 8, "1:1", "out-1")]
    [InlineData("out.PDF", 9, 4, "1:4|5:8|9:9", "out-1-4.PDF|out-5-8.PDF|out-9.PDF")]
    [InlineData("out.PDF", 10, 4, "1:4|5:8|9:10", "out-01-04.PDF|out-05-08.PDF|out-09-10.PDF")]
    [InlineData("out%d.%d", 3, 2, "1:2|3:3", "out1-2.%d|out3.%d")]
    [InlineData("out.pdf", int.MaxValue, int.MaxValue - 1, "1:2147483646|2147483647:2147483647",
        "out-0000000001-2147483646.pdf|out-2147483647.pdf")]
    public void PreparedGroupsKeepTheNameAndRangeTogether(string pattern, int pages, int size, string ranges, string names)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<SplitAssembly.SplitGroup> groups = SplitAssembly.PrepareGroups(pattern, pages, size, memory, governor);
        Assert.Equal(ranges, string.Join("|", groups.Select(static group => FormattableString.Invariant($"{group.First}:{group.Last}"))));
        Assert.Equal(names, string.Join("|", groups.Select(static group => group.Target.Value)));
    }

    [Fact]
    public void PathLimitUsesTheLongestTargetInThePlan()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        string prefix = new('x', QpdfPath.MaxLength - 2);
        SplitAssembly.SplitGroup group = Assert.Single(SplitAssembly.PrepareGroups(prefix, 1, 2, memory, governor));
        Assert.Equal(QpdfPath.MaxLength, group.Target.Value.Length);
        Assert.Throws<QpdfParseException>(() => SplitAssembly.PrepareGroups(prefix, 2, 2, memory, governor));
    }
}
