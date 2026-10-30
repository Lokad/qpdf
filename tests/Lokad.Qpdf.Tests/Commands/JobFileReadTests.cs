using System.Runtime.InteropServices;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JobFileReadTests
{
    private sealed class Host(InMemoryQpdfHost storage, int length, int maximumRead, string outcome,
        CancellationTokenSource? cancellation) : IQpdfHost
    {
        internal int Reads { get; private set; }
        private readonly int _length = length;
        private readonly int _maximumRead = maximumRead;
        private readonly string _outcome = outcome;
        private readonly CancellationTokenSource? _cancellation = cancellation;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(this, await storage.OpenInputAsync(path, token));
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => storage.CreateOutputAsync(path, token);
        private sealed class Input(Host host, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out ArraySegment<byte> backing));
                Assert.NotNull(backing.Array);
                Assert.Equal(host._length, backing.Array.Length);
                Assert.Equal(offset, backing.Offset);
                Assert.InRange(destination.Length, 1, 65536);
                host.Reads++;
                if (host._outcome == "zero") { return 0; }
                if (host._outcome == "read") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                int count = await inner.ReadAsync(offset, destination[..Math.Min(host._maximumRead, destination.Length)], token);
                if (offset + count == host._length) { host._cancellation?.Cancel(); }
                return count;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(65536)]
    public async Task ShortAndCompleteReadsFillOneAdmittedDestination(int maximumRead)
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"empty\":\"\",\"showNpages\":\"\"}" + new string(' ', 131072));
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("job"), json, QpdfInputOwnership.Transfer);
        Host host = new(storage, json.Length, maximumRead, "success", null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (JobJsonLoader.LoadedJob job = await JobJsonLoader.ParseArgvAsync(host, ["--job-json-file=job"],
            QpdfLimits.Default, governor, CancellationToken.None))
        { Assert.NotNull(job.Plan); }
        Assert.True(host.Reads >= (json.Length + 65535L) / 65536L);
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("governor")]
    [InlineData("zero")]
    [InlineData("read")]
    public async Task FinalReadCancellationAndTransportErrorsCloseTheInput(string outcome)
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"empty\":\"\",\"showNpages\":\"\"}" + new string(' ', 65536));
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("job"), json, QpdfInputOwnership.Transfer);
        using CancellationTokenSource cancellation = new();
        Host host = new(storage, json.Length, 1009, outcome, outcome is "caller" or "governor" ? cancellation : null);
        ResourceGovernor governor = new(QpdfLimits.Default, outcome == "governor" ? cancellation.Token : CancellationToken.None);
        Func<Task> load = async () =>
        {
            using JobJsonLoader.LoadedJob job = await JobJsonLoader.ParseArgvAsync(host, ["--job-json-file=job"],
                QpdfLimits.Default, governor, outcome == "caller" ? cancellation.Token : CancellationToken.None);
        };
        if (outcome is "caller" or "governor") { await Assert.ThrowsAnyAsync<OperationCanceledException>(load); }
        else
        {
            QpdfHostException error = await Assert.ThrowsAsync<QpdfHostException>(load);
            Assert.Equal(outcome == "zero" ? QpdfHostFailureReason.NonProgressingRead : QpdfHostFailureReason.ReadFailed, error.Reason);
        }
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
