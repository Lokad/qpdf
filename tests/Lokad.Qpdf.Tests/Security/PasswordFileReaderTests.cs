using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordFileReaderTests
{
    [Theory]
    [InlineData("", false, false, "", false)]
    [InlineData("\n", false, true, "", false)]
    [InlineData("\r\n", false, true, "", false)]
    [InlineData(" reader \r\nignored", false, true, "2072656164657220", true)]
    [InlineData("reader\r", false, true, "7265616465720D", false)]
    [InlineData("reader\0suffix", false, true, "726561646572", false)]
    [InlineData("café\u0080\n", false, true, "636166E980", false)]
    [InlineData("63:61:66:e9:8\r\nignored", true, true, "636166E980", true)]
    [InlineData("zz\n", true, true, "", false)]
    [InlineData("72656164657200ff", true, true, "726561646572", false)]
    public async Task FirstLinePreservesNativeByteAndLineSemantics(string text, bool hex, bool hasLine, string expected, bool more)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("password"), Encoding.Latin1.GetBytes(text), QpdfInputOwnership.Transfer);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        owner.Reserve(159); byte[] prefix = new byte[127];
        PasswordFileLine line = await PasswordFileReader.ReadAsync(storage, QpdfPath.From("password"), prefix,
            hex, QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal(hasLine, line.HasLine); Assert.Equal(more, line.HasMoreLines);
        Assert.Equal(Convert.FromHexString(expected), prefix.AsSpan(0, line.Count).ToArray());
        Assert.Equal(0, storage.OpenLeaseCount); owner.Dispose(); Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefixTruncationAndCrLfWorkAcrossShortReads(bool hex)
    {
        using InMemoryQpdfHost storage = new();
        string text = hex ? string.Concat(Enumerable.Repeat("61:", 500)) + "\r\nignored" : new string('a', 126) + "\r\nignored";
        storage.AddInput(QpdfPath.From("password"), Encoding.ASCII.GetBytes(text), QpdfInputOwnership.Transfer);
        ReaderHost host = new(storage, null, "short");
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(159);
        byte[] prefix = new byte[127];
        PasswordFileLine line = await PasswordFileReader.ReadAsync(host, QpdfPath.From("password"), prefix,
            hex, QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal(hex ? 127 : 126, line.Count); Assert.True(line.HasMoreLines);
        Assert.All(prefix.Take(line.Count), value => Assert.Equal((byte)'a', value));
        Assert.InRange(host.MaxReadRequest, 1, 512); Assert.Equal(0, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("close")]
    public async Task FailuresClearThePrefixAndRefundReaderStorage(string fault)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("password"), "reader\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        ReaderHost host = new(storage, null, fault);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(159);
        byte[] prefix = new byte[127]; Array.Fill(prefix, (byte)99);
        await Assert.ThrowsAsync<QpdfHostException>(() => PasswordFileReader.ReadAsync(host, QpdfPath.From("password"), prefix,
            false, QpdfLimits.Default, governor, CancellationToken.None).AsTask());
        Assert.All(prefix, value => Assert.Equal(0, value)); Assert.Equal(0, storage.OpenLeaseCount);
        owner.Dispose(); Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task EveryReaderAdmissionRefusalClosesInputAndRefundsStorage()
    {
        (int before, int growths) = await Run(0, 0);
        for (int refusal = 1; refusal <= growths; refusal++) { _ = await Run(before, refusal); }

        static async Task<(int Before, int Growths)> Run(int offset, int refusal)
        {
            using InMemoryQpdfHost storage = new();
            storage.AddInput(QpdfPath.From("password"), "reader\n"u8.ToArray(), QpdfInputOwnership.Transfer);
            RecordingMemoryBudget budget = refusal == 0 ? new(4096) : new(4096) { RefuseOnGrowth = offset + refusal };
            ReaderHost host = new(storage, budget, "");
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(159);
            byte[] prefix = new byte[127];
            int before = budget.GrowthCalls;
            if (refusal != 0) { Assert.Equal(offset, before); }
            Task<PasswordFileLine> Read() => PasswordFileReader.ReadAsync(host, QpdfPath.From("password"), prefix,
                false, QpdfLimits.Default, governor, CancellationToken.None).AsTask();
            if (refusal == 0) { Assert.Equal(6, (await Read()).Count); }
            else { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Read); Assert.All(prefix, value => Assert.Equal(0, value)); }
            int growths = budget.GrowthCalls - before;
            Assert.Equal(0, storage.OpenLeaseCount); owner.Dispose(); Assert.Equal(0, budget.ReservedBytes);
            return (before, growths);
        }
    }

    [Fact]
    public async Task ReaderStorageStaysAdmittedUntilAsynchronousCloseCompletes()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("password"), "reader\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        ReaderHost host = new(storage, null, "delay-close");
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(159);
        byte[] prefix = new byte[127]; long baseline = governor.Snapshot().LiveMemoryBytes;
        Task<PasswordFileLine> reading = PasswordFileReader.ReadAsync(host, QpdfPath.From("password"), prefix,
            false, QpdfLimits.Default, governor, CancellationToken.None).AsTask();
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(reading.IsCompleted); Assert.Equal(1, storage.OpenLeaseCount);
            Assert.True(governor.Snapshot().LiveMemoryBytes > baseline);
        }
        finally { host.ResumeClose.TrySetResult(); await reading; }
        Assert.Equal(0, storage.OpenLeaseCount); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputLimitsAndCancellationLeaveNoOpenReader(bool canceled)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("password"), "reader\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 4 };
        ResourceGovernor governor = new(limits, cancellation.Token);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(159);
        byte[] prefix = new byte[127]; Array.Fill(prefix, (byte)99);
        if (canceled) { cancellation.Cancel(); }
        Task<PasswordFileLine> Read() => PasswordFileReader.ReadAsync(storage, QpdfPath.From("password"), prefix,
            false, limits, governor, cancellation.Token).AsTask();
        if (canceled) { await Assert.ThrowsAnyAsync<OperationCanceledException>(Read); }
        else { await Assert.ThrowsAsync<QpdfLimitException>(Read); }
        Assert.All(prefix, value => Assert.Equal(0, value)); Assert.Equal(0, storage.OpenLeaseCount);
        owner.Dispose(); Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class ReaderHost(InMemoryQpdfHost inner, IMemoryBudget? budget, string fault) : IQpdfHost, IQpdfMemoryHost
    {
        private readonly string _fault = fault;
        private readonly IMemoryBudget _budget = budget ?? new RecordingMemoryBudget(4096);
        public IMemoryBudget MemoryBudget => _budget;
        internal int MaxReadRequest;
        internal readonly TaskCompletionSource Closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ResumeClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(this, await inner.OpenInputAsync(path, token));
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("Password reading cannot publish output.");
        private sealed class Input(ReaderHost host, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                host.MaxReadRequest = Math.Max(host.MaxReadRequest, destination.Length);
                if (host._fault == "read") { return new(0); }
                return inner.ReadAsync(offset, host._fault == "short" ? destination[..Math.Min(1, destination.Length)] : destination, token);
            }
            public async ValueTask DisposeAsync()
            {
                if (host._fault == "delay-close") { host.Closing.TrySetResult(); await host.ResumeClose.Task; }
                await inner.DisposeAsync();
                if (host._fault == "close") { throw new QpdfHostException(Path, QpdfHostFailureReason.NonProgressingRead); }
            }
        }
    }
}
