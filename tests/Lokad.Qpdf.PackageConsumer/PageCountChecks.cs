using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.TestSupport;

internal static class PageCountChecks
{
    private static void Require(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class RangeHost(InMemoryQpdfHost inner, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal long ReadBytes { get; private set; }
        public IMemoryBudget MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(this, await inner.OpenInputAsync(path, token));
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("Page counting must not create output.");

        private sealed class Input(RangeHost host, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                int count = await inner.ReadAsync(offset, destination[..Math.Min(destination.Length, 37)], token);
                host.ReadBytes += count; return count;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    internal static async Task RunAsync(byte[] source)
    {
        Require(source.Length > 524288, "Page-count input must contain a large unrelated object.");
        QpdfPath input = QpdfPath.From("count.pdf");
        using InMemoryQpdfHost storage = new(); storage.AddInput(input, source, QpdfInputOwnership.Copy);
        using RecordingMemoryBudget root = new(131072);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 131072 };
        for (int route = 0; route < 3; route++)
        {
            QpdfPageCountResult result;
            using (IMemoryScope owner = root.CreateScope())
            {
                RangeHost host = new(storage, owner);
                result = route switch
                {
                    0 => await Qpdf.CountPagesAsync(host, input, CancellationToken.None),
                    1 => await Qpdf.CountPagesAsync(host, input, limits, CancellationToken.None),
                    _ => await Qpdf.CountPagesAsync(host, input, null, QpdfRecoveryMode.BoundedRepair, limits, CancellationToken.None),
                };
                Require(result.PageCount == 2 && result.Warnings.Count == 0, "Typed count validates both pages.");
                Require(host.ReadBytes is > 0 and < 262144, "Count omits the large unrelated payload.");
                Require(result.Counters.OpenHandles == 0 && storage.OpenLeaseCount == 0, "Count closes its input before returning.");
                Require(result.Counters.PeakMemoryBytes <= limits.MaxMemoryBytes, "Count respects the smaller allowance.");
                Require(root.ReservedBytes > 0, "Returned count/counters retain caller admission.");
            }
            Require(root.ReservedBytes == 0 && result.PageCount == 2 && result.Counters.OpenHandles == 0,
                "Caller closure refunds count owners without changing returned values.");
        }
        Console.WriteLine("PASS typed bounded page count through short range reads and all public overloads");
    }
}
