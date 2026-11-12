using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptedJsonCreationTests
{
    private static readonly QpdfPath Json = QpdfPath.From("input.json"), Output = QpdfPath.From("output.pdf"), Template = QpdfPath.From("template.pdf");
    internal const string Source = """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Metadata":"10 0 R"}},
          "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},
          "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,612,792],"/Contents":"9 0 R","/Resources":{}}},
          "obj:9 0 R":{"stream":{"dict":{},"data":"QlQgKFBhZ2UpIFRqIEVU"}},
          "obj:10 0 R":{"stream":{"dict":{"/Type":"/Metadata","/Subtype":"/XML"},"data":"PE1ldGE+Q3JlYXRlZDwvTWV0YT4="}},
          "obj:21 0 R":{"value":{"/Title":"u:Created title"}},
          "trailer":{"value":{"/Root":"1 0 R","/Info":"21 0 R"}}
        }]}
        """;

    public static IEnumerable<object[]> Cases()
    {
        foreach (string policy in new[] { "preserve", "remove", "new", "template4", "template6" })
        foreach (bool metadata in new[] { false, true }) { yield return [policy, metadata]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TypedCreationProtectsStringsStreamsAndFinalCatalogMetadata(string policy, bool metadata)
    {
        using InMemoryQpdfHost host = new(); AddJson(host, Source);
        int templateRevision = policy == "template4" ? 4 : 6;
        host.AddInput(Template, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"security-r{templateRevision}-{(metadata ? "ascii" : "empty")}.pdf")), QpdfInputOwnership.Copy);
        QpdfOutputEncryption security = policy switch
        {
            "remove" => QpdfOutputEncryption.Remove,
            "new" => NewProtection(metadata),
            "template4" or "template6" => QpdfOutputEncryption.CopyFrom(Template, QpdfPassword.FromText("fixture-owner")),
            _ => QpdfOutputEncryption.Preserve,
        };
        QpdfJsonImportResult result = await Qpdf.CreatePdfFromJsonAsync(host, Json, Output, security, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        bool protectedOutput = policy is not ("remove" or "preserve");
        Assert.Equal(!protectedOutput || !metadata, Encoding.Latin1.GetString(bytes).Contains("<Meta>Created</Meta>", StringComparison.Ordinal));
        QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        string[] passwords = policy == "new" ? ["reader", "owner"] : protectedOutput ? [metadata ? "reader" : "", "fixture-owner"] : [""];
        foreach (string password in passwords)
        {
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None))
            {
                Assert.Equal(1, document.PageCount);
                Assert.Equal(protectedOutput ? policy == "template4" ? 4 : 6 : (int?)null, document.Encryption?.Revision);
                if (protectedOutput) { Assert.Equal(metadata, document.Encryption?.EncryptMetadata); }
                PdfDictionary info = Assert.IsType<PdfDictionary>(document.GetObject(Ref(document.Engine.Trailer, "Info")));
                Assert.True(info.TryGet(PdfName.FromAscii("Title"), out QpdfObject title)); Assert.Equal("Created title"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
                Assert.Equal("BT (Page) Tj ET"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(Ref(document.PageReferences[0].Page, "Contents"))).Data);
                PdfDictionary root = Assert.IsType<PdfDictionary>(document.GetObject(Ref(document.Engine.Trailer, "Root")));
                Assert.Equal("<Meta>Created</Meta>"u8.ToArray(), Assert.IsType<PdfStream>(document.GetObject(Ref(root, "Metadata"))).Data);
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = QpdfPassword.FromText(password) }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task DeclaredJsonEncryptionRequiresAnExplicitPolicy()
    {
        using InMemoryQpdfHost host = new(); AddJson(host, Source.Replace("\"/Info\":\"21 0 R\"", "\"/Info\":\"21 0 R\",\"/Encrypt\":\"21 0 R\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<QpdfCryptoException>(() => Qpdf.CreatePdfFromJsonAsync(host, Json, Output, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task InvalidTemplateCredentialsCannotFallBackToPlaintext()
    {
        using InMemoryQpdfHost host = new(); AddJson(host, Source);
        host.AddInput(Template, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-ascii.pdf")), QpdfInputOwnership.Copy);
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.CreatePdfFromJsonAsync(host, Json, Output,
            QpdfOutputEncryption.CopyFrom(Template, QpdfPassword.FromText("wrong")), QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, error.Reason); Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EveryProtectedCreationAdmissionRefusalRefundsBeforePublication()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); AddJson(host, Source); using IMemoryScope caller = budget.CreateScope();
            Task<QpdfJsonImportResult> Create() => Qpdf.CreatePdfFromJsonAsync(new BudgetHost(host, caller), Json, Output, NewProtection(false), QpdfLimits.Default, CancellationToken.None).AsTask();
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Create); Assert.False(host.TryGetOutput(Output, out _)); } else { _ = await Create(); }
            Assert.Equal(0, host.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }
    private static QpdfOutputEncryption NewProtection(bool metadata) => QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("reader"), QpdfPassword.FromText("owner"), -4, metadata);
    private static void AddJson(InMemoryQpdfHost host, string json) => host.AddInput(Json, Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
    private static int Ref(PdfDictionary dictionary, string name) => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) && value is PdfReference reference ? reference.ObjectNumber : 0;
    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
}
