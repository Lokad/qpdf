using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonMetadataPolicyTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ImportsApplyIndependentInfoAndXmlPolicies(bool update, bool removeInfo, bool removeMetadata)
    {
        using InMemoryQpdfHost host = await Prepare("none");
        QpdfExecutionResult result = await Run(host, update, removeInfo, removeMetadata, "argv", [], CancellationToken.None);
        await Check(host, result, removeInfo, removeMetadata, "none");
    }

    [Theory]
    [InlineData(false, true, false, "job")]
    [InlineData(true, false, true, "job")]
    [InlineData(true, true, true, "mixed")]
    public async Task JobAndNestedSettingsProjectMetadataPolicies(bool update, bool removeInfo, bool removeMetadata, string route)
    {
        using InMemoryQpdfHost host = await Prepare("none");
        QpdfExecutionResult result = await Run(host, update, removeInfo, removeMetadata, route, [], CancellationToken.None);
        await Check(host, result, removeInfo, removeMetadata, "none");
    }

    [Theory]
    [InlineData("info")]
    [InlineData("xml")]
    [InlineData("catalog")]
    [InlineData("unreferenced")]
    public async Task RetiresOnlySupersededRolesWhilePreservingPrivateRoots(string alias)
    {
        using InMemoryQpdfHost host = await Prepare(alias);
        string[] flags = alias == "unreferenced" ? ["--preserve-unreferenced"] : [];
        QpdfExecutionResult result = await Run(host, false, true, true, "argv", flags, CancellationToken.None);
        await Check(host, result, true, true, alias);
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(true, "99 0 R")]
    [InlineData(true, "5 1 R")]
    public async Task DatePresenceUsesTheBoundJsonGraph(bool update, string date)
    {
        using InMemoryQpdfHost host = await Prepare("none");
        if (update)
        {
            byte[] patch = Encoding.UTF8.GetBytes("""{"qpdf":[{"jsonversion":2},{"obj:4 0 R":{"value":{"/Title":"u:PATCHED","/ModDate":DATE}}}]}"""
                .Replace("DATE", JsonSerializer.Serialize(date), StringComparison.Ordinal));
            host.AddInput(QpdfPath.From("patch"), patch, QpdfInputOwnership.Transfer);
        }
        else
        {
            host.AddInput(QpdfPath.From("json"), Model("none", null), QpdfInputOwnership.Transfer);
        }
        QpdfExecutionResult result = await Run(host, update, true, false, "argv", [], CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.False(document.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out _));
        Assert.False(Catalog(document).TryGet(PdfName.FromAscii("Metadata"), out _));
    }

    [Fact]
    public async Task FreshDeclaredDateRemainsIndependentOfTheOriginalDate()
    {
        using InMemoryQpdfHost host = await Prepare("info");
        host.AddInput(QpdfPath.From("patch"), """{"qpdf":[{"jsonversion":2},{"obj:4 0 R":{"value":{"/Title":"u:PATCHED","/ModDate":"10 9 R"}},"obj:10 9 R":{"value":"u:D:20250102030405Z"}}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, true, true, false, "argv", ["--preserve-unreferenced"], CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary info = Dictionary(document, Entry(document.Engine.Trailer, "Info"));
        PdfDictionary original = Dictionary(document, Entry(Catalog(document), "AuditInfo"));
        Assert.Equal(Entry(original, "ModDate"), Entry(info, "ModDate"));
        Assert.Equal("D:20250102030405Z", Text(document, Entry(info, "ModDate")));
        Assert.Equal("PATCHED", Text(document, Entry(original, "Title")));
        KeyValuePair<int, XrefEntry> oldDate = Assert.Single(document.Engine.XRefEntries, pair =>
            pair.Value.Kind != XrefEntryKind.Free && document.GetObject(pair.Key) is PdfByteString value
            && value.ValueSpan.SequenceEqual("D:20240102030405Z"u8));
        Assert.NotEqual(oldDate.Key, Assert.IsType<PdfReference>(Entry(info, "ModDate")).ObjectNumber);
    }

    [Theory]
    [InlineData("packed")]
    [InlineData("linear")]
    public async Task MetadataPolicyUsesTheSharedPhysicalWriter(string layout)
    {
        using InMemoryQpdfHost host = await Prepare("info");
        string[] flags = layout == "packed" ? ["--object-streams=generate"] : ["--linearize"];
        QpdfExecutionResult result = await Run(host, true, true, false, "argv", flags, CancellationToken.None);
        await Check(host, result, true, false, "info");
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("physical"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("physical"), CancellationToken.None);
        if (layout == "packed")
        {
            PdfReference info = Assert.IsType<PdfReference>(Entry(document.Engine.Trailer, "Info"));
            Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[info.ObjectNumber].Kind);
        }
        else
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(LinearizationStatus.Valid, LinearizationCheck.Inspect(document.Engine, 1, QpdfLimits.Default, governor, CancellationToken.None).Status);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Fact]
    public async Task SourcePackingBelongsToTheRetainedOriginalInfoRole()
    {
        using InMemoryQpdfHost host = await Prepare("info");
        QpdfExecutionResult packed = await Qpdf.ExecuteAsync(host,
            new(["input", "packed", "--object-streams=generate"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, packed.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("packed"), out byte[] source));
        host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        await using (QpdfDocument before = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        {
            PdfReference info = Assert.IsType<PdfReference>(Entry(before.Engine.Trailer, "Info"));
            Assert.Equal(XrefEntryKind.Compressed, before.Engine.XRefEntries[info.ObjectNumber].Kind);
        }
        QpdfExecutionResult result = await Run(host, true, true, false, "argv", [], CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfReference reduced = Assert.IsType<PdfReference>(Entry(document.Engine.Trailer, "Info"));
        PdfReference original = Assert.IsType<PdfReference>(Entry(Catalog(document), "AuditInfo"));
        Assert.NotEqual(original, reduced);
        Assert.Equal(XrefEntryKind.Uncompressed, document.Engine.XRefEntries[reduced.ObjectNumber].Kind);
        Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[original.ObjectNumber].Kind);
        Assert.Equal(1, Dictionary(document, reduced).Count);
        Assert.Equal("TITLE", Text(document, Entry(Dictionary(document, original), "Title")));
    }

    [Fact]
    public async Task RetirementCannotHideAnOversizedObjectTable()
    {
        using InMemoryQpdfHost host = await Prepare("none");
        // Nine declared objects, while every individual PDF dictionary/array fits.
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["json", "--json-input", "--remove-info", "output"]),
            new(QpdfLimits.Default with { MaxContainerSize = 8 }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataRemovalRetainsExplicitCreationAndPreservedProtection(bool update)
    {
        using InMemoryQpdfHost host = await Prepare("none");
        if (update)
        {
            await Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), new PdfPageSelectionOptions(1))], QpdfPath.From("encrypted"),
                new PdfPageMergeOptions
                {
                    OutputEncryption = QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("reader"), QpdfPassword.FromText("owner"), -4, true)
                }, CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("encrypted"), out byte[] encrypted));
            host.AddInput(QpdfPath.From("input"), encrypted, QpdfInputOwnership.Transfer);
        }
        string[] flags = update ? ["--password=reader"] : ["--encrypt", "reader", "owner", "256", "--"];
        QpdfExecutionResult result = await Run(host, update, true, false, "argv", flags, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"),
            QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None);
        Assert.NotNull(document.Encryption);
        PdfDictionary info = Dictionary(document, Entry(document.Engine.Trailer, "Info"));
        Assert.Equal(1, info.Count); Assert.Equal("D:20240102030405Z", Text(document, Entry(info, "ModDate")));
        Assert.False(Catalog(document).TryGet(PdfName.FromAscii("Metadata"), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ImportMetadataPreparationRefundsAndPreservesActualTargets(bool update)
        => PreparationAdmissionChecks.VerifyAsync(() => Prepare("catalog"),
            (host, token) => Run(host, update, true, true, "argv", [], token),
            QpdfPath.From("output"), 128, ["write", "cancel"]);

    private static async Task Check(InMemoryQpdfHost host, QpdfExecutionResult result, bool removeInfo, bool removeMetadata, string alias)
    {
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        PdfDictionary catalog = Catalog(document);
        bool keepXml = !removeInfo && !removeMetadata;
        Assert.Equal(keepXml, catalog.TryGet(PdfName.FromAscii("Metadata"), out _));
        PdfDictionary info = Dictionary(document, Entry(document.Engine.Trailer, "Info"));
        Assert.Equal(removeInfo ? 1 : 2, info.Count);
        Assert.Equal("D:20240102030405Z", Text(document, Entry(info, "ModDate")));
        if (alias == "info")
        {
            QpdfObject raw = Entry(catalog, "AuditInfo"); Assert.NotEqual(Entry(document.Engine.Trailer, "Info"), raw);
            PdfDictionary original = Dictionary(document, raw); Assert.Equal("TITLE", Text(document, Entry(original, "Title")));
            Assert.Equal(Entry(info, "ModDate"), Entry(original, "ModDate"));
        }
        if (alias == "catalog")
        {
            PdfReference originalReference = Assert.IsType<PdfReference>(Entry(catalog, "AuditCatalog"));
            Assert.NotEqual(Entry(document.Engine.Trailer, "Root"), originalReference);
            PdfDictionary original = Dictionary(document, originalReference);
            Assert.True(original.TryGet(PdfName.FromAscii("Metadata"), out _));
            Assert.Equal(originalReference, Entry(original, "AuditCatalog"));
        }
        if (alias == "xml")
        { Assert.Equal("<x/>", Encoding.ASCII.GetString(StreamObservation.Decode(Assert.IsType<PdfStream>(Resolve(document, Entry(catalog, "AuditXml")))))); }
        List<string> textValues = []; int streams = 0;
        foreach ((int number, XrefEntry entry) in document.Engine.XRefEntries)
        {
            if (entry.Kind == XrefEntryKind.Free) { continue; }
            QpdfObject value = document.GetObject(number);
            if (value is PdfDictionary dictionary && dictionary.TryGet(PdfName.FromAscii("Title"), out QpdfObject title))
            { textValues.Add(Text(document, title)); }
            if (value is PdfByteString text) { textValues.Add(Encoding.ASCII.GetString(text.Value)); }
            if (value is PdfStream stream && stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype)
                && subtype is PdfName name && name.DisplayText == "XML")
            { Assert.Equal("<x/>"u8.ToArray(), StreamObservation.Decode(stream)); streams++; }
        }
        Assert.Equal(alias == "unreferenced", textValues.Contains("UNRELATED"));
        Assert.DoesNotContain("UNRELATED-2", textValues);
        Assert.Equal(!removeInfo || alias is "info" or "unreferenced", textValues.Contains("TITLE"));
        bool retainedXml = keepXml || alias is "xml" or "catalog" or "unreferenced";
        Assert.Equal(retainedXml ? 1 : 0, streams);
        Assert.Equal(retainedXml, textValues.Contains("XML-CHILD"));
    }

    private static QpdfObject Entry(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return raw; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject raw)
        => raw is PdfReference reference ? document.GetObject(reference) : raw;
    private static PdfDictionary Dictionary(QpdfDocument document, QpdfObject raw) => Assert.IsType<PdfDictionary>(Resolve(document, raw));
    private static PdfDictionary Catalog(QpdfDocument document) => Dictionary(document, Entry(document.Engine.Trailer, "Root"));
    private static string Text(QpdfDocument document, QpdfObject raw) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Resolve(document, raw)).Value);

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, bool update, bool removeInfo, bool removeMetadata,
        string route, string[] extra, CancellationToken token)
    {
        List<string> argv = update ? ["input", "--update-from-json=patch"] : ["json", "--json-input"];
        Dictionary<string, object> job = new() { ["inputFile"] = update ? "input" : "json", ["outputFile"] = "output" };
        if (update) { job["updateFromJson"] = "patch"; } else { job["jsonInput"] = ""; }
        if (removeInfo) { argv.Add("--remove-info"); job["removeInfo"] = ""; }
        if (removeMetadata) { argv.Add("--remove-metadata"); job["removeMetadata"] = ""; }
        argv.AddRange(extra); argv.Add("output");
        if (route == "argv") { return Qpdf.ExecuteAsync(host, new(argv), token); }
        if (route == "job") { return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), token); }
        InMemoryQpdfHost storage = Assert.IsType<InMemoryQpdfHost>(host);
        storage.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(job), QpdfInputOwnership.Transfer);
        return Qpdf.ExecuteAsync(host, new(["--job-json-file=settings"]), token);
    }

    private static async Task<InMemoryQpdfHost> Prepare(string alias)
    {
        string catalog = "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R" + (alias switch
        { "info" => " /AuditInfo 4 0 R", "xml" => " /AuditXml 6 0 R", "catalog" => " /AuditCatalog 1 0 R", _ => "" }) + " >>";
        byte[] source = PdfTestDocument.Assemble([
            (1, catalog, null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Title (TITLE) /ModDate 5 0 R >>", null), (5, "(D:20240102030405Z)", null),
            (6, "<< /Type /Metadata /Subtype /XML /Private 9 0 R /Length 4 >>", "<x/>"u8.ToArray()),
            (7, "(UNRELATED)", null),
            (8, alias == "unreferenced" ? "<< /PrivateInfo 4 0 R /PrivateXml 6 0 R >>" : "(UNRELATED-2)", null),
            (9, "(XML-CHILD)", null)], 1, null, "1.4", "/Info 4 0 R");
        InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(source, QpdfPath.From("output"));
        host.AddInput(QpdfPath.From("json"), Model(alias, "5 0 R"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("patch"), """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static byte[] Model(string alias, object? date)
    {
        Dictionary<string, object> catalog = new() { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R", ["/Metadata"] = "6 0 R" };
        if (alias == "info") { catalog["/AuditInfo"] = "4 0 R"; }
        if (alias == "xml") { catalog["/AuditXml"] = "6 0 R"; }
        if (alias == "catalog") { catalog["/AuditCatalog"] = "1 0 R"; }
        Dictionary<string, object> objects = new()
        {
            ["obj:1 0 R"] = new { value = catalog },
            ["obj:2 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Count"] = 1, ["/Kids"] = new[] { "3 0 R" } } },
            ["obj:3 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/MediaBox"] = new[] { 0, 0, 100, 100 }, ["/Resources"] = new { } } },
            ["obj:4 0 R"] = new { value = new Dictionary<string, object?> { ["/Title"] = "u:TITLE", ["/ModDate"] = date } },
            ["obj:5 0 R"] = new { value = "u:D:20240102030405Z" },
            ["obj:6 0 R"] = new { stream = new { dict = new Dictionary<string, object> { ["/Type"] = "/Metadata", ["/Subtype"] = "/XML", ["/Private"] = "9 0 R" }, data = "PHgvPg==" } },
            ["obj:7 0 R"] = new { value = "u:UNRELATED" },
            ["obj:8 0 R"] = new { value = alias == "unreferenced" ? (object)new Dictionary<string, object> { ["/PrivateInfo"] = "4 0 R", ["/PrivateXml"] = "6 0 R" } : "u:UNRELATED-2" },
            ["obj:9 0 R"] = new { value = "u:XML-CHILD" },
            ["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = "1 0 R", ["/Info"] = "4 0 R", ["/Size"] = 10 } },
        };
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.4" }, objects } });
    }
}
