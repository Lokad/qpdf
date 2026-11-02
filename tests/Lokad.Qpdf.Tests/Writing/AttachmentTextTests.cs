using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentTextTests
{
    private static InMemoryQpdfHost Host()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("base"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Names [(K) 5 0 R] >>", null),
            (5, "<< /Type /Filespec /UF (file) /EF << /UF 6 0 R >> >>", null),
            (6, "<< /Type /EmbeddedFile /Length 1 >>", "A"u8.ToArray())], 1), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("added"), "A"u8.ToArray(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    private static void AssertPrior(InMemoryQpdfHost host)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> InvalidCases()
    {
        foreach (string kind in new[] { "key-high", "key-low", "filename-high", "description-low", "prefix-high", "mime-nul" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [kind, route]; }
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task InvalidRequestTextCannotChangeThePublishedIdentifier(string kind, string route)
    {
        using InMemoryQpdfHost host = Host(); await Seed(host);
        bool low = kind.EndsWith("low", StringComparison.Ordinal);
        string malformed = low ? "\udc00" : "\ud800";
        string key = kind.StartsWith("key", StringComparison.Ordinal) ? malformed : "K";
        string fileName = kind.StartsWith("filename", StringComparison.Ordinal) ? malformed : "file";
        string description = kind.StartsWith("description", StringComparison.Ordinal) ? malformed : "description";
        string mime = kind == "mime-nul" ? "text/plain\0bad" : "text/plain";
        bool copy = kind.StartsWith("prefix", StringComparison.Ordinal);
        if (route == "typed")
        {
            if (copy)
            {
                await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("base"), QpdfPath.From("output"),
                    [new(QpdfPath.From("source"), malformed)], CancellationToken.None).AsTask());
            }
            else
            {
                await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.AddAttachmentsAsync(host, QpdfPath.From("base"), QpdfPath.From("output"),
                    [new("added", key, fileName, description, mime, null, null, false)], CancellationToken.None).AsTask());
            }
        }
        else
        {
            QpdfExecutionResult result;
            if (route == "argv")
            {
                string[] argv = copy ? ["base", "--copy-attachments-from", "source", "--prefix=" + malformed, "--", "output"]
                    : ["base", "--add-attachment", "added", "--key=" + key, "--filename=" + fileName, "--description=" + description, "--mimetype=" + mime, "--", "output"];
                result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            }
            else
            {
                Dictionary<string, object> config = new() { ["inputFile"] = "base", ["outputFile"] = "output" };
                if (copy) { config["copyAttachmentsFrom"] = new[] { new { file = "source", prefix = "BROKEN" } }; }
                else
                {
                    Dictionary<string, object> add = new() { ["file"] = "added", ["key"] = "K", ["filename"] = "file", ["description"] = "description", ["mimetype"] = mime };
                    if (kind != "mime-nul") { add[kind.Split('-')[0]] = "BROKEN"; }
                    config["addAttachment"] = new[] { add };
                }
                string json = JsonSerializer.Serialize(config).Replace("\"BROKEN\"", low ? "\"\\udc00\"" : "\"\\ud800\"", StringComparison.Ordinal);
                result = await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None);
            }
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Error);
        }
        AssertPrior(host);
    }

    public static IEnumerable<object[]> ValidCases()
    {
        foreach (string kind in new[] { "replacement", "pair", "boundary", "nul" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [kind, route]; }
    }

    [Theory]
    [MemberData(nameof(ValidCases))]
    public async Task ValidScalarsAndChunkBoundariesRoundTrip(string kind, string route)
    {
        string value = kind switch { "replacement" => "\ufffd", "pair" => "\ud83d\ude00", "boundary" => new string('X', 255) + "\ud83d\ude00Z", _ => "NUL\0text" };
        using InMemoryQpdfHost host = Host();
        if (route == "typed")
        { _ = await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("base"), QpdfPath.From("output"), [new("added", value, value, value, null, null, null, false)], CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "base", outputFile = "output", addAttachment = new[] { new { file = "added", key = value, filename = value, description = value } } }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["base", "--add-attachment", "added", "--key=" + value, "--filename=" + value, "--description=" + value, "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo info = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("again"), CancellationToken.None));
        Assert.Equal(value, info.Key); Assert.Equal(value, info.FileName); Assert.Equal(value, info.Description);
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("again"), value, QpdfPath.From("payload"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload)); Assert.Equal("A"u8.ToArray(), payload);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task TwoMalformedKeysCannotCollapseIntoOneEmittedName(string route)
    {
        using InMemoryQpdfHost host = Host(); await Seed(host);
        if (route == "typed")
        {
            await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.AddAttachmentsAsync(host, QpdfPath.From("base"), QpdfPath.From("output"),
                [new("added", "\ud800", "one", null, null, null, null, false), new("added", "\ud801", "two", null, null, null, null, false)], CancellationToken.None).AsTask());
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"base","outputFile":"output","addAttachment":[{"file":"added","key":"\ud800"},{"file":"added","key":"\ud801"}]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["base", "--add-attachment", "added", "--key=\ud800", "--", "--add-attachment", "added", "--key=\ud801", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        }
        AssertPrior(host);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task ValidUnicodePrefixKeepsItsIdentity(string route)
    {
        const string prefix = "\ufffd\ud83d\ude00";
        using InMemoryQpdfHost host = Host();
        if (route == "typed")
        { _ = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("base"), QpdfPath.From("output"), [new(QpdfPath.From("source"), prefix)], CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "base", outputFile = "output", copyAttachmentsFrom = new[] { new { file = "source", prefix } } }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["base", "--copy-attachments-from", "source", "--prefix=" + prefix, "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        Assert.Equal(prefix + "K", Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("again"), CancellationToken.None)).Key);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
