using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PasswordDescriptorTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Descriptor = QpdfPath.From("-"), Output = QpdfPath.From("output");

    [Theory]
    [InlineData("security-r6-ascii.pdf", "726561646572", false)]
    [InlineData("security-r6-ascii.pdf", "7265616465720D0A", false)]
    [InlineData("security-r6-empty.pdf", "0A", false)]
    [InlineData("security-r6-empty.pdf", "", false)]
    [InlineData("security-r4-pdfdoc.pdf", "636166E980", false)]
    [InlineData("security-r4-pdfdoc.pdf", "36333A36313A36363A65393A38", true)]
    public async Task HostDescriptorUsesTheSameFirstLineAndPasswordModes(string fixture, string hexBytes, bool hex)
    {
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); Add(host, fixture, Convert.FromHexString(hexBytes));
            QpdfExecutionResult result = await Execute(host, job, "inspect", hex, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal("input.pdf: 1", Assert.Single(result.Diagnostics).Message);
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
        }
    }

    [Theory]
    [InlineData(false, "json")]
    [InlineData(true, "json")]
    [InlineData(false, "extract")]
    [InlineData(true, "extract")]
    [InlineData(false, "rewrite")]
    [InlineData(true, "rewrite")]
    public async Task DescriptorCredentialsReachExistingAuthenticatedOperations(bool job, string operation)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r4-attachment.pdf", "6f776e6572e991\n"u8.ToArray());
        QpdfExecutionResult result = await Execute(host, job, operation, true, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        if (operation == "extract") { Assert.Equal("Private attachment payload.\n"u8.ToArray(), bytes); }
        else if (operation == "json")
        {
            using JsonDocument json = JsonDocument.Parse(bytes);
            Assert.True(json.RootElement.GetProperty("encrypt").GetProperty("ownerpasswordmatched").GetBoolean());
        }
        else
        {
            QpdfPath reopened = QpdfPath.From("reopened.pdf"); host.AddInput(reopened, bytes, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reopened, CancellationToken.None);
            Assert.Null(document.Encryption); Assert.Equal(1, document.PageCount);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmappedDescriptorsFailThroughTheHostBeforeOpeningThePdf(bool job)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Execute(host, job, "inspect", false, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.HostOperationFailed, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, result.Counters.HostOperations); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task DescriptorSettersRetainOrderAndEmptyInputKeepsThePreviousPassword()
    {
        foreach (string content in new[] { "", "reader\nunused-secret\n" })
        {
            using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf", Encoding.ASCII.GetBytes(content));
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, "--password=reader", "--password-file=-", "--show-npages"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.DoesNotContain(result.Diagnostics, item => item.Message.Contains("unused-secret", StringComparison.Ordinal));
            Assert.Equal(content.Length > 0, result.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.PasswordFileNote));
            QpdfExecutionResult replaced = await Qpdf.ExecuteAsync(host, new([Input.Value, "--password-file=-", "--password=wrong", "--show-npages"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, replaced.Status); Assert.Contains(replaced.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidPassword);
            Assert.Equal(0, host.OpenLeaseCount);
        }
    }

    [Fact]
    public async Task EveryDescriptorAdmissionRefusalClosesAndRefundsTheOperation()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost storage = new(); Add(storage, "security-r4-attachment.pdf", "6f776e6572e991\n"u8.ToArray());
            using IMemoryScope caller = budget.CreateScope(); MemoryBudgetHost host = new(storage, caller);
            QpdfExecutionResult result = await Execute(host, true, "rewrite", true, CancellationToken.None);
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses) { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded); Assert.False(storage.TryGetOutput(Output, out _)); }
            Assert.Equal(0, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, bool job, string operation, bool hex, CancellationToken token)
    {
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["passwordFile"] = "-" };
        List<string> argv = [Input.Value, "--password-file=-"];
        if (hex) { fields["passwordMode"] = "hex-bytes"; argv.Add("--password-mode=hex-bytes"); }
        if (operation == "inspect") { fields["showNpages"] = ""; argv.Add("--show-npages"); }
        else
        {
            fields["outputFile"] = Output.Value;
            if (operation == "json") { fields["json"] = "2"; fields["jsonKey"] = new[] { "encrypt" }; argv.AddRange(["--json", "--json-key=encrypt"]); }
            else if (operation == "extract") { fields["showAttachment"] = "private.bin"; argv.Add("--show-attachment=private.bin"); }
            else { fields["decrypt"] = ""; argv.Add("--decrypt"); }
            argv.AddRange(["--", Output.Value]);
        }
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), token) : Qpdf.ExecuteAsync(host, new(argv), token);
    }
    private static void Add(InMemoryQpdfHost host, string fixture, byte[] descriptor)
    {
        host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), QpdfInputOwnership.Copy);
        host.AddInput(Descriptor, descriptor, QpdfInputOwnership.Copy);
    }

}
