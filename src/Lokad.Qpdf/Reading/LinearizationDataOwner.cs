using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Reading;

/// <summary>Owns parsed hints while their checker/report borrows them.</summary>
internal sealed class LinearizationDataOwner : IDisposable
{
    private OwnedAllocationBatch? _allocations;
    private LinearizationData? _data;

    internal LinearizationDataOwner(OwnedAllocationBatch allocations, LinearizationData data)
    {
        _allocations = allocations;
        _data = data;
    }

    internal LinearizationData Data => _data ?? throw new ObjectDisposedException(nameof(LinearizationDataOwner));

    // Each table, entry and array is a separate carrier, so a surviving child
    // keeps its receipt without retaining discarded siblings or decoded scratch.
    internal LinearizationData Escape()
    {
        LinearizationData data = Data;
        OwnedAllocationBatch allocations = _allocations ?? throw new ObjectDisposedException(nameof(LinearizationDataOwner));
        allocations.Retain();
        _data = null;
        return data;
    }

    public void Dispose()
    {
        OwnedAllocationBatch? allocations = _allocations;
        _data = null;
        _allocations = null;
        allocations?.Dispose();
    }
}
