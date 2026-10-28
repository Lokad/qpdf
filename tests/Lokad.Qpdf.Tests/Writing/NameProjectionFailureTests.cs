using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class NameProjectionFailureTests
{
    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string fault, CancellationTokenSource cancellation)
        : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(await storage.CreateOutputAsync(path, token), fault, cancellation);

        private sealed class Transaction(IQpdfOutputTransaction inner, string fault, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                if (fault == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (fault == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token)
            {
                if (fault == "commit") { throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed); }
                return inner.CommitAsync(token);
            }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static async Task<QpdfExecutionResult?> Run(IQpdfHost host, string route, string key, CancellationToken token)
    {
        if (route == "typed")
        {
            await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, [key]), token);
            return null;
        }
        return route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=" + key, "--", "output"]), token)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
            { inputFile = "input", outputFile = "output", json = "2", jsonKey = new[] { key } }), token);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string key in new[] { "attachments", "acroform" })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string fault in new[] { "write", "commit", "cancel", "admission" }) { yield return [key, route, fault]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FailuresPreserveOutputAndReleasePrivateStorage(string key, string route, string fault)
    {
        using InMemoryQpdfHost storage = new(); using CancellationTokenSource cancellation = new();
        string name = new string('N', 4096) + "#23#C3#A9";
        storage.AddInput(QpdfPath.From("input"), key == "attachments" ? NameProjectionTests.Attachment("/" + name)
            : NameProjectionTests.Form("/" + name), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }

        RecordingMemoryBudget baseline = new(4194304L);
        int growths = 1;
        if (fault == "admission")
        {
            using IMemoryScope warm = baseline.CreateScope();
            await Run(new Host(storage, warm, "none", cancellation), route, key, CancellationToken.None);
            growths = baseline.GrowthCalls;
            await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        }
        for (int growth = 1; growth <= growths; growth++)
        {
            RecordingMemoryBudget root = new(4194304L) { RefuseOnGrowth = fault == "admission" ? growth : 0 };
            using IMemoryScope caller = root.CreateScope();
            Host host = new(storage, caller, fault, cancellation);
            if (route == "typed")
            {
                if (fault == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(host, route, key, cancellation.Token)); }
                else if (fault == "admission") { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Run(host, route, key, cancellation.Token)); }
                else { await Assert.ThrowsAsync<QpdfHostException>(() => Run(host, route, key, cancellation.Token)); }
            }
            else
            {
                QpdfExecutionResult? result = await Run(host, route, key, cancellation.Token);
                Assert.NotNull(result);
                Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
                if (fault != "cancel") { Assert.NotEmpty(result.Diagnostics); }
            }
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.InRange(root.ReservedBytes, 0L, route == "typed" ? 0L : 4096L);
            caller.Dispose(); Assert.Equal(0L, root.ReservedBytes);
        }
    }
}
