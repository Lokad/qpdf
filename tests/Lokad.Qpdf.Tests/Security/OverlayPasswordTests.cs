using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class OverlayPasswordTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Source = QpdfPath.From("source.pdf"), Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (bool underlay in new[] { false, true })
        foreach (string policy in new[] { "preserve", "remove", "new", "plain" }) { yield return [job, underlay, policy]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PlacementCredentialsKeepTheRequestedOutputProtection(bool job, bool underlay, string policy)
    {
        using InMemoryQpdfHost host = new(); int primaryRevision = job ? 4 : 6;
        Add(host, Input, policy == "plain" ? "meta-1page.pdf" : $"security-r{primaryRevision}-ascii.pdf");
        Add(host, Source, underlay ? "security-r4-pdfdoc.pdf" : "security-r6-empty.pdf");
        string role = underlay ? "underlay" : "overlay", sourcePassword = underlay ? "café•" : "";
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value,
            [role] = new { file = Source.Value, password = sourcePassword } };
        List<string> argv = [Input.Value, Output.Value, "--" + role, Source.Value, "--password=" + sourcePassword, "--"];
        if (policy != "plain") { fields["password"] = "reader"; argv.Add("--password=reader"); }
        if (policy == "remove") { fields["decrypt"] = ""; argv.Add("--decrypt"); }
        if (policy == "preserve") { fields["qdf"] = ""; argv.Add("--qdf"); }
        if (policy == "new")
        {
            fields["encrypt"] = new Dictionary<string, object> { ["userPassword"] = "new-reader", ["ownerPassword"] = "new-owner", ["256bit"] = new { } };
            fields["linearize"] = ""; fields["objectStreams"] = "generate";
            argv.AddRange(["--encrypt", "new-reader", "new-owner", "256", "--", "--linearize", "--object-streams=generate"]);
        }
        QpdfExecutionResult result = await Execute(host, job, fields, argv); Completed(result);
        await Verify(host, policy == "preserve" ? "fixture-owner" : policy == "new" ? "new-owner" : "",
            policy == "preserve" ? primaryRevision : policy == "new" ? 6 : null, 2);
    }

    [Theory]
    [InlineData(false, "reader")]
    [InlineData(true, "reader")]
    [InlineData(false, "fixture-owner")]
    [InlineData(true, "fixture-owner")]
    [InlineData(false, "private-invalid")]
    [InlineData(true, "private-invalid")]
    public async Task RepeatedSourcesAuthenticateEachDistinctCredential(bool job, string secondPassword)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Source, "security-r6-ascii.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value,
            ["underlay"] = new { file = Source.Value, password = "reader" }, ["overlay"] = new { file = Source.Value, password = secondPassword } };
        QpdfExecutionResult result = await Execute(host, job, fields, [Input.Value, Output.Value,
            "--underlay", Source.Value, "--password=reader", "--", "--overlay", Source.Value, "--password=" + secondPassword, "--"]);
        if (secondPassword == "private-invalid")
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code);
            Assert.DoesNotContain(secondPassword, result.Diagnostics[0].Message, StringComparison.Ordinal);
            Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        }
        else
        {
            Completed(result); await Verify(host, "", null, secondPassword == "reader" ? 2 : 3);
            Assert.True(host.TryGetOutput(QpdfPath.From("inspect.json"), out byte[] jsonBytes));
            using JsonDocument json = JsonDocument.Parse(jsonBytes); int invocations = 0;
            foreach (JsonProperty property in json.RootElement.GetProperty("qpdf")[1].EnumerateObject())
            {
                if (property.Value.TryGetProperty("stream", out JsonElement stream))
                { invocations += Encoding.ASCII.GetString(Convert.FromBase64String(stream.GetProperty("data").GetString() ?? "")).Split(" Do", StringSplitOptions.None).Length - 1; }
            }
            Assert.Equal(3, invocations);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HexOwnerPasswordsUseTheSharedDecoder(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Source, "security-r4-pdfdoc.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value,
            ["passwordMode"] = "hex-bytes", ["suppressPasswordRecovery"] = "", ["overlay"] = new { file = Source.Value, password = "6f776e6572e991" } };
        QpdfExecutionResult result = await Execute(host, job, fields, [Input.Value, Output.Value, "--password-mode=hex-bytes", "--suppress-password-recovery",
            "--overlay", Source.Value, "--password=6f776e6572e991", "--"]);
        Completed(result); await Verify(host, "", null, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransformedInspectionAuthenticatesPlacementSources(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); Add(host, Source, "security-r4-pdfdoc.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["password"] = "reader", ["check"] = "", ["showNpages"] = "",
            ["overlay"] = new { file = Source.Value, password = "café•" } };
        QpdfExecutionResult result = await Execute(host, job, fields, [Input.Value, "--password=reader", "--overlay", Source.Value, "--password=café•", "--", "--check", "--show-npages"]);
        Completed(result); Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageCount && item.Message == "input.pdf: 1");
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryPlacementRequiresItsOwnCredential(bool supplied)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf");
        List<string> argv = [Input.Value, Output.Value, "--password=reader", "--overlay", "."];
        if (supplied) { argv.Add("--password=fixture-owner"); }
        argv.Add("--"); QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        if (supplied) { Completed(result); await Verify(host, "reader", 6, 2); }
        else { Assert.Equal(QpdfDiagnosticCode.PasswordRequired, Assert.Single(result.Diagnostics).Code); Assert.False(host.TryGetOutput(Output, out _)); }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task AbsentCredentialsOpenEmptyPasswordPlacementSources()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Source, "security-r6-empty.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--overlay", Source.Value, "--"]), CancellationToken.None);
        Completed(result); await Verify(host, "", null, 2);
    }

    [Fact]
    public async Task EveryPlacementAdmissionRefusalRefundsBeforeEncryptedPublication()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); Add(host, Source, "security-r4-pdfdoc.pdf");
            using IMemoryScope caller = budget.CreateScope();
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new MemoryBudgetHost(host, caller), new([Input.Value, Output.Value, "--password=reader",
                "--overlay", Source.Value, "--password=café•", "--", "--encrypt", "new-reader", "new-owner", "256", "--", "--linearize", "--object-streams=generate"]), CancellationToken.None);
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses) { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded); Assert.False(host.TryGetOutput(Output, out _)); }
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static async Task Verify(InMemoryQpdfHost host, string password, int? revision, int expectedForms)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None))
        { Assert.Equal(1, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision); }
        QpdfExecutionResult exported = await Qpdf.ExecuteAsync(host, new([copy.Value, "--password=" + password, "--json", "--json-key=qpdf", "--json-stream-data=inline", "--", "inspect.json"]), CancellationToken.None);
        Completed(exported); Assert.True(host.TryGetOutput(QpdfPath.From("inspect.json"), out byte[] exportedBytes)); using JsonDocument json = JsonDocument.Parse(exportedBytes);
        int forms = 0;
        foreach (JsonProperty property in json.RootElement.GetProperty("qpdf")[1].EnumerateObject())
        {
            if (!property.Value.TryGetProperty("stream", out JsonElement stream)
                || !stream.GetProperty("dict").TryGetProperty("/Subtype", out JsonElement subtype) || subtype.GetString() != "/Form") { continue; }
            forms++; Assert.Contains("(Page)", Encoding.ASCII.GetString(Convert.FromBase64String(stream.GetProperty("data").GetString() ?? "")), StringComparison.Ordinal);
        }
        Assert.Equal(expectedForms, forms);
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
