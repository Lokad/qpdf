using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class AttachmentNameSetterTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");
    private const string FileName = "payload.bin";
    private const string PayloadFile = "files/payload.bin";

    private static string[] Setters(string role, string form) => form switch
    {
        "omitted" => [],
        "inline" => ["--" + role + "="],
        "separate" => ["--" + role, ""],
        "value" => ["--" + role + "=chosen"],
        _ => ["--" + role + "=old", "--" + role + "="],
    };

    public static IEnumerable<object[]> SyntaxCases()
    {
        foreach (string role in new[] { "key", "filename" })
        foreach (string form in new[] { "omitted", "inline", "separate", "value", "reset" })
        { yield return [role, form]; }
    }

    [Theory]
    [MemberData(nameof(SyntaxCases))]
    public void EmptyAndResetArgvNamesSelectTheDefault(string role, string form)
    {
        string[] argv = ["--add-attachment", PayloadFile, .. Setters(role, form), "--", "after"];
        int cursor = 0;
        AttachmentAdd add = ArgvParser.ParseAddBlock(argv, ref cursor, new(QpdfLimits.Default, CancellationToken.None));
        Assert.Equal(form == "value" ? "chosen" : null, role == "key" ? add.Key : add.FileName);
        Assert.Equal(PayloadFile, add.File); Assert.Equal(argv.Length - 1, cursor);
    }

    public static IEnumerable<object?[]> NameCases()
    {
        foreach (string? key in new string?[] { null, "", "chosen" })
        foreach (string? filename in new string?[] { null, "", "shown.txt" })
        { yield return [key, filename]; }
    }

    [Theory]
    [MemberData(nameof(NameCases))]
    public async Task JobNamesDefaultIndependentlyToThePayloadBasename(string? key, string? filename)
    {
        using InMemoryQpdfHost host = Prepare();
        Dictionary<string, object> add = new() { ["file"] = PayloadFile };
        if (key is not null) { add["key"] = key; }
        if (filename is not null) { add["filename"] = filename; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, Job([add]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await Verify(host, string.IsNullOrEmpty(key) ? FileName : key, string.IsNullOrEmpty(filename) ? FileName : filename, "first"u8.ToArray());
    }

    [Theory]
    [InlineData("key")]
    [InlineData("filename")]
    public async Task LastEmptyJobSetterResetsOnlyItsOwnName(string role)
    {
        using InMemoryQpdfHost host = Prepare();
        string other = role == "key" ? "filename" : "key";
        string job = "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"addAttachment\":[{\"file\":\"" + PayloadFile
            + "\",\"" + role + "\":\"old\",\"" + role + "\":\"\",\"" + other + "\":\"other\"}]}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await Verify(host, role == "key" ? FileName : "other", role == "filename" ? FileName : "other", "first"u8.ToArray());
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task CommandOriginsUseTheSameDefaultRequest(string route)
    {
        using InMemoryQpdfHost host = Prepare();
        List<string> argv = ["input"];
        if (route == "argv")
        { argv.AddRange(["--add-attachment", PayloadFile, "--key=old", "--key=", "--filename=old", "--filename", "", "--"]); }
        else
        {
            host.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(new
            { addAttachment = new[] { new { file = PayloadFile, key = "", filename = "" } } }), QpdfInputOwnership.Transfer);
            argv.Add("--job-json-file=settings");
        }
        argv.Add("output");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        await Verify(host, FileName, FileName, "first"u8.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyDefaultsKeepCollisionAndReplacementPolicy(bool replace)
    {
        using InMemoryQpdfHost host = Prepare(); await Seed(host);
        Dictionary<string, object> second = new() { ["file"] = "other/payload.bin", ["key"] = "", ["filename"] = "" };
        if (replace) { second["replace"] = ""; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, Job([
            new Dictionary<string, object> { ["file"] = PayloadFile }, second]), CancellationToken.None);
        Assert.Equal(replace ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        if (replace) { await Verify(host, FileName, FileName, "second"u8.ToArray()); }
        else
        {
            Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("argv-key")]
    [InlineData("argv-filename")]
    [InlineData("argv-file")]
    [InlineData("job-file")]
    [InlineData("job-key")]
    [InlineData("job-filename")]
    public async Task MissingRequiredValuesAndNullPropertiesStillRejectBeforeInput(string kind)
    {
        using InMemoryQpdfHost host = Prepare(); await Seed(host);
        QpdfExecutionResult result;
        if (kind.StartsWith("argv-", StringComparison.Ordinal))
        {
            string role = kind[5..];
            string[] argv = role == "file" ? ["input", "--add-attachment", "", "--", "output"]
                : ["input", "--add-attachment", PayloadFile, "--" + role, "--", "output"];
            result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        }
        else
        {
            string role = kind[4..];
            string properties = role == "file" ? "\"file\":\"\",\"key\":\"\",\"filename\":\"\"" : "\"file\":\"" + PayloadFile + "\",\"" + role + "\":null";
            result = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"addAttachment\":[{" + properties + "}]}", CancellationToken.None);
        }
        Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
    }

    private static string Job(Dictionary<string, object>[] adds)
        => JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", addAttachment = adds });

    private static InMemoryQpdfHost Prepare()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From(PayloadFile), "first"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("other/payload.bin"), "second"u8.ToArray(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    private static async Task Verify(InMemoryQpdfHost host, string key, string filename, byte[] payload)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo entry = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("read"), CancellationToken.None));
        Assert.Equal(key, entry.Key); Assert.Equal(filename, entry.FileName); Assert.NotEmpty(entry.Key); Assert.NotEmpty(entry.FileName);
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("read"), key, QpdfPath.From("extracted"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("extracted"), out bytes)); Assert.Equal(payload, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
