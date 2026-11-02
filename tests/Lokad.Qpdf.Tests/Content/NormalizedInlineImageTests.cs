using System.Globalization;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class NormalizedInlineImageTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string sample in new[] { "name", "comment", "literal", "binary", "asciihex" })
        foreach (string mode in new[] { "normalize", "qdf", "externalize-skip" })
        foreach (bool job in new[] { false, true })
        { yield return [sample, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NormalizationPreservesOpaqueSamples(string sample, string mode, bool job)
    {
        byte[] pixels = sample switch
        {
            "name" => "/A#42BCD"u8.ToArray(),
            "comment" => "%ABC\r\nEF"u8.ToArray(),
            "literal" => "(ABC[]{}"u8.ToArray(),
            "binary" => [0, 255, 128, 1, 2, 3, 4, 5],
            _ => "41424344>"u8.ToArray(),
        };
        byte[] opaque = [.. "ID "u8, .. pixels, .. " EI"u8];
        string header = "q BI /W " + (sample == "asciihex" ? "4" : "8") + " /H 1 /BPC 8 /CS /G "
            + (sample == "asciihex" ? "/F /AHx " : string.Empty);
        byte[] content = [.. Encoding.ASCII.GetBytes(header), .. opaque, .. " Q\n"u8];
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>", content)], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        List<string> arguments = ["input"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress" };
        if (mode == "qdf") { arguments.Add("--qdf"); settings["qdf"] = true; }
        else { arguments.Add("--normalize-content=y"); settings["normalizeContent"] = "y"; }
        if (mode == "externalize-skip")
        {
            arguments.AddRange(["--externalize-inline-images", "--ii-min-bytes=9999"]);
            settings["externalizeInlineImages"] = true; settings["iiMinBytes"] = "9999";
        }
        arguments.AddRange(["--stream-data=uncompress", "output"]);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        PdfReference reference = Assert.IsType<PdfReference>(contents);
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(reference.ObjectNumber));
        Assert.True(stream.RawMemory.Span.IndexOf(opaque) >= 0, "Normalization changed the inline image's encoded bytes or delimiters.");
    }
}
