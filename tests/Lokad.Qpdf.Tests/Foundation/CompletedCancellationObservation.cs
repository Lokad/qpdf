using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

internal static class CompletedCancellationObservation
{
    internal static ResourceGovernor FindGovernor(Task running)
    {
        const BindingFlags members = BindingFlags.Static | BindingFlags.NonPublic;
        return (ResourceGovernor)(typeof(ExecutionCounterTests).GetMethod("FindGovernor", members)?.Invoke(null,
            [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
            ?? throw new InvalidOperationException("Missing original complete-job governor observer."));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Retire(ResourceGovernor governor, QpdfExecutionResult result)
    {
        // The producer has returned. Retained result/counter aliases survive
        // while private plan text and decoded names have been released.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        GC.KeepAlive(result);
    }
}
