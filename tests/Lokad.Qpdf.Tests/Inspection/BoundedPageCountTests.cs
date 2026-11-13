using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Reading;
using Lokad.Qpdf.Tests.Writing;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class BoundedPageCountTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");

    private static byte[] Document(int payloadBytes, string shape)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray());
        objects.Add((6, $"<< /Length {payloadBytes} >>", new byte[payloadBytes]));
        if (shape == "metadata") { objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>", null); }
        if (shape == "large-page")
        { objects[2] = (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Audit (" + new string('A', 70000) + ") >>", null); }
        if (shape == "large-xref")
        { for (int number = 7; number < 4007; number++) { objects.Add((number, "null", null)); } }
        if (shape == "indirect")
        {
            objects[1] = (2, "<< /Type /Pages /Kids [3 0 R] /Count 7 0 R >>", null); objects.Add((7, "1", null));
        }
        if (shape == "mismatch") { objects[1] = (2, "<< /Type /Pages /Kids [3 0 R] /Count 9 >>", null); }
        if (shape == "missing-count") { objects[1] = (2, "<< /Type /Pages /Kids [3 0 R] >>", null); }
        if (shape == "bad-kid") { objects[1] = (2, "<< /Type /Pages /Kids [6 0 R] /Count 1 >>", null); }
        if (shape == "cycle") { objects[1] = (2, "<< /Type /Pages /Kids [2 0 R] /Count 1 >>", null); }
        if (shape == "nested-mismatch")
        {
            objects[1] = (2, "<< /Type /Pages /Kids [7 0 R] /Count 1 >>", null);
            objects.Add((7, "<< /Type /Pages /Kids [3 0 R] /Count 9 >>", null));
        }
        byte[] bytes = CheckTests.Assemble(objects, 1);
        if (shape == "incremental")
        {
            string text = Encoding.ASCII.GetString(bytes);
            long previous = long.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
            byte[] extra = "7 0 obj\nnull\nendobj\n"u8.ToArray();
            byte[] tail = Encoding.ASCII.GetBytes($"xref\n7 1\n{bytes.Length:D10} 00000 n \ntrailer\n<< /Size 8 /Root 1 0 R /Prev {previous} >>\nstartxref\n{bytes.Length + extra.Length}\n%%EOF\n");
            return [.. bytes, .. extra, .. tail];
        }
        return shape == "zero-entry" ? TypedSelectionRecoveryTests.Damage(bytes, "zero-entry") : bytes;
    }

    [Theory]
    [InlineData("incremental", "job")]
    [InlineData("large-page", "typed")]
    [InlineData("large-xref", "argv")]
    public async Task UnsupportedCompactProfilesUseCompleteAcquisitionWithoutReopening(string shape, string route)
    {
        byte[] bytes = Document(524288, shape);
        SelectiveInputTests.Host source = new(bytes, int.MaxValue) { ReplaceFutureInput = true };
        if (route == "typed")
        { Assert.Equal(1, (await Qpdf.CountPagesAsync(source, Input, CancellationToken.None)).PageCount); }
        else
        {
            QpdfExecutionResult result = await CountCommand(source, route, QpdfLimits.Default, false);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal("input.pdf: 1", Assert.Single(result.Diagnostics).Message);
        }
        Assert.Contains(source.Reads, read => read.Offset == 0 && read.Count == bytes.Length);
        Assert.Equal(1, source.Opened); Assert.Equal(0, source.Leases);
    }

    [Fact]
    public async Task LargeProtectedCountRetriesTheSameLeaseAndKeepsPasswordAndMemoryFailures()
    {
        using InMemoryQpdfHost setup = new(); setup.AddInput(Input, Document(524288, "metadata"), QpdfInputOwnership.Transfer);
        QpdfPath output = QpdfPath.From("encrypted.pdf");
        await Qpdf.SelectPageAsync(setup, Input, output, new(1), null,
            QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("reader"), QpdfPassword.FromText("owner"), -4, true), CancellationToken.None);
        Assert.True(setup.TryGetOutput(output, out byte[] bytes)); Assert.True(bytes.Length > 131072);
        SelectiveInputTests.Host source = new(bytes, int.MaxValue) { ReplaceFutureInput = true };
        QpdfPageCountResult result = await Qpdf.CountPagesAsync(source, Input, QpdfPassword.FromText("reader"),
            QpdfRecoveryMode.Strict, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.Equal(1, source.Opened); Assert.Equal(0, source.Leases);
        Assert.Contains(source.Reads, read => read.Offset == 0 && read.Count == bytes.Length);
        SelectiveInputTests.Host limited = new(bytes, int.MaxValue);
        await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.CountPagesAsync(limited, Input, QpdfPassword.FromText("reader"),
            QpdfRecoveryMode.Strict, QpdfLimits.Default with { MaxMemoryBytes = 131072 }, CancellationToken.None).AsTask());
        Assert.DoesNotContain(limited.Reads, read => read.Offset == 0 && read.Count == bytes.Length); Assert.Equal(0, limited.Leases);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("structure")]
    [InlineData("memory")]
    [InlineData("invalid-mode")]
    [InlineData("empty-read")]
    public async Task CountPreservesHardLimitsAndRejectsUnreliableProviderReads(string mode)
    {
        byte[] bytes = Document(524288, "normal");
        SelectiveInputTests.Host source = new(bytes, int.MaxValue) { Failure = mode == "empty-read" ? "empty" : "" };
        QpdfLimits limits = mode switch
        {
            "input" => QpdfLimits.Default with { MaxInputBytes = bytes.Length - 1 },
            "structure" => QpdfLimits.Default with { MaxContainerSize = 2 },
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 2048 },
            _ => QpdfLimits.Default,
        };
        if (mode == "invalid-mode")
        { await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.CountPagesAsync(source, Input, null, (QpdfRecoveryMode)99, limits, CancellationToken.None).AsTask()); Assert.Equal(0, source.Opened); }
        else if (mode == "empty-read")
        { await Assert.ThrowsAnyAsync<QpdfException>(() => Qpdf.CountPagesAsync(source, Input, limits, CancellationToken.None).AsTask()); }
        else
        { await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.CountPagesAsync(source, Input, limits, CancellationToken.None).AsTask()); }
        Assert.Equal(0, source.Leases); Assert.Equal(0, source.OutputAttempts);
        if (mode == "memory") { Assert.DoesNotContain(source.Reads, read => read.Offset == 0 && read.Count == bytes.Length); }
    }

    private sealed class BudgetHost(IQpdfHost inner, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => inner.CreateOutputAsync(path, token);
    }

    private static ValueTask<QpdfExecutionResult> CountCommand(IQpdfHost host, string route, QpdfLimits limits, bool strict)
        => route == "argv"
            ? Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(strict ? [Input.Value, "--show-npages", "--suppress-recovery"] : [Input.Value, "--show-npages"]), new QpdfExecutionOptions(limits), CancellationToken.None)
            : Qpdf.ExecuteJobAsync(host, strict ? """{"inputFile":"input.pdf","showNpages":true,"suppressRecovery":true}""" : """{"inputFile":"input.pdf","showNpages":true}""", new QpdfExecutionOptions(limits), CancellationToken.None);

    [Theory]
    [InlineData("typed", 524288, 37, "normal")]
    [InlineData("argv", 9437184, int.MaxValue, "normal")]
    [InlineData("job", 9437184, 37, "indirect")]
    public async Task ValidCountsSkipUnrelatedPayloadUnderSmallerLocalAndSharedAllowances(string route, int bulk, int chunk, string shape)
    {
        byte[] bytes = Document(bulk, shape), hash = SHA256.HashData(bytes);
        SelectiveInputTests.Host source = new(bytes, chunk) { ReplaceFutureInput = true };
        using RecordingMemoryBudget root = new(131072);
        QpdfExecutionCounters counters;
        using (IMemoryScope owner = root.CreateScope())
        {
            BudgetHost host = new(source, owner);
            QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 131072 };
            if (route == "typed")
            {
                QpdfPageCountResult result = await Qpdf.CountPagesAsync(host, Input, limits, CancellationToken.None);
                Assert.Equal(1, result.PageCount); Assert.Empty(result.Warnings); counters = result.Counters;
            }
            else
            {
                QpdfExecutionResult result = await CountCommand(host, route, limits, false);
                Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
                Assert.Equal("input.pdf: 1", Assert.Single(result.Diagnostics).Message); counters = result.Counters;
            }
            Assert.Equal(0, counters.OpenHandles); Assert.Equal(0, source.Leases); Assert.Equal(1, source.Opened);
            Assert.InRange(source.ReadBytes, 1, 262144); Assert.Equal(0, source.OutputAttempts);
            Assert.InRange(counters.PeakMemoryBytes, 1, limits.MaxMemoryBytes);
            Assert.Equal(hash, SHA256.HashData(bytes));
            // The request range log also excludes the interior of the bulk stream.
            long middle = bytes.AsSpan().IndexOf(new byte[4096]) + 131072L;
            Assert.DoesNotContain(source.Reads, read => read.Offset <= middle && read.Offset + read.Count > middle);
        }
        Assert.Equal(0, root.ReservedBytes); Assert.Equal(0, counters.OpenHandles);
    }

    [Theory]
    [InlineData("bad-kid")]
    [InlineData("cycle")]
    public async Task UnvalidatedPageTreesNeverReturnADeclaredCount(string shape)
    {
        byte[] bytes = Document(524288, shape);
        SelectiveInputTests.Host typed = new(bytes, int.MaxValue);
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.CountPagesAsync(typed, Input, CancellationToken.None).AsTask());
        Assert.Equal(0, typed.Leases); Assert.Equal(0, typed.OutputAttempts);
        SelectiveInputTests.Host argv = new(bytes, int.MaxValue);
        QpdfExecutionResult result = await CountCommand(argv, "argv", QpdfLimits.Default, false);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount); Assert.Equal(0, argv.Leases);
    }

    [Theory]
    [InlineData("mismatch", "argv", 9)]
    [InlineData("missing-count", "job", 0)]
    [InlineData("nested-mismatch", "argv", 1)]
    public async Task UncertainDeclarationsRequireCompleteInputAndKeepExistingCommandPolicy(string shape, string route, int declaration)
    {
        byte[] bytes = Document(524288, shape);
        SelectiveInputTests.Host source = new(bytes, int.MaxValue) { ReplaceFutureInput = true };
        QpdfExecutionResult result = await CountCommand(source, route, QpdfLimits.Default, false);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message == $"input.pdf: {declaration}");
        Assert.Contains(source.Reads, read => read.Offset == 0 && read.Count == bytes.Length);
        Assert.Equal(1, source.Opened); Assert.Equal(0, source.Leases);
        SelectiveInputTests.Host limited = new(bytes, int.MaxValue);
        QpdfExecutionResult refusal = await CountCommand(limited, route, QpdfLimits.Default with { MaxMemoryBytes = 131072 }, false);
        Assert.Equal(QpdfExecutionStatus.Failed, refusal.Status);
        Assert.DoesNotContain(refusal.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount);
        Assert.DoesNotContain(limited.Reads, read => read.Offset == 0 && read.Count == bytes.Length);
        QpdfPageCountResult typed = await Qpdf.CountPagesAsync(new SelectiveInputTests.Host(bytes, int.MaxValue), Input, CancellationToken.None);
        Assert.Equal(1, typed.PageCount);
        if (shape != "missing-count") { Assert.Contains(typed.Warnings, d => d.Code == QpdfDiagnosticCode.PageCountMismatch); }
    }

    [Fact]
    public async Task XrefRepairFallbackUsesTheSameLeaseAndTheRequestedPolicy()
    {
        byte[] bytes = Document(524288, "zero-entry");
        SelectiveInputTests.Host strict = new(bytes, 37) { ReplaceFutureInput = true };
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.CountPagesAsync(strict, Input, CancellationToken.None).AsTask());
        Assert.Equal(1, strict.Opened); Assert.Equal(0, strict.Leases);
        SelectiveInputTests.Host repaired = new(bytes, 37) { ReplaceFutureInput = true };
        QpdfPageCountResult result = await Qpdf.CountPagesAsync(repaired, Input, null, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.Contains(result.Warnings, d => d.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.True(repaired.ReadBytes >= bytes.Length); Assert.Equal(1, repaired.Opened); Assert.Equal(0, repaired.Leases);
        SelectiveInputTests.Host command = new(bytes, int.MaxValue);
        QpdfExecutionResult rejected = await CountCommand(command, "argv", QpdfLimits.Default, true);
        Assert.Equal(QpdfExecutionStatus.Failed, rejected.Status);
        Assert.DoesNotContain(rejected.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount);
    }

    [Theory]
    [InlineData("security-r4-rc4-ascii.pdf")]
    [InlineData("security-r6-ascii.pdf")]
    [InlineData("linearized-2page.pdf")]
    [InlineData("hybrid-xstm.pdf")]
    public async Task ProtectedOrStreamedInputsKeepTheCompleteReaderAndAuthentication(string fixture)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        using InMemoryQpdfHost baseline = new(); baseline.AddInput(Input, bytes, QpdfInputOwnership.Transfer);
        QpdfPassword? password = null;
        // Recorded security fixtures use the public user credential from their vectors.
        if (fixture.StartsWith("security-", StringComparison.Ordinal))
        {
            string vectors = fixture.StartsWith("security-r4", StringComparison.Ordinal) ? "security-r4-rc4-vectors.json" : "security-r6-vectors.json";
            using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", vectors)));
            System.Text.Json.JsonElement vector = json.RootElement.EnumerateArray().Single(e => e.GetProperty("file").GetString() == fixture);
            password = QpdfPassword.FromText(Encoding.UTF8.GetString(Convert.FromHexString(vector.GetProperty("userHex").GetString() ?? throw new InvalidOperationException())));
        }
        await using QpdfDocument document = await QpdfDocument.OpenAsync(baseline, Input, password, QpdfLimits.Default, CancellationToken.None);
        SelectiveInputTests.Host source = new(bytes, 37);
        QpdfPageCountResult result = await Qpdf.CountPagesAsync(source, Input, password, QpdfRecoveryMode.Strict, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(document.PageCount, result.PageCount); Assert.Equal(bytes.Length, source.ReadBytes); Assert.Equal(0, source.Leases);
        if (password is not null)
        {
            SelectiveInputTests.Host wrong = new(bytes, int.MaxValue);
            await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.CountPagesAsync(wrong, Input, QpdfPassword.FromText("wrong"), QpdfRecoveryMode.Strict, QpdfLimits.Default, CancellationToken.None).AsTask());
            Assert.Equal(0, wrong.Leases);
        }
    }

    [Fact]
    public async Task MalformedHybridOffsetsRetainStrictRejection()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "xstm-classic.pdf"));
        SelectiveInputTests.Host source = new(bytes, int.MaxValue);
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.CountPagesAsync(source, Input, CancellationToken.None).AsTask());
        Assert.Equal(0, source.Leases);
    }

    [Theory]
    [InlineData("cancel", "read")]
    [InlineData("host-cancel", "read")]
    [InlineData("budget", "read")]
    [InlineData("fault", "input-close")]
    [InlineData("success", "read")]
    public async Task PendingIoAndCleanupRetainTheirActualOwners(string outcome, string boundary)
    {
        using RecordingJobHost host = new(boundary, null, outcome is "fault" or "host-cancel" ? outcome : "success", true);
        host.Inner.AddInput(Input, Document(524288, "normal"), QpdfInputOwnership.Transfer);
        using CancellationTokenSource cancel = new();
        Task<QpdfPageCountResult> pending = Qpdf.CountPagesAsync(host, Input, cancel.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(pending.IsCompleted);
            Assert.True(host.Budget.ReservedBytes > 0);
            if (outcome == "cancel") { cancel.Cancel(); }
            if (outcome == "budget") { host.Budget.RefuseGrowth = true; }
        }
        finally { host.Resume(); }
        QpdfPageCountResult? result = null;
        if (outcome == "success") { result = await pending; Assert.Equal(1, result.PageCount); }
        else if (outcome is "cancel" or "host-cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        else if (outcome == "budget") { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => pending); }
        else { await Assert.ThrowsAsync<QpdfHostException>(() => pending); }
        Assert.True(host.ReadBytes < 262144); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.Empty(host.PendingRead.ToArray()); ExecutionJobFixtures.AssertClosed(host, result);
    }
}
