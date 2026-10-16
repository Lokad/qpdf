using System.Runtime.InteropServices;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class JsonPublicationMemoryTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    private sealed class Fixture : IDisposable
    {
        internal readonly RecordingMemoryBudget Root;
        private readonly IMemoryScope _storageOwner;
        internal readonly InMemoryQpdfHost Storage;
        internal Fixture(long limit)
        {
            Root = new(limit); _storageOwner = Root.CreateScope();
            Storage = new(QpdfLimits.Default, null, _storageOwner);
            byte[] data = new byte[1048576]; data.AsSpan().Fill((byte)'A');
            Storage.AddInput(Input, CheckTests.Assemble(CheckTests.SinglePage("<< /Length 1048576 >>", data), 1), QpdfInputOwnership.Transfer);
            Storage.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
            Storage.AddInput(QpdfPath.From("arguments"), "input\n--json=2\n--json-stream-data=inline\n--\noutput\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        }
        public void Dispose() { Storage.Dispose(); _storageOwner.Dispose(); }
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget caller, string phase, Exception? fault) : IQpdfHost, IQpdfMemoryHost
    {
        private readonly string _phase = phase;
        private readonly Exception? _fault = fault;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int BorrowedCapacity;
        public IMemoryBudget MemoryBudget => caller;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            if (_phase == "sidecar" && path.Value == "side-4" && _fault is not null) { throw _fault; }
            return new Transaction(this, await storage.CreateOutputAsync(path, token));
        }
        private sealed class Transaction(Host owner, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                Assert.True(MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> backing));
                owner.BorrowedCapacity = Assert.IsType<byte[]>(backing.Array).Length;
                if (owner._phase == "write")
                {
                    owner.Entered.TrySetResult(); await owner.Resume.Task;
                    token.ThrowIfCancellationRequested();
                    if (owner._fault is not null) { throw owner._fault; }
                }
                await inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token) => inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            {
                if (owner._phase == "close") { owner.Entered.TrySetResult(); await owner.Resume.Task; }
                await inner.DisposeAsync();
                if (owner._phase == "close" && owner._fault is not null) { throw owner._fault; }
            }
        }
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string route, string mode, CancellationToken token) => route switch
    {
        "job" => Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"json\":\"2\",\"jsonStreamData\":\"" + mode + "\",\"jsonStreamPrefix\":\"side\"}", token),
        "mixed" => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--job-json-file=settings", "--json=2", "--json-stream-data=" + mode, "--json-stream-prefix=side", "--", "output"]), token),
        "response" => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["@arguments"]), token),
        _ => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--json=2", "--json-stream-data=" + mode, "--json-stream-prefix=side", "--", "output"]), token),
    };

    [Theory]
    [InlineData("argv", "inline")]
    [InlineData("argv", "file")]
    [InlineData("job", "inline")]
    [InlineData("job", "file")]
    [InlineData("mixed", "inline")]
    [InlineData("response", "inline")]
    public async Task RepeatedPublicationRefundsPrivateBytesWhileCallerAndHostOwnersStayAlive(string route, string mode)
    {
        using Fixture fixture = new(16777216L);
        using IMemoryScope caller = fixture.Root.CreateScope();
        Host host = new(fixture.Storage, caller, "none", null);
        List<QpdfExecutionResult> results = [];
        for (int index = 0; index < 16; index++)
        {
            QpdfExecutionResult result = await Run(host, route, mode, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Null(result.OutputText);
            Assert.InRange(result.Counters.LiveMemoryBytes, 1L, 1024L);
            Assert.Equal(0L, fixture.Storage.OpenLeaseCount);
            results.Add(result);
        }
        using (IMemoryScope exported = fixture.Root.CreateScope())
        {
            Assert.True(fixture.Storage.TryGetOutput(Output, exported, out byte[] main));
            using JsonDocument parsed = JsonDocument.Parse(main);
            Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
            if (mode == "file")
            {
                Assert.True(fixture.Storage.TryGetOutput(QpdfPath.From("side-4"), exported, out byte[] sidecar));
                Assert.Equal(1048576, sidecar.Length);
                Assert.All(sidecar, value => Assert.Equal((byte)'A', value));
            }
        }
        fixture.Storage.Dispose();
        Assert.InRange(fixture.Root.ReservedBytes, 1L, 16384L);
        Assert.All(results, result => Assert.Equal(QpdfExitCode.Success, result.ExitCode));
        results.Clear(); caller.Dispose();
        Assert.Equal(0L, fixture.Root.ReservedBytes);
    }

    [Fact]
    public async Task TypedJsonKeepsItsReturnedTextAndRefundsItsUnreturnedUtf8PublicationCopy()
    {
        using Fixture fixture = new(16777216L);
        using IMemoryScope caller = fixture.Root.CreateScope();
        Host host = new(fixture.Storage, caller, "none", null);
        QpdfJsonResult? result = await Qpdf.GetDocumentJsonAsync(host, Input, Output,
            new QpdfJsonOptions(2, [], "inline", null, [], false), CancellationToken.None);
        string? json = result.Json;
        long textBytes = GovernedText.StringBytes(json.Length);
        Assert.InRange(result.Counters.LiveMemoryBytes - textBytes, 1L, 1024L);
        Assert.Equal(0L, fixture.Storage.OpenLeaseCount);
        fixture.Storage.Dispose();
        Assert.InRange(fixture.Root.ReservedBytes - textBytes, 1L, 1024L);
        using (JsonDocument parsed = JsonDocument.Parse(json)) { Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32()); }
        result = null; json = null; caller.Dispose();
        Assert.Equal(0L, fixture.Root.ReservedBytes);
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("close", false)]
    [InlineData("close", true)]
    public async Task PublicationBorrowStaysFundedUntilPendingIoAndCleanupSettle(string phase, bool oom)
    {
        using Fixture fixture = new(16777216L);
        using IMemoryScope caller = fixture.Root.CreateScope();
        using CancellationTokenSource cancellation = new();
        OutOfMemoryException? injected = oom ? new("injected") : null;
        Host host = new(fixture.Storage, caller, phase, injected);
        Task<QpdfExecutionResult> running = Run(host, "argv", "inline", cancellation.Token).AsTask();
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        long admitted = fixture.Root.ReservedWithin(caller);
        Assert.True(admitted >= host.BorrowedCapacity + 32L);
        if (!oom) { cancellation.Cancel(); }
        Assert.False(running.IsCompleted);
        Assert.Equal(admitted, fixture.Root.ReservedWithin(caller));
        host.Resume.TrySetResult();
        if (oom) { Assert.Same(injected, await Record.ExceptionAsync(async () => await running)); }
        else
        {
            QpdfExecutionResult result = await running;
            Assert.Equal(phase == "write" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Completed, result.Status);
        }
        Assert.Equal(0L, fixture.Storage.OpenLeaseCount);
        fixture.Storage.Dispose();
        Assert.InRange(fixture.Root.ReservedBytes, 0L, 1024L);
        caller.Dispose(); Assert.Equal(0L, fixture.Root.ReservedBytes);
    }

    [Fact]
    public async Task LaterSidecarFailureRefundsPreparationAndPreservesTheCommittedMainFile()
    {
        using Fixture fixture = new(16777216L);
        using IMemoryScope caller = fixture.Root.CreateScope();
        OutOfMemoryException injected = new("injected");
        Host host = new(fixture.Storage, caller, "sidecar", injected);
        Assert.Same(injected, await Record.ExceptionAsync(async () => await Run(host, "argv", "file", CancellationToken.None)));
        Assert.Equal(0L, fixture.Storage.OpenLeaseCount);
        Assert.InRange(fixture.Root.ReservedWithin(caller), 0L, 1024L);
        using (IMemoryScope exported = fixture.Root.CreateScope())
        {
            Assert.True(fixture.Storage.TryGetOutput(Output, exported, out byte[] main));
            using JsonDocument parsed = JsonDocument.Parse(main);
            Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
            Assert.False(fixture.Storage.TryGetOutput(QpdfPath.From("side-4"), exported, out _));
        }
        caller.Dispose(); fixture.Storage.Dispose();
        Assert.Equal(0L, fixture.Root.ReservedBytes);
    }

    [Fact]
    public async Task SharedRefusalPreservesThePriorPublicationAndAllowsTheNextCommand()
    {
        using Fixture fixture = new(4194304L);
        await using (IQpdfOutputTransaction prior = await fixture.Storage.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        using IMemoryScope caller = fixture.Root.CreateScope();
        Host host = new(fixture.Storage, caller, "none", null);
        QpdfExecutionResult refused = await Run(host, "argv", "inline", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, refused.Status);
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        using (IMemoryScope exported = fixture.Root.CreateScope())
        {
            Assert.True(fixture.Storage.TryGetOutput(Output, exported, out byte[] bytes));
            Assert.Equal("prior"u8.ToArray(), bytes);
        }
        QpdfExecutionResult next = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, next.Status);
        Assert.Equal(0L, fixture.Storage.OpenLeaseCount);
        fixture.Storage.Dispose();
        Assert.InRange(fixture.Root.ReservedBytes, 1L, 4096L);
        caller.Dispose(); Assert.Equal(0L, fixture.Root.ReservedBytes);
    }
}
