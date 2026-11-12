using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptionPredicateTests
{
    // Exit values recorded with qpdf 12.4.2. A successful password query uses 3,
    // independently of warning presentation; a plain input uses 2 without error.
    [Theory]
    [InlineData("minimal-1page.pdf", null, 2, 2)]
    [InlineData("minimal-1page.pdf", "wrong", 2, 2)]
    [InlineData("security-r6-ascii.pdf", null, 0, 0)]
    [InlineData("security-r6-ascii.pdf", "", 0, 0)]
    [InlineData("security-r6-ascii.pdf", "wrong", 0, 0)]
    [InlineData("security-r6-ascii.pdf", "reader", 0, 3)]
    [InlineData("security-r6-ascii.pdf", "fixture-owner", 0, 3)]
    [InlineData("security-r6-empty.pdf", null, 0, 3)]
    [InlineData("security-r6-empty.pdf", "wrong", 0, 0)]
    [InlineData("security-r4-ascii.pdf", "reader", 0, 3)]
    [InlineData("security-r4-pdfdoc.pdf", "café•", 0, 3)]
    public async Task ArgvAndJobPredicatesMatchNativeExitSemantics(string file, string? password, int encrypted, int required)
    {
        foreach (bool job in new[] { false, true })
        foreach (bool needsPassword in new[] { false, true })
        {
            using InMemoryQpdfHost host = new();
            host.AddInput(QpdfPath.From(file), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result;
            if (job)
            {
                Dictionary<string, string> fields = new() { ["inputFile"] = file, [needsPassword ? "requiresPassword" : "isEncrypted"] = "", ["warningExit0"] = "" };
                if (password is not null) { fields["password"] = password; }
                result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
            }
            else
            {
                List<string> argv = [file, needsPassword ? "--requires-password" : "--is-encrypted", "--warning-exit-0"];
                if (password is not null) { argv.Add("--password=" + password); }
                result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
            }
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(needsPassword ? required : encrypted, (int)result.ExitCode);
            Assert.Empty(result.Diagnostics); Assert.Null(result.OutputBytes); Assert.Null(result.OutputText);
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
        }
    }

    [Fact]
    public async Task PredicateModesAreMutuallyExclusiveBeforeInputAccess()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
            ["unopened.pdf", "--is-encrypted", "--requires-password"]), CancellationToken.None);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"unopened.pdf","isEncrypted":"","requiresPassword":""}""", CancellationToken.None);
        foreach (QpdfExecutionResult result in new[] { argv, job })
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
            Assert.Equal(0, result.Counters.HostOperations);
        }
    }

    [Fact]
    public async Task QueriesIgnoreOtherInspectionsAndNeverPublishPdfOutput()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("in.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-ascii.pdf")), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
            ["in.pdf", "--is-encrypted", "--show-npages", "--show-encryption", "out.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.False(host.TryGetOutput(QpdfPath.From("out.pdf"), out _));
    }

    [Fact]
    public async Task UnsupportedHandlersDoNotBecomeSuccessfulQueries()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("in.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "encrypted-decl.pdf")), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["in.pdf", "--is-encrypted"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(result.Diagnostics).Code);
    }
}
