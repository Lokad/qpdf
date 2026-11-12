using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordFileTests
{
    [Theory]
    [InlineData("security-r6-ascii.pdf", "726561646572", false)]
    [InlineData("security-r6-ascii.pdf", "7265616465720D0A", false)]
    [InlineData("security-r6-ascii.pdf", "72656164657200737566666978", false)]
    [InlineData("security-r4-pdfdoc.pdf", "636166E980", false)]
    [InlineData("security-r4-pdfdoc.pdf", "636166C3A9E280A2", false)]
    [InlineData("security-r4-pdfdoc.pdf", "36333A36313A36363A65393A38", true)]
    [InlineData("security-r6-empty.pdf", "0D0A", false)]
    [InlineData("security-r6-empty.pdf", "", false)]
    public async Task ArgvAndJobsUseFirstLineBytesForInspectionsAndJson(string file, string hexBytes, bool hexMode)
    {
        foreach (bool job in new[] { false, true })
        foreach (bool json in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); Add(host, file);
            host.AddInput(QpdfPath.From("password"), Convert.FromHexString(hexBytes), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result;
            if (job)
            {
                Dictionary<string, object> fields = new() { ["inputFile"] = file, ["passwordFile"] = "password",
                    [json ? "json" : "showNpages"] = json ? "2" : "", ["passwordMode"] = hexMode ? "hex-bytes" : "auto" };
                if (json) { fields["jsonKey"] = new[] { "encrypt" }; }
                result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None);
            }
            else
            {
                List<string> args = [file, "--password-file", "password", "--password-mode=" + (hexMode ? "hex-bytes" : "auto"), json ? "--json" : "--show-npages"];
                if (json) { args.Add("--json-key=encrypt"); }
                result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
            }
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            if (json)
            {
                using JsonDocument document = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
                Assert.True(document.RootElement.GetProperty("encrypt").GetProperty("userpasswordmatched").GetBoolean());
            }
            else { Assert.Equal(file + ": 1", Assert.Single(result.Diagnostics).Message); }
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
        }
    }

    [Theory]
    [InlineData("", QpdfDiagnosticCode.PasswordRequired)]
    [InlineData("\n", QpdfDiagnosticCode.InvalidPassword)]
    [InlineData(" reader \n", QpdfDiagnosticCode.InvalidPassword)]
    [InlineData("reader\r", QpdfDiagnosticCode.InvalidPassword)]
    public async Task EmptyFileBlankLineSpacesAndEofCarriageReturnRemainDistinct(string content, QpdfDiagnosticCode code)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        host.AddInput(QpdfPath.From("password"), Encoding.UTF8.GetBytes(content), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["security-r6-ascii.pdf", "--password-file=password", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("--password=wrong", "--password-file=good")]
    [InlineData("--password-file=bad", "--password=reader")]
    [InlineData("--password-file=bad", "--password-file=good")]
    [InlineData("--password=reader", "--password-file=empty")]
    [InlineData("--password-file=good", "--password-file=empty")]
    public async Task SettersKeepOrderAndEmptyFilesKeepThePreviousPassword(string first, string second)
    {
        using InMemoryQpdfHost host = HostWithSetters();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["security-r6-ascii.pdf", first, second, "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal("security-r6-ascii.pdf: 1", Assert.Single(result.Diagnostics).Message);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SortedJobKeysAndNestedConfigurationKeepNativeSetterOrder()
    {
        using InMemoryQpdfHost host = HostWithSetters();
        QpdfExecutionResult sorted = await Qpdf.ExecuteJobAsync(host,
            """{"passwordFile":"good","password":"wrong","inputFile":"security-r6-ascii.pdf","showNpages":""}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, sorted.Status);
        host.AddInput(QpdfPath.From("job.json"), """{"passwordFile":"bad"}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult nested = await Qpdf.ExecuteAsync(host,
            new(["security-r6-ascii.pdf", "--password-file=good", "--job-json-file=job.json", "--password=reader", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, nested.Status); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task OverwrittenMissingFileStillFailsWithoutOpeningThePdf()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["missing.pdf", "--password-file=private-missing-password-file", "--password=private-secret", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.HostOperationFailed, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, result.Counters.HostOperations); Assert.Equal(0, host.OpenLeaseCount);
        Assert.DoesNotContain("private-secret", result.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipleLinesProduceANonSecretNoteWithoutChangingNativeExitCodes()
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        host.AddInput(QpdfPath.From("private-password-file"), "reader\nprivate-unused-line\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        foreach (string operation in new[] { "--show-npages", "--requires-password", "--show-encryption" })
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
                new(["security-r6-ascii.pdf", "--password-file=private-password-file", operation]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(operation == "--requires-password" ? 3 : 0, (int)result.ExitCode);
            Assert.Equal(QpdfDiagnosticSeverity.Info, Assert.Single(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.PasswordFileNote).Severity);
            Assert.All(result.Diagnostics, item => Assert.DoesNotContain("private", item.Message, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task FileEncodingRecoveryCanBeSuppressed()
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r4-pdfdoc.pdf");
        host.AddInput(QpdfPath.From("password"), Encoding.UTF8.GetBytes("café•\n"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"security-r4-pdfdoc.pdf","passwordFile":"password","suppressPasswordRecovery":"","showNpages":""}""", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("{\"inputFile\":\"missing.pdf\",\"passwordFile\":\"secret\",\"outputFile\":\"out.pdf\"}", QpdfDiagnosticCode.UnsupportedCryptoOperation)]
    [InlineData("{\"inputFile\":\"missing.pdf\",\"passwordFile\":\"secret\",\"showNpages\":\"\",\"outputFile\":\"out.pdf\",\"check\":\"\"}", QpdfDiagnosticCode.InvalidArguments)]
    [InlineData("{\"inputFile\":\"missing.pdf\",\"passwordFile\":\"-\",\"showNpages\":\"\"}", QpdfDiagnosticCode.UnsupportedFeature)]
    public async Task InvalidOperationsRejectBeforePasswordFileAccess(string json, QpdfDiagnosticCode code)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code); Assert.Equal(0, result.Counters.HostOperations);
    }

    [Fact]
    public async Task EveryAdmissionRefusalClosesFilesAndRefundsTheOperation()
    {
        RecordingMemoryBudget baseline = new(1048576);
        await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++)
        { await Run(new(1048576) { RefuseOnGrowth = growth }, true); }

        static async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost storage = new(); Add(storage, "security-r4-pdfdoc.pdf");
            storage.AddInput(QpdfPath.From("password"), Convert.FromHexString("636166E980"), QpdfInputOwnership.Transfer);
            using IMemoryScope caller = budget.CreateScope();
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new BudgetHost(storage, caller),
                new(["security-r4-pdfdoc.pdf", "--password-file=password", "--show-npages"]), CancellationToken.None);
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses) { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded); }
            Assert.Equal(0, storage.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
            caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    private static InMemoryQpdfHost HostWithSetters()
    {
        InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        host.AddInput(QpdfPath.From("good"), "reader\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("bad"), "wrong\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("empty"), [], QpdfInputOwnership.Transfer); return host;
    }

    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(QpdfPath.From(file), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Transfer);
}
