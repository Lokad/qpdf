using System.Reflection;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[CollectionDefinition(nameof(CompletedJobOwnershipCollection), DisableParallelization = true)]
public sealed class CompletedJobOwnershipCollection;

// Shared observer for the remaining diagnostic-history controls. Small metadata
// receipt minima and prescribed collection phases are no longer test obligations.
public sealed class CompletedJobOwnershipTests
{
    private static WeakReference[] Metadata(ResourceGovernor governor)
    {
        FieldInfo owners = governor.GetType().GetField("_valueOwners", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing ownership field: _valueOwners");
        object? retained = owners.GetValue(governor);
        return retained is null ? [new(governor)] : [new(governor), new(retained)];
    }

}
