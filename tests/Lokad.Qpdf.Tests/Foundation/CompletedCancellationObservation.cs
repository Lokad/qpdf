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
        // while unescaped plan text and private decoded-name receipts retire.
        for (int round = 0; round < 4; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            governor.ReserveMemory(0L);
        }
        Assert.Equal(256L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        GC.KeepAlive(result);
    }
}
