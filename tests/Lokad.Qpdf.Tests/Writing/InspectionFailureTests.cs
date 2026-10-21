using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class InspectionFailureTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    private static byte[] Fixture(string mode) => mode switch {
        "attachments" => AttachmentMetadataTests.Fixture(new string('N', 32768), "/ModDate (D:20250607080910Z) /CheckSum <0102>"),
        "pages" => OutlineInspectionTests.Fixture("<< /Title (" + new string('T', 32768) + ") /First 6 0 R /Dest [3 0 R /Fit] >>", "<< /Title (Child) /Dest [3 0 R /Fit] >>"),
        "acroform" => AcroFormJsonTests.Fixture("/Parent 6 0 R", "/T (" + new string('N', 32768) + ")", "/FT /Ch /TU (label) /Opt [(one) [(export) (two)]]"),
        "contents" => PageJsonStorageTests.Fixture("6 0 R", "[" + string.Join(' ', Enumerable.Repeat("4 0 R", 256)) + "]", "/Resources << >>"),
        "images" => PageJsonStorageTests.Fixture("[]", "[]", "/Resources << /XObject << " + string.Join(' ', Enumerable.Range(0, 128).Reverse().Select(i => "/Image" + i + " 4 0 R")) + " >> >>"),
        _ => JsonLabelCursorTests.Fixture(128, string.Join(" ", Enumerable.Range(0, 128).Select(i => i + " " + i))) };

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string phase, Exception? failure) : IQpdfHost, IQpdfMemoryHost
    {
        internal bool Injected;
        public IMemoryBudget MemoryBudget => budget;
        private void Hit(string current) { if (phase == current && failure is not null) { Injected = true; throw failure; } }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Lease(this, await storage.OpenInputAsync(path, token));
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Hit("create"); return new Transaction(this, await storage.CreateOutputAsync(path, token)); }
        private sealed class Lease(Host owner, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            { ReadOnlyMemory<byte> bytes = await inner.ReadAsync(offset, count, token); owner.Hit("read"); return bytes; }
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); owner.Hit("input-close"); }
        }
        private sealed class Transaction(Host owner, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) { owner.Hit("write"); return inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token) { owner.Hit("commit"); return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); owner.Hit("close"); }
        }
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string mode, bool job)
    {
        string key = mode is "labels" or "contents" or "images" ? "pages" : mode;
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", json = "2", jsonKey = new[] { key } }), CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--json=2", "--json-key=" + key, "--", "output"]), CancellationToken.None);
    }

    public static IEnumerable<object[]> Faults()
    {
        foreach (string mode in new[] { "attachments", "pages", "labels", "acroform", "contents", "images" })
        foreach (string phase in new[] { "read", "input-close", "create", "write", "commit", "close" })
        foreach (string kind in new[] { "host", "cancel", "oom" }) { yield return [mode, phase, kind]; }
    }

    private static async Task Seed(InMemoryQpdfHost storage)
    {
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task FailuresRefundScratchAndRespectCommitBoundary(string mode, string phase, string kind)
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(Input, Fixture(mode), QpdfInputOwnership.Transfer); await Seed(storage);
        RecordingMemoryBudget root = new(16777216L); using IMemoryScope caller = root.CreateScope();
        Exception failure = kind == "oom" ? new OutOfMemoryException("injected") : kind == "cancel"
            ? new OperationCanceledException(new CancellationToken(true)) : new QpdfHostException(Input, QpdfHostFailureReason.ReadFailed);
        Host host = new(storage, caller, phase, failure);
        if (kind == "oom") { Assert.Same(failure, await Record.ExceptionAsync(async () => await Run(host, mode, false))); }
        else
        {
            QpdfExecutionResult result = await Run(host, mode, false);
            Assert.Equal(kind == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        }
        Assert.True(host.Injected); Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(Output, out byte[] output));
        if (phase == "close") { using JsonDocument json = JsonDocument.Parse(output); Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32()); }
        else { Assert.Equal("prior"u8.ToArray(), output); }
        Assert.InRange(root.ReservedBytes, 0L, 2048L); caller.Dispose(); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("attachments", false)]
    [InlineData("attachments", true)]
    [InlineData("pages", false)]
    [InlineData("pages", true)]
    [InlineData("labels", false)]
    [InlineData("labels", true)]
    [InlineData("acroform", false)]
    [InlineData("acroform", true)]
    [InlineData("contents", false)]
    [InlineData("contents", true)]
    [InlineData("images", false)]
    [InlineData("images", true)]
    public async Task EveryObservedSharedAdmissionRefusalPreservesOutput(string mode, bool job)
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(Input, Fixture(mode), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget baseline = new(16777216L);
        using (IMemoryScope caller = baseline.CreateScope())
        { Assert.Equal(QpdfExitCode.Success, (await Run(new Host(storage, caller, "none", null), mode, job)).ExitCode); }
        for (int fail = 1; fail <= baseline.GrowthCalls; fail++)
        {
            await Seed(storage); RecordingMemoryBudget root = new(16777216L) { RefuseOnGrowth = fail };
            using IMemoryScope caller = root.CreateScope(); QpdfExecutionResult result = await Run(new Host(storage, caller, "none", null), mode, job);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.True(storage.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.InRange(root.ReservedBytes, 0L, 2048L);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedListingCloseFailureDoesNotRetainUnreturnedAliases(bool cancel)
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(Input, AttachmentMetadataTests.Fixture(new string('N', 4096), ""), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget root = new(4194304L); using IMemoryScope caller = root.CreateScope();
        Exception failure = cancel ? new OperationCanceledException(new CancellationToken(true)) : new QpdfHostException(Input, QpdfHostFailureReason.ReadFailed);
        Host host = new(storage, caller, "input-close", failure);
        Assert.Same(failure, await Record.ExceptionAsync(async () => await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None)));
        Assert.True(host.Injected); Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, root.ReservedBytes);
    }
}
