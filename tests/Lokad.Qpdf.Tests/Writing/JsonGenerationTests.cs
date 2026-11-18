using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonGenerationTests
{
    private static byte[] Authored(string role, int generation)
    {
        string Reference(int number) => number + " " + generation + " R";
        Dictionary<string, object> catalog = new() { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R" };
        Dictionary<string, object> pages = new() { ["/Type"] = "/Pages", ["/Kids"] = new[] { "3 0 R" }, ["/Count"] = 1 };
        Dictionary<string, object> page = new() { ["/Type"] = "/Page", ["/Parent"] = "2 0 R",
            ["/MediaBox"] = new[] { 0, 0, 80, 40 }, ["/Resources"] = new Dictionary<string, object>(), ["/Contents"] = "4 0 R" };
        Dictionary<string, object> trailer = new() { ["/Root"] = "1 0 R" };
        Dictionary<string, object> stream = new();
        switch (role)
        {
            case "private": catalog["/Audit"] = Reference(5); break;
            case "root": trailer["/Root"] = Reference(1); break;
            case "pages": catalog["/Pages"] = Reference(2); break;
            case "kid": pages["/Kids"] = new[] { Reference(3) }; break;
            case "resources": page["/Resources"] = Reference(6); break;
            case "info": trailer["/Info"] = Reference(5); break;
            case "attachment": catalog["/Names"] = new Dictionary<string, object> {
                ["/EmbeddedFiles"] = new Dictionary<string, object> { ["/Names"] = new[] { "u:K", Reference(5) } } }; break;
            case "stream-header": stream["/Audit"] = Reference(5); break;
            case "array": catalog["/Audit"] = new[] { Reference(5), "5 0 R" }; break;
            default: throw new ArgumentException("Unknown role.", nameof(role));
        }
        Dictionary<string, object> objects = new()
        {
            ["obj:1 0 R"] = new { value = catalog }, ["obj:2 0 R"] = new { value = pages },
            ["obj:3 0 R"] = new { value = page }, ["obj:4 0 R"] = new { stream = new { dict = stream, data = "cSBR" } },
            ["obj:5 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Filespec", ["/F"] = "u:K",
                ["/Title"] = "u:kept", ["/EF"] = new Dictionary<string, object> { ["/F"] = "4 0 R" } } },
            ["obj:6 0 R"] = new { value = new Dictionary<string, object>() }, ["trailer"] = new { value = trailer }
        };
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.4" }, objects } });
    }

    public static IEnumerable<object[]> AuthoredCases()
    {
        foreach (string role in new[] { "private", "root", "pages", "kid", "resources", "info", "attachment", "stream-header", "array" })
        foreach (int generation in new[] { 0, 1 })
        foreach (bool update in new[] { false, true })
        { yield return [role, generation, update, "typed"]; }
        foreach (int generation in new[] { 65535, 65536, int.MaxValue })
        foreach (bool update in new[] { false, true })
        { yield return ["private", generation, update, "typed"]; }
        foreach (string role in new[] { "private", "root" })
        foreach (int generation in new[] { 0, 1 })
        foreach (bool update in new[] { false, true })
        foreach (string route in new[] { "argv", "job" }) { yield return [role, generation, update, route]; }
    }

    [Theory]
    [MemberData(nameof(AuthoredCases))]
    public async Task AuthoredReferencesBindOnlyDeclaredPairs(string role, int generation, bool update, string route)
    {
        using InMemoryQpdfHost host = new(); JsonPageTreeTests.Register(host, Authored(role, generation)); await JsonPageTreeTests.Seed(host);
        if (generation == 0 || role is not ("root" or "pages" or "kid"))
        {
            await Run(host, update, route); Assert.Equal(0L, host.OpenLeaseCount);
            await using QpdfDocument reader = await OpenOutput(host); Assert.Equal(1, reader.PageCount);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Root")));
            QpdfObject target;
            switch (role)
            {
                case "root": case "pages": case "kid": return;
                case "resources": target = Get(reader.PageReferences[0].Page, "Resources"); break;
                case "info": target = Get(reader.Engine.Trailer, "Info"); break;
                case "attachment":
                    PdfDictionary names = Assert.IsType<PdfDictionary>(Get(catalog, "Names"));
                    PdfDictionary files = Assert.IsType<PdfDictionary>(Get(names, "EmbeddedFiles"));
                    target = Assert.IsType<PdfArray>(Get(files, "Names"))[1]; break;
                case "stream-header": target = Get(Assert.IsType<PdfStream>(Resolve(reader, Get(reader.PageReferences[0].Page, "Contents"))).Dictionary, "Audit"); break;
                case "array":
                    PdfArray array = Assert.IsType<PdfArray>(Get(catalog, "Audit")); target = array[0];
                    Assert.IsType<PdfDictionary>(Resolve(reader, array[1])); break;
                default: target = Get(catalog, "Audit"); break;
            }
            if (generation == 0) { Assert.IsType<PdfDictionary>(Resolve(reader, target)); }
            else { Assert.IsType<PdfNull>(target); }
            return;
        }
        if (route == "typed")
        {
            await Assert.ThrowsAsync<QpdfParseException>(() => JsonPageTreeTests.Typed(host, update, QpdfLimits.Default, CancellationToken.None));
        }
        else
        {
            QpdfExecutionResult result = await JsonPageTreeTests.Execute(host, update, route, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        JsonPageTreeTests.AssertPrior(host);
    }

    internal static byte[] Source(string role, int active, bool stale)
    {
        string live = "5 " + active + " R", target = "5 " + (stale ? active + 1 : active) + " R";
        string catalog = " /Live " + live, page = "", contents = "4 0 R", resources = "<< >>";
        string value = "<< /Marker (old) >>", header = "";
        switch (role)
        {
            case "private": catalog += " /Audit " + target; break;
            case "resources": resources = target; break;
            case "contents": contents = target; break;
            case "info": break;
            case "attachment": catalog += " /Names << /EmbeddedFiles << /Names [(K) " + target + "] >> >>";
                value = "<< /Type /Filespec /F (K) /EF << /F 4 0 R >> >>"; break;
            case "stream-header": header = " /Audit " + target; break;
            case "null": catalog += " /Audit " + target; value = "null"; break;
            case "cycle": catalog += " /Audit " + target; value = "<< /Self " + live + " /Dead 5 " + (active + 1) + " R >>"; break;
            default: throw new ArgumentException("Unknown role.", nameof(role));
        }
        byte[] data = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R" + catalog + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources " + resources + " /Contents " + contents + page + " >>", null),
            (4, "<< /Length 3" + header + " >>", "q Q"u8.ToArray()),
            (5, role == "contents" ? "<< /Length 3 /Marker (old) >>" : value, role == "contents" ? "q Q"u8.ToArray() : null)],
            1, new Dictionary<int, int> { [5] = active });
        if (role != "info") { return data; }
        return Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(data).Replace("/Root 1 0 R >>", "/Root 1 0 R /Info " + target + " >>", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> SourceCases()
    {
        foreach (string role in new[] { "private", "resources", "contents", "info", "attachment", "stream-header", "null", "cycle" })
        foreach (int active in new[] { 0, 7 })
        foreach (bool stale in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [role, active, stale, route]; }
    }

    [Theory]
    [MemberData(nameof(SourceCases))]
    public async Task EmptyUpdatesValidateSourcePairsBeforeRenumbering(string role, int active, bool stale, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Source(role, active, stale), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("json"), "{\"qpdf\":[{\"jsonversion\":2},{}]}"u8.ToArray(), QpdfInputOwnership.Copy);
        await JsonPageTreeTests.Seed(host); await Run(host, true, route);
        await using QpdfDocument reader = await OpenOutput(host);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Root")));
        PdfReference live = Assert.IsType<PdfReference>(Get(catalog, "Live")); Assert.Equal(0, live.Generation);
        QpdfObject target;
        switch (role)
        {
            case "resources": target = Get(reader.PageReferences[0].Page, "Resources"); break;
            case "contents": target = Get(reader.PageReferences[0].Page, "Contents"); break;
            case "info": target = reader.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject info) ? info : PdfNull.Instance; break;
            case "attachment":
                PdfDictionary names = Assert.IsType<PdfDictionary>(Resolve(reader, Get(catalog, "Names")));
                PdfDictionary files = Assert.IsType<PdfDictionary>(Resolve(reader, Get(names, "EmbeddedFiles")));
                target = Assert.IsType<PdfArray>(Resolve(reader, Get(files, "Names")))[1]; break;
            case "stream-header": target = Get(Assert.IsType<PdfStream>(Resolve(reader, Get(reader.PageReferences[0].Page, "Contents"))).Dictionary, "Audit"); break;
            default: target = Get(catalog, "Audit"); break;
        }
        if (stale) { Assert.IsType<PdfNull>(target); }
        else { Assert.Equal(live, Assert.IsType<PdfReference>(target)); }
        if (role == "cycle")
        {
            PdfDictionary cycle = Assert.IsType<PdfDictionary>(Resolve(reader, live));
            Assert.Equal(live, Get(cycle, "Self")); Assert.IsType<PdfNull>(Get(cycle, "Dead"));
        }
        if (role == "contents")
        {
            Assert.Equal("q Q"u8.ToArray(), StreamObservation.Decode(Assert.IsType<PdfStream>(Resolve(reader, live))));
        }
        Assert.Equal(1, reader.PageCount); await reader.DisposeAsync(); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static async Task Run(IQpdfHost host, bool update, string route)
    {
        if (route == "typed")
        { QpdfJsonImportResult result = await JsonPageTreeTests.Typed(host, update, QpdfLimits.Default, CancellationToken.None); Assert.Equal(0L, result.Counters.OpenHandles); }
        else
        { QpdfExecutionResult result = await JsonPageTreeTests.Execute(host, update, route, QpdfLimits.Default, CancellationToken.None); Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, result.Counters.OpenHandles); }
    }

    private static async Task<QpdfDocument> OpenOutput(InMemoryQpdfHost host)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
    }

    [Theory]
    [InlineData(false, "typed")]
    [InlineData(true, "typed")]
    [InlineData(false, "argv")]
    [InlineData(true, "argv")]
    [InlineData(false, "job")]
    [InlineData(true, "job")]
    public async Task CompressedSourceMembersKeepGenerationZeroSemantics(bool flate, string route)
    {
        byte[] input = Reading.ReaderAcceptanceTests.ObjectStreamPdf("<< /Marker (compressed) >>", "null", flate, false, null,
            "/Resources << >> /Live 6 0 R /Dead 6 1 R /Free 7 0 R /Missing 99 0 R");
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("json"), "{\"qpdf\":[{\"jsonversion\":2},{}]}"u8.ToArray(), QpdfInputOwnership.Copy);
        await Run(host, true, route); await using QpdfDocument reader = await OpenOutput(host);
        PdfDictionary page = reader.PageReferences[0].Page;
        PdfReference live = Assert.IsType<PdfReference>(Get(page, "Live")); Assert.Equal(0, live.Generation);
        PdfDictionary member = Assert.IsType<PdfDictionary>(Resolve(reader, live)); Assert.Equal("compressed"u8.ToArray(), Assert.IsType<PdfByteString>(Get(member, "Marker")).Value);
        Assert.IsType<PdfNull>(Get(page, "Dead")); Assert.IsType<PdfNull>(Get(page, "Free")); Assert.IsType<PdfNull>(Get(page, "Missing"));
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task NewDeclarationsDoNotReviveMissingSourceEdges(string route)
    {
        byte[] input = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Old 4 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("json"), "{\"qpdf\":[{\"jsonversion\":2},{\"obj:4 0 R\":{\"value\":17},\"obj:3 0 R\":{\"value\":{\"/Type\":\"/Page\",\"/Parent\":\"2 0 R\",\"/MediaBox\":[0,0,10,10],\"/Resources\":{},\"/New\":\"4 0 R\"}}}]}"u8.ToArray(), QpdfInputOwnership.Copy);
        await Run(host, true, route); await using QpdfDocument reader = await OpenOutput(host);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Root")));
        Assert.IsType<PdfNull>(Get(catalog, "Old")); Assert.Equal(17L, Assert.IsType<PdfInteger>(Resolve(reader, Get(reader.PageReferences[0].Page, "New"))).Value);
    }

    [Theory]
    [InlineData(0, "typed")]
    [InlineData(7, "typed")]
    [InlineData(8, "typed")]
    [InlineData(0, "argv")]
    [InlineData(0, "job")]
    public async Task UpdatesMatchPairsAndBindNewEdgesWithoutRevivingStaleSourceAliases(int generation, string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Source("private", 7, true), QpdfInputOwnership.Copy);
        Dictionary<string, object> objects = new()
        {
            ["obj:3 0 R"] = new { value = new Dictionary<string, object> {
                ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/MediaBox"] = new[] { 0, 0, 80, 40 }, ["/Resources"] = new Dictionary<string, object>(),
                ["/Live"] = "5 7 R", ["/Zero"] = "5 0 R", ["/Other"] = "5 8 R", ["/Forward"] = "99 11 R", ["/Missing"] = "99 0 R" } },
            ["obj:5 " + generation + " R"] = new { value = new Dictionary<string, object> { ["/Marker"] = "u:new" } },
            ["obj:99 11 R"] = new { value = 17 }
        };
        host.AddInput(QpdfPath.From("json"), JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2 }, objects } }), QpdfInputOwnership.Copy);
        await Run(host, true, route); await using QpdfDocument reader = await OpenOutput(host);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Root")));
        string Marker(QpdfObject raw) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(Assert.IsType<PdfDictionary>(Resolve(reader, raw)), "Marker")).Value);
        Assert.Equal(generation == 7 ? "new" : "old", Marker(Get(catalog, "Live")));
        Assert.IsType<PdfNull>(Get(catalog, "Audit"));
        PdfDictionary page = reader.PageReferences[0].Page;
        Assert.Equal(Get(catalog, "Live"), Get(page, "Live"));
        foreach ((int pairGeneration, string key) in new[] { (0, "Zero"), (8, "Other") })
        {
            if (generation == pairGeneration) { Assert.Equal("new", Marker(Get(page, key))); Assert.NotEqual(Get(catalog, "Live"), Get(page, key)); }
            else { Assert.IsType<PdfNull>(Get(page, key)); }
        }
        Assert.Equal(17L, Assert.IsType<PdfInteger>(Resolve(reader, Get(page, "Forward"))).Value);
        Assert.IsType<PdfNull>(Get(page, "Missing"));
    }
}
