using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class EncryptedJsonUpdateTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Patch = QpdfPath.From("update.json"), Output = QpdfPath.From("output.pdf"), Template = QpdfPath.From("template.pdf");
    private static readonly byte[] Content = "BT (Updated) Tj ET\n"u8.ToArray(), Metadata = "<x:xmpmeta>UpdatedMeta</x:xmpmeta>"u8.ToArray();

    public static IEnumerable<object[]> Cases()
    {
        foreach (int revision in new[] { 4, 6 })
        foreach (bool empty in new[] { false, true })
        foreach (string policy in new[] { "preserve", "remove", "new", "template" }) { yield return [revision, empty, policy]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TypedUpdatesProtectChangedStringsStreamsAndMetadata(int revision, bool empty, string policy)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, $"security-r{revision}-{(empty ? "empty" : "ascii")}.pdf");
        Add(host, Template, "security-r4-pdfdoc.pdf"); await AddPatch(host);
        QpdfOutputEncryption outputPolicy = policy switch
        {
            "remove" => QpdfOutputEncryption.Remove,
            "new" => QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner"), -4, false),
            "template" => QpdfOutputEncryption.CopyFrom(Template, QpdfPassword.FromText("café•")),
            _ => QpdfOutputEncryption.Preserve,
        };
        QpdfJsonImportResult result = await Qpdf.UpdatePdfFromJsonAsync(host, Input, Patch, Output,
            empty ? null : QpdfPassword.FromText("reader"), outputPolicy, CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        QpdfPassword?[] passwords = policy switch
        {
            "remove" => [null],
            "new" => [QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner")],
            "template" => [QpdfPassword.FromText("café•"), QpdfPassword.FromBytes(Convert.FromHexString("6f776e6572e991"))],
            _ => [empty ? null : QpdfPassword.FromText("reader"), QpdfPassword.FromText("fixture-owner")],
        };
        int? expectedRevision = policy == "remove" ? null : policy == "template" ? 4 : policy == "new" ? 6 : revision;
        bool encryptMetadata = policy == "template" || policy == "preserve" && !empty;
        await Verify(host, passwords, expectedRevision, encryptMetadata);
    }

    [Fact]
    public async Task ExistingUpdateOverloadPreservesEmptyPasswordProtection()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-empty.pdf"); await AddPatch(host);
        _ = await Qpdf.UpdatePdfFromJsonAsync(host, Input, Patch, Output, CancellationToken.None);
        await Verify(host, [null, QpdfPassword.FromText("fixture-owner")], 6, false);
    }

    [Fact]
    public async Task WrongInputPasswordPreservesPriorOutputWithoutLeaking()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); await AddPatch(host);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfSecurityException failure = await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.UpdatePdfFromJsonAsync(host, Input, Patch, Output,
            QpdfPassword.FromText("private-invalid"), QpdfOutputEncryption.Remove, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, failure.Reason); Assert.DoesNotContain("private-invalid", failure.Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReplacementTrailersCannotInjectEncryptionHandlers()
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf");
        host.AddInput(Patch, """{"qpdf":[{"jsonversion":2},{"trailer":{"value":{"/Root":"1 0 R","/Encrypt":"1 0 R"}}}]}"""u8.ToArray(), QpdfInputOwnership.Copy);
        await Assert.ThrowsAsync<QpdfCryptoException>(() => Qpdf.UpdatePdfFromJsonAsync(host, Input, Patch, Output,
            QpdfPassword.FromText("reader"), QpdfOutputEncryption.Preserve, CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EveryUpdateAdmissionRefusalRefundsBeforeEncryptedPublication()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); await AddPatch(host);
            using IMemoryScope caller = budget.CreateScope();
            Task<QpdfJsonImportResult> Update() => Qpdf.UpdatePdfFromJsonAsync(new BudgetHost(host, caller), Input, Patch, Output,
                QpdfPassword.FromText("reader"), QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("new-reader"), QpdfPassword.FromText("new-owner"), -4, false), CancellationToken.None).AsTask();
            if (refuses) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(Update); Assert.False(host.TryGetOutput(Output, out _)); }
            else { _ = await Update(); }
            Assert.Equal(0, host.OpenLeaseCount); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static async Task AddPatch(InMemoryQpdfHost host)
    {
        await using QpdfDocument input = await QpdfDocument.OpenAsync(host, Input, QpdfPassword.FromText("fixture-owner"), QpdfLimits.Default, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(input, Get(input.Engine.Trailer, "Root")));
        int info = Assert.IsType<PdfReference>(Get(input.Engine.Trailer, "Info")).ObjectNumber;
        int content = Assert.IsType<PdfReference>(Get(input.PageReferences[0].Page, "Contents")).ObjectNumber;
        int metadata = Assert.IsType<PdfReference>(Get(catalog, "Metadata")).ObjectNumber;
        Dictionary<string, object> objects = new()
        {
            [$"obj:{info} 0 R"] = new { value = new Dictionary<string, object> { ["/Title"] = "u:Updated title" } },
            [$"obj:{content} 0 R"] = new { stream = new { dict = new { }, data = Convert.ToBase64String(Content) } },
            [$"obj:{metadata} 0 R"] = new { stream = new { dict = new Dictionary<string, object> { ["/Type"] = "/Metadata", ["/Subtype"] = "/XML" }, data = Convert.ToBase64String(Metadata) } },
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2 }, objects } });
        host.AddInput(Patch, json, QpdfInputOwnership.Copy);
    }

    private static async Task Verify(InMemoryQpdfHost host, QpdfPassword?[] passwords, int? revision, bool encryptMetadata)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); string raw = Encoding.Latin1.GetString(bytes);
        if (encryptMetadata) { Assert.DoesNotContain("UpdatedMeta", raw, StringComparison.Ordinal); }
        else { Assert.Contains("UpdatedMeta", raw, StringComparison.Ordinal); }
        QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        foreach (QpdfPassword? password in passwords)
        {
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, password, QpdfLimits.Default, CancellationToken.None))
            {
                Assert.Equal(1, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision);
                if (revision is not null) { Assert.Equal(encryptMetadata, document.Encryption?.EncryptMetadata); }
                PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Info")));
                Assert.Equal("Updated title"u8.ToArray(), Assert.IsType<PdfByteString>(Get(info, "Title")).Value);
                Assert.Equal(Content, Assert.IsType<PdfStream>(Resolve(document, Get(document.PageReferences[0].Page, "Contents"))).Data);
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Root")));
                Assert.Equal(Metadata, Assert.IsType<PdfStream>(Resolve(document, Get(catalog, "Metadata"))).Data);
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = password }, QpdfLimits.Default, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
    private sealed class BudgetHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static void Add(InMemoryQpdfHost host, QpdfPath path, string file)
        => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
}
