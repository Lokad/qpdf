using System.Text.Json;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Tests.Writing;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class RunLengthRecoveryTests
{
    public static IEnumerable<object[]> LiteralCases()
    {
        foreach (int declared in new[] { 1, 2, 3, 16, 128 })
        foreach (int available in new[] { 0, 1, Math.Max(0, declared - 1), declared }.Distinct())
        foreach (bool prefix in new[] { false, true }) { yield return [declared, available, prefix]; }
    }

    [Theory]
    [MemberData(nameof(LiteralCases))]
    public void EmitsAvailableLiteralsAndChargesRecoveredBytes(int declared, int available, bool prefix)
    {
        byte[] input = [.. prefix ? new byte[] { 0, 80 } : [], (byte)(declared - 1), .. Enumerable.Repeat((byte)65, available)];
        byte[] expected = [.. prefix ? new byte[] { 80 } : [], .. Enumerable.Repeat((byte)65, available)];
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = Math.Max(1, expected.Length) };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        using (FilterPlan plan = FilterChain.Resolve(new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("RunLengthDecode") }), null, governor, CancellationToken.None))
        {
            Assert.Equal(expected, FilterChain.Decode(input, plan, limits, governor, memory, CancellationToken.None));
            Assert.Equal(expected.Length, governor.Snapshot().TotalDecodedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        if (expected.Length > 1)
        {
            QpdfLimits tight = limits with { MaxDecodedBytes = expected.Length - 1 };
            ResourceGovernor refused = new(tight, CancellationToken.None);
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => RunLengthFilter.Decode(input, tight, refused, CancellationToken.None));
            Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), error.LimitName);
            Assert.Equal(0L, refused.Snapshot().LiveMemoryBytes);
        }
    }

    [Theory]
    [InlineData("0241>", 3L)]
    [InlineData("00010241>", 6L)]
    public void ChainsChargeIntermediateAndRecoveredBytes(string hex, long decoded)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = decoded };
        foreach (long maximum in new[] { decoded, decoded - 1 })
        {
            QpdfLimits current = limits with { MaxDecodedBytes = maximum };
            ResourceGovernor governor = new(current, CancellationToken.None);
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            using (FilterPlan plan = FilterChain.Resolve(new(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("ASCIIHexDecode"), PdfName.FromAscii("RunLengthDecode")])
            }), null, governor, CancellationToken.None))
            {
                byte[] input = System.Text.Encoding.ASCII.GetBytes(hex);
                if (maximum == decoded)
                {
                    Assert.Equal(hex == "0241>" ? new byte[] { 65 } : new byte[] { 1, 65 }, FilterChain.Decode(input, plan, current, governor, memory, CancellationToken.None));
                    Assert.Equal(decoded, governor.Snapshot().TotalDecodedBytes);
                }
                else { Assert.Throws<QpdfLimitException>(() => FilterChain.Decode(input, plan, current, governor, memory, CancellationToken.None)); }
            }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Fact]
    public void EndMarkerIsRecognizedOnlyBetweenRecords()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(new byte[] { 65, 128 }, RunLengthFilter.Decode([2, 65, 128], QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Empty(RunLengthFilter.Decode([128, 127, 65], QpdfLimits.Default, governor, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionRefusalRefundsScratchAndPreservesPriorStorage(bool shared)
    {
        RecordingMemoryBudget root = new(1L << 20) { RefuseOnGrowth = shared ? 2 : 0 };
        using IMemoryScope caller = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 1L << 20 : 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        using (ParserMemoryScope pressure = ParserMemoryScope.Create(governor))
        {
            pressure.Reserve(600L);
            long before = governor.Snapshot().LiveMemoryBytes;
            Exception? error = Record.Exception(() => RunLengthFilter.Decode([127, .. Enumerable.Repeat((byte)65, 128), 2, 66], limits, governor, CancellationToken.None));
            if (shared) { Assert.IsType<QpdfMemoryAdmissionException>(error); }
            else { Assert.IsType<QpdfLimitException>(error); }
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        governor.CompletePrivateStorage();
        Assert.Equal(0L, root.ReservedBytes);
    }

    private static byte[] Fixture(byte[] encoded, bool chained)
    {
        string filter = chained ? "[/ASCIIHexDecode /RunLengthDecode]" : "/RunLengthDecode";
        byte[] data = chained ? System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(encoded) + ">") : encoded;
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 6 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, "<< /Length " + data.Length + " /Filter " + filter + " >>", data),
            (5, "<< /Type /Filespec /F (payload.bin) /EF << /F 4 0 R >> >>", null),
            (6, "<< /Names [(K) 5 0 R] >>", null)], 1);
    }

    public static IEnumerable<object[]> PublicCases()
    {
        foreach ((byte[] encoded, byte[] expected) in new (byte[], byte[])[]
        { ([2, 65], [65]), ([0, 65, 2, 66], [65, 66]), ([3, 113, 32, 81, 10, 4, 65, 66], [113, 32, 81, 10, 65, 66]), ([0], []), ([254], []), ([1, 65, 66], [65, 66]), ([128, 2, 65], []) })
        foreach (bool chained in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job", "argv-fileless", "job-fileless" })
        { yield return [encoded, expected, chained, route]; }
    }

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, string route, CancellationToken token)
    {
        bool fileless = route.EndsWith("fileless", StringComparison.Ordinal);
        if (route.StartsWith("argv", StringComparison.Ordinal))
        {
            List<string> argv = ["input", "--show-object=4", "--filtered-stream-data"];
            if (!fileless) { argv.Add("output"); }
            return Qpdf.ExecuteAsync(host, new(argv), token);
        }
        Dictionary<string, object> job = new() { ["inputFile"] = "input", ["showObject"] = "4", ["filteredStreamData"] = true };
        if (!fileless) { job["outputFile"] = "output"; }
        return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), token);
    }

    [Theory]
    [MemberData(nameof(PublicCases))]
    public async Task PublicRoutesReturnNativeRecoveryBytes(byte[] encoded, byte[] expected, bool chained, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(encoded, chained), QpdfInputOwnership.Transfer);
        ReadOnlyMemory<byte> payload;
        if (route == "typed")
        {
            QpdfAttachmentExtraction result = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "K", QpdfPath.From("output"), CancellationToken.None);
            Assert.Empty(result.Warnings);
            Assert.Equal(expected.Length, result.ByteCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
            payload = bytes;
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(expected.Length, result.Counters.TotalOutputBytes);
            if (route.EndsWith("fileless", StringComparison.Ordinal)) { payload = Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes); }
            else { Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); payload = bytes; }
        }
        Assert.Equal(0, host.OpenLeaseCount);
        host.Dispose();
        Assert.Equal(expected, payload.ToArray());
    }

    [Theory]
    [InlineData("typed", "write")]
    [InlineData("argv", "commit")]
    [InlineData("job", "write")]
    [InlineData("typed", "cancel")]
    [InlineData("argv", "cancel")]
    public async Task RecoveredOutputFailuresPreservePriorTarget(string route, string fault)
    {
        using InMemoryQpdfHost inner = new();
        inner.AddInput(QpdfPath.From("input"), Fixture([0, 65, 2, 66], true), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await inner.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource cancellation = new();
        SelectiveAttachmentExtractionTests.FaultHost host = new(inner, fault, cancellation);
        if (route == "typed")
        {
            Exception? error = await Record.ExceptionAsync(() => Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "K", QpdfPath.From("output"), cancellation.Token).AsTask());
            if (fault == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(error); }
            else { Assert.IsAssignableFrom<QpdfException>(error); }
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, cancellation.Token);
            Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        }
        Assert.Equal(1, host.Created);
        Assert.True(inner.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0, inner.OpenLeaseCount);
    }
}
