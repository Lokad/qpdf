using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordModeTests
{
    [Theory]
    [InlineData("security-r4-pdfdoc.pdf", "auto", "café•")]
    [InlineData("security-r4-pdfdoc.pdf", "bytes", "café•")]
    [InlineData("security-r4-pdfdoc.pdf", "unicode", "café•")]
    [InlineData("security-r4-pdfdoc.pdf", "hex-bytes", "636166e980")]
    [InlineData("security-r4-pdfdoc.pdf", "hex-bytes", "63:61:66:E9:8")]
    [InlineData("security-r4-pdfdoc.pdf", "hex-bytes", "636166c3a9e280a2")]
    [InlineData("security-r6-ascii.pdf", "hex-bytes", "726561646572")]
    [InlineData("security-r6-ascii.pdf", "hex-bytes", "72656164657200ff")]
    [InlineData("security-r6-empty.pdf", "hex-bytes", "zz")]
    [InlineData("security-r6-empty.pdf", "hex-bytes", "0")]
    public async Task InputModesMatchNativeDecodingAndRecovery(string file, string mode, string password)
    {
        foreach (bool job in new[] { false, true })
        foreach (bool json in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); Add(host, file);
            QpdfExecutionResult result;
            if (job)
            {
                Dictionary<string, object> fields = new() { ["inputFile"] = file, ["password"] = password, ["passwordMode"] = mode,
                    [json ? "json" : "showNpages"] = json ? "2" : "" };
                if (json) { fields["jsonKey"] = new[] { "encrypt" }; }
                result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
            }
            else
            {
                List<string> arguments = [file, "--password=" + password, "--password-mode=" + mode, json ? "--json" : "--show-npages"];
                if (json) { arguments.Add("--json-key=encrypt"); }
                result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
            }
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
        }
    }

    [Fact]
    public async Task HexRecoveryCanBeSuppressed()
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r4-pdfdoc.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["security-r4-pdfdoc.pdf", "--password=636166c3a9e280a2", "--password-mode=hex-bytes", "--suppress-password-recovery", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task InvalidModesAndUnsupportedTransformsFailBeforeInputAccess()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult invalid = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"missing.pdf","showNpages":"","passwordMode":"private-invalid-mode"}""", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(invalid.Diagnostics).Code);
        Assert.DoesNotContain("private-invalid-mode", invalid.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(0, invalid.Counters.HostOperations);
        QpdfExecutionResult transform = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"missing.pdf","outputFile":"out.pdf","updateFromJson":"update.json","passwordMode":"auto"}""", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(transform.Diagnostics).Code);
        Assert.Equal(0, transform.Counters.HostOperations);
    }

    [Fact]
    public async Task LongHexInputStillHonorsConfigurationLimits()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["missing.pdf", "--password=" + new string('A', 65536), "--password-mode=hex-bytes", "--show-npages"]),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxInputBytes = 1024 }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, result.Counters.HostOperations);
    }

    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(QpdfPath.From(file), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
}
