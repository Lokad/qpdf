using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class SelectionStorageAdmissionTests
{
    private sealed class Capture(ResourceGovernor governor, object value)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Capture Create(QpdfLimits limits, CancellationToken token, bool mapping)
    {
        ResourceGovernor governor = new(limits, token);
        using OwnedAllocationBatch storage = OwnedAllocationBatch.Create(governor, mapping ? 1 : 2, token);
        object value;
        if (mapping)
        {
            storage.Memory.Reserve(24L);
            PdfPageSelectionMapping entry = new(0, 1);
            storage.Add(entry, 24L);
            value = entry;
        }
        else { value = ResultCollectionStorage.Copy(storage, new[] { 2, 1 }, sizeof(int)); }
        storage.RetainWithPrepaidRetirement();
        return new(governor, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Capture? Prepare(string boundary)
    {
        Assert.True(GC.TryStartNoGCRegion(16L * 1024L * 1024L));
        Capture? capture = null;
        try
        {
            bool mapping = boundary.StartsWith("work", StringComparison.Ordinal);
            Capture baseline = Create(QpdfLimits.Default, CancellationToken.None, mapping);
            QpdfExecutionCounters counters = baseline.Governor.Snapshot();
            QpdfLimits limits = QpdfLimits.Default with
            {
                MaxMemoryBytes = mapping ? QpdfLimits.Default.MaxMemoryBytes : counters.PeakMemoryBytes - (boundary == "memory-short" ? 1 : 0),
                // One fresh mapping has no identity-chain collision. Array/view
                // admission can visit a bucket collision, so its paired jobs
                // prove exact memory while work stays under the default limit.
                MaxWork = mapping ? counters.TotalWork + 1L - (boundary == "work-short" ? 1 : 0) : QpdfLimits.Default.MaxWork,
                CancellationCheckInterval = 1L,
            };
            if (boundary == "pre-canceled") { Assert.ThrowsAny<OperationCanceledException>(() => Create(limits, new CancellationToken(true), mapping)); }
            else if (boundary.EndsWith("short", StringComparison.Ordinal))
            {
                QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => Create(limits, CancellationToken.None, mapping));
                Assert.Equal(boundary == "memory-short" ? nameof(QpdfLimits.MaxMemoryBytes) : nameof(QpdfLimits.MaxWork), error.LimitName);
            }
            else
            {
                capture = Create(limits, CancellationToken.None, mapping);
                if (mapping) { Assert.Equal(new PdfPageSelectionMapping(0, 1), capture.Value); }
                else { Assert.Equal(new[] { 2, 1 }, (IReadOnlyList<int>)(capture.Value ?? throw new InvalidOperationException())); }
                if (!mapping) { Assert.Equal(counters.PeakMemoryBytes, capture.Governor.Snapshot().PeakMemoryBytes); }
                else { Assert.Equal(counters.TotalWork, capture.Governor.Snapshot().TotalWork); }
            }
            GC.KeepAlive(baseline);
        }
        finally { GC.EndNoGCRegion(); }
        return capture;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Drop(Capture capture) => capture.Value = null;

    [Theory]
    [InlineData("memory-exact")]
    [InlineData("memory-short")]
    [InlineData("work-exact")]
    [InlineData("work-short")]
    [InlineData("pre-canceled")]
    public void ExactAdmissionIncludesBothPacketsAndTheirRetirementWork(string boundary)
    {
        Capture? capture = Prepare(boundary);
        if (capture is not null)
        {
            Drop(capture);
            for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            for (int boundaryIndex = 0; boundaryIndex < 4; boundaryIndex++) { capture.Governor.ReserveMemory(0L); }
            Assert.Equal(0L, capture.Governor.Snapshot().LiveMemoryBytes);
            GC.KeepAlive(capture);
        }
    }
}
