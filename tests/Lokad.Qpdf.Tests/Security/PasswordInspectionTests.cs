using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordInspectionTests
{
    [Theory]
    [InlineData("security-r4-ascii.pdf", "reader")]
    [InlineData("security-r6-ascii.pdf", "fixture-owner")]
    [InlineData("security-r4-pdfdoc.pdf", "café•")]
    [InlineData("security-r6-empty.pdf", "")]
    [InlineData("security-r4-empty.pdf", null)]
    public async Task ArgvAndJobsInspectAuthenticatedPrimaryInputs(string file, string? password)
    {
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); Add(host, file);
            QpdfExecutionResult result = await Execute(host, file, password, job);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCount && diagnostic.Message.EndsWith(": 1", StringComparison.Ordinal));
            Assert.Equal(0, host.OpenLeaseCount);
            Assert.Equal(0, result.Counters.OpenHandles);
        }
    }

    [Theory]
    [InlineData(null, QpdfDiagnosticCode.PasswordRequired)]
    [InlineData("", QpdfDiagnosticCode.InvalidPassword)]
    [InlineData("private-wrong-password", QpdfDiagnosticCode.InvalidPassword)]
    public async Task PasswordFailuresHaveDistinctSecretFreeDiagnostics(string? password, QpdfDiagnosticCode expected)
    {
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
            QpdfExecutionResult result = await Execute(host, "security-r6-ascii.pdf", password, job);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(expected, diagnostic.Code);
            Assert.DoesNotContain("private-wrong-password", diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
        }
    }

    [Fact]
    public async Task ExplicitStreamInspectionExportsDecryptedDecodedBytes()
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
            ["security-r6-ascii.pdf", "--password=reader", "--show-object=7", "--filtered-stream-data"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.NotNull(result.OutputBytes);
        Assert.Equal("BT (Page) Tj ET", Encoding.ASCII.GetString(result.OutputBytes.Value.Span));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterCredentialSettersReplaceEarlierOnes(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        host.AddInput(QpdfPath.From("base.json"), Encoding.UTF8.GetBytes(
            """{"inputFile":"security-r6-ascii.pdf","check":"","password":"wrong-secret"}"""), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"base.json","password":"reader","showNpages":""}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--job-json-file=base.json", "--password=reader", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCount);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message.Contains("wrong-secret", StringComparison.Ordinal));
    }

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string file, string? password, bool job)
    {
        if (job)
        {
            Dictionary<string, string> fields = new() { ["inputFile"] = file, ["check"] = "", ["showNpages"] = "" };
            if (password is not null) { fields["password"] = password; }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
        }
        List<string> argv = [file, "--check", "--show-npages"];
        if (password is not null) { argv.Add("--password=" + password); }
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
    }

    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(QpdfPath.From(file), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
}
