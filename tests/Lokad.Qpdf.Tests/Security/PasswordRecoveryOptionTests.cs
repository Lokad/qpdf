using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordRecoveryOptionTests
{
    [Theory]
    [InlineData("security-r4-pdfdoc.pdf", "café•", false)]
    [InlineData("security-r4-utf8.pdf", "café", true)]
    [InlineData("security-r4-ascii.pdf", "reader", true)]
    [InlineData("security-r6-ascii.pdf", "fixture-owner", true)]
    [InlineData("security-r6-empty.pdf", null, true)]
    public async Task SuppressionUsesOnlyTheSuppliedEncoding(string file, string? password, bool exactMatch)
    {
        foreach (bool job in new[] { false, true })
        foreach (bool json in new[] { false, true })
        foreach (bool suppress in new[] { false, true })
        {
            using InMemoryQpdfHost host = new();
            host.AddInput(QpdfPath.From(file), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result;
            if (job)
            {
                Dictionary<string, object> fields = new() { ["inputFile"] = file, [json ? "json" : "showNpages"] = json ? "2" : "" };
                if (json) { fields["jsonKey"] = new[] { "encrypt" }; }
                if (password is not null) { fields["password"] = password; }
                if (suppress) { fields["suppressPasswordRecovery"] = ""; }
                result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
            }
            else
            {
                List<string> arguments = [file, json ? "--json" : "--show-npages"];
                if (json) { arguments.Add("--json-key=encrypt"); }
                if (password is not null) { arguments.Add("--password=" + password); }
                if (suppress) { arguments.Add("--suppress-password-recovery"); }
                result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
            }
            bool success = !suppress || exactMatch;
            Assert.Equal(success ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
            if (!success)
            { Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code); Assert.Null(result.OutputText); }
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
        }
    }

    [Fact]
    public async Task PasswordQueriesHonorSuppressionWithoutTurningItIntoAnError()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("in.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r4-pdfdoc.pdf")), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["in.pdf", "--requires-password", "--password=café•", "--suppress-password-recovery"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task SuppressionDoesNotEnableEncryptedJsonInput()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"missing.json","outputFile":"out.pdf","jsonInput":"","suppressPasswordRecovery":""}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, result.Counters.HostOperations);
    }
}
