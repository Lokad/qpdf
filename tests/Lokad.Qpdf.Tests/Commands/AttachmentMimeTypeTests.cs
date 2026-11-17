using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class AttachmentMimeTypeTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");
    private static readonly byte[] Payload = [1, 0, 255, 10];

    [Theory]
    [InlineData("", false)]
    [InlineData("text", false)]
    [InlineData("text plain", false)]
    [InlineData("text/plain", true)]
    [InlineData("/", true)]
    [InlineData("type/", true)]
    [InlineData("/subtype", true)]
    [InlineData("text/a/b", true)]
    public void CommandRuleRequiresOnlyASlash(string value, bool valid)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        if (valid) { AttachmentMimeType.RequireCommand(value, governor); }
        else { Assert.Throws<QpdfParseException>(() => AttachmentMimeType.RequireCommand(value, governor)); }
    }

    [Fact]
    public void LongValuesBorrowStorageAndRespectCancellation()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1 }, CancellationToken.None);
        AttachmentMimeType.RequireCommand(new string('a', 65536) + "/", governor);
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        ResourceGovernor stopped = new(QpdfLimits.Default, canceled.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => AttachmentMimeType.RequireCommand("", stopped));
    }

    [Theory]
    [InlineData("text", "text/plain", false)]
    [InlineData("text/plain", "text", false)]
    [InlineData("", "text/plain", false)]
    [InlineData("text/plain", "", false)]
    [InlineData("type/", "text/plain", true)]
    [InlineData("/", "/subtype", true)]
    public void EachArgvSetterValidatesBeforeReplacement(string first, string last, bool valid)
    {
        string[] argv = ["--add-attachment", "payload", "--mimetype=" + first, "--mimetype=" + last, "--"];
        int cursor = 0;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        if (valid) { Assert.Equal(last, ArgvParser.ParseAddBlock(argv, ref cursor, governor).MimeType); }
        else { Assert.Throws<QpdfParseException>(() => ArgvParser.ParseAddBlock(argv, ref cursor, governor)); }
    }

    [Theory]
    [InlineData("text", "text/plain", true)]
    [InlineData("text/plain", "text", false)]
    [InlineData("", "text/plain", true)]
    [InlineData("text/plain", "", false)]
    [InlineData("type/", "text/plain", true)]
    [InlineData("/", "/subtype", true)]
    public async Task LastJobPropertyOwnsMimeValidation(string first, string last, bool valid)
    {
        using InMemoryQpdfHost host = Prepare();
        string job = "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"addAttachment\":[{\"file\":\"payload\",\"mimetype\":"
            + JsonSerializer.Serialize(first) + ",\"mimetype\":" + JsonSerializer.Serialize(last) + "}]}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        if (valid) { await Verify(host, last); }
        else
        {
            Assert.Equal(0L, result.Counters.HostOperations);
            Assert.False(host.TryGetOutput(Output, out _));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", "text")]
    [InlineData("job", "")]
    public async Task InvalidMimePreservesActualTargetBeforeInputWork(string route, string value)
    {
        using InMemoryQpdfHost host = new();
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["missing", "--add-attachment", "payload", "--mimetype=" + value, "--", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "missing", outputFile = "output", addAttachment = new[] { new { file = "payload", mimetype = value } } }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Contains("MIME", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", "text/a/b")]
    [InlineData("job", "/")]
    [InlineData("mixed", "type/")]
    public async Task CommandAdaptersPublishExactPermissiveMimeAndPayload(string route, string value)
    {
        using InMemoryQpdfHost host = Prepare();
        object attachments = new[] { new { file = "payload", mimetype = value } };
        QpdfExecutionResult result;
        if (route == "job")
        { result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", addAttachment = attachments }), CancellationToken.None); }
        else
        {
            List<string> argv = ["input"];
            if (route == "mixed")
            {
                host.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(new { addAttachment = attachments }), QpdfInputOwnership.Transfer);
                argv.Add("--job-json-file=settings");
            }
            else { argv.AddRange(["--add-attachment", "payload", "--mimetype=" + value, "--"]); }
            argv.Add("output");
            result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        await Verify(host, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task TypedOptionalEmptyMimeStillOmitsTheSubtype(string? value)
    {
        using InMemoryQpdfHost host = Prepare();
        await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), Output,
            [new("payload", null, null, null, value, null, null, false)], CancellationToken.None);
        await Verify(host, "");
    }

    private static InMemoryQpdfHost Prepare()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), Payload, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task Verify(InMemoryQpdfHost host, string mime)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] pdf));
        host.AddInput(QpdfPath.From("read"), pdf, QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo attachment = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("read"), CancellationToken.None));
        Assert.Equal("payload", attachment.Key); Assert.Equal(mime, attachment.MimeType);
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("read"), "payload", QpdfPath.From("extracted"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("extracted"), out byte[] bytes)); Assert.Equal(Payload, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
