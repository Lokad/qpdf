using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonPairRoundTripTests
{
    internal static byte[] Source()
    {
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 4 R /Live 5 7 R /Stale 5 0 R /Missing 99 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 5 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 4 R /MediaBox [0 0 80 40] /Resources << >> /Contents 4 6 R >>", null),
            (4, "<< /Length 3 /Audit 5 7 R /Stale 5 0 R /Self 4 6 R >>", "q Q"u8.ToArray()),
            (5, "<< /Marker (kept) /Array [5 7 R 6 8 R 5 0 R] >>", null),
            (6, "<< /Title (Information) >>", null)], 1,
            new Dictionary<int, int> { [1] = 3, [2] = 4, [3] = 5, [4] = 6, [5] = 7, [6] = 8 }, "1.4", " /Info 6 8 R");
        // Only trailer syntax changes; object/xref offsets remain exact.
        return Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(source).Replace("/Root 1 0 R", "/Root 1 3 R", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "inline", "typed")]
    [InlineData(true, "inline", "typed")]
    [InlineData(true, "none", "typed")]
    [InlineData(false, "inline", "argv")]
    [InlineData(true, "none", "argv")]
    [InlineData(false, "inline", "job")]
    [InlineData(true, "none", "job")]
    public async Task ExportedPairsRoundTripThroughCreationAndUpdates(bool update, string streamData, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Source(), QpdfInputOwnership.Transfer);
        QpdfJsonResult export = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"),
            new QpdfJsonOptions(2, ["qpdf"], streamData, null, [], true), CancellationToken.None);
        using (JsonDocument json = JsonDocument.Parse(export.Json))
        {
            JsonElement objects = json.RootElement.GetProperty("qpdf")[1];
            Assert.True(objects.TryGetProperty("obj:4 6 R", out _));
            Assert.Equal("1 3 R", objects.GetProperty("trailer").GetProperty("value").GetProperty("/Root").GetString());
        }
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(export.Json), QpdfInputOwnership.Transfer);
        await JsonPageTreeTests.Seed(host);
        if (route == "typed") { _ = await JsonPageTreeTests.Typed(host, update, QpdfLimits.Default, CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = await JsonPageTreeTests.Execute(host, update, route, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        await using QpdfDocument document = await OpenOutput(host);
        PdfDictionary catalog = Dict(document, Get(document.Engine.Trailer, "Root"));
        PdfReference live = Assert.IsType<PdfReference>(Get(catalog, "Live")); Assert.Equal(0, live.Generation);
        Assert.Equal("kept", Text(Get(Dict(document, live), "Marker")));
        Assert.IsType<PdfNull>(Get(catalog, "Stale")); Assert.IsType<PdfNull>(Get(catalog, "Missing"));
        PdfArray array = Assert.IsType<PdfArray>(Get(Dict(document, live), "Array"));
        Assert.Equal(live, array[0]); Assert.IsType<PdfNull>(array[2]);
        QpdfObject info = Get(document.Engine.Trailer, "Info"); Assert.Equal(info, array[1]);
        Assert.Equal("Information", Text(Get(Dict(document, info), "Title")));
        Assert.Equal(1, document.PageCount);
        QpdfObject contents = Get(document.PageReferences[0].Page, "Contents");
        PdfStream stream = Assert.IsType<PdfStream>(Resolve(document, contents));
        Assert.Equal("q Q"u8.ToArray(), StreamObservation.Decode(stream));
        Assert.Equal(contents, Get(stream.Dictionary, "Self")); Assert.Equal(live, Get(stream.Dictionary, "Audit"));
        Assert.IsType<PdfNull>(Get(stream.Dictionary, "Stale"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompressedMembersRoundTripWithGenerationZero(bool update)
    {
        byte[] source = Reading.ReaderAcceptanceTests.ObjectStreamPdf("<< /Marker (compressed) >>", "null", true, false, null,
            "/Resources << >> /Live 6 0 R /Dead 6 1 R /Free 7 0 R /Missing 99 0 R");
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        QpdfJsonResult export = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"),
            new QpdfJsonOptions(2, ["qpdf"], update ? "none" : "inline", null, [], true), CancellationToken.None);
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(export.Json), QpdfInputOwnership.Transfer);
        _ = await JsonPageTreeTests.Typed(host, update, QpdfLimits.Default, CancellationToken.None);
        await using QpdfDocument document = await OpenOutput(host);
        PdfDictionary page = document.PageReferences[0].Page;
        Assert.Equal("compressed", Text(Get(Dict(document, Get(page, "Live")), "Marker")));
        foreach (string key in new[] { "Dead", "Free", "Missing" }) { Assert.IsType<PdfNull>(Get(page, key)); }
    }

    [Fact]
    public async Task CreationKeepsDistinctGenerationsAndMaximumNumbersWithoutSparseAllocation()
    {
        Dictionary<string, object> objects = new()
        {
            ["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = "1 7 R" } },
            ["obj:2147483647 0 R"] = new { value = "u:zero" },
            ["obj:2147483647 2147483647 R"] = new { value = "u:high" },
            ["obj:1 7 R"] = new { value = new Dictionary<string, object> {
                ["/Type"] = "/Catalog", ["/Pages"] = "2 8 R", ["/Zero"] = "2147483647 0 R", ["/High"] = "2147483647 2147483647 R", ["/Absent"] = "2147483647 7 R" } },
            ["obj:2 8 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Kids"] = Array.Empty<string>(), ["/Count"] = 0 } }
        };
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("json"), JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.4" }, objects } }), QpdfInputOwnership.Transfer);
        _ = await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), QpdfLimits.Default with { MaxMemoryBytes = 65536 }, CancellationToken.None);
        await using QpdfDocument document = await OpenOutput(host);
        PdfDictionary catalog = Dict(document, Get(document.Engine.Trailer, "Root"));
        Assert.Equal("zero", Text(Resolve(document, Get(catalog, "Zero")))); Assert.Equal("high", Text(Resolve(document, Get(catalog, "High"))));
        Assert.NotEqual(Get(catalog, "Zero"), Get(catalog, "High")); Assert.IsType<PdfNull>(Get(catalog, "Absent"));
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(0, true)]
    public async Task StreamPayloadReuseRequiresTheExactExistingPair(int generation, bool supplyData)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Source(), QpdfInputOwnership.Transfer);
        Dictionary<string, object> stream = new() { ["dict"] = new Dictionary<string, object> { ["/Audit"] = "5 7 R" } };
        if (supplyData) { stream["data"] = "bmV3"; }
        Dictionary<string, object> objects = new() { ["obj:4 " + generation + " R"] = new { stream } };
        if (supplyData)
        {
            objects["obj:1 3 R"] = new { value = new Dictionary<string, object> {
                ["/Type"] = "/Catalog", ["/Pages"] = "2 4 R", ["/Copy"] = "4 0 R" } };
        }
        host.AddInput(QpdfPath.From("json"), JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2 }, objects } }), QpdfInputOwnership.Transfer);
        _ = await JsonPageTreeTests.Typed(host, true, QpdfLimits.Default, CancellationToken.None);
        await using QpdfDocument document = await OpenOutput(host);
        PdfStream original = Assert.IsType<PdfStream>(Resolve(document, Get(document.PageReferences[0].Page, "Contents")));
        Assert.Equal("q Q"u8.ToArray(), original.Data);
        Assert.Equal("kept", Text(Get(Dict(document, Get(original.Dictionary, "Audit")), "Marker")));
        if (supplyData)
        {
            PdfDictionary catalog = Dict(document, Get(document.Engine.Trailer, "Root"));
            PdfStream copy = Assert.IsType<PdfStream>(Resolve(document, Get(catalog, "Copy")));
            Assert.Equal("new"u8.ToArray(), copy.Data);
            Assert.NotEqual(Get(catalog, "Copy"), Get(document.PageReferences[0].Page, "Contents"));
        }
    }

    [Fact]
    public async Task MismatchedStreamWithoutDataRejectsAndPreservesPriorTarget()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Source(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("json"), "{\"qpdf\":[{\"jsonversion\":2},{\"obj:4 0 R\":{\"stream\":{\"dict\":{}}}}]}"u8.ToArray(), QpdfInputOwnership.Transfer);
        await JsonPageTreeTests.Seed(host);
        QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() => JsonPageTreeTests.Typed(host, true, QpdfLimits.Default, CancellationToken.None));
        Assert.Contains("new stream", error.Message, StringComparison.Ordinal); JsonPageTreeTests.AssertPrior(host);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task AuthenticatedGenerationExportsKeepDecryptedInfoAndExplicitProtection(bool create, bool preserve)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r4-generations.pdf")), QpdfInputOwnership.Transfer);
        QpdfJsonResult export = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], true) { Password = QpdfPassword.FromText("reader") }, CancellationToken.None);
        using (JsonDocument json = JsonDocument.Parse(export.Json))
        { Assert.EndsWith(" 513 R", Assert.IsType<string>(json.RootElement.GetProperty("qpdf")[1].GetProperty("trailer").GetProperty("value").GetProperty("/Info").GetString()), StringComparison.Ordinal); }
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(export.Json), QpdfInputOwnership.Transfer);
        QpdfOutputEncryption policy = preserve ? QpdfOutputEncryption.Preserve : QpdfOutputEncryption.Remove;
        if (create) { _ = await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), policy, QpdfLimits.Default, CancellationToken.None); }
        else { _ = await Qpdf.UpdatePdfFromJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"), QpdfPath.From("output"), QpdfPassword.FromText("reader"), policy, CancellationToken.None); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("result"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), preserve ? QpdfPassword.FromText("reader") : null, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(preserve, document.Encryption is not null);
        Assert.Equal("TitleText", Text(Get(Dict(document, Get(document.Engine.Trailer, "Info")), "Title")));
    }

    private static QpdfObject Get(PdfDictionary dictionary, string name) => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) ? value : PdfNull.Instance;
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static PdfDictionary Dict(QpdfDocument document, QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(document, value));
    private static string Text(QpdfObject value) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(value).ValueSpan);
    private static async Task<QpdfDocument> OpenOutput(InMemoryQpdfHost host)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("result"), bytes, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
    }
}
