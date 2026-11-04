using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class PredictorApplicabilityTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 600).Select(index => (byte)((index * 73) ^ (index >> 3))).ToArray();

    private static string Geometry(string field) => field switch
    {
        "columns" => "/Columns 0", "colors" => "/Colors 0", "bits" => "/BitsPerComponent 3",
        "wide" => "/Columns 2147483648", "type" => "/BitsPerComponent /Ignored", "cycle" => "/Columns 7 0 R", _ => "",
    };

    public static IEnumerable<object[]> DormantCases()
    {
        foreach (string codec in new[] { "FlateDecode", "LZWDecode" })
        foreach (bool explicitPredictor in new[] { false, true })
        foreach (string field in new[] { "columns", "colors", "bits", "wide", "type", "cycle" })
        foreach (bool indirect in new[] { false, true }) { yield return [codec, explicitPredictor, field, indirect]; }
    }

    private static byte[] Fixture(string codec, string fields, bool indirect, bool earlyChange)
    {
        byte[] encoded;
        if (codec == "LZWDecode") { encoded = LzwTests.LzwEncode(Payload, earlyChange); }
        else
        {
            using MemoryStream buffer = new();
            using (ZLibStream zlib = new(buffer, CompressionLevel.SmallestSize, true)) { zlib.Write(Payload); }
            encoded = buffer.ToArray();
        }
        string parameters = "<< " + fields + " >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 5 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Length " + encoded.Length + " /Filter /" + codec + " /DecodeParms " + (indirect ? "6 0 R" : parameters) + " >>", encoded),
            (6, parameters, null), (7, fields.Contains("7 0 R", StringComparison.Ordinal) ? "7 0 R" : "null", null),
        ], 1);
    }

    private static async Task VerifyFiltered(byte[] input)
    {
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"showObject\":\"5\",\"filteredStreamData\":true}", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--show-object=5", "--filtered-stream-data", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(Payload, output);
            Assert.Equal(Payload.Length, result.Counters.TotalDecodedBytes); Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [MemberData(nameof(DormantCases))]
    public async Task DormantGeometryDoesNotChangeDecodedBytes(string codec, bool explicitPredictor, string field, bool indirect)
    {
        await VerifyFiltered(Fixture(codec, (explicitPredictor ? "/Predictor 1 " : "") + Geometry(field), indirect, true));
    }

    public static IEnumerable<object[]> WidthModes()
    {
        foreach (bool earlyChange in new[] { false, true })
        foreach (string field in new[] { "control", "columns", "colors", "bits" })
        foreach (bool indirect in new[] { false, true }) { yield return [earlyChange, field, indirect]; }
    }

    [Theory]
    [MemberData(nameof(WidthModes))]
    public async Task DormantGeometryDoesNotDisableLzwWidthMode(bool earlyChange, string field, bool indirect)
    {
        await VerifyFiltered(Fixture("LZWDecode", "/Predictor 1 /EarlyChange " + (earlyChange ? 1 : 0) + Geometry(field), indirect, earlyChange));
    }

    [Theory]
    [InlineData("FlateDecode")]
    [InlineData("LZWDecode")]
    public async Task DecodedCeilingStillRefusesBeforePublication(string codec)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(codec, "/Predictor 1 /Columns 0", false, true), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--show-object=5", "--filtered-stream-data", "--", "output"]),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxDecodedBytes = Payload.Length - 1 }), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.BudgetExceeded && x.Message.Contains(nameof(QpdfLimits.MaxDecodedBytes), StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("/Invalid")]
    public async Task LzwEarlyChangeRemainsApplicableWithoutPrediction(string value)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("LZWDecode", "/Predictor 1 /Columns 0 /EarlyChange " + value, false, true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--show-object=5", "--filtered-stream-data"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, x => x.Message.Contains("EarlyChange", StringComparison.Ordinal)); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static JsonDocument Corpus()
    {
        using Stream stream = typeof(PredictorApplicabilityTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Filters.PredictorApplicabilityCases.json") ?? throw new InvalidOperationException();
        return JsonDocument.Parse(stream);
    }

    public static IEnumerable<object[]> CompressedMembers()
    {
        using JsonDocument corpus = Corpus();
        foreach (JsonElement item in corpus.RootElement.GetProperty("Cases").EnumerateArray())
        { yield return [item.GetProperty("Id").GetString() ?? throw new InvalidOperationException(), item.GetProperty("Role").GetString() ?? throw new InvalidOperationException()]; }
    }

    private static byte[] CompressedFixture(string id)
    {
        using JsonDocument corpus = Corpus();
        JsonElement item = corpus.RootElement.GetProperty("Cases").EnumerateArray().Single(x => x.GetProperty("Id").GetString() == id);
        string sha = item.GetProperty("SHA256").GetString() ?? throw new InvalidOperationException();
        byte[] bytes = Convert.FromBase64String(corpus.RootElement.GetProperty("Files").GetProperty(sha).GetString() ?? throw new InvalidOperationException());
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(bytes))); return bytes;
    }

    [Theory]
    [MemberData(nameof(CompressedMembers))]
    public async Task CompressedMetadataAttachmentAndPageMembersKeepTheirLifetimes(string id, string role)
    {
        RecordingMemoryBudget budget = new(16L * 1048576L); using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("input"), CompressedFixture(id), QpdfInputOwnership.Transfer);
        PdfDictionary member;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        { member = Assert.IsType<PdfDictionary>(document.GetObject(6)); Assert.IsType<PdfStream>(document.GetObject(7)); }
        host.Dispose();
        string key = role == "info" ? "Title" : role == "attachment" ? "F" : "Marker";
        Assert.True(member.TryGet(PdfName.FromAscii(key), out QpdfObject marker));
        Assert.Equal("Keep"u8.ToArray(), Assert.IsType<PdfByteString>(marker).CopyValue(owner, CancellationToken.None));
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(CompressedMembers))]
    public async Task RewritingCompressedMemberRolesPreservesTheirValues(string id, string role)
    {
        foreach (string layout in new[] { "plain", "packed", "linear" })
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), CompressedFixture(id), QpdfInputOwnership.Transfer);
            List<string> arguments = ["input"]; Dictionary<string, object> values = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
            if (layout == "packed") { arguments.Add("--object-streams=generate"); values["objectStreams"] = "generate"; }
            if (layout == "linear") { arguments.Add("--linearize"); values["linearize"] = true; }
            arguments.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(values), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None))
            {
                PdfReference root = Assert.IsType<PdfReference>(document.Engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject rootValue) ? rootValue : null);
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(root.ObjectNumber));
                PdfReference audit = Assert.IsType<PdfReference>(catalog.TryGet(PdfName.FromAscii("Audit"), out QpdfObject auditValue) ? auditValue : null);
                PdfDictionary member = Assert.IsType<PdfDictionary>(document.GetObject(audit.ObjectNumber));
                Assert.True(member.TryGet(PdfName.FromAscii(role == "info" ? "Title" : role == "attachment" ? "F" : "Marker"), out QpdfObject marker));
                Assert.Equal("Keep"u8.ToArray(), Assert.IsType<PdfByteString>(marker).Value);
            }
            if (role == "attachment")
            {
                await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("copy"), "K", QpdfPath.From("data"), CancellationToken.None);
                Assert.True(host.TryGetOutput(QpdfPath.From("data"), out byte[] data)); Assert.Equal("payload\0\u00ff".Select(x => (byte)x).ToArray(), data);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }
}
