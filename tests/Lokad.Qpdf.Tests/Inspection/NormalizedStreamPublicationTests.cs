using System.IO.Compression;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class NormalizedStreamPublicationTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");
    private static readonly byte[] Original = "q\rQ  \r\n  "u8.ToArray();
    private static readonly byte[] Normalized = "q\nQ  \n  "u8.ToArray();

    private static (byte[] Pdf, byte[] Payload) Fixture(bool compressed)
    {
        byte[] payload = Original;
        if (compressed)
        {
            using MemoryStream buffer = new();
            using (ZLibStream zlib = new(buffer, CompressionLevel.Optimal, true)) { zlib.Write(payload); }
            payload = buffer.ToArray();
        }
        string header = "<< /Length " + payload.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>";
        return (PdfTestDocument.Assemble(PdfTestDocument.SinglePage(header, payload), 1), payload);
    }

    private static ValueTask<QpdfExecutionResult> ExecuteAsync(IQpdfHost host, string route, bool normalize, bool raw,
        QpdfPath? target, QpdfLimits limits, CancellationToken token)
    {
        string setting = normalize ? "y" : "n";
        List<string> argv = ["input", "--show-object=4", raw ? "--raw-stream-data" : "--filtered-stream-data", "--normalize-content=" + setting];
        Dictionary<string, object> job = new()
        {
            ["inputFile"] = "input", ["showObject"] = "4", ["normalizeContent"] = setting,
            [raw ? "rawStreamData" : "filteredStreamData"] = ""
        };
        if (target.HasValue) { argv.Add(target.Value.Value); job["outputFile"] = target.Value.Value; }
        if (route == "argv") { return Qpdf.ExecuteAsync(host, new(argv), new(limits), token); }
        string json = JsonSerializer.Serialize(job);
        if (route == "job") { return Qpdf.ExecuteJobAsync(host, json, new(limits), token); }
        Assert.Equal("mixed", route);
        Assert.IsType<InMemoryQpdfHost>(host).AddInput(QpdfPath.From("settings"), System.Text.Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Transfer);
        return Qpdf.ExecuteAsync(host, new(["--job-json-file=settings"]), new(limits), token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NormalizationIsIndependentOfOutputTransport(bool compressed, bool normalize)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(compressed).Pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult returned = await ExecuteAsync(host, "argv", normalize, false, null, QpdfLimits.Default, CancellationToken.None);
        QpdfExecutionResult published = await ExecuteAsync(host, "argv", normalize, false, Output, QpdfLimits.Default, CancellationToken.None);
        byte[] expected = normalize ? Normalized : Original;
        Assert.Equal(QpdfExitCode.Success, returned.ExitCode); Assert.Equal(QpdfExitCode.Success, published.ExitCode);
        Assert.Empty(returned.Diagnostics); Assert.Empty(published.Diagnostics);
        Assert.Equal(expected, Assert.IsType<ReadOnlyMemory<byte>>(returned.OutputBytes).ToArray());
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(expected, bytes); Assert.Null(published.OutputBytes);
        Assert.Equal(expected.Length, returned.Counters.TotalOutputBytes); Assert.Equal(expected.Length, published.Counters.TotalOutputBytes);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, published.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task JobAndIncludedJobForwardNormalizationToTheTarget(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(true).Pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await ExecuteAsync(host, route, true, false, Output, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Null(result.OutputBytes);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(Normalized, bytes);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Fact]
    public async Task RawPublicationKeepsEncodedBytesWithNormalizationEnabled()
    {
        var fixture = Fixture(true);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), fixture.Pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await ExecuteAsync(host, "argv", true, true, Output, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(fixture.Payload, bytes);
        Assert.Equal(0L, result.Counters.TotalDecodedBytes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NormalizationOutputLimitPreservesThePriorTarget()
    {
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Fixture(true).Pdf, Output);
        Assert.True(host.TryGetOutput(Output, out byte[] prior));
        QpdfExecutionResult result = await ExecuteAsync(host, "argv", true, false, Output,
            QpdfLimits.Default with { MaxOutputBytes = Normalized.Length - 1L }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded && d.Message.Contains(nameof(QpdfLimits.MaxOutputBytes), StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(prior, bytes);
        Assert.Null(result.OutputBytes); Assert.Equal(0L, result.Counters.TotalOutputBytes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public Task NormalizedTargetPreparationOwnsItsAdmissionAndPublication()
        => PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(Fixture(true).Pdf, Output),
            (host, token) => ExecuteAsync(host, "argv", true, false, Output, QpdfLimits.Default, token),
            Output, 2048, ["write", "commit", "cancel"]);

    [Theory]
    [InlineData("--qdf")]
    [InlineData("--stream-data=uncompress")]
    [InlineData("--object-streams=generate")]
    public async Task WriterSettingsRemainInvalidForBinaryTargets(string writer)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--show-object=4", "--filtered-stream-data", "--normalize-content=y", writer, "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.Equal(0L, result.Counters.HostOperations); Assert.False(host.TryGetOutput(Output, out _));
    }
}
