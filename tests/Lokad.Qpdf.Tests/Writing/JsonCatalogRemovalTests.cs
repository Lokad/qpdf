using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonCatalogRemovalTests
{
    [Theory]
    [InlineData(false, "form")]
    [InlineData(true, "form")]
    [InlineData(false, "structure")]
    [InlineData(true, "structure")]
    [InlineData(false, "labels")]
    [InlineData(true, "labels")]
    public async Task ImportsRemoveOnlyTheRequestedCatalogRoles(bool update, string policy)
    {
        using InMemoryQpdfHost host = await Prepare("none");
        await Check(host, await Run(host, update, policy, "argv", [], CancellationToken.None), policy, "none");
    }

    [Theory]
    [InlineData(false, "form", "job")]
    [InlineData(true, "structure", "job")]
    [InlineData(false, "labels", "mixed")]
    public async Task JobAndNestedImportsProjectCatalogRemoval(bool update, string policy, string route)
    {
        using InMemoryQpdfHost host = await Prepare("none");
        await Check(host, await Run(host, update, policy, route, [], CancellationToken.None), policy, "none");
    }

    [Theory]
    [InlineData("form")]
    [InlineData("structure")]
    [InlineData("labels")]
    [InlineData("catalog")]
    [InlineData("unreferenced")]
    public async Task PrivateRootsKeepTheirOriginalCatalogData(string alias)
    {
        using InMemoryQpdfHost host = await Prepare(alias);
        await Check(host, await Run(host, false, "all", "argv", [], CancellationToken.None), "all", alias);
    }

    [Fact]
    public async Task CatalogAndMetadataPoliciesComposeWithoutRemovingWidgets()
    {
        using InMemoryQpdfHost host = await Prepare("none");
        await Check(host, await Run(host, false, "everything", "argv", [], CancellationToken.None), "everything", "none");
    }

    [Theory]
    [InlineData("packed")]
    [InlineData("linear")]
    public async Task SharedWritersRetainTheRemainingFieldGraph(string layout)
    {
        using InMemoryQpdfHost host = await Prepare("none");
        string[] extra = layout == "packed" ? ["--object-streams=generate"] : ["--linearize"];
        await Check(host, await Run(host, true, "all", "argv", extra, CancellationToken.None), "all", "none");
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("physical"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("physical"), CancellationToken.None);
        if (layout == "packed")
        {
            PdfReference field = Assert.IsType<PdfReference>(Entry(Widget(document), "Parent"));
            Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[field.ObjectNumber].Kind);
        }
        else
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(LinearizationStatus.Valid, LinearizationCheck.Inspect(document.Engine, 1, QpdfLimits.Default, governor, CancellationToken.None).Status);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Fact]
    public async Task RemovedRegistrationsDoNotBypassSignatureDisabling()
    {
        using InMemoryQpdfHost host = await Prepare("none");
        JsonObject graph = Graph("none"); JsonObject objects = Objects(graph);
        JsonObject field = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(objects["obj:8 0 R"])["value"]);
        field["/FT"] = "/Sig"; field.Remove("/V");
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(graph.ToJsonString()), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, false, "form", "argv", [], CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.False(Catalog(document).TryGet(PdfName.FromAscii("AcroForm"), out _));
        PdfDictionary annotation = Widget(document);
        Assert.Equal("Stamp", Assert.IsType<PdfName>(Entry(annotation, "Subtype")).DisplayText);
        Assert.False(annotation.TryGet(PdfName.FromAscii("FT"), out _));
        Assert.False(annotation.TryGet(PdfName.FromAscii("V"), out _));
    }

    [Fact]
    public async Task RemovalCannotHideTheOriginalObjectTableLimit()
    {
        using InMemoryQpdfHost host = await Prepare("none");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["json", "--json-input", "--remove-acroform", "--remove-structure", "--remove-page-labels", "output"]),
            new(QpdfLimits.Default with { MaxContainerSize = 10 }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CatalogPreparationRefundsAndPreservesTheActualTarget(bool update)
        => PreparationAdmissionChecks.VerifyAsync(() => Prepare("catalog"),
            (host, token) => Run(host, update, "all", "argv", [], token), QpdfPath.From("output"), 128, ["write", "cancel"]);

    private static async Task Check(InMemoryQpdfHost host, QpdfExecutionResult result, string policy, string alias)
    {
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(1, document.PageCount); PdfDictionary catalog = Catalog(document);
        bool all = policy is "all" or "everything";
        foreach ((string key, bool removed) in new[] { ("AcroForm", all || policy == "form"),
            ("StructTreeRoot", all || policy == "structure"), ("MarkInfo", all || policy == "structure"),
            ("PageLabels", all || policy == "labels"), ("Metadata", policy == "everything") })
        { Assert.Equal(!removed, catalog.TryGet(PdfName.FromAscii(key), out _)); }
        Assert.Equal(policy != "everything", document.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject info));
        if (policy != "everything") { Assert.Equal("TITLE", Text(document, Entry(Dictionary(document, info), "Title"))); }
        Assert.Equal("KEEP", Text(document, Entry(Dictionary(document, Entry(catalog, "Unrelated")), "Tag")));
        PdfDictionary widget = Widget(document);
        Assert.Equal("Widget", Assert.IsType<PdfName>(Entry(widget, "Subtype")).DisplayText);
        Assert.Equal("VALUE", Text(document, Entry(Dictionary(document, Entry(widget, "Parent")), "V")));
        if (alias == "catalog")
        {
            QpdfObject raw = Entry(catalog, "PrivateCatalog"); Assert.NotEqual(Entry(document.Engine.Trailer, "Root"), raw);
            PdfDictionary original = Dictionary(document, raw);
            Assert.Equal(raw, Entry(original, "PrivateCatalog"));
            foreach (string key in new[] { "AcroForm", "StructTreeRoot", "MarkInfo", "PageLabels" })
            { Assert.True(original.TryGet(PdfName.FromAscii(key), out _)); }
        }
        HashSet<string> tags = [];
        foreach ((int number, XrefEntry entry) in document.Engine.XRefEntries)
        {
            if (entry.Kind != XrefEntryKind.Free && document.GetObject(number) is PdfDictionary value
                && value.TryGet(PdfName.FromAscii("Tag"), out QpdfObject tag)) { tags.Add(Text(document, tag)); }
        }
        bool privateCatalog = alias is "catalog" or "unreferenced";
        Assert.Equal(!(all || policy == "form") || privateCatalog || alias == "form", tags.Contains("FORM"));
        Assert.Equal(!(all || policy == "structure") || privateCatalog || alias == "structure", tags.Contains("STRUCT"));
        Assert.Equal(!(all || policy == "labels") || privateCatalog || alias == "labels", tags.Contains("LABEL"));
    }

    private static PdfDictionary Widget(QpdfDocument document)
    {
        PdfArray annotations = Assert.IsType<PdfArray>(Resolve(document, Entry(document.PageReferences[0].Page, "Annots")));
        Assert.Equal(1, annotations.Count); return Dictionary(document, annotations[0]);
    }
    private static QpdfObject Entry(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return raw; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference) : raw;
    private static PdfDictionary Dictionary(QpdfDocument document, QpdfObject raw) => Assert.IsType<PdfDictionary>(Resolve(document, raw));
    private static PdfDictionary Catalog(QpdfDocument document) => Dictionary(document, Entry(document.Engine.Trailer, "Root"));
    private static string Text(QpdfDocument document, QpdfObject raw) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Resolve(document, raw)).Value);

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, bool update, string policy, string route, string[] extra, CancellationToken token)
    {
        Dictionary<string, object> job = new() { ["inputFile"] = update ? "input" : "json", ["outputFile"] = "output" };
        List<string> argv = update ? ["input", "--update-from-json=patch"] : ["json", "--json-input"];
        if (update) { job["updateFromJson"] = "patch"; } else { job["jsonInput"] = ""; }
        foreach ((string name, string key, string flag) in new[] { ("form", "removeAcroform", "remove-acroform"),
            ("structure", "removeStructure", "remove-structure"), ("labels", "removePageLabels", "remove-page-labels"),
            ("info", "removeInfo", "remove-info"), ("xml", "removeMetadata", "remove-metadata") })
        {
            if (policy != name && policy != "everything" && !(policy == "all" && name is "form" or "structure" or "labels")) { continue; }
            job[key] = true; argv.Add("--" + flag);
        }
        argv.AddRange(extra); argv.Add("output");
        if (route == "argv") { return Qpdf.ExecuteAsync(host, new(argv), token); }
        if (route == "job") { return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), token); }
        InMemoryQpdfHost storage = Assert.IsType<InMemoryQpdfHost>(host);
        storage.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(job), QpdfInputOwnership.Transfer);
        return Qpdf.ExecuteAsync(host, new(["--job-json-file=settings"]), token);
    }

    private static async Task<InMemoryQpdfHost> Prepare(string alias)
    {
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> /PageLabels 6 0 R /Metadata 10 0 R /Unrelated 11 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [7 0 R] >>", null),
            (4, "<< /Fields [8 0 R] /Tag (FORM) >>", null), (5, "<< /Type /StructTreeRoot /K [] /Tag (STRUCT) >>", null),
            (6, "<< /Nums [0 << /S /D /St 7 >>] /Tag (LABEL) >>", null),
            (7, "<< /Type /Annot /Subtype /Widget /Rect [10 10 30 30] /Parent 8 0 R /P 3 0 R >>", null),
            (8, "<< /FT /Tx /T (Field) /Kids [7 0 R] /V (VALUE) >>", null), (9, "<< /Title (TITLE) >>", null),
            (10, "<< /Type /Metadata /Subtype /XML /Length 4 >>", "<x/>"u8.ToArray()), (11, "<< /Tag (KEEP) >>", null)], 1, null, "1.4", "/Info 9 0 R");
        InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(source, QpdfPath.From("output"));
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(Graph(alias).ToJsonString()), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("patch"), """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static JsonObject Objects(JsonObject graph) => Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(graph["qpdf"])[1]);
    private static JsonObject Graph(string alias)
    {
        JsonObject graph = Assert.IsType<JsonObject>(JsonNode.Parse("""
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
            "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/AcroForm":"4 0 R","/StructTreeRoot":"5 0 R","/MarkInfo":{"/Marked":true},"/PageLabels":"6 0 R","/Metadata":"10 0 R","/Unrelated":"11 0 R"}},
            "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},
            "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{},"/Annots":["7 0 R"]}},
            "obj:4 0 R":{"value":{"/Fields":["8 0 R"],"/Tag":"u:FORM"}},
            "obj:5 0 R":{"value":{"/Type":"/StructTreeRoot","/K":[],"/Tag":"u:STRUCT"}},
            "obj:6 0 R":{"value":{"/Nums":[0,{"/S":"/D","/St":7}],"/Tag":"u:LABEL"}},
            "obj:7 0 R":{"value":{"/Type":"/Annot","/Subtype":"/Widget","/Rect":[10,10,30,30],"/Parent":"8 0 R","/P":"3 0 R"}},
            "obj:8 0 R":{"value":{"/FT":"/Tx","/T":"u:Field","/Kids":["7 0 R"],"/V":"u:VALUE"}},
            "obj:9 0 R":{"value":{"/Title":"u:TITLE"}},
            "obj:10 0 R":{"stream":{"dict":{"/Type":"/Metadata","/Subtype":"/XML"},"data":"PHgvPg=="}},
            "obj:11 0 R":{"value":{"/Tag":"u:KEEP"}},"trailer":{"value":{"/Root":"1 0 R","/Info":"9 0 R","/Size":12}}}]}
            """));
        JsonObject objects = Objects(graph);
        if (alias is "form" or "structure" or "labels")
        { Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(objects["obj:11 0 R"])["value"])["/Private"] = (alias == "form" ? "4" : alias == "structure" ? "5" : "6") + " 0 R"; }
        if (alias == "catalog")
        { Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(objects["obj:1 0 R"])["value"])["/PrivateCatalog"] = "1 0 R"; }
        if (alias == "unreferenced")
        {
            objects["obj:12 0 R"] = new JsonObject { ["value"] = new JsonObject { ["/PrivateForm"] = "4 0 R", ["/PrivateStructure"] = "5 0 R", ["/PrivateLabels"] = "6 0 R" } };
            Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(objects["trailer"])["value"])["/Size"] = 13;
        }
        return graph;
    }
}
