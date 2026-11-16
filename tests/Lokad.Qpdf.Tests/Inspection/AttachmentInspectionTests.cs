using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class AttachmentInspectionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");
    private static readonly byte[] First = "version-A"u8.ToArray(), Second = "version-B-longer"u8.ToArray(), Prior = [17, 23, 41];

    private static byte[] Fixture(byte[] payload, bool filtered)
    {
        byte[] encoded = filtered ? Encoding.ASCII.GetBytes(Convert.ToHexString(payload) + ">") : payload;
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(K) 4 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Type /Filespec /F (payload.bin) /EF << /F 5 0 R >> >>", null),
            (5, "<< /Type /EmbeddedFile /Length " + encoded.Length + (filtered ? " /Filter /ASCIIHexDecode" : "")
                + " /Params << /Size " + payload.Length + " >> >>", encoded)], 1);
    }

    private static async Task Seed(InMemoryQpdfHost storage, byte[] input)
    {
        storage.AddInput(Input, input, QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction seed = await storage.CreateOutputAsync(Output, CancellationToken.None);
        await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
    }

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, string route, bool target, string key, bool reports,
        string? conflict, QpdfExecutionOptions options, CancellationToken token)
    {
        List<string> argv = ["input", "--show-attachment=" + key];
        Dictionary<string, object> job = new() { ["inputFile"] = "input", ["showAttachment"] = key };
        if (reports) { argv.AddRange(["--list-attachments", "--show-npages"]); job.Add("listAttachments", ""); job.Add("showNpages", ""); }
        if (conflict is not null)
        {
            argv.Add("--password-file=missing-password"); job.Add("passwordFile", "missing-password");
            switch (conflict)
            {
                case "json": argv.Add("--json=2"); job.Add("json", "2"); break;
                case "json-input": argv.Add("--json-input"); job.Add("jsonInput", ""); break;
                case "update": argv.Add("--update-from-json=missing-patch"); job.Add("updateFromJson", "missing-patch"); break;
                default: throw new ArgumentException("Unknown conflict.", nameof(conflict));
            }
        }
        if (target) { argv.Add("output"); job.Add("outputFile", "output"); }
        if (route == "mixed") { argv.Insert(0, "--job-json-file=settings"); }
        return route == "job" ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), options, token)
            : Qpdf.ExecuteAsync(host, new(argv), options, token);
    }

    public static IEnumerable<object[]> ConflictCases()
    {
        foreach (string conflict in new[] { "json", "json-input", "update" })
        foreach (string route in new[] { "argv", "job", "mixed" })
        foreach (bool target in new[] { false, true }) { yield return [conflict, route, target]; }
    }

    [Theory]
    [MemberData(nameof(ConflictCases))]
    public async Task JsonConflictsRejectBeforePasswordPdfOrOutputAccess(string conflict, string route, bool target)
    {
        using InMemoryQpdfHost storage = new(); await Seed(storage, Fixture(First, false));
        using RecordingMemoryBudget budget = new(64L << 20);
        using (IMemoryScope caller = budget.CreateScope())
        {
            Host host = new(storage, caller, false, "", null);
            QpdfExecutionResult result = await Execute(host, route, target, "K", false, conflict, new(), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
            Assert.Equal(route == "mixed" ? new[] { "settings" } : [], host.Opened);
            Assert.Equal(0, host.Created); Assert.False(host.Committed); Assert.Null(result.OutputBytes);
            Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(Prior, bytes);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    public static IEnumerable<object[]> SnapshotCases()
    {
        foreach (string route in new[] { "argv", "job", "mixed" })
        foreach (bool target in new[] { false, true })
        foreach (bool replace in new[] { false, true }) { yield return [route, target, replace]; }
    }

    [Theory]
    [MemberData(nameof(SnapshotCases))]
    public async Task ListingCountAndPayloadUseOneInputSnapshot(string route, bool target, bool replace)
    {
        using InMemoryQpdfHost storage = new(); await Seed(storage, Fixture(First, false));
        using RecordingMemoryBudget budget = new(64L << 20);
        using (IMemoryScope caller = budget.CreateScope())
        {
            Host host = new(storage, caller, replace, "", null);
            QpdfExecutionResult result = await Execute(host, route, target, "K", true, null,
                new(QpdfLimits.Default with { MaxOpenHandles = 1 }), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(1, host.PdfOpens); Assert.True(host.ReadBytes > 0);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message == "input: 1");
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.AttachmentListing && d.Message.Contains("size: 9", StringComparison.OrdinalIgnoreCase));
            if (target)
            {
                Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(First, bytes);
                Assert.Null(result.OutputBytes); Assert.Equal(1, host.Created); Assert.True(host.Committed);
            }
            else { Assert.Equal(First, Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes).ToArray()); Assert.Equal(0, host.Created); }
            Assert.Equal(First.Length, result.Counters.TotalOutputBytes);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    [Theory]
    [InlineData("protected", false)]
    [InlineData("protected", true)]
    [InlineData("repair", false)]
    [InlineData("repair", true)]
    public async Task CombinedReportsPreserveAuthenticationAndRepair(string kind, bool target)
    {
        byte[] input = kind == "protected" ? await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-attachment.pdf")) : Fixture(First, false);
        if (kind == "repair")
        {
            int at = input.AsSpan().LastIndexOf("startxref\n"u8) + "startxref\n"u8.Length;
            Assert.True(at >= "startxref\n"u8.Length);
            while (input[at] is >= (byte)'0' and <= (byte)'9') { input[at++] = (byte)'9'; }
        }
        using InMemoryQpdfHost storage = new(); await Seed(storage, input);
        using RecordingMemoryBudget budget = new(64L << 20);
        using (IMemoryScope caller = budget.CreateScope())
        {
            Host host = new(storage, caller, true, "", null);
            QpdfExecutionResult result = await Execute(host, "job", target, kind == "protected" ? "private.bin" : "K", true, null, new(), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(kind == "repair" ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            byte[] expected = kind == "protected" ? "Private attachment payload.\n"u8.ToArray() : First;
            if (target) { Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(expected, bytes); }
            else { Assert.Equal(expected, Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes).ToArray()); }
            Assert.Equal(1, host.PdfOpens); Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    [Fact]
    public async Task FailedLinearizationReportCannotPublishAnOtherwiseUsableAttachment()
    {
        using InMemoryQpdfHost storage = new(); await Seed(storage, Fixture(First, false));
        QpdfExecutionResult prepared = await Qpdf.ExecuteAsync(storage, new(["input", "--linearize", "linearized"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, prepared.ExitCode);
        Assert.True(storage.TryGetOutput(QpdfPath.From("linearized"), out byte[] bytes));
        int at = bytes.AsSpan().IndexOf("/N 1"u8); Assert.True(at >= 0); bytes[at + 3] = (byte)'2';
        storage.AddInput(Input, bytes, QpdfInputOwnership.Transfer);
        using RecordingMemoryBudget budget = new(64L << 20);
        using (IMemoryScope caller = budget.CreateScope())
        {
            Host host = new(storage, caller, false, "", null);
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--check-linearization", "--show-attachment=K", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed);
            Assert.Equal(0, host.Created); Assert.False(host.Committed);
            Assert.True(storage.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    public static IEnumerable<object[]> PendingCases()
    {
        foreach (string encoding in new[] { "raw", "filtered", "protected" })
        foreach (string outcome in new[] { "success", "cancel", "fault" }) { yield return [encoding, outcome]; }
    }

    [Theory]
    [MemberData(nameof(PendingCases))]
    public async Task PayloadBackingSurvivesSourceCloseAndPendingPublication(string encoding, string outcome)
    {
        using InMemoryQpdfHost storage = new();
        byte[] input = encoding == "protected" ? await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-attachment.pdf"))
            : Fixture(First, encoding == "filtered");
        await Seed(storage, input);
        byte[] expected = encoding == "protected" ? "Private attachment payload.\n"u8.ToArray() : First;
        using RecordingMemoryBudget budget = new(64L << 20); using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = budget.CreateScope())
        {
            Host host = new(storage, caller, false, "pending-" + outcome, cancellation);
            string key = encoding == "protected" ? "private.bin" : "K";
            Task<QpdfExecutionResult> running = Qpdf.ExecuteAsync(host, new(["input", "--show-attachment=" + key, "output"]), cancellation.Token).AsTask();
            try
            {
                await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(host.SourceClosed); Assert.Equal(1, host.PdfOpens); Assert.Equal(1L, storage.OpenLeaseCount);
                Assert.True(budget.ReservedBytes > expected.Length); Assert.Equal(expected, host.Borrow.ToArray());
                budget.RefuseGrowth = true; host.Resume.TrySetResult();
                QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
                Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(outcome == "success" ? expected : Prior, bytes);
                Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(host.Borrow.IsEmpty);
            }
            finally { host.Resume.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    [Theory]
    [InlineData("input-close")]
    [InlineData("write")]
    [InlineData("commit")]
    public async Task PublicationFaultsPreservePriorTargetAndCloseTheSingleSource(string phase)
    {
        using InMemoryQpdfHost storage = new(); await Seed(storage, Fixture(First, false));
        using RecordingMemoryBudget budget = new(64L << 20);
        using (IMemoryScope caller = budget.CreateScope())
        {
            Host host = new(storage, caller, false, phase, null);
            QpdfExecutionResult result = await Execute(host, "argv", true, "K", true, null, new(), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.HostOperationFailed);
            Assert.Equal(1, host.PdfOpens); Assert.Equal(phase == "input-close" ? 0 : 1, host.Created);
            Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(Prior, bytes);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, bool replace, string phase, CancellationTokenSource? cancellation)
        : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        private string Phase => phase;
        private CancellationTokenSource? Cancellation => cancellation;
        internal List<string> Opened { get; } = [];
        internal int PdfOpens, Created;
        internal long ReadBytes;
        internal bool SourceClosed, Committed;
        internal ReadOnlyMemory<byte> Borrow;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            Opened.Add(path.Value);
            if (path == Input)
            {
                PdfOpens++;
                if (replace && PdfOpens == 2)
                { Assert.Equal(0L, storage.OpenLeaseCount); storage.AddInput(Input, Fixture(Second, false), QpdfInputOwnership.Transfer); }
            }
            return new Lease(this, await storage.OpenInputAsync(path, token));
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; Assert.True(SourceClosed); return new Transaction(this, await storage.CreateOutputAsync(path, token)); }
        private sealed class Lease(Host owner, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> target, CancellationToken token)
            { int read = await inner.ReadAsync(offset, target, token); if (Path == Input) { owner.ReadBytes += read; } return read; }
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync(); if (Path != Input) { return; } owner.SourceClosed = true;
                if (owner.Phase == "input-close") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
            }
        }
        private sealed class Transaction(Host owner, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                owner.Borrow = bytes;
                try
                {
                    if (owner.Phase.StartsWith("pending-", StringComparison.Ordinal))
                    {
                        owner.Entered.TrySetResult(); await owner.Resume.Task;
                        if (owner.Phase == "pending-cancel") { owner.Cancellation?.Cancel(); token.ThrowIfCancellationRequested(); }
                    }
                    if (owner.Phase is "write" or "pending-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                    await inner.WriteAsync(bytes, token);
                }
                finally { owner.Borrow = default; }
            }
            public async ValueTask CommitAsync(CancellationToken token)
            {
                if (owner.Phase == "commit") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                await inner.CommitAsync(token); owner.Committed = true;
            }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
