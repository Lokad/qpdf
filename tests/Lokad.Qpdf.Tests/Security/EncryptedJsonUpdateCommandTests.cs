using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptedJsonUpdateCommandTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Patch = QpdfPath.From("update.json"), Output = QpdfPath.From("output.pdf"), Template = QpdfPath.From("template.pdf");

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (int revision in new[] { 4, 6 })
        foreach (string policy in new[] { "preserve", "remove", "new", "template" }) { yield return [job, revision, policy]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CommandsApplyPlaintextPatchesWithTheChosenProtection(bool job, int revision, string policy)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, $"security-r{revision}-ascii.pdf"); Add(host, Template, "security-r4-empty.pdf");
        await AddPatch(host);
        QpdfExecutionResult result = await Execute(host, job, policy);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        string[] passwords = policy switch { "remove" => [""], "new" => ["new-reader", "new-owner"], "template" => ["", "fixture-owner"], _ => ["reader", "fixture-owner"] };
        await Verify(host, passwords, policy == "remove" ? null : policy == "new" ? 6 : policy == "template" ? 4 : revision,
            policy is "preserve");
    }

    [Theory]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    [InlineData(false, "hex")]
    [InlineData(true, "hex")]
    [InlineData(false, "recovery")]
    [InlineData(true, "recovery")]
    public async Task UpdatesSharePrimaryPasswordFilesModesAndRecovery(bool job, string mode)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r4-pdfdoc.pdf"); await AddPatch(host, QpdfPassword.FromText("café•"));
        host.AddInput(QpdfPath.From("password"), "café•\n"u8.ToArray(), QpdfInputOwnership.Copy);
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["updateFromJson"] = Patch.Value, ["decrypt"] = "" };
        List<string> argv = [Input.Value, Output.Value, "--update-from-json=" + Patch.Value, "--decrypt"];
        if (mode == "file") { fields["passwordFile"] = "password"; argv.Add("--password-file=password"); }
        else
        {
            string password = mode == "hex" ? "636166E980" : "café•";
            fields["password"] = password; argv.Add("--password=" + password);
            if (mode == "hex") { fields["passwordMode"] = "hex-bytes"; fields["suppressPasswordRecovery"] = ""; argv.AddRange(["--password-mode=hex-bytes", "--suppress-password-recovery"]); }
        }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await Verify(host, [""], null, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongUpdateCredentialsPreservePriorOutputAndStayPrivate(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); await AddPatch(host);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","outputFile":"output.pdf","password":"private-wrong","decrypt":"","updateFromJson":"update.json"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password=private-wrong", "--decrypt", "--update-from-json=" + Patch.Value]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("private-wrong", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleForcedVersionsCannotProducePlaintext(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); await AddPatch(host);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","outputFile":"output.pdf","password":"reader","updateFromJson":"update.json","forceVersion":"1.4"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password=reader", "--force-version=1.4", "--update-from-json=" + Patch.Value]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledUpdatesPublishNothingAndCloseInput(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); await AddPatch(host);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        QpdfExecutionResult result = await Execute(host, job, "new", cancellation.Token);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status); Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EveryCommandUpdateAdmissionRefusalRefundsBeforePublication()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); Add(host, Template, "security-r4-empty.pdf"); await AddPatch(host);
            using IMemoryScope caller = budget.CreateScope();
            QpdfExecutionResult result = await Execute(new BudgetHost(host, caller), true, "template");
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses) { Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code); Assert.False(host.TryGetOutput(Output, out _)); }
            Assert.Equal(0, host.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static Task<QpdfExecutionResult> Execute(IQpdfHost host, bool job, string policy)
        => Execute(host, job, policy, CancellationToken.None);
    private static Task<QpdfExecutionResult> Execute(IQpdfHost host, bool job, string policy, CancellationToken token)
    {
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["updateFromJson"] = Patch.Value, ["password"] = "reader" };
        List<string> argv = [Input.Value, Output.Value, "--password=reader", "--update-from-json=" + Patch.Value];
        if (policy == "remove") { fields["decrypt"] = ""; argv.Add("--decrypt"); }
        if (policy == "template") { fields["copyEncryption"] = Template.Value; fields["encryptionFilePassword"] = ""; argv.AddRange(["--copy-encryption=" + Template.Value, "--encryption-file-password="]); }
        if (policy == "new")
        {
            fields["encrypt"] = new Dictionary<string, object> { ["userPassword"] = "new-reader", ["ownerPassword"] = "new-owner", ["256bit"] = new { cleartextMetadata = "" } };
            argv.AddRange(["--encrypt", "new-reader", "new-owner", "256", "--cleartext-metadata", "--"]);
        }
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), token).AsTask() : Qpdf.ExecuteAsync(host, new(argv), token).AsTask();
    }
    private static Task AddPatch(InMemoryQpdfHost host) => AddPatch(host, QpdfPassword.FromText("fixture-owner"));
    private static async Task AddPatch(InMemoryQpdfHost host, QpdfPassword password)
    {
        await using QpdfDocument source = await QpdfDocument.OpenAsync(host, Input, password, QpdfLimits.Default, CancellationToken.None);
        PdfDictionary catalog = (PdfDictionary)source.GetObject(Ref(source.Engine.Trailer, "Root"));
        Dictionary<string, object> objects = new()
        {
            [$"obj:{Ref(source.Engine.Trailer, "Info")} 0 R"] = new { value = new Dictionary<string, object> { ["/Title"] = "u:Command update" } },
            [$"obj:{Ref(source.PageReferences[0].Page, "Contents")} 0 R"] = new { stream = new { dict = new { }, data = Convert.ToBase64String("BT (Command update) Tj ET\n"u8) } },
            [$"obj:{Ref(catalog, "Metadata")} 0 R"] = new { stream = new { dict = new Dictionary<string, object> { ["/Type"] = "/Metadata", ["/Subtype"] = "/XML" }, data = Convert.ToBase64String("<x:xmpmeta>UpdatedMeta</x:xmpmeta>"u8) } },
        };
        host.AddInput(Patch, JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2 }, objects } }), QpdfInputOwnership.Copy);
    }
    private static async Task Verify(InMemoryQpdfHost host, string[] passwords, int? revision, bool encryptMetadata)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        Assert.Equal(!encryptMetadata, Encoding.Latin1.GetString(bytes).Contains("UpdatedMeta", StringComparison.Ordinal));
        QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        foreach (string password in passwords)
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision);
            PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(Ref(document.Engine.Trailer, "Info")));
            Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title)); Assert.Equal("Command update"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
            Assert.Equal("BT (Command update) Tj ET\n"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(Ref(document.PageReferences[0].Page, "Contents"))).Data);
            PdfDictionary root = Assert.IsType<PdfDictionary>(document.GetObject(Ref(document.Engine.Trailer, "Root")));
            Assert.Equal("<x:xmpmeta>UpdatedMeta</x:xmpmeta>"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(Ref(root, "Metadata"))).Data);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
    private static int Ref(PdfDictionary dictionary, string name) => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) && value is PdfReference reference ? reference.ObjectNumber : 0;
    private static void Add(InMemoryQpdfHost host, QpdfPath path, string file) => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
}
