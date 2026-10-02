using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ContentTransformsTests
{
    private static byte[] BuildPdf(
        string catalogExtra,
        string pageExtra,
        List<(string Dict, byte[]? StreamData)> extras)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" + pageExtra + " >>",
        };
        foreach ((string dict, byte[]? _) in extras)
        {
            bodies.Add(dict);
        }

        List<byte[]?> payloads = new() { null, null, null };
        foreach ((string _, byte[]? streamData) in extras)
        {
            payloads.Add(streamData);
        }

        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = payloads[i];
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            if (streamData is null)
            {
                byte[] close = Encoding.ASCII.GetBytes("endobj\n");
                offsets.Add(position);
                position += open.Length + close.Length;
                parts.Add(open);
                parts.Add(close);
                continue;
            }

            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            offsets.Add(position);
            position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
            parts.Add(open);
            parts.Add(streamOpen);
            parts.Add(streamData);
            parts.Add(streamClose);
        }

        int size = bodies.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        parts.Add(Encoding.ASCII.GetBytes(xref.ToString()));
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<byte[]> TransformAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        return pdf;
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    private static async Task<byte[]> PageContentAsync(byte[] pdf)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/r.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/r.pdf", "--json", "--json-stream-data=inline", "--", "outputs/r.json"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/r.json"), out byte[] json));
        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(json));
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        JsonElement page = document.RootElement.GetProperty("pages")[0];
        List<byte> joined = new();
        foreach (JsonElement content in page.GetProperty("contents").EnumerateArray())
        {
            string reference = content.GetString() ?? string.Empty;
            string[] parts = reference.Split((char)32);
            string key = "obj:" + parts[0] + " " + parts[1] + " R";
            string data = objects.GetProperty(key).GetProperty("stream").GetProperty("data").GetString() ?? string.Empty;
            joined.AddRange(Convert.FromBase64String(data));
        }

        return [.. joined];
    }

    private static async Task<JsonDocument> InspectAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] json));
        return JsonDocument.Parse(Encoding.UTF8.GetString(json));
    }
    [Fact]
    public async Task CoalesceJoinsStreams()
    {
        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents [4 0 R 5 0 R]",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 12 >>", Encoding.ASCII.GetBytes("BT /F1 12 Tf")),
                ("<< /Length 10 >>", Encoding.ASCII.GetBytes("(Hi) Tj ET")),
            });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", "--coalesce-contents");
        byte[] content = await PageContentAsync(pdf);
        Assert.Equal("BT /F1 12 Tf\n(Hi) Tj ET", Encoding.ASCII.GetString(content));
        using JsonDocument document = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=pages");
        JsonElement contents = document.RootElement.GetProperty("pages")[0].GetProperty("contents");
        Assert.Equal(1, contents.GetArrayLength());
    }

    [Fact]
    public async Task CoalesceSingleStreamIsUnchanged()
    {
        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents 4 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 15 >>", Encoding.ASCII.GetBytes("BT (Page) Tj ET")),
            });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", "--coalesce-contents");
        byte[] content = await PageContentAsync(pdf);
        Assert.Equal("BT (Page) Tj ET", Encoding.ASCII.GetString(content));
    }

    private static byte[] ResourceFixture(string content)
    {
        byte[] image = [(byte)0x80];
        List<(string Dict, byte[]? StreamData)> extras = new()
        {
            ("<< /Length " + content.Length + " >>", Encoding.ASCII.GetBytes(content)),
            ("<< /Font << /F1 6 0 R /F2 7 0 R >> /XObject << /Im1 8 0 R >> >>", null),
            ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            ("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
            ("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 1 /Length 1 >>", image),
        };
        string pageExtra = " /Contents 4 0 R /Resources 5 0 R";
        return BuildPdf(string.Empty, pageExtra, extras);
    }

    private static async Task<HashSet<string>> ResourceKeysAsync(byte[] pdf)
    {
        using JsonDocument document = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=pages");
        JsonElement page = document.RootElement.GetProperty("pages")[0];
        string reference = page.GetProperty("object").GetString() ?? string.Empty;
        string[] parts = reference.Split((char)32);
        string key = "obj:" + parts[0] + " " + parts[1] + " R";
        using JsonDocument full = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=qpdf");
        JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
        JsonElement resources = objects.GetProperty(key).GetProperty("value").GetProperty("/Resources");
        if (resources.ValueKind == JsonValueKind.String)
        {
            string[] linked = (resources.GetString() ?? string.Empty).Split((char)32);
            resources = objects.GetProperty("obj:" + linked[0] + " " + linked[1] + " R").GetProperty("value");
        }
        HashSet<string> keys = new(StringComparer.Ordinal);
        if (resources.TryGetProperty("/Font", out JsonElement fonts))
        {
            foreach (JsonProperty property in fonts.EnumerateObject())
            {
                keys.Add("Font:" + property.Name);
            }
        }

        if (resources.TryGetProperty("/XObject", out JsonElement xobjects))
        {
            foreach (JsonProperty property in xobjects.EnumerateObject())
            {
                keys.Add("XObject:" + property.Name);
            }
        }

        return keys;
    }


    [Fact]
    public async Task PruneYesRemovesUnused()
    {
        byte[] fixture = ResourceFixture("/F1 12 Tf (x) Tj");
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", "--remove-unreferenced-resources=yes");
        HashSet<string> keys = await ResourceKeysAsync(pdf);
        Assert.Contains("Font:/F1", keys);
        Assert.DoesNotContain("Font:/F2", keys);
        Assert.DoesNotContain("XObject:/Im1", keys);
    }

    [Fact]
    public async Task CheckedInUnreferencedFixturePrunes()
    {
        byte[] fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "unref-1page.pdf"));
        byte[] pdf = await TransformAsync(HostWith("inputs/a.pdf", fixture), "--pages", ".", "1", "--remove-unreferenced-resources=yes");
        HashSet<string> keys = await ResourceKeysAsync(pdf);
        Assert.Contains("Font:/F1", keys);
        Assert.DoesNotContain("Font:/F2", keys);
    }

    [Fact]
    public async Task PruneNoKeepsEverything()
    {
        byte[] fixture = ResourceFixture("/F1 12 Tf (x) Tj");
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", "--remove-unreferenced-resources=no");
        HashSet<string> keys = await ResourceKeysAsync(pdf);
        Assert.Contains("Font:/F1", keys);
        Assert.Contains("Font:/F2", keys);
        Assert.Contains("XObject:/Im1", keys);
    }

    [Fact]
    public async Task PruneAutoKeepsUnshared()
    {
        byte[] fixture = ResourceFixture("/F1 12 Tf (x) Tj");
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1");
        HashSet<string> keys = await ResourceKeysAsync(pdf);
        Assert.Contains("Font:/F2", keys);
        Assert.Contains("XObject:/Im1", keys);
    }
    private static byte[] SharedFixture()
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources 7 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources 7 0 R >>",
            "<< /Length 15 >>",
            "<< /Length 15 >>",
            "<< /Font << /F1 8 0 R /F2 9 0 R >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
        };
        List<byte[]?> payloads = new()
        {
            null, null, null, null,
            Encoding.ASCII.GetBytes("BT /F1 12 Tf ET"),
            Encoding.ASCII.GetBytes("BT /F1 12 Tf ET"),
            null, null, null,
        };
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = payloads[i];
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            if (streamData is null)
            {
                byte[] close = Encoding.ASCII.GetBytes("endobj\n");
                offsets.Add(position);
                position += open.Length + close.Length;
                parts.Add(open);
                parts.Add(close);
                continue;
            }

            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            offsets.Add(position);
            position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
            parts.Add(open);
            parts.Add(streamOpen);
            parts.Add(streamData);
            parts.Add(streamClose);
        }

        int size = bodies.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        parts.Add(Encoding.ASCII.GetBytes(xref.ToString()));
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    [Fact]
    public async Task PruneAutoPrunesShared()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SharedFixture());
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", ".", "2");
        using JsonDocument document = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=pages");
        Assert.Equal(2, document.RootElement.GetProperty("pages").GetArrayLength());
        using JsonDocument full = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=qpdf");
        JsonElement objects = full.RootElement.GetProperty("qpdf")[1];
        int pages = 0;
        int withoutF2 = 0;
        foreach (JsonProperty property in objects.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("value", out JsonElement value))
            {
                continue;
            }

            if (!value.TryGetProperty("/Type", out JsonElement type) || type.GetString() != "/Page")
            {
                continue;
            }

            pages += 1;
            JsonElement fonts = value.GetProperty("/Resources").GetProperty("/Font");
            Assert.True(fonts.TryGetProperty("/F1", out _));
            if (!fonts.TryGetProperty("/F2", out _))
            {
                withoutF2 += 1;
            }
        }

        Assert.Equal(2, pages);
        Assert.Equal(2, withoutF2);
    }
    [Fact]
    public async Task PruneKeepsUnresolvedPageNames()
    {
        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents 4 0 R /Resources 5 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 12 >>", Encoding.ASCII.GetBytes("/F9 12 Tf ET")),
                ("<< /Font << /F1 6 0 R /F2 7 0 R >> >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
            });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", "--remove-unreferenced-resources=yes");
        HashSet<string> keys = await ResourceKeysAsync(pdf);
        Assert.Contains("Font:/F1", keys);
        Assert.Contains("Font:/F2", keys);
    }

    [Fact]
    public async Task PruneRecursesIntoForms()
    {
        byte[] formContent = Encoding.ASCII.GetBytes("/F1 12 Tf ET");
        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents 4 0 R /Resources 5 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 5 >>", Encoding.ASCII.GetBytes("BT ET")),
                ("<< /Font << /F1 6 0 R /F2 7 0 R >> /XObject << /Fm1 8 0 R >> >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
                ("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 12 >>", formContent),
            });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", "--remove-unreferenced-resources=yes");
        HashSet<string> keys = await ResourceKeysAsync(pdf);
        Assert.Contains("Font:/F1", keys);
        Assert.DoesNotContain("Font:/F2", keys);
    }

    [Fact]
    public async Task PruneUnresolvedFormNameBlocksFormOnly()
    {
        byte[] formContent = Encoding.ASCII.GetBytes("/F9 12 Tf ET");
        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents 4 0 R /Resources 5 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 5 >>", Encoding.ASCII.GetBytes("BT ET")),
                ("<< /Font << /F1 6 0 R /F2 7 0 R >> /XObject << /Fm1 8 0 R >> >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
                ("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 12 >>", formContent),
            });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", "--remove-unreferenced-resources=yes");
        HashSet<string> keys = await ResourceKeysAsync(pdf);
        Assert.DoesNotContain("Font:/F1", keys);
    }
    [Fact]
    public async Task NormalizeDecodesFlate()
    {
        byte[] compressed;
        {
            using MemoryStream output = new();
            using (System.IO.Compression.ZLibStream compressor = new(output, System.IO.Compression.CompressionLevel.Optimal))
            {
                byte[] raw = Encoding.ASCII.GetBytes("BT\r\n/F1 12 Tf ET");
                compressor.Write(raw, 0, raw.Length);
            }

            compressed = output.ToArray();
        }

        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents 4 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length " + compressed.Length + " /Filter /FlateDecode >>", compressed),
            });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", "--normalize-content=y");
        byte[] content = await PageContentAsync(pdf);
        Assert.Equal("BT\n/F1 12 Tf ET", Encoding.ASCII.GetString(content));
    }

    [Fact]
    public async Task CoalesceKeepsOpaqueStreams()
    {
        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents [4 0 R 5 0 R]",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 4 /Filter /DCTDecode >>", [(byte)0xFF, (byte)0xD8, (byte)0x00, (byte)0x01]),
                ("<< /Length 2 >>", Encoding.ASCII.GetBytes("ET")),
            });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        byte[] pdf = await TransformAsync(host, "--pages", ".", "1", "--coalesce-contents");
        byte[] content = await PageContentAsync(pdf);
        byte[] expected = [(byte)0xFF, (byte)0xD8, (byte)0x00, (byte)0x01, (byte)'\n', (byte)'E', (byte)'T'];
        Assert.Equal(expected, content);
    }

    [Fact]
    public async Task NormalizeKeepsDamagedStreamsWithWarning()
    {
        byte[] garbage = Encoding.ASCII.GetBytes("(a");
        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents 4 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 2 /Filter /FlateDecode >>", garbage),
            });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--normalize-content=y", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedFeature);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        byte[] content = await PageContentAsync(pdf);
        Assert.Equal(garbage, content);
    }

    [Fact]
    public async Task PruneSkipsDamagedContentWithWarning()
    {
        byte[] garbage = Encoding.ASCII.GetBytes("(a");
        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents 4 0 R /Resources 5 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 2 /Filter /FlateDecode >>", garbage),
                ("<< /Font << /F1 6 0 R /F2 7 0 R >> >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
                ("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
            });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--remove-unreferenced-resources=yes", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedFeature);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        HashSet<string> keys = await ResourceKeysAsync(pdf);
        Assert.Contains("Font:/F1", keys);
        Assert.Contains("Font:/F2", keys);
    }
    [Fact]
    public async Task PruneAppliesOnMerge()
    {
        byte[] fixture = ResourceFixture("/F1 12 Tf (x) Tj");
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        host.AddInput(QpdfPath.From("inputs/b.pdf"), fixture, QpdfInputOwnership.Copy);
        QpdfExecutionResult merge = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--coalesce-contents", "--remove-unreferenced-resources=yes", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, merge.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        using JsonDocument document = await InspectAsync(HostWith("inputs/a.pdf", pdf), "--json", "--json-key=pages");
        Assert.Equal(2, document.RootElement.GetProperty("pages").GetArrayLength());
    }

    [Fact]
    public async Task SplitOutputsPrune()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SharedFixture());
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", ".", "2", "--split-pages=1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a-1.pdf"), out byte[] first));
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a-2.pdf"), out byte[] second));
        HashSet<string> keys = await ResourceKeysAsync(first);
        Assert.Contains("Font:/F1", keys);
        Assert.DoesNotContain("Font:/F2", keys);
    }

    [Theory]
    [InlineData("--normalize-content")]
    [InlineData("--normalize-content=x")]
    [InlineData("--remove-unreferenced-resources=x")]
    [InlineData("--coalesce-contents", "--coalesce-contents")]
    [InlineData("--normalize-content=y", "--normalize-content=n")]
    public async Task MalformedContentFlagsFail(params string[] flags)
    {
        byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[]? StreamData)>
        {
            ("<< /Length 15 >>", Encoding.ASCII.GetBytes("BT (Page) Tj ET")),
        });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobContentFlagsRoundTrip()
    {
        byte[] fixture = BuildPdf(
            string.Empty,
            " /Contents [4 0 R 5 0 R]",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 2 >>", Encoding.ASCII.GetBytes("BT")),
                ("<< /Length 2 >>", Encoding.ASCII.GetBytes("ET")),
            });
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", fixture);
        byte[] a = await TransformAsync(argvHost, "--pages", ".", "1", "--coalesce-contents", "--normalize-content=y", "--remove-unreferenced-resources=yes");
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", fixture);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"coalesceContents\": true, \"normalizeContent\": \"y\", \"removeUnreferencedResources\": \"yes\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"normalizeContent\": \"x\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"removeUnreferencedResources\": \"sometimes\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"coalesceContents\": \"yes\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"coalesceContents\": true, \"coalesceContents\": true}")]
    public async Task JobContentFlagsMalformedFail(string jobJson)
    {
        byte[] fixture = BuildPdf(string.Empty, " /Contents 4 0 R", new List<(string Dict, byte[]? StreamData)>
        {
            ("<< /Length 15 >>", Encoding.ASCII.GetBytes("BT (Page) Tj ET")),
        });
        InMemoryQpdfHost host0 = HostWith("inputs/a.pdf", fixture);
        InMemoryQpdfHost host = host0;
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
}