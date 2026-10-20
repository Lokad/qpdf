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

    public void Dispose()
    {
        OwnedAllocationBatch? allocations = _allocations;
        _data = null;
        _allocations = null;
        allocations?.Dispose();
    }
}
