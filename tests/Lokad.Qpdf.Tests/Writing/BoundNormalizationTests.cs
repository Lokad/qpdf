using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class BoundNormalizationTests
{
    private static readonly QpdfPath Graph = QpdfPath.From("graph.json"), Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Original = "q\r\n0 0 1 1 re f\r\nQ\r\n"u8.ToArray();
    private static readonly byte[] Normalized = "q\n0 0 1 1 re f\nQ\n"u8.ToArray();

    private static byte[] Model(bool alias, bool identityCrypt)
    {
        JsonObject entries = new()
        {
            ["obj:1 0 R"] = JsonNode.Parse(alias
                ? """{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Audit":"4 7 R"}}"""
                : """{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}}"""),
            ["obj:2 0 R"] = JsonNode.Parse("""
                {"value":{"/Type":"/Pages","/Kids":["3 0 R","5 0 R"],"/Count":2,"/MediaBox":[0,0,100,100],"/Resources":{}}}
                """),
            ["obj:3 0 R"] = JsonNode.Parse("""{"value":{"/Type":"/Page","/Parent":"2 0 R","/Contents":"4 7 R"}}"""),
            ["obj:4 7 R"] = Stream(Original, identityCrypt),
            ["obj:5 0 R"] = JsonNode.Parse("""{"value":{"/Type":"/Page","/Parent":"2 0 R","/Contents":"4 7 R"}}"""),
            ["obj:8 0 R"] = JsonNode.Parse("""{"value":"u:detached"}"""),
            ["trailer"] = JsonNode.Parse("""{"value":{"/Root":"1 0 R","/Size":9}}"""),
        };
        return System.Text.Encoding.UTF8.GetBytes(new JsonObject
        {
            ["qpdf"] = new JsonArray(new JsonObject { ["jsonversion"] = 2, ["pdfversion"] = "1.7" }, entries),
        }.ToJsonString());
    }

    private static JsonObject Stream(byte[] bytes, bool identityCrypt)
        => new() { ["stream"] = new JsonObject
        {
            ["dict"] = identityCrypt ? JsonNode.Parse("""{"/Filter":"/Crypt","/DecodeParms":{"/Name":"/Identity"}}""") : new JsonObject(),
            ["data"] = Convert.ToBase64String(bytes),
        } };

    [Theory]
    [InlineData("y")]
    [InlineData("n")]
    public async Task CreationNormalizesTheSharedPageProgramAndPreservesTheAlias(string setting)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Graph, Model(true, false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Create(host, QpdfLimits.Default, "--normalize-content=" + setting);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using (QpdfDocument document = await Read(host, Output))
        {
            Assert.Equal(2, document.PageCount);
            PdfReference active = Assert.Single(document.PageReferences.Select(page => Link(page.Page, "Contents")).Distinct());
            PdfReference alias = Link(Catalog(document), "Audit");
            Assert.Equal(setting == "n", active.Equals(alias));
            Assert.Equal(setting == "y" ? Normalized : Original, Bytes(document, active));
            Assert.Equal(Original, Bytes(document, alias));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task UpdateCoalescesAndNormalizesThePatchedProgramBeforeSplitting()
    {
        byte[] prefix = "q\r\n0 0 2 2 re f\r\n"u8.ToArray();
        byte[] ending = "Q\r\n"u8.ToArray();
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("source.pdf"), PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 4 7 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 /MediaBox [0 0 100 100] /Resources <<>> >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>", null),
            (4, $"<< /Length {Original.Length} >>", Original),
            (5, "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>", null),
            (6, "[4 7 R 7 0 R]", null),
            (7, $"<< /Length {ending.Length} >>", ending)], 1, new Dictionary<int, int> { [4] = 7 }), QpdfInputOwnership.Transfer);
        string patch = $$"""{"qpdf":[{"jsonversion":2},{"obj:4 7 R":{{Stream(prefix, false).ToJsonString()}}}]}""";
        host.AddInput(Graph, System.Text.Encoding.UTF8.GetBytes(patch), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"source.pdf","updateFromJson":"graph.json","outputFile":"part.pdf",
             "normalizeContent":"y","coalesceContents":"","splitPages":"1","streamData":"preserve"}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        foreach (string part in new[] { "part-1.pdf", "part-2.pdf" })
        {
            await using QpdfDocument document = await Read(host, QpdfPath.From(part));
            PdfReference active = Link(Assert.Single(document.PageReferences).Page, "Contents");
            PdfReference alias = Link(Catalog(document), "Audit");
            Assert.NotEqual(active, alias);
            Assert.Equal("q\n0 0 2 2 re f\nQ\n"u8.ToArray(), Bytes(document, active));
            Assert.Equal(prefix, Bytes(document, alias));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NormalizedIdentityCryptProgramReplacesTheOldStreamBeforeEncryption()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Graph, Model(false, true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Create(host, QpdfLimits.Default, "--normalize-content=y", "--preserve-unreferenced",
            "--encrypt", "reader", "owner", "256", "--");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Output, out byte[] pdf));
        QpdfPath read = QpdfPath.From("read");
        host.AddInput(read, pdf, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, QpdfPassword.FromText("reader"),
            QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(6, document.Encryption?.Revision);
        PdfReference active = Assert.Single(document.PageReferences.Select(page => Link(page.Page, "Contents")).Distinct());
        Assert.Equal(Normalized, Bytes(document, active));
        QpdfObject[] values = document.Engine.XRefEntries.Keys.Where(id => id != 0).Select(document.GetObject).ToArray();
        Assert.Single(values.OfType<PdfStream>());
        Assert.Single(values.OfType<PdfByteString>(), value => value.Value.AsSpan().SequenceEqual("detached"u8));
    }

    [Fact]
    public async Task NormalizationLimitLeavesThePriorTargetAndClosesInputs()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        host.Inner.AddInput(Graph, Model(true, false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = Normalized.Length - 1 };
        QpdfExecutionResult result = await Create(host, limits, "--normalize-content=y");
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded
            && item.Message.Contains(nameof(QpdfLimits.MaxDecodedBytes), StringComparison.Ordinal));
        Assert.Empty(host.CreatedPaths);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] kept));
        Assert.Equal("prior"u8.ToArray(), kept);
        ExecutionJobFixtures.AssertClosed(host, result);
        using InMemoryQpdfHost disabled = new();
        disabled.AddInput(Graph, Model(true, false), QpdfInputOwnership.Transfer);
        Assert.Equal(QpdfExitCode.Success, (await Create(disabled, limits, "--normalize-content=n")).ExitCode);
    }

    private static ValueTask<QpdfExecutionResult> Create(IQpdfHost host, QpdfLimits limits, params string[] flags)
        => Qpdf.ExecuteAsync(host, new([Graph.Value, "--json-input", "--stream-data=preserve", .. flags, Output.Value]),
            new(limits), CancellationToken.None);

    private static async ValueTask<QpdfDocument> Read(InMemoryQpdfHost host, QpdfPath output)
    {
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        QpdfPath input = QpdfPath.From("read-" + output.Value);
        host.AddInput(input, bytes, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, input, CancellationToken.None);
    }

    private static PdfDictionary Catalog(QpdfDocument document)
        => Assert.IsType<PdfDictionary>(document.GetObject(Link(document.Engine.Trailer, "Root")));

    private static PdfReference Link(PdfDictionary dictionary, string name)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value));
        return Assert.IsType<PdfReference>(value);
    }

    private static byte[] Bytes(QpdfDocument document, PdfReference reference)
        => StreamObservation.Decode(Assert.IsType<PdfStream>(document.GetObject(reference)));
}
