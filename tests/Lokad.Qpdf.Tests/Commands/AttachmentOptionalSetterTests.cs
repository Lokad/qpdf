using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class AttachmentOptionalSetterTests
{
    private static string[] Setters(string role, string form) => form switch
    {
        "omitted" => [], "empty" => ["--" + role + "="], "separate" => ["--" + role, ""],
        "value" => ["--" + role + "=pré-"], "clear" => ["--" + role + "=pré-", "--" + role + "="],
        _ => ["--" + role + "=old", "--" + role + "=pré-"]
    };

    [Theory]
    [InlineData("prefix", "omitted", null)]
    [InlineData("prefix", "empty", null)]
    [InlineData("prefix", "separate", null)]
    [InlineData("prefix", "value", "pré-")]
    [InlineData("prefix", "clear", null)]
    [InlineData("prefix", "replace", "pré-")]
    [InlineData("description", "omitted", null)]
    [InlineData("description", "empty", null)]
    [InlineData("description", "separate", null)]
    [InlineData("description", "value", "pré-")]
    [InlineData("description", "clear", null)]
    [InlineData("description", "replace", "pré-")]
    public void OptionalBlockSettersKeepTheirEffectiveValue(string role, string form, string? expected)
    {
        string[] argv = [role == "prefix" ? "--copy-attachments-from" : "--add-attachment", "payload",
            .. Setters(role, form), "--", "after"];
        int cursor = 0;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        string? value = role == "prefix" ? ArgvParser.ParseCopyBlock(argv, ref cursor).Prefix
            : ArgvParser.ParseAddBlock(argv, ref cursor, governor).Description;
        Assert.Equal(expected, value);
        Assert.Equal(argv.Length - 1, cursor);
    }

    [Theory]
    [InlineData("prefix", "--")]
    [InlineData("prefix", "--password=p")]
    [InlineData("description", "--")]
    [InlineData("description", "--replace")]
    public void MissingOptionalSetterValueStillRejects(string role, string next)
    {
        string[] argv = [role == "prefix" ? "--copy-attachments-from" : "--add-attachment", "payload", "--" + role, next];
        int cursor = 0;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfParseException failure = Assert.Throws<QpdfParseException>(() =>
        {
            if (role == "prefix") { ArgvParser.ParseCopyBlock(argv, ref cursor); }
            else { ArgvParser.ParseAddBlock(argv, ref cursor, governor); }
        });
        Assert.Contains("Missing parameter", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2L, failure.ByteOffset);
    }

    [Theory]
    [InlineData("mimetype")]
    [InlineData("creationdate")]
    [InlineData("moddate")]
    public void OtherMetadataKeepsItsInlineEmptyRule(string role)
    {
        int cursor = 0;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfParseException>(() => ArgvParser.ParseAddBlock(
            ["--add-attachment", "payload", "--" + role + "=", "--"], ref cursor, governor));
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task EmptySettersShareAttachmentDefaultsAcrossConfigurationOrigins(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), AttachmentCopyTests.SourceFixture(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), "PAYLOAD"u8.ToArray(), QpdfInputOwnership.Transfer);
        object copy = new[] { new { file = "source", prefix = "" } };
        QpdfExecutionResult result;
        if (route == "job")
        {
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
            {
                inputFile = "input", outputFile = "output", copyAttachmentsFrom = copy,
                addAttachment = new[] { new { file = "payload", description = "" } }
            }), CancellationToken.None);
        }
        else
        {
            List<string> argv = ["input"];
            if (route == "mixed")
            {
                host.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(new { copyAttachmentsFrom = copy }), QpdfInputOwnership.Transfer);
                argv.Add("--job-json-file=settings");
            }
            else { argv.AddRange(["--copy-attachments-from", "source", "--prefix=old-", "--prefix=", "--"]); }
            argv.AddRange(["--add-attachment", "payload", "--description=old", "--description=", "--", "output"]);
            result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("reread"), output, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> infos = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        Assert.Equal(new[] { "alpha.txt", "beta.txt", "payload" }, infos.Select(info => info.Key));
        Assert.Equal(string.Empty, infos.Single(info => info.Key == "payload").Description);
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("reread"), "payload", QpdfPath.From("data"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("data"), out byte[] data)); Assert.Equal("PAYLOAD"u8.ToArray(), data);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("description")]
    public async Task MissingValuesFailBeforeInputsAndPreserveTheRequestedTarget(string role)
    {
        using InMemoryQpdfHost host = new(); QpdfPath output = QpdfPath.From("output");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input",
            role == "prefix" ? "--copy-attachments-from" : "--add-attachment", "missing", "--" + role, "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
    }
}
