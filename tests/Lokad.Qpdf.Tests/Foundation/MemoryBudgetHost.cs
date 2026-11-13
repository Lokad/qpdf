namespace Lokad.Qpdf.Tests;

// Borrow storage and the caller's budget; neither owner is closed by this view.
internal sealed class MemoryBudgetHost(IQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
{
    public IMemoryBudget MemoryBudget => budget;
    public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
    public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
}
