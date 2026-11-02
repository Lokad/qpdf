using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class DisabledEmptySettingTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "argv", "response", "job" })
        foreach (bool before in new[] { false, true })
        foreach (string flag in new[] { "false", "true", "bare" }) { yield return [route, before, flag]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DisabledEmptyKeepsInputAcrossEquivalentConfigurationPositions(string route, bool before, string flag)
    {
        object value = flag == "bare" ? "" : flag == "true";
        bool success = flag == "false" || before || route == "job";
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result;
        if (route == "job")
        {
            Dictionary<string, object> settings = new();
            if (before) { settings["empty"] = value; }
            settings["inputFile"] = "input"; settings["outputFile"] = "output";
            if (!before) { settings["empty"] = value; }
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None);
        }
        else
        {
            host.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["empty"] = value }), QpdfInputOwnership.Copy);
            string[] args = before ? ["--job-json-file=settings", "input", "output"] : ["input", "--job-json-file=settings", "output"];
            if (route == "response")
            {
                host.AddInput(QpdfPath.From("args"), Encoding.UTF8.GetBytes(string.Join('\n', args)), QpdfInputOwnership.Copy);
                args = ["@args"];
            }
            result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(result.ExitCode == (success ? QpdfExitCode.Success : QpdfExitCode.Error), string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (!success)
        {
            Assert.Equal("prior"u8.ToArray(), output);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("Empty input cannot follow", StringComparison.Ordinal));
            return;
        }
        Assert.Empty(result.Diagnostics);
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
    }
}
