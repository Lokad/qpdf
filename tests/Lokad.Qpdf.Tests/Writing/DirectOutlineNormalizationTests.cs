using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class DirectOutlineNormalizationTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output"), Json = QpdfPath.From("model.json");

    private static byte[] Source(bool direct, bool populated, bool open, bool aliases)
    {
        string outline = "<< /Type /Outlines /Probe (KEEP) "
            + (populated ? "/Count 1 /First 9 0 R /Last 9 0 R" : "/Count 0") + " >>";
        byte[] first = "BT /F1 12 Tf 1 1 Td (One) Tj ET\n"u8.ToArray(), second = "BT /F1 12 Tf 1 1 Td (Two) Tj ET\n"u8.ToArray();
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 10 0 R /Outlines " + (direct ? outline : "5 0 R")
                + (open ? " /PageMode /UseOutlines" : "") + (aliases ? " /OutlineAlias 5 0 R" : "") + " >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 8 0 R >> >> /Contents 6 0 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 8 0 R >> >> /Contents 7 0 R >>", null),
            (5, outline, null), (6, $"<< /Length {first.Length} >>", first), (7, $"<< /Length {second.Length} >>", second),
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (9, "<< /Title (Chapter) /Dest [3 0 R /Fit] " + (direct ? "" : "/Parent 5 0 R") + " >>", null), (10, "17", null)
        ], 1);
    }

    private static QpdfObject Value(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }

    private static async Task Verify(InMemoryQpdfHost host, bool populated, bool packed, bool linearized, bool reordered)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(Output, bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(Value(document.Engine.Trailer, "Root"))));
            PdfReference reference = Assert.IsType<PdfReference>(Value(catalog, "Outlines"));
            PdfDictionary outlines = Assert.IsType<PdfDictionary>(document.GetObject(reference));
            Assert.Equal("KEEP"u8.ToArray(), Assert.IsType<PdfByteString>(Value(outlines, "Probe")).Value);
            Assert.Equal(packed, document.Engine.XRefEntries.Values.Any(entry => entry.Kind == XrefEntryKind.Compressed));
            if (packed && !linearized) { Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[reference.ObjectNumber].Kind); }
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(linearized ? LinearizationStatus.Valid : LinearizationStatus.NotLinearized,
                LinearizationCheck.Inspect(document.Engine, 2, QpdfLimits.Default, governor, CancellationToken.None).Status);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        QpdfJsonResult report = await Qpdf.GetDocumentJsonAsync(host, Output, new QpdfJsonOptions(2, ["outlines"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(report.Json);
        JsonElement items = json.RootElement.GetProperty("outlines");
        Assert.Equal(populated ? 1 : 0, items.GetArrayLength());
        if (populated)
        { Assert.Equal("Chapter", items[0].GetProperty("title").GetString()); Assert.Equal(reordered ? 2 : 1, items[0].GetProperty("destpageposfrom1").GetInt32()); }
        Assert.True((await Qpdf.CheckAsync(host, Output, CancellationToken.None)).Passed);
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(reordered ? "Two" : "One", independent.GetPage(1).Text);
        Assert.Equal(reordered ? "One" : "Two", independent.GetPage(2).Text);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> Shapes()
    {
        foreach (bool direct in new[] { false, true })
        foreach (bool populated in new[] { false, true })
        foreach (bool open in new[] { false, true })
        foreach (bool packed in new[] { false, true }) { yield return [direct, populated, open, packed]; }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task CopyingPromotesOnlyTheOutlineRootBeforeLinearization(bool direct, bool populated, bool open, bool packed)
    {
        byte[] source = Source(direct, populated, open, false), before = (byte[])source.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, source, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, "--linearize", "--object-streams=" + (packed ? "generate" : "disable"), Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        await Verify(host, populated, packed, true, false); Assert.Equal(before, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionRemapsDestinationsWhilePrivateRootAliasesKeepTheirIdentity(bool direct)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Source(direct, true, false, true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, "--linearize", "--object-streams=generate", "--pages", ".", "2,1", "--", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); await Verify(host, true, true, true, true);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(Value(document.Engine.Trailer, "Root"))));
        PdfReference outline = Assert.IsType<PdfReference>(Value(catalog, "Outlines")), alias = Assert.IsType<PdfReference>(Value(catalog, "OutlineAlias"));
        Assert.Equal(!direct, outline.Equals(alias));
        Assert.Equal("KEEP"u8.ToArray(), Assert.IsType<PdfByteString>(Value(Assert.IsType<PdfDictionary>(document.GetObject(alias)), "Probe")).Value);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task AdaptersUseTheSameDirectOutlinePreparation(string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Source(true, true, true, false), QpdfInputOwnership.Transfer);
        const string job = """{"inputFile":"input","outputFile":"output","linearize":true,"objectStreams":"generate"}""";
        QpdfExecutionResult result;
        if (route == "job") { result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None); }
        else
        { host.AddInput(Json, Encoding.UTF8.GetBytes(job), QpdfInputOwnership.Transfer); result = await Qpdf.ExecuteAsync(host, new(["--job-json-file=" + Json.Value]), CancellationToken.None); }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); await Verify(host, true, true, true, false);
    }

    private static byte[] Model()
    {
        Dictionary<string, object> outlines = new() { ["/Type"] = "/Outlines", ["/Probe"] = "u:KEEP", ["/Count"] = 1, ["/First"] = "9 0 R", ["/Last"] = "9 0 R" };
        Dictionary<string, object> font = new() { ["/Font"] = new Dictionary<string, object> { ["/F1"] = "8 0 R" } };
        Dictionary<string, object> objects = new()
        {
            ["obj:1 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R", ["/Outlines"] = outlines, ["/Private"] = outlines, ["/Audit"] = "10 0 R" } },
            ["obj:2 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Count"] = 2, ["/Kids"] = new[] { "3 0 R", "4 0 R" } } },
            ["obj:8 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Font", ["/Subtype"] = "/Type1", ["/BaseFont"] = "/Helvetica" } },
            ["obj:9 0 R"] = new { value = new Dictionary<string, object> { ["/Title"] = "u:Chapter", ["/Dest"] = new[] { "3 0 R", "/Fit" } } },
            ["obj:10 0 R"] = new { value = 17 }, ["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = "1 0 R", ["/Size"] = 11 } }
        };
        for (int index = 0; index < 2; index++)
        {
            objects[$"obj:{3 + index} 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/MediaBox"] = new[] { 0, 0, 100, 100 }, ["/Resources"] = font, ["/Contents"] = $"{6 + index} 0 R" } };
            objects[$"obj:{6 + index} 0 R"] = new { stream = new { dict = new Dictionary<string, object>(), data = Convert.ToBase64String(Encoding.ASCII.GetBytes("BT /F1 12 Tf 1 1 Td (" + (index == 0 ? "One" : "Two") + ") Tj ET\n")) } };
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.7" }, objects } });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task JsonCreationAndUpdatePromoteTheCatalogRoleBeforePacking(bool update, bool packed, bool linearized)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Source(true, true, false, false), QpdfInputOwnership.Transfer);
        byte[] model = Model(), before = (byte[])model.Clone(); host.AddInput(Json, model, QpdfInputOwnership.Transfer);
        QpdfJsonImportOptions options = new(packed ? "generate" : "disable", linearized);
        QpdfJsonImportResult result = update
            ? await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, options, null, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None)
            : await Qpdf.CreatePdfFromJsonAsync(host, Json, Output, options, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(2, result.PageCount); Assert.Equal(0L, result.Counters.OpenHandles);
        await Verify(host, true, packed, linearized, false); Assert.Equal(before, model);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(Value(document.Engine.Trailer, "Root"))));
        Assert.Equal("KEEP"u8.ToArray(), Assert.IsType<PdfByteString>(Value(Assert.IsType<PdfDictionary>(Value(catalog, "Private")), "Probe")).Value);
    }

    [Theory]
    [InlineData("17")]
    [InlineData("[5 0 R]")]
    public async Task NonDictionaryDirectOutlinesRetainExplicitFailureAndPriorOutput(string raw)
    {
        byte[] source = PdfTestDocument.Assemble([(1, "<< /Type /Catalog /Pages 2 0 R /Outlines " + raw + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null), (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>", null)], 1);
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(source, Output);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, "--linearize", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.True(host.TryGetOutput(Output, out byte[] prior)); Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PromotionRetiresOnEveryAdmissionAndPublicationFailure(bool json)
        => PreparationAdmissionChecks.VerifyAsync(async () =>
            {
                InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Source(true, true, true, false), Output);
                if (json) { host.AddInput(Json, Model(), QpdfInputOwnership.Transfer); }
                return host;
            },
            (host, token) => Qpdf.ExecuteAsync(host, json
                ? new([Json.Value, "--json-input", "--linearize", "--object-streams=generate", Output.Value])
                : new([Input.Value, "--linearize", "--object-streams=generate", Output.Value]), token),
            Output, 256, ["create", "write", "commit", "cancel", "close"]);
}
