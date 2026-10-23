using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class VersionLengthTests
{
    private static readonly byte[] HelloData = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Hello) Tj ET");

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildPdf(string headerVersion, string catalogExtra, string? lengthDecl, byte[] streamData, List<string> extraBodies)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            lengthDecl is null ? "<< >>" : "<< /Length " + lengthDecl + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach (string extra in extraBodies)
        {
            bodies.Add(extra);
        }

        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-" + headerVersion + "\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            byte[] close;
            byte[] payload;
            if (number == 4)
            {
                byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
                byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
                offsets.Add(position);
                position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
                payload = new byte[open.Length + streamOpen.Length + streamData.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, payload, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(streamData, 0, payload, at, streamData.Length);
                at += streamData.Length;
                Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
                parts.Add(payload);
                continue;
            }

            close = Encoding.ASCII.GetBytes("endobj\n");
            offsets.Add(position);
            position += open.Length + close.Length;
            payload = new byte[open.Length + close.Length];
            Buffer.BlockCopy(open, 0, payload, 0, open.Length);
            Buffer.BlockCopy(close, 0, payload, open.Length, close.Length);
            parts.Add(payload);
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
        byte[] tail = Encoding.ASCII.GetBytes(xref.ToString());
        parts.Add(tail);

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

    private static byte[] Basic(string headerVersion, string catalogExtra, string? lengthDecl, byte[] streamData)
    {
        return BuildPdf(headerVersion, catalogExtra, lengthDecl, streamData, new List<string>());
    }

    [Fact]
    public async Task HeaderVersionIsPreservedWithoutCatalogOverride()
    {
        byte[] bytes = Basic("1.4", string.Empty, HelloData.Length.ToString(), HelloData);
        InMemoryQpdfHost host = HostWith("inputs/version.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/version.pdf"), CancellationToken.None);
        Assert.Equal("1.4", document.Version.ToString());
    }

    [Fact]
    public async Task CatalogVersionOverridesHeader()
    {
        byte[] bytes = Basic("1.4", " /Version /1.7", HelloData.Length.ToString(), HelloData);
        InMemoryQpdfHost host = HostWith("inputs/version.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/version.pdf"), CancellationToken.None);
        Assert.Equal("1.7", document.Version.ToString());
    }

    [Fact]
    public async Task CatalogNullVersionKeepsHeader()
    {
        byte[] bytes = Basic("1.4", " /Version null", HelloData.Length.ToString(), HelloData);
        InMemoryQpdfHost host = HostWith("inputs/version.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/version.pdf"), CancellationToken.None);
        Assert.Equal("1.4", document.Version.ToString());
    }

    [Fact]
    public async Task IndirectCatalogVersionResolves()
    {
        byte[] bytes = BuildPdf("1.4", " /Version 6 0 R", HelloData.Length.ToString(), HelloData, new List<string> { "/1.5" });
        InMemoryQpdfHost host = HostWith("inputs/version.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/version.pdf"), CancellationToken.None);
        Assert.Equal("1.5", document.Version.ToString());
    }

    [Theory]
    [InlineData("1.7", "1.4", "1.7", false)]
    [InlineData("1.7", "1.4", "1.7", true)]
    [InlineData("2.0", "1.7", "2.0", false)]
    [InlineData("2.0", "1.7", "2.0", true)]
    [InlineData("1.4", "1.7", "1.7", false)]
    [InlineData("1.4", "1.7", "1.7", true)]
    [InlineData("1.7", "1.7", "1.7", false)]
    [InlineData("1.7", "1.7", "1.7", true)]
    public async Task EffectiveVersionKeepsTheHigherDeclarationAfterDisposal(string header, string catalog, string expected, bool indirect)
    {
        byte[] bytes = BuildPdf(header, " /Version " + (indirect ? "6 0 R" : "/" + catalog), HelloData.Length.ToString(),
            HelloData, indirect ? ["/" + catalog] : []);
        using InMemoryQpdfHost host = HostWith("input.pdf", bytes);
        QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), CancellationToken.None);
        PdfVersion effective = document.Version;
        PdfVersion originalHeader = document.HeaderVersion;
        await document.DisposeAsync();
        Assert.Equal(expected, effective.ToString());
        Assert.Equal(header, originalHeader.ToString());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("typed", true)]
    [InlineData("argv", false)]
    [InlineData("argv", true)]
    [InlineData("job", false)]
    [InlineData("job", true)]
    public async Task RewriteDoesNotDowngradeToALowerCatalogVersion(string route, bool indirect)
    {
        byte[] bytes = BuildPdf("1.7", " /Version " + (indirect ? "6 0 R" : "/1.4"), HelloData.Length.ToString(),
            HelloData, indirect ? ["/1.4"] : []);
        using InMemoryQpdfHost host = HostWith("input.pdf", bytes);
        if (route == "typed")
        {
            await Qpdf.SelectPageAsync(host, QpdfPath.From("input.pdf"), QpdfPath.From("output.pdf"),
                new PdfPageSelectionOptions(1), CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input.pdf", "output.pdf"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input.pdf","outputFile":"output.pdf"}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] output));
        Assert.Equal("%PDF-1.7", Encoding.ASCII.GetString(output, 0, 8));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task ExplicitForceVersionStillAllowsDowngrading(string route)
    {
        using InMemoryQpdfHost host = HostWith("input.pdf", Basic("1.7", " /Version /1.4", HelloData.Length.ToString(), HelloData));
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input.pdf", "--force-version=1.3", "output.pdf"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input.pdf","outputFile":"output.pdf","forceVersion":"1.3"}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] output));
        Assert.Equal("%PDF-1.3", Encoding.ASCII.GetString(output, 0, 8));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SelectionOutputEchoesEffectiveVersion()
    {
        byte[] bytes = Basic("1.4", " /Version /1.7", HelloData.Length.ToString(), HelloData);
        InMemoryQpdfHost host = HostWith("inputs/in.pdf", bytes);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/in.pdf"), QpdfPath.From("outputs/out.pdf"),
            new PdfPageSelectionOptions(1), CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        if (!host.TryGetOutput(QpdfPath.From("outputs/out.pdf"), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException("Output was not published.");
        }

        Assert.True(published.Length > 8);
        Assert.Equal("%PDF-1.7", Encoding.ASCII.GetString(published, 0, 8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergePreservesIndirectEffectiveVersionAfterSourcesClose(bool highFirst)
    {
        QpdfPath high = QpdfPath.From("inputs/high.pdf"), low = QpdfPath.From("inputs/low.pdf"), target = QpdfPath.From("outputs/merged.pdf");
        InMemoryQpdfHost host = new();
        byte[] content = "0 0 10 10 re S"u8.ToArray();
        host.AddInput(high, BuildPdf("1.4", " /Version 6 0 R", content.Length.ToString(), content, ["/1.7"]), QpdfInputOwnership.Copy);
        host.AddInput(low, Basic("1.5", string.Empty, content.Length.ToString(), content), QpdfInputOwnership.Copy);
        PdfPageSource[] sources = [new(highFirst ? high : low, new PdfPageSelectionOptions(1)),
            new(highFirst ? low : high, new PdfPageSelectionOptions(1))];
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host, sources, target, CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(target, out byte[] published));
        Assert.Equal("%PDF-1.7", Encoding.ASCII.GetString(published, 0, 8));
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(published);
        Assert.Equal(2, independent.NumberOfPages);
        Assert.All(independent.GetPages(), page => Assert.Equal(612, page.Width));
    }

    [Fact]
    public async Task RejectsNonNameCatalogVersion()
    {
        byte[] bytes = Basic("1.4", " /Version 1.7", HelloData.Length.ToString(), HelloData);
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RejectsMalformedCatalogVersion()
    {
        byte[] bytes = Basic("1.4", " /Version /Foo", HelloData.Length.ToString(), HelloData);
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RejectsUnsupportedCatalogVersion()
    {
        byte[] bytes = Basic("1.4", " /Version /1.8", HelloData.Length.ToString(), HelloData);
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RejectsUnresolvableCatalogVersion()
    {
        byte[] bytes = BuildPdf("1.4", " /Version 99 0 R", HelloData.Length.ToString(), HelloData, new List<string>());
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task IndirectStreamLengthResolves()
    {
        byte[] bytes = BuildPdf("1.4", string.Empty, "6 0 R", HelloData, new List<string> { HelloData.Length.ToString() });
        InMemoryQpdfHost host = HostWith("inputs/len.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/len.pdf"), CancellationToken.None);
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(4));
        Assert.Equal(HelloData, stream.Data);
    }

    [Fact]
    public async Task SelfReferentialLengthFailsWithoutRecursion()
    {
        byte[] bytes = Basic("1.4", string.Empty, "4 0 R", HelloData);
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None);
        QpdfParseException ex = Assert.Throws<QpdfParseException>(() => document.GetObject(4));
        Assert.Contains("cyclically", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnresolvableIndirectLengthFails()
    {
        byte[] bytes = Basic("1.4", string.Empty, "99 0 R", HelloData);
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None);
        Assert.Throws<QpdfParseException>(() => document.GetObject(4));
    }

    [Fact]
    public async Task MissingLengthFails()
    {
        byte[] bytes = Basic("1.4", string.Empty, null, HelloData);
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None);
        Assert.Throws<QpdfParseException>(() => document.GetObject(4));
    }

    [Fact]
    public async Task NegativeLengthFails()
    {
        byte[] bytes = Basic("1.4", string.Empty, "-5", HelloData);
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None);
        Assert.Throws<QpdfParseException>(() => document.GetObject(4));
    }

    [Fact]
    public async Task HugeDeclaredLengthFailsBounded()
    {
        byte[] bytes = Basic("1.4", string.Empty, "10000000", HelloData);
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None);
        Assert.Throws<QpdfParseException>(() => document.GetObject(4));
    }

    [Fact]
    public async Task StreamDataWithDelimiterBytesIsPreservedByLength()
    {
        byte[] tricky = Encoding.ASCII.GetBytes("BT (x) Tj endstream >> << endobj stream Q");
        byte[] bytes = Basic("1.4", string.Empty, tricky.Length.ToString(), tricky);
        InMemoryQpdfHost host = HostWith("inputs/tricky.pdf", bytes);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/tricky.pdf"), CancellationToken.None);
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(4));
        Assert.Equal(tricky, stream.Data);
    }
    [Fact]
    public async Task IndirectLengthChainRespectsLoadDepth()
    {
        List<string> objects = new() { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>" };
        for (int n = 4; n < 40; n++) { objects.Add("<< /Length " + (n + 1).ToString() + " 0 R >>\nstream\n\nendstream"); }
        objects.Add("0");
        byte[] bytes = ChainPdf(objects);
        InMemoryQpdfHost host = HostWith("inputs/chain.pdf", bytes);
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 4L };
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/chain.pdf"), limits, CancellationToken.None);
        Assert.Throws<QpdfParseException>(() => document.GetObject(4));
    }

    private static byte[] ChainPdf(List<string> objects)
    {
        StringBuilder pdf = new("%PDF-1.7\n");
        List<int> offsets = new();
        for (int i = 0; i < objects.Count; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) { pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n"); }
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
