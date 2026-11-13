using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptedJsonRoundTripTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Json = QpdfPath.From("full.json"), Output = QpdfPath.From("output.pdf"), Template = QpdfPath.From("template.pdf");
    public static IEnumerable<object[]> Cases()
    {
        foreach (int revision in new[] { 4, 6 })
        foreach (bool create in new[] { false, true })
        foreach (string policy in new[] { "preserve", "remove", "new", "template" })
        { if (!create || policy != "preserve") { yield return ["typed", revision, create, policy]; } }
        // Command creation/update policy tables own parser wiring. These pairs
        // specifically prove wiring from complete authenticated exports.
        yield return ["argv", 4, false, "preserve"];
        yield return ["argv", 6, true, "new"];
        yield return ["job", 6, false, "preserve"];
        yield return ["job", 4, true, "template"];
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FullAuthenticatedExportsRoundTripThroughExplicitOutputPolicies(string route, int revision, bool create, string policy)
    {
        using InMemoryQpdfHost host = new(); await Prepare(host, revision);
        await Execute(host, route, revision, create, policy);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        string[] passwords = policy switch { "remove" => [""], "new" => ["new-reader", "new-owner"], "template" => ["reader", "fixture-owner"], _ => [revision == 4 ? "reader" : "", "fixture-owner"] };
        int? expectedRevision = policy == "remove" ? null : policy == "new" ? 6 : policy == "template" ? 4 : revision;
        bool metadata = policy == "template" || policy == "preserve" && revision == 4;
        foreach (string password in passwords)
        {
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None))
            {
                Assert.Equal(1, document.PageCount); Assert.Equal(expectedRevision, document.Encryption?.Revision);
                if (expectedRevision.HasValue) { Assert.Equal(metadata, document.Encryption?.EncryptMetadata); }
                PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(Ref(document.Engine.Trailer, "Info")));
                Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title)); Assert.Equal("TitleText"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
                AssertStream(Assert.IsType<PdfStream>(document.GetObject(Ref(document.PageReferences[0].Page, "Contents"))), document.Engine, "BT (Page) Tj ET");
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(Ref(document.Engine.Trailer, "Root")));
                AssertStream(Assert.IsType<PdfStream>(document.GetObject(Ref(catalog, "Metadata"))), document.Engine, "<x:xmpmeta>Meta</x:xmpmeta>");
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = QpdfPassword.FromText(password) }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
    [Theory]
    [InlineData("typed", 4)]
    [InlineData("typed", 6)]
    [InlineData("argv", 4)]
    [InlineData("job", 6)]
    public async Task CreationCannotInferProtectionFromUnauthenticatedJson(string route, int revision)
    {
        using InMemoryQpdfHost host = new(); await Prepare(host, revision);
        if (route == "typed") { await Assert.ThrowsAsync<QpdfCryptoException>(() => Qpdf.CreatePdfFromJsonAsync(host, Json, Output, CancellationToken.None).AsTask()); }
        else
        {
            QpdfExecutionResult result = route == "job" ? await Qpdf.ExecuteJobAsync(host,
                """{"inputFile":"full.json","outputFile":"output.pdf","jsonInput":""}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new([Json.Value, "--json-input", "--", Output.Value]), CancellationToken.None);
            Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(result.Diagnostics).Code);
        }
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }
    [Theory]
    [InlineData("reference")]
    [InlineData("permissions")]
    [InlineData("filter")]
    public async Task PreservationRejectsEditedHandlersBeforePublication(string change)
    {
        using InMemoryQpdfHost host = new(); await Prepare(host, 4);
        Assert.True(host.TryGetOutput(Json, out byte[] json)); JsonNode root = JsonNode.Parse(json) ?? throw new InvalidOperationException();
        JsonObject objects = root["qpdf"]?[1]?.AsObject() ?? throw new InvalidOperationException();
        JsonObject trailer = objects["trailer"]?["value"]?.AsObject() ?? throw new InvalidOperationException();
        string reference = trailer["/Encrypt"]?.GetValue<string>() ?? throw new InvalidOperationException();
        if (change == "reference") { trailer["/Encrypt"] = "1 0 R"; }
        else
        {
            JsonObject encryption = objects["obj:" + reference]?["value"]?.AsObject() ?? throw new InvalidOperationException();
            if (change == "permissions") { encryption["/P"] = -4; } else { encryption["/StrF"] = "/Identity"; }
        }
        host.AddInput(Json, JsonSerializer.SerializeToUtf8Bytes(root), QpdfInputOwnership.Copy);
        await Assert.ThrowsAsync<QpdfCryptoException>(() => Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output,
            QpdfPassword.FromText("reader"), QpdfOutputEncryption.Preserve, CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
        // An explicit policy owns security independently of those JSON objects.
        await Execute(host, "typed", 4, false, "remove");
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("plain.pdf"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("plain.pdf"), CancellationToken.None);
        Assert.Null(document.Encryption); Assert.Equal(1, document.PageCount);
    }
    [Fact]
    public async Task EveryFullUpdateAdmissionRefusalRefundsBeforePublication()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); await Prepare(host, 4); using IMemoryScope caller = budget.CreateScope();
            Task<QpdfJsonImportResult> Update() => Qpdf.UpdatePdfFromJsonAsync(new BudgetHost(host, caller), Input, Json, Output,
                QpdfPassword.FromText("reader"), QpdfOutputEncryption.Preserve, CancellationToken.None).AsTask();
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Update); Assert.False(host.TryGetOutput(Output, out _)); } else { _ = await Update(); }
            Assert.Equal(0, host.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }
    private static async Task Execute(InMemoryQpdfHost host, string route, int revision, bool create, string policy)
    {
        if (route == "typed")
        {
            QpdfOutputEncryption protection = policy switch
            {
                "remove" => QpdfOutputEncryption.Remove,
                "new" => QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner"), -4, false),
                "template" => QpdfOutputEncryption.CopyFrom(Template, QpdfPassword.FromText("reader")),
                _ => QpdfOutputEncryption.Preserve,
            };
            if (create) { _ = await Qpdf.CreatePdfFromJsonAsync(host, Json, Output, protection, QpdfLimits.Default, CancellationToken.None); }
            else { _ = await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, revision == 4 ? QpdfPassword.FromText("reader") : null, protection, CancellationToken.None); }
            return;
        }
        Dictionary<string, object> fields = new() { ["inputFile"] = create ? Json.Value : Input.Value, ["outputFile"] = Output.Value };
        List<string> argv = [create ? Json.Value : Input.Value, Output.Value];
        if (create) { fields["jsonInput"] = ""; argv.Add("--json-input"); }
        else { fields["updateFromJson"] = Json.Value; fields["password"] = revision == 4 ? "reader" : ""; argv.AddRange(["--update-from-json=" + Json.Value, "--password=" + (revision == 4 ? "reader" : "")]); }
        if (policy == "remove") { fields["decrypt"] = ""; argv.Add("--decrypt"); }
        if (policy == "template") { fields["copyEncryption"] = Template.Value; fields["encryptionFilePassword"] = "reader"; argv.AddRange(["--copy-encryption=" + Template.Value, "--encryption-file-password=reader"]); }
        if (policy == "new")
        {
            fields["encrypt"] = new Dictionary<string, object> { ["userPassword"] = "new-reader", ["ownerPassword"] = "new-owner", ["256bit"] = new { cleartextMetadata = "" } };
            argv.AddRange(["--encrypt", "new-reader", "new-owner", "256", "--cleartext-metadata", "--"]);
        }
        QpdfExecutionResult result = route == "job" ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None) : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
    }
    private static async Task Prepare(InMemoryQpdfHost host, int revision)
    {
        host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", revision == 4 ? "security-r4-ascii.pdf" : "security-r6-empty.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(Template, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r4-ascii.pdf")), QpdfInputOwnership.Copy);
        _ = await Qpdf.GetDocumentJsonAsync(host, Input, Json, new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], true) { Password = QpdfPassword.FromText("fixture-owner") }, CancellationToken.None);
        Assert.True(host.TryGetOutput(Json, out byte[] json)); host.AddInput(Json, json, QpdfInputOwnership.Copy);
    }
    private static void AssertStream(PdfStream stream, IIndirectResolver resolver, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using FilterPlan plan = FilterChain.ResolveScoped(stream.Dictionary, resolver, governor, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.Contains(expected, Encoding.ASCII.GetString(FilterChain.Decode(stream.Data, plan, QpdfLimits.Default, governor, memory)), StringComparison.Ordinal);
    }
    private static int Ref(PdfDictionary dictionary, string name) => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) && value is PdfReference reference ? reference.ObjectNumber : 0;
    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
}
