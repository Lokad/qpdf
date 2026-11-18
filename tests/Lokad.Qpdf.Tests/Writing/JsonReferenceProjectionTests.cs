using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Reading;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonReferenceProjectionTests
{
    private sealed class DelegatingHost(InMemoryQpdfHost storage) : IQpdfHost
    {
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
    private const string Fields = "/LiteralNull null /Live 6 7 R /Stale 6 0 R /Free 8 1 R /Missing 99 0 R /Null 7 7 R"
        + " /Array [null 6 7 R 6 0 R 8 0 R 99 0 R 7 7 R] /Nested << /Live 6 7 R /Stale 6 0 R /Null 7 7 R >> /Self 4 0 R";

    internal static byte[] Fixture(bool malformed)
    {
        byte[] pdf = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 4 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> /Contents 5 0 R /Audit 4 0 R >>", null),
            (4, "<< " + Fields + " >>", null),
            (5, "<< /Length 3 /Filter 7 7 R " + Fields + " >>", "q Q"u8.ToArray()),
            (6, malformed ? "(unterminated" : "<< /Marker (live) >>", null),
            (7, "null", null), (8, "null", null)], 1,
            new Dictionary<int, int> { [6] = 7, [7] = 7, [8] = 1 }, "1.7", " " + Fields);
        string text = Encoding.ASCII.GetString(pdf);
        // Turn only object 8's xref entry into a free entry, retaining offsets.
        return Encoding.ASCII.GetBytes(Regex.Replace(text, @"(?m)^\d{10} 00001 n $", "0000000000 00001 f "));
    }

    private static void AssertFields(JsonElement dictionary)
    {
        foreach (string name in new[] { "/LiteralNull", "/Stale", "/Free", "/Missing", "/Null" })
        { Assert.False(dictionary.TryGetProperty(name, out _), name); }
        Assert.Equal("6 7 R", dictionary.GetProperty("/Live").GetString());
        Assert.Equal("4 0 R", dictionary.GetProperty("/Self").GetString());
        JsonElement[] values = dictionary.GetProperty("/Array").EnumerateArray().ToArray();
        Assert.Equal(6, values.Length); Assert.Equal("6 7 R", values[1].GetString());
        Assert.Equal("7 7 R", values[5].GetString());
        foreach (int index in new[] { 0, 2, 3, 4 }) { Assert.Equal(JsonValueKind.Null, values[index].ValueKind); }
        JsonElement nested = dictionary.GetProperty("/Nested");
        Assert.Equal("/Live", Assert.Single(nested.EnumerateObject()).Name);
        Assert.Equal("6 7 R", nested.GetProperty("/Live").GetString());
    }

    [Theory]
    [InlineData(1, "none")]
    [InlineData(2, "none")]
    [InlineData(2, "inline")]
    [InlineData(2, "file")]
    public async Task SourceTablesStreamHeadersAndTrailersShareReferenceProjection(int version, string mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(false), QpdfInputOwnership.Transfer);
        string[] keys = version == 1 ? ["objects", "objectinfo"] : ["qpdf"];
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"),
            new QpdfJsonOptions(version, keys, mode, "side", [], false), CancellationToken.None);
        using JsonDocument parsed = JsonDocument.Parse(result.Json);
        JsonElement table = version == 1 ? parsed.RootElement.GetProperty("objects") : parsed.RootElement.GetProperty("qpdf")[1];
        JsonElement Value(string reference) => version == 1 ? table.GetProperty(reference) : table.GetProperty("obj:" + reference).GetProperty("value");
        AssertFields(Value("4 0 R"));
        AssertFields(version == 1 ? table.GetProperty("trailer") : table.GetProperty("trailer").GetProperty("value"));
        JsonElement stream = version == 1 ? table.GetProperty("5 0 R") : table.GetProperty("obj:5 0 R").GetProperty("stream").GetProperty("dict");
        AssertFields(stream); Assert.False(stream.TryGetProperty("/Filter", out _));
        Assert.Equal(JsonValueKind.Null, Value("7 7 R").ValueKind);
        Assert.False(table.TryGetProperty(version == 1 ? "8 1 R" : "obj:8 1 R", out _));
        if (version == 1)
        {
            JsonElement info = parsed.RootElement.GetProperty("objectinfo").GetProperty("5 0 R").GetProperty("stream");
            Assert.Equal(3, info.GetProperty("length").GetInt32()); Assert.Equal(JsonValueKind.Null, info.GetProperty("filter").ValueKind);
        }
        if (mode == "file") { Assert.True(host.TryGetOutput(QpdfPath.From("side-5"), out byte[] data)); Assert.Equal("q Q"u8.ToArray(), data); }
        Assert.Empty(result.Warnings); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedObjectExportsResolveRequiredSourcePairsWithoutEmittingThem(bool job)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","jsonOutput":"2","jsonObject":["4"],"testJsonSchema":true}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--json-output", "--json-object=4", "--test-json-schema"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement table = json.RootElement.GetProperty("qpdf")[1]; Assert.Single(table.EnumerateObject());
        AssertFields(table.GetProperty("obj:4 0 R").GetProperty("value")); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NullQueriesDoNotCopyUnselectedStreamPayloadsThroughACustomHost()
    {
        byte[] payload = new byte[2 * 1024 * 1024]; payload[0] = 17; payload[^1] = 23;
        byte[] input = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /PrivateStream 4 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] >>", null),
            (4, "<< /Length " + payload.Length + " >>", payload)], 1);
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = input.Length + 65536L };
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(new DelegatingHost(storage), QpdfPath.From("input"),
            new QpdfJsonOptions(2, ["qpdf"], "none", null, ["1"], true), limits, CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json);
        JsonElement objects = json.RootElement.GetProperty("qpdf")[1]; Assert.Single(objects.EnumerateObject());
        Assert.Equal("4 0 R", objects.GetProperty("obj:1 0 R").GetProperty("value").GetProperty("/PrivateStream").GetString());
        Assert.InRange(result.Counters.PeakMemoryBytes, input.Length, input.Length + 65536L);
        Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(17, payload[0]); Assert.Equal(23, payload[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompressedPairsUseGenerationZeroAndRetainLiveNullArrayReferences(bool flate)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), ReaderAcceptanceTests.ObjectStreamPdf("<< /Marker (live) >>", "null", flate, true, null,
            "/Live 6 0 R /Stale 6 1 R /Null 7 0 R /Missing 99 0 R /Array [6 0 R 6 1 R 7 0 R 99 0 R]"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["qpdf"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json);
        JsonElement page = json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:3 0 R").GetProperty("value");
        foreach (string key in new[] { "/Stale", "/Null", "/Missing" }) { Assert.False(page.TryGetProperty(key, out _)); }
        Assert.Equal("6 0 R", page.GetProperty("/Live").GetString());
        JsonElement array = page.GetProperty("/Array"); Assert.Equal("6 0 R", array[0].GetString());
        Assert.Equal("7 0 R", array[2].GetString()); Assert.Equal(JsonValueKind.Null, array[1].ValueKind); Assert.Equal(JsonValueKind.Null, array[3].ValueKind);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task AssembledObjectsKeepTheirNewLiveIdentities()
    {
        using InMemoryQpdfHost host = new();
        // Assembly has its own strict missing-object policy. Keep dead pairs
        // here while isolating the new copied identities from that policy.
        byte[] input = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Fixture(false)).Replace("99 0 R", "08 1 R", StringComparison.Ordinal));
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["--empty", "--pages", "input", "1", "--", "--json=2", "--json-key=pages", "--json-key=qpdf"]), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        JsonElement Value(string reference) => table.GetProperty("obj:" + reference).GetProperty("value");
        string pageReference = json.RootElement.GetProperty("pages")[0].GetProperty("object").GetString() ?? throw new InvalidOperationException();
        string auditReference = Value(pageReference).GetProperty("/Audit").GetString() ?? throw new InvalidOperationException();
        JsonElement audit = Value(auditReference); Assert.Equal(auditReference, audit.GetProperty("/Self").GetString());
        string live = audit.GetProperty("/Live").GetString() ?? throw new InvalidOperationException();
        Assert.Equal("u:live", Value(live).GetProperty("/Marker").GetString());
        JsonElement array = audit.GetProperty("/Array"); Assert.Equal(live, array[1].GetString());
        foreach (int index in new[] { 0, 2, 3, 4, 5 }) { Assert.Equal(JsonValueKind.Null, array[index].ValueKind); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSelectedReferenceTargetsPreserveThePriorReport(bool command)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(true), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("report"), CancellationToken.None))
        { await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        if (command)
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
                new(["input", "--json=2", "--json-key=qpdf", "--json-object=4", "--suppress-recovery", "report"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        }
        else
        {
            await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("report"),
                new QpdfJsonOptions(2, ["qpdf"], "none", null, ["4"], false), CancellationToken.None).AsTask());
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("memory")]
    [InlineData("writer-cancel")]
    [InlineData("reader-cancel")]
    public async Task SourceSerializationPreservesRawValuesAndPriorOwners(string outcome)
    {
        using CancellationTokenSource cancellation = new();
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), outcome == "reader-cancel" ? cancellation.Token : CancellationToken.None);
        PdfDictionary raw = Assert.IsType<PdfDictionary>(document.GetObject(4));
        Assert.True(raw.TryGet(PdfName.FromAscii("Stale"), out QpdfObject stale));
        Assert.True(raw.TryGet(PdfName.FromAscii("Null"), out QpdfObject liveNull));
        ResourceGovernor writer = new(QpdfLimits.Default with { MaxMemoryBytes = outcome == "memory" ? 256 : 1048576 },
            outcome == "writer-cancel" ? cancellation.Token : CancellationToken.None);
        using (ParserMemoryScope earlier = ParserMemoryScope.Create(writer))
        {
            earlier.Reserve(97);
            long baseline = writer.Snapshot().LiveMemoryBytes;
            using (DisplayBuffer output = DisplayBuffer.CreateJson(writer))
            {
                if (outcome.EndsWith("-cancel", StringComparison.Ordinal)) { cancellation.Cancel(); }
                void Render() => PdfJsonWriter.AppendSourceValue(output, raw, 2, writer, 0, QpdfLimits.Default, new JsonDocumentView(document));
                if (outcome == "memory") { Assert.Throws<QpdfLimitException>(Render); }
                else if (outcome.EndsWith("-cancel", StringComparison.Ordinal)) { Assert.ThrowsAny<OperationCanceledException>(Render); }
                else
                {
                    Render(); using GovernedText text = output.Finish(); using JsonDocument json = JsonDocument.Parse(text.Value); AssertFields(json.RootElement);
                }
            }
            Assert.Equal(baseline, writer.Snapshot().LiveMemoryBytes); Assert.Equal(97L, earlier.ReservedBytes);
            Assert.True(raw.TryGet(PdfName.FromAscii("Stale"), out QpdfObject afterStale)); Assert.Same(stale, afterStale);
            Assert.True(raw.TryGet(PdfName.FromAscii("Null"), out QpdfObject afterNull)); Assert.Same(liveNull, afterNull);
        }
        Assert.Equal(0L, writer.Snapshot().LiveMemoryBytes);
    }
}
