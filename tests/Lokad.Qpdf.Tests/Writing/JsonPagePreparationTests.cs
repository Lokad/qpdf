using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonPagePreparationTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Json = QpdfPath.From("json"), Output = QpdfPath.From("output");
    private const string Leaf = "/Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Tag (old)";
    private const string Ancestor = "/CropBox [0 0 90 180] /MediaBox [0 0 100 200] /Resources << /Marker (old) >> /Rotate 90";

    private static byte[] Source(string shape, int generation, string leaf, string ancestor, string catalog, string trailer)
    {
        string page = "<< " + leaf + " >>", reference = "3 " + generation + " R";
        string kids = shape == "direct" ? page : shape == "repeated" ? reference + " " + reference : reference;
        List<(int, string, byte[]?)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R " + catalog + " >>", null),
            (2, "<< /Type /Pages /Count " + (shape == "repeated" ? "2" : "1") + " /Kids [" + kids + "] " + ancestor + " >>", null)];
        if (shape != "direct") { objects.Add((3, page, null)); }
        return PdfTestDocument.Assemble(objects, 1, new Dictionary<int, int> { [3] = generation }, "1.7", trailer);
    }

    private static byte[] Patch(string metadata, Dictionary<string, object> objects)
        => Encoding.UTF8.GetBytes("{\"qpdf\":[{\"jsonversion\":2" + (metadata.Length == 0 ? "" : "," + metadata)
            + "}," + JsonSerializer.Serialize(objects) + "]}");

    private static object Page(string tag) => new { value = new Dictionary<string, object>
        { ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/MediaBox"] = new[] { 0, 0, 100, 200 },
          ["/Resources"] = new Dictionary<string, object>(), ["/Tag"] = "u:" + tag } };

    private static InMemoryQpdfHost Host(byte[] source, byte[] patch)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Input, source, QpdfInputOwnership.Copy); host.AddInput(Json, patch, QpdfInputOwnership.Copy);
        return host;
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference) : value;
    private static QpdfObject Entry(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
    private static string Text(QpdfObject value) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(value).ValueMemory.Span);

    [Theory]
    [InlineData("direct", false, 1)]
    [InlineData("repeated", true, 2)]
    public async Task ExportedRotationUsesInheritedValuesAndVisiblePageIdentities(string shape, bool job, int count)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Source(shape, 17, Leaf, Ancestor, "", ""), QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = job ? await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"input","json":"2","jsonKey":["qpdf","pages"],"rotate":["+90:2"]}
            """, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", "--rotate=+90:1"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, report.ExitCode);
        string text = Assert.IsType<string>(report.OutputText);
        using JsonDocument json = JsonDocument.Parse(text);
        JsonElement model = json.RootElement.GetProperty("qpdf"), table = model[1];
        Assert.True(model[0].GetProperty("calledgetallpages").GetBoolean());
        JsonElement pages = table.GetProperty("obj:2 0 R").GetProperty("value");
        string?[] kids = pages.GetProperty("/Kids").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(count, kids.Length);
        Assert.Equal(count, kids.Distinct().Count());
        Assert.Equal(90, pages.GetProperty("/Rotate").GetInt32());
        Assert.Equal(180, table.GetProperty("obj:" + kids[^1]).GetProperty("value").GetProperty("/Rotate").GetInt32());
        if (job)
        {
            Assert.Equal("3 17 R", kids[0]);
            Assert.False(table.GetProperty("obj:3 17 R").GetProperty("value").TryGetProperty("/Rotate", out _));
            Assert.Equal(kids, json.RootElement.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("object").GetString()));
        }
        host.AddInput(Json, Encoding.UTF8.GetBytes(text), QpdfInputOwnership.Transfer);
        await Qpdf.CreatePdfFromJsonAsync(host, Json, Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] recreated));
        host.AddInput(QpdfPath.From("recreated"), recreated, QpdfInputOwnership.Transfer);
        await using (QpdfDocument reopened = await QpdfDocument.OpenAsync(host, QpdfPath.From("recreated"), CancellationToken.None))
        {
            Assert.Equal(count, reopened.PageCount);
            Assert.Equal(180L, Assert.IsType<PdfInteger>(Entry(reopened.PageReferences[^1].Page, "Rotate")).Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> PageCases()
    {
        foreach (string shape in new[] { "ordinary", "direct", "repeated" })
        foreach (string flag in new[] { "omitted", "false", "true" }) { yield return [shape, flag]; }
    }

    [Theory]
    [MemberData(nameof(PageCases))]
    public async Task DeclaredTraversalBindsEditsToTheVisiblePage(string shape, string flag)
    {
        int edited = shape == "repeated" ? 4 : 3;
        string meta = flag == "omitted" ? "" : "\"calledgetallpages\":" + flag;
        using InMemoryQpdfHost host = Host(Source(shape, 0, Leaf, "", "", ""), Patch(meta, new() { ["obj:" + edited + " 0 R"] = Page("patched") }));
        QpdfJsonImportResult result = await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None);
        Assert.Equal(shape == "repeated" ? 2 : 1, result.PageCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        string[] tags = document.PageReferences.Select(page => Text(Entry(page.Page, "Tag"))).ToArray();
        bool visible = shape == "ordinary" || flag == "true";
        Assert.Equal(shape == "repeated" ? new[] { "old", visible ? "patched" : "old" } : [visible ? "patched" : "old"], tags);
        if (flag == "true")
        {
            Assert.All(document.PageReferences, page => Assert.NotNull(page.Source));
            Assert.Equal(document.PageCount, document.PageReferences.Select(page => page.Source).Distinct().Count());
        }
        await document.DisposeAsync();
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("false")]
    [InlineData("true")]
    public async Task PushingBindsInheritedResourcesBeforeApplyingPatches(string flag)
    {
        string meta = flag == "omitted" ? "" : "\"pushedinheritedpageresources\":" + flag;
        byte[] source = Source("ordinary", 0, "/Type /Page /Parent 2 0 R /Tag (old)", Ancestor, "", "");
        using InMemoryQpdfHost host = Host(source, Patch(meta, new() { ["obj:6 0 R"] = new { value = new Dictionary<string, object> { ["/Marker"] = "u:patched" } } }));
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        PdfDictionary parent = Assert.IsType<PdfDictionary>(Resolve(document, Entry(page, "Parent")));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(document, Entry(flag == "true" ? page : parent, "Resources")));
        Assert.Equal(flag == "true" ? "patched" : "old", Text(Entry(resources, "Marker")));
        if (flag == "true")
        {
            Assert.False(parent.TryGet(PdfName.FromAscii("Resources"), out _));
            Assert.Equal(90, Assert.IsType<PdfInteger>(Entry(page, "Rotate")).Value);
            PdfArray crop = Assert.IsType<PdfArray>(Resolve(document, Entry(page, "CropBox")));
            Assert.Equal(180, Assert.IsType<PdfInteger>(crop[3]).Value);
        }
    }

    [Theory]
    [InlineData("\"calledgetallpages\":true,\"calledgetallpages\":false", false)]
    [InlineData("\"calledgetallpages\":false,\"calledgetallpages\":true", false)]
    [InlineData("\"pushedinheritedpageresources\":true,\"pushedinheritedpageresources\":false", true)]
    public async Task TruePreparationCannotBeUndoneByALaterFalse(string metadata, bool pushed)
    {
        string ancestor = pushed ? Ancestor : "";
        using InMemoryQpdfHost host = Host(Source("direct", 0, Leaf, ancestor, "", ""), Patch(metadata, new() { ["obj:3 0 R"] = Page("patched") }));
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.NotNull(document.PageReferences[0].Source); Assert.Equal("patched", Text(Entry(document.PageReferences[0].Page, "Tag")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task PreparedAndOriginalEdgesRetainTheirGenerationSemantics(int generation)
    {
        using InMemoryQpdfHost host = Host(Source("ordinary", generation, Leaf, "", "/Private [3 0 R 3 17 R]", ""),
            Patch("\"calledgetallpages\":true", new() { ["obj:3 " + generation + " R"] = Page("patched") }));
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal("patched", Text(Entry(document.PageReferences[0].Page, "Tag")));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Entry(document.Engine.Trailer, "Root")));
        PdfArray aliases = Assert.IsType<PdfArray>(Entry(catalog, "Private"));
        Assert.IsType<PdfReference>(aliases[generation == 0 ? 0 : 1]);
        Assert.IsType<PdfNull>(aliases[generation == 0 ? 1 : 0]);
    }

    [Fact]
    public async Task NewPageDoesNotReviveAnOriginalMissingEdgeWithTheSamePair()
    {
        using InMemoryQpdfHost host = Host(Source("direct", 0, Leaf + " /Private [3 0 R]", "", "/Private [3 0 R]", ""), Patch("\"calledgetallpages\":true", []));
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.NotNull(document.PageReferences[0].Source);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Entry(document.Engine.Trailer, "Root")));
        Assert.IsType<PdfNull>(Assert.IsType<PdfArray>(Entry(catalog, "Private"))[0]);
        Assert.IsType<PdfNull>(Assert.IsType<PdfArray>(Entry(document.PageReferences[0].Page, "Private"))[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssembledExportsUpdateTheirOriginalInputIncludingProtectedOutput(bool encrypted)
    {
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 100 200] /Resources << >> >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Tag (A) >>", null), (4, "<< /Type /Page /Parent 2 0 R /Tag (B) >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(Input, source, QpdfInputOwnership.Copy);
        if (encrypted)
        {
            QpdfExecutionResult protect = await Qpdf.ExecuteAsync(host, new(["input", "--encrypt", "reader", "owner", "256", "--", "protected"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, protect.ExitCode); Assert.True(host.TryGetOutput(QpdfPath.From("protected"), out byte[] protectedBytes));
            host.AddInput(Input, protectedBytes, QpdfInputOwnership.Transfer);
        }
        List<string> argv = ["input"];
        if (encrypted) { argv.Add("--password=reader"); }
        argv.AddRange(["--pages", ".", "2,1", "--", "--json=2", "--json-key=qpdf", "--json-stream-data=inline", "report"]);
        QpdfExecutionResult export = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, export.ExitCode); Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] report));
        using JsonDocument json = JsonDocument.Parse(report);
        Assert.True(json.RootElement.GetProperty("qpdf")[0].GetProperty("pushedinheritedpageresources").GetBoolean());
        host.AddInput(Json, report, QpdfInputOwnership.Transfer);
        QpdfPassword? password = encrypted ? QpdfPassword.FromText("reader") : null;
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, password, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), password, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(new[] { "B", "A" }, document.PageReferences.Select(page => Text(Entry(page.Page, "Tag"))));
        Assert.Equal(encrypted, document.Encryption.HasValue);
        await document.DisposeAsync(); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task IndirectKidsKeepTheirIdentityWhenDirectChildrenArePromoted()
    {
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Alias 4 0 R >>", null),
            (2, "<< /Type /Pages /Kids 4 0 R /Count 1 >>", null), (3, "null", null),
            (4, "[<< " + Leaf + " >>]", null)], 1);
        using InMemoryQpdfHost host = Host(source, Patch("\"calledgetallpages\":true", new() { ["obj:5 0 R"] = Page("patched") }));
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Entry(document.Engine.Trailer, "Root")));
        PdfDictionary pages = Assert.IsType<PdfDictionary>(Resolve(document, Entry(catalog, "Pages")));
        Assert.Equal(Entry(catalog, "Alias"), Entry(pages, "Kids"));
        Assert.Equal("patched", Text(Entry(document.PageReferences[0].Page, "Tag")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("indirect-null")]
    [InlineData("stale")]
    [InlineData("local")]
    [InlineData("wrong-type")]
    public async Task PushingUsesNearestNonNullContainersAndKeepsLocalOverrides(string kind)
    {
        string local = kind switch { "missing" => "", "null" => "/Resources null", "indirect-null" => "/Resources 5 0 R",
            "stale" => "/Resources 5 17 R", "local" => "/Resources << /Marker (local) >>", _ => "/Resources 42" };
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 " + Ancestor + " >>", null),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R] /Count 1 /Resources << /Marker (nearest) >> /Rotate 180 /CropBox [1 2 80 160] >>", null),
            (4, "<< /Type /Page /Parent 3 0 R /Rotate 0 /CropBox null " + local + " >>", null), (5, "null", null)], 1);
        using InMemoryQpdfHost host = Host(source, Patch("\"pushedinheritedpageresources\":true", []));
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        QpdfObject resources = Resolve(document, Entry(page, "Resources"));
        if (kind == "wrong-type") { Assert.Equal(42, Assert.IsType<PdfInteger>(resources).Value); }
        else { Assert.Equal(kind == "local" ? "local" : "nearest", Text(Entry(Assert.IsType<PdfDictionary>(resources), "Marker"))); }
        Assert.Equal(0, Assert.IsType<PdfInteger>(Entry(page, "Rotate")).Value);
        PdfArray crop = Assert.IsType<PdfArray>(Resolve(document, Entry(page, "CropBox")));
        Assert.Equal(160, Assert.IsType<PdfInteger>(crop[3]).Value);
    }

    [Fact]
    public async Task TrailerPairsReserveIdsWhileFreeXrefSlotsDoNot()
    {
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Private [3 0 R] >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [<< " + Leaf + " >>] >>", null), (3, "null", null)], 1);
        string text = Encoding.ASCII.GetString(source);
        int xref = text.IndexOf("\nxref\n", StringComparison.Ordinal);
        int row = text.IndexOf("00000 n \n", xref, StringComparison.Ordinal);
        row = text.IndexOf("00000 n \n", row + 1, StringComparison.Ordinal);
        row = text.IndexOf("00000 n \n", row + 1, StringComparison.Ordinal) - 11;
        source = Encoding.ASCII.GetBytes(text[..row] + "0000000000 00000 f \n" + text[(row + 20)..]);
        using InMemoryQpdfHost freeHost = Host(source, Patch("\"calledgetallpages\":true", new() { ["obj:3 0 R"] = Page("free-slot") }));
        await Qpdf.UpdatePdfFromJsonAsync(freeHost, Input, Json, Output, CancellationToken.None);
        Assert.True(freeHost.TryGetOutput(Output, out byte[] freeBytes)); freeHost.AddInput(QpdfPath.From("read"), freeBytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument free = await QpdfDocument.OpenAsync(freeHost, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal("free-slot", Text(Entry(free.PageReferences[0].Page, "Tag")));
        PdfDictionary freeCatalog = Assert.IsType<PdfDictionary>(Resolve(free, Entry(free.Engine.Trailer, "Root")));
        Assert.IsType<PdfNull>(Assert.IsType<PdfArray>(Entry(freeCatalog, "Private"))[0]);
        using InMemoryQpdfHost trailerHost = Host(Source("direct", 0, Leaf, "", "", " /Reserved 100 0 R"),
            Patch("\"calledgetallpages\":true", new() { ["obj:101 0 R"] = Page("trailer-slot") }));
        await Qpdf.UpdatePdfFromJsonAsync(trailerHost, Input, Json, Output, CancellationToken.None);
        Assert.True(trailerHost.TryGetOutput(Output, out byte[] trailerBytes)); trailerHost.AddInput(QpdfPath.From("read"), trailerBytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument trailer = await QpdfDocument.OpenAsync(trailerHost, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal("trailer-slot", Text(Entry(trailer.PageReferences[0].Page, "Tag")));
    }

    [Fact]
    public async Task PreparationBorrowsOriginalValuesAndOwnsItsFreshHeaders()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Source("ordinary", 0, "/Type /Page /Parent 2 0 R /Tag (old)", Ancestor, "", ""), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        QpdfObject parent = document.GetObject(new PdfReference(2, 0));
        PdfDictionary originalPage = document.PageReferences[0].Page;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope values = ParserMemoryScope.Create(governor))
        using (ParserMemoryScope temporary = ParserMemoryScope.Create(governor))
        {
            PageTreePreparation.Prepared prepared = PageTreePreparation.Prepare(document.Engine, true, QpdfLimits.Default,
                values, temporary, governor, CancellationToken.None);
            Assert.Same(parent, document.GetObject(new PdfReference(2, 0)));
            Assert.Same(originalPage, document.PageReferences[0].Page);
            Assert.NotSame(parent, prepared.Objects[2]); Assert.NotSame(originalPage, prepared.Objects[3]);
            Assert.IsType<PdfDictionary>(Entry(Assert.IsType<PdfDictionary>(parent), "Resources"));
            Assert.False(Assert.IsType<PdfDictionary>(prepared.Objects[2]).TryGet(PdfName.FromAscii("Resources"), out _));
            Assert.Equal(new[] { 4, 5, 6 }, prepared.AddedReferences.Select(reference => reference.ObjectNumber));
            Assert.Same(Entry(Assert.IsType<PdfDictionary>(parent), "Resources"), prepared.Objects[6]);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task CreationValidatesButIgnoresPreparationActions()
    {
        string model = """{"qpdf":[{"jsonversion":2,"pdfversion":"1.7","calledgetallpages":true,"pushedinheritedpageresources":true},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},"obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":[{"/Type":"/Page","/Tag":"u:direct"}],"/Count":1}},"trailer":{"value":{"/Root":"1 0 R"}}}]}""";
        using InMemoryQpdfHost host = new(); using JsonDocument json = JsonDocument.Parse(model);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        ImportedModel imported = await JsonImport.BuildCreateModelAsync(host, json, false, QpdfLimits.Default, governor, memory, CancellationToken.None);
        Assert.Equal(2, imported.Objects.Count);
        PdfDictionary pages = Assert.IsType<PdfDictionary>(imported.Objects[2]);
        Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(Entry(pages, "Kids"))[0]);
    }

    [Fact]
    public async Task PreparationRetainsTheSharedNestingAndCancellationBounds()
    {
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R] /Count 1 >>", null),
            (4, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(Input, source, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope values = ParserMemoryScope.Create(governor))
        using (ParserMemoryScope temporary = ParserMemoryScope.Create(governor))
        {
            QpdfLimitException limit = Assert.Throws<QpdfLimitException>(() => PageTreePreparation.Prepare(document.Engine,
                true, QpdfLimits.Default with { MaxNestingDepth = 1 }, values, temporary, governor, CancellationToken.None));
            Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), limit.LimitName);
            Assert.ThrowsAny<OperationCanceledException>(() => PageTreePreparation.Prepare(document.Engine,
                true, QpdfLimits.Default, values, temporary, governor, new CancellationToken(true)));
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, "calledgetallpages")]
    [InlineData(true, "calledgetallpages")]
    [InlineData(false, "pushedinheritedpageresources")]
    [InlineData(true, "pushedinheritedpageresources")]
    public async Task BothPoliciesValidatePreparationFlagTypesBeforePublication(bool update, string flag)
    {
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Source("ordinary", 0, Leaf, "", "", ""), Output);
        host.AddInput(Json, Patch("\"pdfversion\":\"1.7\",\"" + flag + "\":1", []), QpdfInputOwnership.Transfer);
        if (update)
        { await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None).AsTask()); }
        else
        { await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.CreatePdfFromJsonAsync(host, Json, Output, CancellationToken.None).AsTask()); }
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PreparationAndPublicationFailuresRetainTheActualTargetAndRefundAdmission()
    {
        async Task<InMemoryQpdfHost> Prepare()
        {
            InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Source("direct", 0, Leaf, Ancestor, "", ""), Output);
            host.AddInput(Json, Patch("\"pushedinheritedpageresources\":true", []), QpdfInputOwnership.Transfer); return host;
        }
        await PreparationAdmissionChecks.VerifyAsync(Prepare,
            (host, token) => Qpdf.ExecuteAsync(host, new(["input", "--update-from-json=json", "output"]), token),
            Output, 4096, ["create", "write", "commit", "cancel", "close"]);
    }
}
