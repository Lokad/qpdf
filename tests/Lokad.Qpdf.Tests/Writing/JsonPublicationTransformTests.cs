using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonPublicationTransformTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");

    internal static byte[] Fixture(int generation, string shape)
    {
        string Ref(int number) => number + " " + generation + " R";
        byte[] metadata = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">METADATA-MARKER</x:xmpmeta>"u8.ToArray();
        byte[] content = "BT /F1 10 Tf 10 100 Td (PAGE-MARKER) Tj ET"u8.ToArray();
        bool signature = shape == "signature";
        string date = shape switch
        {
            "no-date" => "",
            "null-date" => " /ModDate null",
            "stale-date" => " /ModDate 10 0 R",
            _ => " /ModDate " + Ref(10)
        };
        List<(int, string, byte[]?)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm " + Ref(4) + " /StructTreeRoot " + Ref(6)
                + " /MarkInfo << /Marked true >> /PageLabels " + Ref(7) + " /Metadata " + Ref(8)
                + " /Private << /Form " + Ref(4) + " /Structure " + Ref(6) + " /Labels " + Ref(7)
                + " /Meta " + Ref(8) + " /Info " + Ref(9) + " /Catalog 1 0 R >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [" + Ref(3) + "] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << /Font << /F1 " + Ref(12)
                + " >> >> /Contents " + Ref(11) + " /Annots [" + Ref(5) + "] >>", null),
            (4, "<< /Fields [" + Ref(5) + "] /DA (/F1 10 Tf 0 g) /DR << /Font << /F1 " + Ref(12) + " >> >> >>", null),
            (5, "<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P " + Ref(3)
                + (signature ? " /FT /Sig /V " + Ref(13) : " /FT /Tx /V (FORM-VALUE)") + " /T (FORM-KEY) >>", null),
            (6, "<< /Type /StructTreeRoot /K [] >>", null),
            (7, shape == "inactive-labels" ? "<< /Nums [0] >>" : "<< /Nums [0 << /S /D /St 1 /P (OLD-) >>] >>", null),
            (8, "<< /Type /Metadata /Subtype /XML /Length " + metadata.Length + " >>", metadata),
            (9, "<< /Title (INFO-TITLE) /Producer (INFO-PRODUCER)" + date + " >>", null),
            (10, "(D:20260101000000Z)", null), (11, "<< /Length " + content.Length + " >>", content),
            (12, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)];
        if (signature) { objects.Add((13, "<< /Type /Sig /Contents <01020304> /ByteRange [0 0 0 0] >>", null)); }
        return PdfTestDocument.Assemble(objects, 1,
            objects.Where(item => item.Item1 > 2).ToDictionary(item => item.Item1, _ => generation), "1.7", " /Info " + Ref(9));
    }

    private static string[] Flags(string mode) => mode switch
    {
        "baseline" => [], "info" => ["--remove-info"], "metadata" => ["--remove-metadata"],
        "form" => ["--remove-acroform"], "structure" => ["--remove-structure"], "remove-labels" => ["--remove-page-labels"],
        "labels" => ["--set-page-labels", "1:D/5/NEW-", "--"],
        _ => ["--remove-info", "--remove-acroform", "--remove-structure", "--remove-page-labels"],
    };

    public static IEnumerable<object[]> Policies()
    {
        foreach (int generation in new[] { 0, 17 })
        foreach (string mode in new[] { "baseline", "info", "metadata", "form", "structure", "remove-labels", "labels", "all" })
        { yield return [generation, mode]; }
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task ActiveRolesChangeWhileOriginalAliasesAndGenerationsRemain(int generation, string mode)
    {
        using InMemoryQpdfHost host = Host(Fixture(generation, "ordinary"));
        using JsonDocument report = await Run(host, ["input", "--json-output=2", .. Flags(mode)]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1], trailer = table.GetProperty("trailer").GetProperty("value");
        JsonElement catalog = Value(table, trailer.GetProperty("/Root"));
        Assert.Equal(mode is not ("info" or "metadata" or "all"), catalog.TryGetProperty("/Metadata", out _));
        Assert.Equal(mode is not ("form" or "all"), catalog.TryGetProperty("/AcroForm", out _));
        Assert.Equal(mode is not ("structure" or "all"), catalog.TryGetProperty("/StructTreeRoot", out _));
        Assert.Equal(mode is not ("structure" or "all"), catalog.TryGetProperty("/MarkInfo", out _));
        Assert.Equal(mode is not ("remove-labels" or "all"), catalog.TryGetProperty("/PageLabels", out _));
        JsonElement info = Value(table, trailer.GetProperty("/Info"));
        if (mode is "info" or "all")
        {
            Assert.Equal("/ModDate", Assert.Single(info.EnumerateObject()).Name);
            Assert.Equal("10 " + generation + " R", info.GetProperty("/ModDate").GetString());
        }
        else { Assert.Equal("9 " + generation + " R", trailer.GetProperty("/Info").GetString()); }
        if (mode == "labels")
        {
            JsonElement label = Value(table, catalog.GetProperty("/PageLabels")).GetProperty("/Nums")[1];
            Assert.Equal(5, label.GetProperty("/St").GetInt32()); Assert.Equal("u:NEW-", label.GetProperty("/P").GetString());
        }
        AssertPrivate(table, catalog, generation);
        JsonElement metadata = report.RootElement.GetProperty("qpdf")[0];
        Assert.Equal(mode == "labels", metadata.GetProperty("calledgetallpages").GetBoolean());
        Assert.False(metadata.GetProperty("pushedinheritedpageresources").GetBoolean());
    }

    [Fact]
    public async Task CatalogOnlyRemovalDoesNotInterpretAnUnrequestedLabelTree()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "inactive-labels"));
        using JsonDocument report = await Run(host, ["input", "--json-output=2", "--remove-info"]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        Assert.Equal(1, Value(table, catalog.GetProperty("/PageLabels")).GetProperty("/Nums").GetArrayLength());
        Assert.False(report.RootElement.GetProperty("qpdf")[0].GetProperty("calledgetallpages").GetBoolean());
    }

    [Fact]
    public async Task CatalogOnlyRemovalPreservesReadOnlySignatureValues()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "signature"));
        using JsonDocument report = await Run(host, ["input", "--json-output=2", "--remove-metadata"]);
        JsonElement field = report.RootElement.GetProperty("qpdf")[1].GetProperty("obj:5 17 R").GetProperty("value");
        Assert.Equal("/Sig", field.GetProperty("/FT").GetString()); Assert.Equal("13 17 R", field.GetProperty("/V").GetString());
    }

    [Theory]
    [InlineData("no-date")]
    [InlineData("null-date")]
    [InlineData("stale-date")]
    public async Task RemoveInfoOmittedForNonLiveModificationDate(string shape)
    {
        using InMemoryQpdfHost host = Host(Fixture(17, shape));
        using JsonDocument report = await Run(host, ["input", "--json-output=2", "--remove-info"]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement trailer = table.GetProperty("trailer").GetProperty("value");
        Assert.False(trailer.TryGetProperty("/Info", out _));
        AssertPrivate(table, Value(table, trailer.GetProperty("/Root")), 17);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedPagesDoNotInterpretLabelsThatAreRemovedOrReplaced(bool replace)
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "inactive-labels"));
        string[] labels = replace ? ["--set-page-labels", "1:D/5/NEW-", "z:D/9/LAST-", "--"] : [];
        using JsonDocument report = await Run(host, ["input", "--json=2", "--pages", ".", "1,1", "--",
            "--remove-page-labels", .. labels]);
        JsonElement pages = report.RootElement.GetProperty("pages");
        Assert.Equal(2, pages.GetArrayLength());
        Assert.NotEqual(pages[0].GetProperty("object").GetString(), pages[1].GetProperty("object").GetString());
        if (replace)
        {
            Assert.Equal(5, pages[0].GetProperty("label").GetProperty("/St").GetInt32());
            Assert.Equal(9, pages[1].GetProperty("label").GetProperty("/St").GetInt32());
            Assert.Equal("u:LAST-", pages[1].GetProperty("label").GetProperty("/P").GetString());
            Assert.Equal(2, report.RootElement.GetProperty("pagelabels").GetArrayLength());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, pages[0].GetProperty("label").ValueKind);
            Assert.Equal(JsonValueKind.Null, pages[1].GetProperty("label").ValueKind);
            Assert.Empty(report.RootElement.GetProperty("pagelabels").EnumerateArray());
        }
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        Assert.Equal(1, Value(table, catalog.GetProperty("/Private").GetProperty("/Labels")).GetProperty("/Nums").GetArrayLength());
    }

    [Theory]
    [InlineData("all")]
    [InlineData("labels")]
    public Task PreparationAndPublicationFailuresKeepTheActualTargetAndRefundAdmission(string mode)
        => PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(Fixture(17, "ordinary"), Output),
            (host, token) => Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", .. Flags(mode), "output"]), token),
            Output, 512, ["write", "cancel"]);

    [Fact]
    public async Task JobLabelsUpdateTheTreeAndFullReportDescriptors()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "ordinary"));
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
        { inputFile = "input", outputFile = "output", json = "2", setPageLabels = new[] { "1:D/5/NEW-" } }), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        using JsonDocument report = JsonDocument.Parse(bytes);
        Assert.Equal(5, report.RootElement.GetProperty("pagelabels")[0].GetProperty("label").GetProperty("/St").GetInt32());
        Assert.Equal(5, report.RootElement.GetProperty("pages")[0].GetProperty("label").GetProperty("/St").GetInt32());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task AuthoredImportLabelsReplaceTheActiveTreeAndKeepPrivateAliases()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("labels.json");
        host.AddInput(input, """
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.2"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/PageLabels":"4 0 R","/PrivateLabels":"4 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/Resources":{},"/MediaBox":[0,0,10,20]}},
              "obj:4 0 R":{"value":{"/Nums":[0]}},
              "trailer":{"value":{"/Root":"1 0 R"}}
            }]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([input.Value, "--json-input", "--remove-page-labels", "--set-page-labels", "1:r/7/NEW-", "--", Output.Value]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        QpdfPath read = QpdfPath.From("result");
        host.AddInput(read, bytes, QpdfInputOwnership.Transfer);
        using JsonDocument report = await Run(host, [read.Value, "--json-output=2"]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement numbers = Value(table, catalog.GetProperty("/PageLabels")).GetProperty("/Nums");
        Assert.Equal(0, numbers[0].GetInt32());
        JsonElement label = numbers[1];
        Assert.Equal("/r", label.GetProperty("/S").GetString());
        Assert.Equal(7, label.GetProperty("/St").GetInt32());
        Assert.Equal("u:NEW-", label.GetProperty("/P").GetString());
        Assert.Equal(1, Value(table, catalog.GetProperty("/PrivateLabels")).GetProperty("/Nums").GetArrayLength());
        Assert.Equal("1.3", report.RootElement.GetProperty("qpdf")[0].GetProperty("pdfversion").GetString());
    }

    [Fact]
    public async Task ReturnedMetadataPolicyUsesTheSameActiveInfoRole()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "ordinary"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", "--remove-info"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument report = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement info = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Info"));
        Assert.Equal("/ModDate", Assert.Single(info.EnumerateObject()).Name);
    }

    [Fact]
    public async Task AuthenticatedExportKeepsTheDecryptedDate()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "ordinary"));
        QpdfPath encrypted = QpdfPath.From("encrypted");
        QpdfExecutionResult rewrite = await Qpdf.ExecuteAsync(host,
            new(["input", "--encrypt", "reader", "owner", "256", "--", encrypted.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, rewrite.ExitCode);
        Assert.True(host.TryGetOutput(encrypted, out byte[] bytes));
        host.AddInput(encrypted, bytes, QpdfInputOwnership.Transfer);
        using JsonDocument report = await Run(host, [encrypted.Value, "--password=reader", "--json=2", "--remove-info"]);
        Assert.True(report.RootElement.GetProperty("encrypt").GetProperty("encrypted").GetBoolean());
        Assert.True(report.RootElement.GetProperty("encrypt").GetProperty("userpasswordmatched").GetBoolean());
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement trailer = table.GetProperty("trailer").GetProperty("value");
        JsonElement info = Value(table, trailer.GetProperty("/Info"));
        JsonProperty date = Assert.Single(info.EnumerateObject());
        Assert.Equal("/ModDate", date.Name);
        Assert.Equal("u:D:20260101000000Z", Value(table, date.Value).GetString());
        JsonElement catalog = Value(table, trailer.GetProperty("/Root"));
        Assert.False(catalog.TryGetProperty("/Metadata", out _));
    }

    private static InMemoryQpdfHost Host(byte[] source)
    {
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer); return host;
    }

    private static async Task<JsonDocument> Run(InMemoryQpdfHost host, IReadOnlyList<string> arguments)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([.. arguments, "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); return JsonDocument.Parse(bytes);
    }

    private static JsonElement Value(JsonElement table, JsonElement value)
        => value.ValueKind == JsonValueKind.String ? table.GetProperty("obj:" + value.GetString()).GetProperty("value") : value;

    private static void AssertPrivate(JsonElement table, JsonElement catalog, int generation)
    {
        JsonElement aliases = catalog.GetProperty("/Private");
        Assert.Equal("u:INFO-TITLE", Value(table, aliases.GetProperty("/Info")).GetProperty("/Title").GetString());
        Assert.True(Value(table, aliases.GetProperty("/Form")).TryGetProperty("/Fields", out _));
        Assert.Equal("/StructTreeRoot", Value(table, aliases.GetProperty("/Structure")).GetProperty("/Type").GetString());
        Assert.Equal("u:OLD-", Value(table, aliases.GetProperty("/Labels")).GetProperty("/Nums")[1].GetProperty("/P").GetString());
        JsonElement originalCatalog = Value(table, aliases.GetProperty("/Catalog"));
        Assert.Equal("8 " + generation + " R", originalCatalog.GetProperty("/Metadata").GetString());
        Assert.Equal("7 " + generation + " R", originalCatalog.GetProperty("/PageLabels").GetString());
    }
}
