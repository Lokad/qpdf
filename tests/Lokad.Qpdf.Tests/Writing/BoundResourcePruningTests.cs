using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class BoundResourcePruningTests
{
    private static readonly QpdfPath ModelPath = QpdfPath.From("graph.json");
    private static readonly byte[] Used = "BT /Used 12 Tf 10 10 Td (first) Tj ET\n"u8.ToArray();
    private static readonly byte[] Patched = "BT /Unused 12 Tf 10 10 Td (patched) Tj ET\n"u8.ToArray();

    [Theory]
    [InlineData("inherited", "", true, true)]
    [InlineData("shared", "auto", true, true)]
    [InlineData("distinct", "auto", true, false)]
    [InlineData("distinct", "yes", true, true)]
    [InlineData("inherited", "no", true, false)]
    [InlineData("inherited", "yes", false, false)]
    public async Task CreationPrunesBySplitPolicyAndKeepsPrivateResources(string shape, string mode, bool split, bool prune)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(ModelPath, Model(shape, false), QpdfInputOwnership.Transfer);
        List<string> arguments = [ModelPath.Value, "--json-input", "--stream-data=preserve"];
        if (mode.Length != 0) { arguments.Add("--remove-unreferenced-resources=" + mode); }
        if (split) { arguments.Add("--split-pages=1"); }
        arguments.Add("output.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        foreach (string part in split ? new[] { "output-1.pdf", "output-2.pdf" } : new[] { "output.pdf" })
        {
            await using QpdfDocument document = await Read(host, part);
            Assert.Equal(split ? 1 : 2, document.PageCount);
            foreach (var page in document.PageReferences)
            {
                PdfDictionary active = FindResources(document, page.Page);
                ExpectFonts(document, active, true, !prune);
            }
            ExpectFonts(document, Dictionary(document, Catalog(document), "Audit"), true, true);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task JobUpdatePrunesThePatchedProgramWithinEachPart()
    {
        using InMemoryQpdfHost host = new();
        byte[] pdf = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 7 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] /Resources 7 0 R /MediaBox [0 0 200 200] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Contents 5 7 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>", null),
            (5, $"<< /Length {Used.Length} >>", Used), (6, $"<< /Length {Used.Length} >>", Used),
            (7, "<< /Font << /Used 9 0 R /Unused 10 0 R >> >>", null), (8, "null", null),
            (9, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (10, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null)], 1, new Dictionary<int, int> { [5] = 7 });
        host.AddInput(QpdfPath.From("source.pdf"), pdf, QpdfInputOwnership.Transfer);
        string payload = Convert.ToBase64String(Patched);
        host.AddInput(ModelPath, Encoding.UTF8.GetBytes($$$$"""
            {"qpdf":[{"jsonversion":2},{"obj:5 7 R":{"stream":{"dict":{},"data":"{{{{payload}}}}"}}}]}
            """), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"source.pdf","updateFromJson":"graph.json","outputFile":"part.pdf",
             "splitPages":"1","objectStreams":"generate","removeUnreferencedResources":"yes","preserveUnreferenced":""}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        for (int index = 1; index <= 2; index++)
        {
            await using QpdfDocument document = await Read(host, $"part-{index}.pdf");
            PdfDictionary page = Assert.Single(document.PageReferences).Page;
            ExpectFonts(document, FindResources(document, page), index == 2, index == 1);
            ExpectFonts(document, Dictionary(document, Catalog(document), "Audit"), true, true);
            PdfStream program = Assert.IsType<PdfStream>(Resolve(document, Value(page, "Contents")));
            Assert.Equal(index == 1 ? Patched : Used, StreamObservation.Decode(program));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SplitPrunesTheActiveFormAndKeepsItsPrivateAlias()
    {
        JsonObject graph = Assert.IsType<JsonObject>(JsonNode.Parse(Model("inherited", false)));
        JsonObject table = Assert.IsType<JsonObject>(graph["qpdf"]?[1]);
        JsonObject catalog = Assert.IsType<JsonObject>(table["obj:1 0 R"]?["value"]);
        catalog["/ArchivedForm"] = "11 0 R";
        JsonObject resources = Assert.IsType<JsonObject>(table["obj:7 0 R"]?["value"]);
        resources["/XObject"] = new JsonObject { ["/Stamp"] = "11 0 R" };
        string program = Convert.ToBase64String(Used);
        table["obj:11 0 R"] = JsonNode.Parse($$$$"""
            {"stream":{"dict":{"/Type":"/XObject","/Subtype":"/Form","/BBox":[0,0,50,50],
              "/Resources":{"/Font":{"/Used":"9 0 R","/Unused":"10 0 R"}}},"data":"{{{{program}}}}"}}
            """);
        Assert.IsType<JsonObject>(table["trailer"]?["value"])["/Size"] = 12;
        JsonObject contents = Assert.IsType<JsonObject>(table["obj:5 0 R"]?["stream"]);
        contents["data"] = Convert.ToBase64String("q /Stamp Do Q\n"u8);
        using InMemoryQpdfHost host = new();
        host.AddInput(ModelPath, Encoding.UTF8.GetBytes(graph.ToJsonString()), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([ModelPath.Value, "--json-input", "--split-pages=1",
            "--remove-unreferenced-resources=yes", "output.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        foreach (string part in new[] { "output-1.pdf", "output-2.pdf" })
        {
            await using QpdfDocument document = await Read(host, part);
            PdfDictionary page = Assert.Single(document.PageReferences).Page;
            PdfReference active = Assert.IsType<PdfReference>(Value(Dictionary(document, FindResources(document, page), "XObject"), "Stamp"));
            PdfReference alias = Assert.IsType<PdfReference>(Value(Catalog(document), "ArchivedForm"));
            Assert.NotEqual(active, alias);
            PdfStream activeForm = Assert.IsType<PdfStream>(document.GetObject(active));
            PdfStream originalForm = Assert.IsType<PdfStream>(document.GetObject(alias));
            ExpectFonts(document, Dictionary(document, activeForm.Dictionary, "Resources"), true, false);
            ExpectFonts(document, Dictionary(document, originalForm.Dictionary, "Resources"), true, true);
            Assert.Equal(Used, StreamObservation.Decode(activeForm));
            Assert.Equal(Used, StreamObservation.Decode(originalForm));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task DecodeLimitDuringSplitPruningPreservesEveryTarget()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        byte[] graph = Model("inherited", true);
        host.Inner.AddInput(ModelPath, graph, QpdfInputOwnership.Copy);
        QpdfPath[] targets = [QpdfPath.From("part-1.pdf"), QpdfPath.From("part-2.pdf")];
        foreach (QpdfPath target in targets)
        {
            await using IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(target, CancellationToken.None);
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionOptions limits = new(QpdfLimits.Default with { MaxDecodedBytes = Used.Length - 1 });
        string[] invocation = [ModelPath.Value, "--json-input", "--split-pages=1", "--stream-data=preserve"];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([.. invocation,
            "--remove-unreferenced-resources=yes", "part.pdf"]), limits, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded
            && item.Message.Contains(nameof(QpdfLimits.MaxDecodedBytes), StringComparison.Ordinal));
        Assert.Empty(host.CreatedPaths);
        foreach (QpdfPath target in targets)
        {
            Assert.True(host.Inner.TryGetOutput(target, out byte[] prior));
            Assert.Equal("prior"u8.ToArray(), prior);
        }
        ExecutionJobFixtures.AssertClosed(host, result);
        using InMemoryQpdfHost disabled = new();
        disabled.AddInput(ModelPath, graph, QpdfInputOwnership.Transfer);
        result = await Qpdf.ExecuteAsync(disabled, new([.. invocation, "--remove-unreferenced-resources=no", "part.pdf"]),
            limits, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    private static byte[] Model(string shape, bool compressed)
    {
        const string shared = ",\"/Resources\":\"7 0 R\"";
        string parent = shape == "inherited" ? shared : "";
        string first = shape == "inherited" ? "" : shared;
        string last = shape == "distinct" ? ",\"/Resources\":\"8 0 R\"" : first;
        string header = compressed ? "\"/Filter\":\"/FlateDecode\"" : "";
        byte[] bytes = Used;
        if (compressed)
        {
            using MemoryStream output = new();
            using (ZLibStream encoder = new(output, CompressionLevel.Optimal, true)) { encoder.Write(Used); }
            bytes = output.ToArray();
        }
        string body = Convert.ToBase64String(bytes);
        return Encoding.UTF8.GetBytes($$$$"""
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Audit":"7 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":2,"/Kids":["3 0 R","4 0 R"],"/MediaBox":[0,0,200,200]{{{{parent}}}}}},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/Contents":"5 0 R"{{{{first}}}}}},
              "obj:4 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/Contents":"5 0 R"{{{{last}}}}}},
              "obj:5 0 R":{"stream":{"dict":{ {{{{header}}}} },"data":"{{{{body}}}}"}},
              "obj:7 0 R":{"value":{"/Font":{"/Used":"9 0 R","/Unused":"10 0 R"}}},
              "obj:8 0 R":{"value":{"/Font":{"/Used":"9 0 R","/Unused":"10 0 R"}}},
              "obj:9 0 R":{"value":{"/Type":"/Font","/Subtype":"/Type1","/BaseFont":"/Helvetica"}},
              "obj:10 0 R":{"value":{"/Type":"/Font","/Subtype":"/Type1","/BaseFont":"/Courier"}},
              "trailer":{"value":{"/Root":"1 0 R","/Size":11}}
            }]}
            """);
    }

    private static async ValueTask<QpdfDocument> Read(InMemoryQpdfHost host, string part)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(part), out byte[] pdf));
        QpdfPath input = QpdfPath.From("read-" + part);
        host.AddInput(input, pdf, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, input, CancellationToken.None);
    }

    private static PdfDictionary FindResources(QpdfDocument document, PdfDictionary page)
    {
        if (page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject raw))
        { return Assert.IsType<PdfDictionary>(Resolve(document, raw)); }
        return Dictionary(document, Dictionary(document, page, "Parent"), "Resources");
    }

    private static void ExpectFonts(QpdfDocument document, PdfDictionary resources, bool used, bool unused)
    {
        PdfDictionary fonts = Dictionary(document, resources, "Font");
        Assert.Equal(used, fonts.TryGet(PdfName.FromAscii("Used"), out _));
        Assert.Equal(unused, fonts.TryGet(PdfName.FromAscii("Unused"), out _));
    }

    private static PdfDictionary Catalog(QpdfDocument document) => Dictionary(document, document.Engine.Trailer, "Root");

    private static PdfDictionary Dictionary(QpdfDocument document, PdfDictionary owner, string key)
        => Assert.IsType<PdfDictionary>(Resolve(document, Value(owner, key)));

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference) : value;

    private static QpdfObject Value(PdfDictionary owner, string key)
    {
        Assert.True(owner.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }
}
