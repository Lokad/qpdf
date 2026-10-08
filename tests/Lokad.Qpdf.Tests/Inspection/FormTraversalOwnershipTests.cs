using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed partial class FormTraversalOwnershipTests
{
    [Theory]
    [InlineData(2048L)]
    [InlineData(8192L)]
    public void GrowthFailureReleasesTraversalSetsBeforeCreatingItsFailureDetail(long available)
    {
        using Prepared prepared = new(512);
        long filler = prepared.Fill(available);
        QpdfExecutionCounters before = prepared.Governor.Snapshot();
        QpdfCheckOutcome result = RunForms(prepared.Document, prepared.Governor, prepared.Diagnostics, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Failed, result.Status);
        Assert.StartsWith("Form traversal exceeded its budget: ", result.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(QpdfLimits.MaxMemoryBytes), result.Detail, StringComparison.Ordinal);
        QpdfExecutionCounters after = prepared.Governor.Snapshot();
        Assert.InRange(after.LiveMemoryBytes - before.LiveMemoryBytes, 2L * result.Detail.Length, 2047L);
        Assert.True(after.TotalWork > before.TotalWork);
        Assert.True(after.PeakMemoryBytes <= prepared.Limits.MaxMemoryBytes);
        Assert.Same(prepared.Prior, Assert.Single(prepared.Diagnostics.Entries));
        prepared.Governor.ReleaseMemory(filler);
        GC.KeepAlive(result);
    }

}
