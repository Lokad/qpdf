using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class EmptyFilenameTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    [Theory]
    [InlineData("empty-input")]
    [InlineData("empty-output")]
    [InlineData("both")]
    [InlineData("real-input")]
    [InlineData("json")]
    [InlineData("binary")]
    public async Task EmptyJobFilenamesSelectNoFile(string kind)
    {
        using InMemoryQpdfHost host = Prepare();
        bool empty = kind is "empty-input" or "empty-output" or "both";
        Dictionary<string, object> job = new();
        if (empty) { job["empty"] = ""; }
        if (kind is "empty-input" or "both") { job["inputFile"] = ""; }
        else if (!empty) { job["inputFile"] = "input"; }
        if (kind != "empty-input") { job["outputFile"] = ""; }
        if (kind == "json") { job["json"] = "2"; job["jsonKey"] = new[] { "pages" }; }
        else if (kind == "binary") { job["showObject"] = "4"; job["rawStreamData"] = ""; }
        else { job["showNpages"] = ""; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
        if (kind == "json")
        {
            Assert.NotNull(result.OutputText); using JsonDocument parsed = JsonDocument.Parse(result.OutputText);
            Assert.Single(parsed.RootElement.GetProperty("pages").EnumerateArray());
        }
        else if (kind == "binary")
        { Assert.True(result.OutputBytes.HasValue); Assert.Equal("q Q\n"u8.ToArray(), result.OutputBytes.Value.ToArray()); }
        else { AssertCount(result, empty ? 0 : 1); }
        if (empty) { Assert.Equal(0L, result.Counters.HostOperations); }
        Assert.False(host.TryGetOutput(Output, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EmptyArgvSlotsKeepTheirPosition(int position)
    {
        string[] argv = position == 0 ? ["", "--empty", "--show-npages"]
            : position == 1 ? ["--empty", "", "--show-npages"] : ["--empty", "--show-npages", ""];
        using InMemoryQpdfHost host = Prepare();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        AssertCount(result, 0); Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("--empty\n\n--show-npages\n")]
    [InlineData("\n--empty\n--show-npages\n")]
    [InlineData("--empty\n--show-npages\n\n")]
    [InlineData("--empty\r\n\r\n--show-npages\r\n")]
    public async Task ResponseBlankLinesRemainEmptyArguments(string response)
    {
        using InMemoryQpdfHost host = Prepare();
        host.AddInput(QpdfPath.From("args"), Encoding.UTF8.GetBytes(response), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["@args"]), CancellationToken.None);
        AssertCount(result, 0); Assert.True(result.Counters.HostOperations > 0); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("extra-slot")]
    [InlineData("whitespace")]
    [InlineData("missing-input")]
    public async Task EmptyNamesDoNotSkipSlotsOrImplyAnEmptyDocument(string kind)
    {
        string[] argv = kind == "extra-slot" ? ["--empty", "", "", "--show-npages"]
            : kind == "whitespace" ? ["--empty", " ", "--show-npages"] : ["", "--show-npages"];
        using InMemoryQpdfHost host = Prepare();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyDefaultsDoNotResetADeclaredInput(bool before)
    {
        using InMemoryQpdfHost host = Prepare();
        host.AddInput(QpdfPath.From("settings"), "{\"inputFile\":\"\",\"outputFile\":\"\"}"u8.ToArray(), QpdfInputOwnership.Transfer);
        string[] argv = before ? ["--job-json-file=settings", "input", "--show-npages"]
            : ["input", "--job-json-file=settings", "--show-npages"];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        if (before) { AssertCount(result, 1); }
        else { Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments); }
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyOutputDeclarationsRespectReplaceInputOrder(bool before)
    {
        using InMemoryQpdfHost host = Prepare(); byte[] original = Source(); await Seed(host, Input, original);
        string[] argv = before ? ["input", "", "--replace-input"] : ["input", "--replace-input", ""];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(before ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        Assert.True(host.TryGetOutput(Input, out byte[] output));
        if (before)
        {
            host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            Assert.Equal(1, document.PageCount);
        }
        else
        { Assert.Equal(original, output); Assert.Equal(0L, result.Counters.HostOperations); }
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NamedInspectionOutputStillRefusesAndPreservesItsActualTarget()
    {
        using InMemoryQpdfHost host = Prepare(); await Seed(host, Output, "prior"u8.ToArray());
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"empty\":\"\",\"outputFile\":\"output\",\"showNpages\":\"\"}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Equal(0L, result.Counters.HostOperations);
        Assert.True(host.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EmptyFilenamesStillRequireStringValues()
    {
        using InMemoryQpdfHost host = Prepare();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"empty\":\"\",\"inputFile\":null,\"showNpages\":\"\"}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments && d.Message.Contains("inputFile", StringComparison.Ordinal));
        Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EffectiveEmptyJobInputPreservesTheExplicitEmptyPolicy()
    {
        using InMemoryQpdfHost host = Prepare();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"empty\":\"\",\"inputFile\":\"input\",\"inputFile\":\"\",\"showNpages\":\"\"}", CancellationToken.None);
        AssertCount(result, 0); Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static byte[] Source() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> /Contents 4 0 R >>", null),
        (4, "<< /Length 4 >>", "q Q\n"u8.ToArray())], 1);

    private static InMemoryQpdfHost Prepare()
    { InMemoryQpdfHost host = new(); host.AddInput(Input, Source(), QpdfInputOwnership.Transfer); return host; }

    private static async Task Seed(InMemoryQpdfHost host, QpdfPath path, byte[] value)
    {
        await using IQpdfOutputTransaction target = await host.CreateOutputAsync(path, CancellationToken.None);
        await target.WriteAsync(value, CancellationToken.None); await target.CommitAsync(CancellationToken.None);
    }

    private static void AssertCount(QpdfExecutionResult result, int count)
    {
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message.EndsWith(": " + count, StringComparison.Ordinal));
        Assert.Equal(0L, result.Counters.OpenHandles);
    }
}
