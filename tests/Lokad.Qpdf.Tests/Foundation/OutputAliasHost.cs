namespace Lokad.Qpdf.Tests.Foundation;

internal sealed class OutputAliasHost(RecordingJobHost host, QpdfPath alias, QpdfPath canonical) : IQpdfHost, IQpdfMemoryHost
{
    public IMemoryBudget MemoryBudget => host.Budget;

    public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
    {
        IQpdfInput input = await host.OpenInputAsync(Map(path), token);
        return path == alias ? new AliasInput(input, path) : input;
    }

    public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        => host.CreateOutputAsync(Map(path), token);

    private QpdfPath Map(QpdfPath path) => path == alias ? canonical : path;

    private sealed class AliasInput(IQpdfInput input, QpdfPath alias) : IQpdfInput
    {
        public QpdfPath Path => alias;
        public string Identity => input.Identity;
        public long Length => input.Length;
        public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            => input.ReadAsync(offset, destination, token);
        public ValueTask DisposeAsync() => input.DisposeAsync();
    }
}
