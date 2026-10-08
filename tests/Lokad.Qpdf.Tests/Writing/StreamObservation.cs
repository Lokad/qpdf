using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

/// <summary>Observe payloads independently of the writer's compression choice.</summary>
internal static class StreamObservation
{
    internal static byte[] Decode(PdfStream stream)
    {
        byte[] data = stream.Data;
        if (!stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject raw)) { return data; }
        string filter = Assert.IsType<PdfName>(raw).DisplayText;
        if (filter == "ASCIIHexDecode") { return Convert.FromHexString(Encoding.ASCII.GetString(data)[..^1]); }
        Assert.Equal("FlateDecode", filter);
        using MemoryStream input = new(data);
        using ZLibStream decoder = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static InMemoryQpdfHost Host(byte[] pdf)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), pdf, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<string> Snapshot(InMemoryQpdfHost host)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--json", "--json-key=qpdf", "--json-stream-data=inline", "--decode-level=all"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        return Assert.IsType<string>(result.OutputText);
    }

    internal static async Task SameDecodedGraph(byte[] first, byte[] second)
    {
        static string WithoutGeneratedId(string text)
        {
            JsonNode? json = JsonNode.Parse(text);
            Assert.NotNull(json);
            JsonObject trailer = Assert.IsType<JsonObject>(json["qpdf"]?[1]?["trailer"]?["value"]);
            if (trailer["/ID"] is JsonArray ids)
            {
                Assert.Equal(2, ids.Count);
                foreach (JsonNode? id in ids)
                {
                    Assert.NotNull(id);
                    string value = id.GetValue<string>();
                    Assert.StartsWith("b:", value, StringComparison.Ordinal);
                    Assert.Equal(16, Convert.FromHexString(value[2..]).Length);
                }
                trailer.Remove("/ID");
            }
            return json.ToJsonString();
        }
        // Generated IDs depend on the encoded bytes; retain every other graph field.
        Assert.Equal(WithoutGeneratedId(await Snapshot(Host(first))), WithoutGeneratedId(await Snapshot(Host(second))));
    }

    internal static async Task<string> PdfText(byte[] pdf)
    {
        InMemoryQpdfHost host = Host(pdf);
        using JsonDocument json = JsonDocument.Parse(await Snapshot(host));
        int maximum = json.RootElement.GetProperty("qpdf")[0].GetProperty("maxobjectid").GetInt32();
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), CancellationToken.None);
        StringBuilder text = new(Encoding.ASCII.GetString(pdf));
        for (int number = 1; number <= maximum; number++)
        {
            QpdfObject value;
            try { value = document.GetObject(number); }
            catch (QpdfParseException) { continue; }
            if (value is PdfStream stream) { text.Append(Encoding.ASCII.GetString(Decode(stream))); }
        }
        return text.ToString();
    }
}
