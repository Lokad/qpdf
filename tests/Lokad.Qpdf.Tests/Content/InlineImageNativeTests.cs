using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

/// <summary>Frozen native observations; no external tools are needed by these tests.</summary>
public sealed class InlineImageNativeTests
{
    private static readonly JsonObject Corpus = Load();
    private static JsonObject Load()
    {
        using Stream stream = typeof(InlineImageNativeTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Content.InlineImageNativeCases.json") ?? throw new InvalidOperationException();
        return Assert.IsType<JsonObject>(JsonNode.Parse(stream));
    }
    public static IEnumerable<object[]> NativeCases()
    {
        foreach (JsonNode? row in Assert.IsType<JsonArray>(Corpus["Cases"]))
        { yield return [Assert.IsType<JsonObject>(row)["Id"]?.GetValue<string>() ?? throw new InvalidOperationException()]; }
    }
    private static JsonObject Case(string id)
        => Assert.IsType<JsonArray>(Corpus["Cases"]).OfType<JsonObject>().Single(row => row["Id"]?.GetValue<string>() == id);

    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task NamesImagesAndCopiedFiltersMatchFrozenNativeFields(string id)
    {
        JsonObject row = Case(id);
        string hash = row["Fixture"]?.GetValue<string>() ?? throw new InvalidOperationException();
        byte[] fixture = Convert.FromBase64String(Corpus["Fixtures"]?[hash]?.GetValue<string>() ?? throw new InvalidOperationException());
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(fixture)));
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("inputs/a.pdf"), fixture, QpdfInputOwnership.Copy);
        foreach ((string path, JsonNode? value) in Assert.IsType<JsonObject>(row["Files"]))
        { host.AddInput(QpdfPath.From(path), Encoding.UTF8.GetBytes(value?.GetValue<string>() ?? throw new InvalidOperationException()), QpdfInputOwnership.Copy); }
        string[] argv = Assert.IsType<JsonArray>(row["Args"]).Select(value => value?.GetValue<string>() ?? throw new InvalidOperationException()).ToArray();
        QpdfExecutionResult result = id.EndsWith("-job", StringComparison.Ordinal) || id.EndsWith("-chain", StringComparison.Ordinal)
            ? await Qpdf.ExecuteJobAsync(host, row["Files"]?["inputs/job.json"]?.GetValue<string>() ?? throw new InvalidOperationException(), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal((QpdfExitCode)(row["Exit"]?.GetValue<int>() ?? throw new InvalidOperationException()), result.ExitCode);
        if (result.ExitCode == QpdfExitCode.Success) { Assert.Empty(result.Diagnostics); }
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("output.pdf"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("output.pdf"), CancellationToken.None);
        List<string> headers = [], decoded = [];
        foreach (int number in document.Engine.XRefEntries.Keys.Where(number => number != 0))
        {
            if (document.GetObject(number) is not PdfStream stream) { continue; }
            JsonObject header = Header(document, stream.Dictionary);
            headers.Add(Canonical(header));
            byte[] data = Decode(document, stream);
            header.Remove("/Filter"); header.Remove("/DecodeParms");
            decoded.Add(Canonical(new JsonObject { ["Header"] = header, ["Data"] = Convert.ToBase64String(data) }));
        }
        JsonObject expected = Assert.IsType<JsonObject>(Corpus["Expected"]?[row["Expected"]?.GetValue<string>() ?? throw new InvalidOperationException()]);
        Assert.Equal(Assert.IsType<JsonArray>(expected["Headers"]).Select(Canonical).Order(StringComparer.Ordinal), headers.Order(StringComparer.Ordinal));
        Assert.Equal(Assert.IsType<JsonArray>(expected["Decoded"]).Select(Canonical).Order(StringComparer.Ordinal), decoded.Order(StringComparer.Ordinal));
    }

    private static JsonObject Header(QpdfDocument document, PdfDictionary dictionary)
    {
        JsonObject result = new();
        foreach (PdfName key in dictionary.Keys)
        {
            if (key.DisplayText is "Length" or "Resources") { continue; }
            Assert.True(dictionary.TryGet(key, out QpdfObject value));
            // The native JSON observation omits dictionary values resolving to
            // null, including an indirect null DecodeParms in preserve mode.
            JsonNode? observed = Value(document, value);
            if (observed is not null) { result["/" + key.DisplayText] = observed; }
        }
        return result;
    }
    private static JsonNode? Value(QpdfDocument document, QpdfObject value)
        => value switch
        {
            PdfReference reference => Value(document, document.GetObject(reference.ObjectNumber)),
            PdfNull => null,
            PdfName name => JsonValue.Create("/" + name.DisplayText),
            PdfInteger integer => JsonValue.Create(integer.Value),
            PdfReal real => JsonValue.Create(real.Value),
            PdfBoolean boolean => JsonValue.Create(boolean.Value),
            PdfArray array => new JsonArray(array.Items.Select(item => Value(document, item)).ToArray()),
            PdfDictionary dictionary => Header(document, dictionary),
            _ => throw new InvalidOperationException("Unexpected native fixture field.")
        };
    private static string Canonical(JsonNode? node)
    {
        if (node is JsonObject dictionary)
        { return "{" + string.Join(",", dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}"; }
        if (node is JsonArray array) { return "[" + string.Join(",", array.Select(Canonical)) + "]"; }
        return node?.ToJsonString() ?? "null";
    }
    private static byte[] Decode(QpdfDocument document, PdfStream stream)
    {
        byte[] data = stream.Data;
        if (!stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter) || filter is PdfNull) { return data; }
        if (filter is PdfReference reference) { filter = document.GetObject(reference.ObjectNumber); }
        QpdfObject[] filters = filter is PdfArray array ? array.Items.ToArray() : [filter];
        foreach (QpdfObject raw in filters)
        {
            string name = Assert.IsType<PdfName>(raw is PdfReference link ? document.GetObject(link.ObjectNumber) : raw).DisplayText;
            if (name == "ASCIIHexDecode")
            { data = Convert.FromHexString(string.Concat(Encoding.ASCII.GetString(data).TakeWhile(c => c != '>').Where(c => !char.IsWhiteSpace(c)))); }
            else if (name == "FlateDecode")
            {
                using MemoryStream input = new(data); using ZLibStream decoder = new(input, CompressionMode.Decompress);
                using MemoryStream output = new(); decoder.CopyTo(output); data = output.ToArray();
            }
            else
            {
                Assert.Equal("RunLengthDecode", name); List<byte> output = [];
                for (int index = 0; index < data.Length;)
                {
                    int control = data[index++]; if (control == 128) { break; }
                    if (control < 128) { output.AddRange(data.AsSpan(index, control + 1).ToArray()); index += control + 1; }
                    else { byte value = data[index++]; for (int count = 0; count < 257 - control; count++) { output.Add(value); } }
                }
                data = output.ToArray();
            }
        }
        return data;
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("mixed", false)]
    [InlineData("chain", false)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    [InlineData("mixed", true)]
    [InlineData("chain", true)]
    public async Task CreatedImageEncodingFailureAndCancellationPreservePriorTarget(string route, bool cancel)
    {
        using MemoryStream encoded = new();
        using (ZLibStream encoder = new(encoded, CompressionLevel.Optimal, true))
        { encoder.Write("q /Tag BMC EMC Q\nBI /W 4 /H 1 /BPC 8 /CS /G ID abcd EI\n"u8); }
        byte[] data = encoded.ToArray();
        byte[] fixture = CheckTests.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R /Resources << >> >>", null),
            (4, "<< /Length " + data.Length + " /Filter /FlateDecode >>", data)
        ], 1);
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input.pdf"), fixture, QpdfInputOwnership.Copy);
        byte[] prior = [9, 8, 7];
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(QpdfPath.From("output.pdf"), CancellationToken.None))
        { await transaction.WriteAsync(prior, CancellationToken.None); await transaction.CommitAsync(CancellationToken.None); }
        Dictionary<string, string> settings = new()
        { ["externalizeInlineImages"] = "", ["iiMinBytes"] = "0", ["objectStreams"] = "disable", ["removeUnreferencedResources"] = "no" };
        using CancellationTokenSource cancellation = new(); if (cancel) { cancellation.Cancel(); }
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxOutputBytes = 1L });
        QpdfExecutionResult result;
        if (route == "job" || route == "chain")
        {
            Dictionary<string, string> job = settings;
            if (route == "chain")
            {
                host.AddInput(QpdfPath.From("settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings), QpdfInputOwnership.Copy);
                job = new() { ["jobJsonFile"] = "settings.json" };
            }
            job["inputFile"] = "input.pdf"; job["outputFile"] = "output.pdf";
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), options, cancellation.Token);
        }
        else
        {
            List<string> argv = ["input.pdf"];
            if (route == "mixed")
            {
                host.AddInput(QpdfPath.From("settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings), QpdfInputOwnership.Copy);
                argv.Add("--job-json-file=settings.json");
            }
            else { argv.AddRange(["--externalize-inline-images", "--ii-min-bytes=0", "--object-streams=disable", "--remove-unreferenced-resources=no"]); }
            argv.Add("output.pdf"); result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, cancellation.Token);
        }
        Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        if (cancel) { Assert.Empty(result.Diagnostics); }
        else { Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] actual)); Assert.Equal(prior, actual);
    }
}
