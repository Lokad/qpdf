using System.Reflection;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[CollectionDefinition(nameof(CompletedJobOwnershipCollection), DisableParallelization = true)]
public sealed class CompletedJobOwnershipCollection;

// Shared observer for the remaining diagnostic-history controls. Small metadata
// receipt minima and prescribed collection phases are no longer test obligations.
public sealed class CompletedJobOwnershipTests
{
    private static object Field(object value, string name)
        => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing ownership field: " + name);

    private static WeakReference[] Metadata(ResourceGovernor governor)
        => [new(governor), new(Field(governor, "_valueOwners"))];

}
