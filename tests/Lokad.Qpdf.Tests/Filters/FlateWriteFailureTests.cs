using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FlateWriteFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusedOutputGrowthDoesNotRetryDuringCompressorCleanup(bool scoped)
    {
        byte[] input = new byte[262144]; new Random(307).NextBytes(input);
        RecordingMemoryBudget baseline = new(1L << 24);
        using (IMemoryScope owner = baseline.CreateScope())
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new BudgetHost(owner));
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            byte[] output = scoped
                ? FlateFilter.Encode(input, null, QpdfLimits.Default, governor, memory, CancellationToken.None)
                : FlateFilter.Encode(input, null, QpdfLimits.Default, governor);
            Assert.True(output.Length > input.Length);
        }
        Assert.True(baseline.GrowthCalls > 3);
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int refusal = 1; refusal <= baseline.GrowthCalls; refusal++)
        {
            RecordingMemoryBudget root = new(1L << 24) { RefuseOnGrowth = refusal };
            using (IMemoryScope owner = root.CreateScope())
            {
                ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new BudgetHost(owner));
                Assert.Throws<QpdfMemoryAdmissionException>(() =>
                {
                    using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
                    if (scoped) { FlateFilter.Encode(input, null, QpdfLimits.Default, governor, memory, CancellationToken.None); }
                    else { FlateFilter.Encode(input, null, QpdfLimits.Default, governor); }
                });
                Assert.Equal(refusal, root.GrowthCalls);
                Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
    }

    private sealed class BudgetHost(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new InvalidOperationException();
    }
}
