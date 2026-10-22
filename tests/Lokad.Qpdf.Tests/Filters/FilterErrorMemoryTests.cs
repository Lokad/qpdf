using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FilterErrorMemoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeDynamicNameErrorsKeepTheAliasWithoutDuplicatingItsText(bool inner)
    {
        string name = new('N', 1048576);
        InvalidOperationException cause = new("codec fault");
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfFilterException error = inner
            ? new(QpdfFilterFailureReason.InvalidData, name, "Cannot decode", cause)
            : new(QpdfFilterFailureReason.UnsupportedFilter, name, "Cannot decode");
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 16384L);
        Assert.InRange(error.Message.Length, 1, 512);
        Assert.Contains(new string('N', 256) + "...", error.Message, StringComparison.Ordinal);
        Assert.Same(name, error.FilterName);
        if (inner) { Assert.Same(cause, error.InnerException); }
    }

    [Fact]
    public void DecodingAnUnknownLargeFilterPreservesItsAdmittedNameAfterPlanCleanup()
    {
        const int length = 1048576;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Filter")] = new PdfName(Enumerable.Repeat((byte)'N', length).ToArray()) });
        using FilterPlan plan = FilterChain.Resolve(dictionary, null, governor, CancellationToken.None);
        string name = plan[0].Name;
        long before = GC.GetAllocatedBytesForCurrentThread();
        using ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor);
        QpdfFilterException error = Assert.Throws<QpdfFilterException>(() =>
            FilterChain.Decode([], plan, QpdfLimits.Default, governor, decodedMemory));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 16384L);
        Assert.Equal(QpdfFilterFailureReason.UnsupportedFilter, error.Reason);
        plan.Dispose();
        governor.CompletePrivateStorage();
        Assert.Equal(length, error.FilterName?.Length);
        Assert.Same(name, error.FilterName);
        Assert.True(governor.CallerStorageBytes >= GovernedText.StringBytes(length));
        Assert.InRange(error.Message.Length, 1, 512);
    }
}
