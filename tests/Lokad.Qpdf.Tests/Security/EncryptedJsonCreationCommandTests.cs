using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptedJsonCreationCommandTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.json"), Output = QpdfPath.From("output.pdf"), Template = QpdfPath.From("template.pdf");
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (string policy in new[] { "preserve", "remove", "new", "template" })
        foreach (bool metadata in new[] { false, true }) { yield return [job, policy, metadata]; }
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CommandsCreatePlaintextJsonWithTheSelectedProtection(bool job, string policy, bool metadata)
    {
        using InMemoryQpdfHost host = new(); Add(host, metadata);
        QpdfExecutionResult result = await Execute(host, job, policy, metadata);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        await Verify(host, policy, metadata);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HexModeAppliesToNewOutputPasswords(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, false);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.json","outputFile":"output.pdf","jsonInput":"","passwordMode":"hex-bytes","encrypt":{"userPassword":"726561646572","ownerPassword":"6f776e6572","256bit":{"cleartextMetadata":""}}}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--json-input", "--password-mode=hex-bytes", "--encrypt", "726561646572", "6f776e6572", "256", "--cleartext-metadata", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await Verify(host, "new", false);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PrimaryPasswordFilesAreReadWithoutAuthenticatingPlaintextJson(bool job, bool missing)
    {
        using InMemoryQpdfHost host = new(); Add(host, false);
        if (!missing) { host.AddInput(QpdfPath.From("password"), "ignored\n"u8.ToArray(), QpdfInputOwnership.Copy); }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.json","outputFile":"output.pdf","jsonInput":"","passwordFile":"password","suppressPasswordRecovery":""}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--json-input", "--password-file=password", "--suppress-password-recovery"]), CancellationToken.None);
        Assert.Equal(missing ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
        if (missing) { Assert.False(host.TryGetOutput(Output, out _)); } else { await Verify(host, "preserve", false); }
        Assert.Equal(0, host.OpenLeaseCount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleForcedVersionsCannotDiscardRequestedProtection(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, true);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.json","outputFile":"output.pdf","jsonInput":"","copyEncryption":"template.pdf","encryptionFilePassword":"reader","forceVersion":"1.4"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--json-input", "--copy-encryption=template.pdf", "--encryption-file-password=reader", "--force-version=1.4"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongTemplateCredentialsPreservePriorOutput(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, true);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.json","outputFile":"output.pdf","jsonInput":"","copyEncryption":"template.pdf","encryptionFilePassword":"private-wrong"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--json-input", "--copy-encryption=template.pdf", "--encryption-file-password=private-wrong"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("private-wrong", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }
    [Fact]
    public async Task CommandCreationTranslatesEarlyAndLateAdmissionRefusals()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        // Typed creation owns the shared creator/key/publication sweep.
        // This wrapper owns diagnostic translation at entry and late publication.
        Assert.True(baseline.GrowthCalls > 1);
        foreach (int growth in new[] { 1, baseline.GrowthCalls }) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); Add(host, false); using IMemoryScope caller = budget.CreateScope();
            QpdfExecutionResult result = await Execute(new BudgetHost(host, caller), true, "new", false);
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses) { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded); Assert.False(host.TryGetOutput(Output, out _)); }
            Assert.Equal(0, host.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }
    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, bool job, string policy, bool metadata)
    {
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["jsonInput"] = "", ["password"] = "ignored" };
        List<string> argv = [Input.Value, Output.Value, "--json-input", "--password=ignored"];
        if (policy == "remove") { fields["decrypt"] = ""; argv.Add("--decrypt"); }
        if (policy == "new")
        {
            Dictionary<string, object> options = new(); if (!metadata) { options["cleartextMetadata"] = ""; }
            fields["encrypt"] = new Dictionary<string, object> { ["userPassword"] = "reader", ["ownerPassword"] = "owner", ["256bit"] = options };
            argv.AddRange(["--encrypt", "reader", "owner", "256"]); if (!metadata) { argv.Add("--cleartext-metadata"); } argv.Add("--");
        }
        if (policy == "template")
        {
            fields["copyEncryption"] = Template.Value; fields["encryptionFilePassword"] = "fixture-owner";
            argv.AddRange(["--copy-encryption=" + Template.Value, "--encryption-file-password=fixture-owner"]);
        }
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None) : Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
    }
    private static async Task Verify(InMemoryQpdfHost host, string policy, bool metadata)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); bool encrypted = policy is "new" or "template";
        Assert.Equal(!encrypted || !metadata, Encoding.Latin1.GetString(bytes).Contains("<Meta>Created</Meta>", StringComparison.Ordinal));
        QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        string[] passwords = policy == "new" ? ["reader", "owner"] : policy == "template" ? [metadata ? "reader" : "", "fixture-owner"] : [""];
        foreach (string password in passwords)
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount); Assert.Equal(encrypted ? policy == "new" ? 6 : 4 : (int?)null, document.Encryption?.Revision);
            if (encrypted) { Assert.Equal(metadata, document.Encryption?.EncryptMetadata); }
            PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(Ref(document.Engine.Trailer, "Info")));
            Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title)); Assert.Equal("Created title"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
            Assert.Equal("BT (Page) Tj ET"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(Ref(document.PageReferences[0].Page, "Contents"))).Data);
            PdfDictionary root = Assert.IsType<PdfDictionary>(document.GetObject(Ref(document.Engine.Trailer, "Root")));
            Assert.Equal("<Meta>Created</Meta>"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(Ref(root, "Metadata"))).Data);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
    private static void Add(InMemoryQpdfHost host, bool metadata)
    {
        host.AddInput(Input, Encoding.UTF8.GetBytes(EncryptedJsonCreationTests.Source), QpdfInputOwnership.Copy);
        host.AddInput(Template, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", metadata ? "security-r4-ascii.pdf" : "security-r4-empty.pdf")), QpdfInputOwnership.Copy);
    }
    private static int Ref(PdfDictionary dictionary, string name) => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) && value is PdfReference reference ? reference.ObjectNumber : 0;
    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
}
