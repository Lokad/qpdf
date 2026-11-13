using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptionTemplateCommandTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Template = QpdfPath.From("template.pdf"), Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (int revision in new[] { 4, 6 })
        foreach (string mode in new[] { "qdf", "linear", "split", "empty" }) { yield return [job, revision, mode]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CopyPolicyUsesItsOwnCredentialAcrossWriterAndSelectionModes(bool job, int revision, string mode)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); Add(host, Template, revision == 4 ? "security-r4-pdfdoc.pdf" : "security-r6-empty.pdf");
        Dictionary<string, object> fields = new() { ["outputFile"] = Output.Value, ["copyEncryption"] = Template.Value,
            ["encryptionFilePassword"] = revision == 4 ? "café•" : "" };
        List<string> argv = [Output.Value, "--copy-encryption=" + Template.Value, "--encryption-file-password=" + (revision == 4 ? "café•" : "")];
        if (mode == "empty")
        {
            fields["empty"] = ""; argv.Insert(0, "--empty"); fields["pages"] = new[] { new { file = Input.Value, password = "reader", range = "1,1" } };
            argv.AddRange(["--pages", Input.Value, "--password=reader", "1,1", "--"]);
        }
        else
        {
            fields["inputFile"] = Input.Value; fields["password"] = "reader"; argv.Insert(0, Input.Value); argv.Add("--password=reader");
        }
        if (mode == "qdf") { fields["qdf"] = ""; argv.Add("--qdf"); }
        if (mode == "linear") { fields["linearize"] = ""; fields["objectStreams"] = "generate"; argv.AddRange(["--linearize", "--object-streams=generate"]); }
        if (mode == "split")
        {
            fields["pages"] = new[] { new { file = ".", range = "1,1" } }; fields["splitPages"] = "1";
            argv.AddRange(["--pages", ".", "1,1", "--", "--split-pages=1"]);
        }
        QpdfExecutionResult result = await Execute(host, job, fields, argv); Completed(result);
        foreach (QpdfPath path in mode == "split" ? new[] { QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf") } : new[] { Output })
        { await Verify(host, path, revision == 4 ? "café•" : "", revision, mode == "empty" ? 2 : 1); }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HexTemplateOwnerCredentialsUseGlobalPasswordMode(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Template, "security-r4-pdfdoc.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value,
            ["copyEncryption"] = Template.Value, ["encryptionFilePassword"] = "6f776e6572e991", ["passwordMode"] = "hex-bytes", ["suppressPasswordRecovery"] = "" };
        QpdfExecutionResult result = await Execute(host, job, fields, [Input.Value, Output.Value, "--copy-encryption=" + Template.Value,
            "--encryption-file-password=6f776e6572e991", "--password-mode=hex-bytes", "--suppress-password-recovery"]);
        Completed(result); await Verify(host, Output, "café•", 4, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputFreeInspectionDoesNotOpenAnEncryptionTemplate(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["copyEncryption"] = "absent.pdf",
            ["encryptionFilePassword"] = "private-unused", ["showNpages"] = "" };
        QpdfExecutionResult result = await Execute(host, job, fields, [Input.Value, "--show-npages", "--copy-encryption=absent.pdf", "--encryption-file-password=private-unused"]);
        Completed(result); Assert.Equal("input.pdf: 1", Assert.Single(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageCount).Message);
        Assert.DoesNotContain(result.Diagnostics, item => item.Message.Contains("private-unused", StringComparison.Ordinal)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidTemplatePasswordsDoNotPublishOrLeak(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Template, "security-r4-pdfdoc.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value,
            ["copyEncryption"] = Template.Value, ["encryptionFilePassword"] = "private-invalid" };
        QpdfExecutionResult result = await Execute(host, job, fields, [Input.Value, Output.Value, "--copy-encryption=" + Template.Value, "--encryption-file-password=private-invalid"]);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("private-invalid", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("copy-remove")]
    [InlineData("remove-copy")]
    [InlineData("copy-new")]
    [InlineData("new-copy")]
    public async Task ArgvOutputPolicyUsesTheLastSetter(string order)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Template, "security-r4-pdfdoc.pdf");
        List<string> argv = [Input.Value, Output.Value, "--encryption-file-password=café•"];
        foreach (string policy in order.Split('-'))
        {
            if (policy == "copy") { argv.Add("--copy-encryption=" + Template.Value); }
            else if (policy == "remove") { argv.Add("--decrypt"); }
            else { argv.AddRange(["--encrypt", "new-reader", "new-owner", "256", "--"]); }
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None); Completed(result);
        await Verify(host, Output, order.EndsWith("copy", StringComparison.Ordinal) ? "café•" : order.EndsWith("new", StringComparison.Ordinal) ? "new-reader" : "",
            order.EndsWith("copy", StringComparison.Ordinal) ? 4 : order.EndsWith("new", StringComparison.Ordinal) ? 6 : null, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JobPoliciesFollowNativeLexicalKeyOrder(bool create)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["decrypt"] = "", ["copyEncryption"] = "absent.pdf" };
        if (create) { fields["encrypt"] = new Dictionary<string, object> { ["userPassword"] = "new-reader", ["ownerPassword"] = "new-owner", ["256bit"] = new { } }; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None); Completed(result);
        await Verify(host, Output, create ? "new-reader" : "", create ? 6 : null, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeterministicIdsRejectEitherSetterOrderBeforeOpening(bool copyFirst)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value,
            copyFirst ? "--copy-encryption=absent.pdf" : "--deterministic-id", copyFirst ? "--deterministic-id" : "--copy-encryption=absent.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code); Assert.Equal(0, result.Counters.HostOperations);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public async Task ForcedOldVersionsDoNotSilentlyDropTemplateProtection(int revision)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Template, $"security-r{revision}-empty.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--copy-encryption=" + Template.Value, "--force-version=1.4"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, Assert.Single(result.Diagnostics).Code); Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task LayeredJobsAcceptASeparateTemplatePasswordSetter()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Template, "security-r4-pdfdoc.pdf");
        host.AddInput(QpdfPath.From("job.json"), """{"inputFile":"input.pdf","outputFile":"output.pdf","copyEncryption":"template.pdf"}"""u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["--job-json-file=job.json", "--encryption-file-password=café•"]), CancellationToken.None);
        Completed(result); await Verify(host, Output, "café•", 4, 1);
    }

    [Fact]
    public async Task EveryEmptyPrimaryTemplateAdmissionRefusalRefundsBeforeSplitPublication()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Template, "security-r4-pdfdoc.pdf");
            using IMemoryScope caller = budget.CreateScope();
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new MemoryBudgetHost(host, caller), new(["--empty", Output.Value,
                "--pages", Input.Value, "1,1", "--", "--split-pages=1", "--copy-encryption=" + Template.Value, "--encryption-file-password=café•",
                "--linearize", "--object-streams=generate"]), CancellationToken.None);
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses)
            {
                Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.False(host.TryGetOutput(QpdfPath.From("output-1.pdf"), out _)); Assert.False(host.TryGetOutput(QpdfPath.From("output-2.pdf"), out _));
            }
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static async Task Verify(InMemoryQpdfHost host, QpdfPath path, string password, int? revision, int pages)
    {
        Assert.True(host.TryGetOutput(path, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy-" + path.Value); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None))
        { Assert.Equal(pages, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision); }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = QpdfPassword.FromText(password) }, QpdfLimits.Default, CancellationToken.None);
        Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed); Assert.Equal(0, host.OpenLeaseCount);
    }
    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, bool job, Dictionary<string, object> fields, List<string> argv)
        => job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None) : Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
    private static void Completed(QpdfExecutionResult result)
        => Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));

    private static void Add(InMemoryQpdfHost host, QpdfPath path, string file)
        => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
}
