using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentTreeResourceTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    public static IEnumerable<object[]> AdmissionCases()
    {
        foreach (string effect in new[] { "pass", "local", "shared", "cancel", "oom" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [effect, route]; }
    }

    [Theory]
    [MemberData(nameof(AdmissionCases))]
    public async Task IndirectKeyAdmissionPreservesRefusalCancellationAndCleanup(string effect, string route)
    {
        const int length = 131072;
        byte[] pdf = AttachmentTreeReferenceTests.Fixture("all", "(" + new string('x', length) + ")", 0);
        RecordingMemoryBudget root = new(16777216L);
        Assert.True(root.TryReserve(4096L, out IMemoryReservation? sibling));
        using (sibling)
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        using (InMemoryQpdfHost storage = new())
        {
            storage.AddInput(Input, pdf, QpdfInputOwnership.Transfer);
            Budget budget = new(owner, cancellation, effect, pdf.LongLength * 2L);
            Host host = new(storage, budget, "none");
            QpdfLimits limits = QpdfLimits.Default with
            {
                MaxMemoryBytes = effect == "local" ? pdf.LongLength * 2L : 8388608L,
                MaxDiagnosticBytes = 1048576L, // Successful listings include the complete large key.
            };
            QpdfExecutionResult? result = null;
            IReadOnlyList<QpdfAttachmentInfo>? listed = null;
            Exception? failure = await Record.ExceptionAsync(async () =>
            {
                if (route == "typed") { listed = await Qpdf.ListAttachmentsAsync(host, Input, limits, cancellation.Token); }
                else if (route == "argv")
                { result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--list-attachments"]), new QpdfExecutionOptions(limits), cancellation.Token); }
                else
                { result = await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","listAttachments":""}""", new QpdfExecutionOptions(limits), cancellation.Token); }
            });
            Assert.True(budget.Armed);
            if (effect is "cancel" or "shared" or "oom") { Assert.True(budget.Triggered); }
            if (effect == "oom") { Assert.Same(budget.Failure, failure); }
            else if (effect == "pass")
            {
                Assert.Null(failure);
                if (route == "typed") { Assert.Equal(length, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<QpdfAttachmentInfo>>(listed)).Key.Length); }
                else { Assert.Equal(QpdfExecutionStatus.Completed, Assert.IsType<QpdfExecutionResult>(result).Status); }
            }
            else if (route == "typed")
            {
                if (effect == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(failure); }
                else if (effect == "shared") { Assert.IsType<QpdfMemoryAdmissionException>(failure); }
                else { Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), Assert.IsType<QpdfLimitException>(failure).LimitName); }
            }
            else
            {
                Assert.Null(failure);
                QpdfExecutionResult completed = Assert.IsType<QpdfExecutionResult>(result);
                Assert.Equal(effect == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, completed.Status);
            }
            Assert.Equal(0L, storage.OpenLeaseCount);
            if (effect != "pass") { Assert.InRange(root.ReservedBytes, 4096L, 65536L); }
            listed = null; result = null; owner.Dispose();
            Assert.Equal(4096L, root.ReservedBytes); Assert.Equal(4096L, sibling.Bytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task ListedMetadataAndExtractedBytesOutliveHostStorage()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope owner = root.CreateScope();
        InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(Input, AttachmentTreeReferenceTests.Fixture("all", "(note.txt)", 0), QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> listed = await Qpdf.ListAttachmentsAsync(host, Input, CancellationToken.None);
        await Qpdf.ExtractAttachmentAsync(host, Input, "note.txt", Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] payload)); Assert.Equal(0L, host.OpenLeaseCount);
        host.Dispose();
        Assert.Equal("note.txt", Assert.Single(listed).Key); Assert.Equal("note.txt", listed[0].FileName);
        Assert.Equal("payload"u8.ToArray(), payload); Assert.True(root.ReservedBytes > 0L);
        owner.Dispose(); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("write", "typed")]
    [InlineData("write", "argv")]
    [InlineData("write", "job")]
    [InlineData("commit", "typed")]
    [InlineData("commit", "argv")]
    [InlineData("commit", "job")]
    public async Task OutputFailureAfterIndirectTraversalPreservesTheTarget(string phase, string route)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Input, AttachmentTreeReferenceTests.Fixture("all", "(note.txt)", 0), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope owner = root.CreateScope();
        using CancellationTokenSource cancellation = new();
        Host host = new(storage, new Budget(owner, cancellation, "pass", long.MaxValue), phase);
        if (route == "typed")
        { await Assert.ThrowsAsync<QpdfHostException>(async () => await Qpdf.ExtractAttachmentAsync(host, Input, "note.txt", Output, CancellationToken.None)); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--show-attachment=note.txt", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showAttachment":"note.txt","outputFile":"output"}""", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.HostOperationFailed);
        }
        Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, storage.OpenLeaseCount); Assert.InRange(root.ReservedBytes, 0L, 65536L);
        owner.Dispose(); Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class Budget(IMemoryBudget inner, CancellationTokenSource cancellation, string effect, long threshold) : IMemoryBudget
    {
        internal bool Armed, Triggered;
        internal readonly OutOfMemoryException Failure = new("injected key admission failure");
        private bool Before(long bytes, long previous)
        {
            if (!Armed || bytes <= previous || bytes <= threshold) { return true; }
            Triggered = true;
            if (effect == "cancel") { cancellation.Cancel(); }
            if (effect == "oom") { throw Failure; }
            return effect != "shared";
        }
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!Before(bytes, 0L) || !inner.TryReserve(bytes, out IMemoryReservation? receipt)) { reservation = null; return false; }
            reservation = new Receipt(this, receipt); return true;
        }
        private sealed class Receipt(Budget owner, IMemoryReservation inner) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes) => owner.Before(bytes, inner.Bytes) && inner.TryResize(bytes);
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class Host(InMemoryQpdfHost storage, Budget budget, string phase) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new InputLease(await storage.OpenInputAsync(path, token), budget);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(await storage.CreateOutputAsync(path, token), phase);
        private sealed class InputLease(IQpdfInput inner, Budget budget) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            { ReadOnlyMemory<byte> bytes = await inner.ReadAsync(offset, count, token); budget.Armed = true; return bytes; }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
        private sealed class Transaction(IQpdfOutputTransaction inner, string phase) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            { if (phase == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); } return inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token)
            { if (phase == "commit") { throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed); } return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
