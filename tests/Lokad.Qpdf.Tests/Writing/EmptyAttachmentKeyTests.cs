using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class EmptyAttachmentKeyTests
{
    private static InMemoryQpdfHost Host(bool empty)
    {
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Names [" + (empty ? "() 5 0 R " : "") + "(K) 8 0 R] >>", null),
            (5, "<< /Type /Filespec /UF (empty-file) /EF << /UF 6 0 R >> >>", null),
            (6, "<< /Type /EmbeddedFile /Length 1 >>", "A"u8.ToArray()), (7, "null", null),
            (8, "<< /Type /Filespec /UF (other) /EF << /UF 9 0 R >> >>", null),
            (9, "<< /Type /EmbeddedFile /Length 1 >>", "B"u8.ToArray())], 1);
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("added"), "N"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("empty.json"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await transaction.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await transaction.CommitAsync(CancellationToken.None);
    }

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string route, string operation, bool named)
    {
        if (route == "job")
        {
            Dictionary<string, object> config = new() { ["inputFile"] = "input" };
            if (named) { config["outputFile"] = "output"; }
            config[operation == "extract" ? "showAttachment" : "removeAttachment"] = operation == "extract" ? string.Empty : new[] { string.Empty };
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(config), CancellationToken.None);
        }
        List<string> argv = ["input"];
        if (route == "mixed") { argv.Add("--job-json-file=empty.json"); }
        if (route == "argv-separated") { argv.AddRange(["--show-attachment", string.Empty]); }
        else { argv.Add(operation == "extract" ? "--show-attachment=" : "--remove-attachment="); }
        if (named) { argv.AddRange(["--", "output"]); }
        return Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
    }

    public static IEnumerable<object[]> LookupCases()
    {
        foreach (string route in new[] { "typed", "argv", "mixed", "job" })
        foreach (string operation in new[] { "extract", "remove" }) { yield return [route, operation]; }
    }

    [Theory]
    [MemberData(nameof(LookupCases))]
    public async Task ExplicitEmptyIdentifierSelectsAnExistingKey(string route, string operation)
    {
        using InMemoryQpdfHost host = Host(true);
        if (route == "typed")
        {
            if (operation == "extract")
            { Assert.Equal(1L, (await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), string.Empty, QpdfPath.From("output"), CancellationToken.None)).ByteCount); }
            else
            { Assert.Equal(1, (await Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), [string.Empty], CancellationToken.None)).AttachmentCount); }
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, operation, true);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (operation == "extract") { Assert.Equal("A"u8.ToArray(), output); }
        else
        {
            host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
            Assert.Equal("K", Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("again"), CancellationToken.None)).Key);
            await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("again"), "K", QpdfPath.From("other"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("other"), out byte[] other)); Assert.Equal("B"u8.ToArray(), other);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("argv-separated")]
    [InlineData("mixed")]
    [InlineData("job")]
    public async Task FilelessEmptyKeyExtractionEscapesItsInputAndHost(string route)
    {
        using InMemoryQpdfHost host = Host(true);
        QpdfExecutionResult result = await Execute(host, route, "extract", false);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.True(result.OutputBytes.HasValue);
        ReadOnlyMemory<byte> escaped = result.OutputBytes.GetValueOrDefault();
        Assert.Equal(0L, host.OpenLeaseCount); host.Dispose(); Assert.Equal("A"u8.ToArray(), escaped.ToArray());
    }

    [Theory]
    [MemberData(nameof(LookupCases))]
    public async Task AbsentEmptyKeyFailsLookupAndPreservesTheTarget(string route, string operation)
    {
        using InMemoryQpdfHost host = Host(false); await Seed(host);
        if (route == "typed")
        {
            if (operation == "extract")
            { await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), string.Empty, QpdfPath.From("output"), CancellationToken.None).AsTask()); }
            else
            { await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), [string.Empty], CancellationToken.None).AsTask()); }
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, operation, true);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("not found", StringComparison.Ordinal));
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "extract")]
    [InlineData(true, "extract")]
    [InlineData(false, "remove")]
    [InlineData(true, "remove")]
    public async Task MissingAndNullParametersStillReject(bool job, string operation)
    {
        using InMemoryQpdfHost host = Host(true); await Seed(host);
        string config = operation == "extract" ? """{"inputFile":"input","outputFile":"output","showAttachment":null}"""
            : """{"inputFile":"input","outputFile":"output","removeAttachment":[]}""";
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, config, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", operation == "extract" ? "--show-attachment" : "--remove-attachment", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EmptyRemovalSetStillRejects()
    {
        using InMemoryQpdfHost host = Host(true);
        await Assert.ThrowsAsync<ArgumentException>(() => Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), [], CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task CreatingAnEmptyKeyStillRejects(string route)
    {
        using InMemoryQpdfHost host = Host(true); await Seed(host);
        if (route == "typed")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                [new("added", string.Empty, null, null, null, null, null, true)], CancellationToken.None).AsTask());
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","addAttachment":[{"file":"added","key":"","replace":true}]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--add-attachment", "added", "--key=", "--replace", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
