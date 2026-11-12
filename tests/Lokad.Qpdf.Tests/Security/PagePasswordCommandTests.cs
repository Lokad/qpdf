using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class PagePasswordCommandTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Other = QpdfPath.From("other.pdf"), Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (bool split in new[] { false, true })
        foreach (string policy in new[] { "preserve", "remove", "new", "empty" }) { yield return [job, split, policy]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EachPageSourceAuthenticatesWithoutReplacingOutputPolicy(bool job, bool split, string policy)
    {
        using InMemoryQpdfHost host = new(); Fixtures(host);
        Dictionary<string, object> fields = new() { ["outputFile"] = Output.Value };
        List<string> argv = [];
        if (policy == "empty") { fields["empty"] = ""; argv.Add("--empty"); }
        else { fields["inputFile"] = Input.Value; fields["password"] = "reader"; argv.AddRange([Input.Value, "--password=reader"]); }
        argv.Add(Output.Value);
        string primary = policy == "empty" ? Input.Value : ".", password = policy == "empty" ? "reader" : "ignored-primary-page-password";
        fields["pages"] = new[] { new { file = primary, range = "1", password }, new { file = Other.Value, range = "1", password = "" }, new { file = Other.Value, range = "1", password = "" } };
        argv.AddRange(["--pages", primary, "--password=" + password, "1", "--file=" + Other.Value, "--range=1", "--password=", Other.Value, "--password=", "1", "--"]);
        if (split) { fields["splitPages"] = "1"; argv.Add("--split-pages=1"); }
        if (policy == "remove") { fields["decrypt"] = ""; argv.Add("--decrypt"); }
        if (policy == "new")
        {
            fields["encrypt"] = new Dictionary<string, object> { ["userPassword"] = "new-reader", ["ownerPassword"] = "new-owner", ["256bit"] = new { } };
            argv.AddRange(["--encrypt", "new-reader", "new-owner", "256", "--"]);
        }
        fields["objectStreams"] = "generate"; argv.Add("--object-streams=generate");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        QpdfPath[] outputs = split ? [QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf"), QpdfPath.From("output-3.pdf")] : [Output];
        foreach (QpdfPath path in outputs)
        {
            Assert.True(host.TryGetOutput(path, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy-" + path.Value); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
            string[] credentials = policy == "preserve" ? ["reader", "fixture-owner"] : policy == "new" ? ["new-reader", "new-owner"] : [""];
            foreach (string credential in credentials)
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(credential), QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(split ? 1 : 3, document.PageCount);
                if (policy is "empty" or "remove") { Assert.Null(document.Encryption); }
                else { Assert.Equal(policy == "new" ? 6 : 4, document.Encryption?.Revision); Assert.Equal(policy == "new" ? -4 : -3388, document.Encryption?.Permissions); }
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = QpdfPassword.FromText(credentials[0]) }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransformedInspectionAuthenticatesSecondaryPages(bool job)
    {
        using InMemoryQpdfHost host = new(); Fixtures(host);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"empty":"","pages":[{"file":"input.pdf","password":"reader","range":"1"},{"file":"other.pdf","password":"","range":"1,1"}],"showNpages":"","check":""}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["--empty", "--pages", Input.Value, "1", "--password=reader", Other.Value, "--password=", "1,1", "--", "--show-npages", "--check"]), CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageCount && item.Message == "--empty: 3");
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondaryHexPasswordsUseTheSharedInputDecoder(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-pdfdoc.pdf");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"empty":"","outputFile":"output.pdf","passwordMode":"hex-bytes","suppressPasswordRecovery":"","pages":[{"file":"input.pdf","password":"636166e980","range":"1"}]}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["--empty", Output.Value, "--password-mode=hex-bytes", "--suppress-password-recovery", "--pages", "--file=" + Input.Value, "--password=636166e980", "--range=1", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None);
        Assert.Equal(1, document.PageCount); Assert.Null(document.Encryption);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RepeatedSecondaryCredentialsMustBeConsistent(bool job, bool inspect)
    {
        using InMemoryQpdfHost host = new(); Fixtures(host);
        Dictionary<string, object> fields = new() { ["empty"] = "", ["pages"] = new[]
            { new { file = Input.Value, password = "reader" }, new { file = Input.Value, password = "private-conflict" } } };
        List<string> argv = ["--empty", "--pages", Input.Value, "--password=reader", "1", Input.Value, "--password=private-conflict", "1", "--"];
        if (inspect) { fields["showNpages"] = ""; argv.Add("--show-npages"); }
        else { fields["outputFile"] = Output.Value; argv.Add(Output.Value); }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("private-conflict", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongSecondaryPasswordDoesNotPublishOrLeak(bool inspect)
    {
        using InMemoryQpdfHost host = new(); Fixtures(host);
        List<string> argv = ["--empty", "--pages", Input.Value, "--password=private-invalid", "1", "--", inspect ? "--show-npages" : Output.Value];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("private-invalid", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EverySecondaryAdmissionRefusalRefundsBeforeOutput()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); Fixtures(host); using IMemoryScope caller = budget.CreateScope();
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new BudgetHost(host, caller), new(["--empty", Output.Value,
                "--pages", Input.Value, "--password=reader", "1", Other.Value, "--password=", "1", "--",
                "--encrypt", "new-reader", "new-owner", "256", "--", "--linearize", "--object-streams=generate"]), CancellationToken.None);
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses) { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded); Assert.False(host.TryGetOutput(Output, out _)); }
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    [Fact]
    public async Task PagePasswordsDoNotSubstituteForThePrimaryCredential()
    {
        using InMemoryQpdfHost host = new(); Fixtures(host);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([Input.Value, Output.Value, "--pages", ".", "--password=reader", "1", "--"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.PasswordRequired, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task APasswordSpecifiedOnceAppliesToEveryOccurrenceOfThatFile(bool job, bool late)
    {
        using InMemoryQpdfHost host = new(); Fixtures(host);
        Dictionary<string, object>[] pages = [new() { ["file"] = Input.Value, ["range"] = "1" }, new() { ["file"] = Input.Value, ["range"] = "1" }];
        pages[late ? 1 : 0]["password"] = "reader";
        List<string> argv = ["--empty", Output.Value, "--pages", Input.Value, "1"];
        if (!late) { argv.Add("--password=reader"); }
        argv.AddRange([Input.Value, "1"]); if (late) { argv.Add("--password=reader"); } argv.Add("--");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            JsonSerializer.Serialize(new Dictionary<string, object> { ["empty"] = "", ["outputFile"] = Output.Value, ["pages"] = pages }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(QpdfPath.From("copy"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None);
        Assert.Equal(2, document.PageCount); Assert.Null(document.Encryption);
    }

    [Fact]
    public async Task ASecondArgvPasswordSetterForOneEntryFailsWithoutEchoingEitherValue()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["--empty", Output.Value, "--pages", Input.Value, "--password=private-first", "--password=private-second", "1", "--"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("private-", result.Diagnostics[0].Message, StringComparison.Ordinal); Assert.Equal(0, result.Counters.HostOperations);
    }

    [Theory]
    [InlineData("before-file")]
    [InlineData("missing-equals")]
    [InlineData("wrong-type")]
    public async Task InvalidPagePasswordSyntaxRejectsBeforeHostAccess(string kind)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = kind == "wrong-type" ? await Qpdf.ExecuteJobAsync(host,
            """{"empty":"","outputFile":"output.pdf","pages":[{"file":"input.pdf","password":7}]}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(kind == "before-file"
                ? ["--empty", Output.Value, "--pages", "--password=private-value", Input.Value, "1", "--"]
                : ["--empty", Output.Value, "--pages", Input.Value, "--password", "private-value", "1", "--"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("private-value", result.Diagnostics[0].Message, StringComparison.Ordinal); Assert.Equal(0, result.Counters.HostOperations);
    }

    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
    private static void Fixtures(InMemoryQpdfHost host)
    { Add(host, Input, "security-r4-ascii.pdf"); Add(host, Other, "security-r6-empty.pdf"); }
    private static void Add(InMemoryQpdfHost host, QpdfPath path, string file)
        => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
}
