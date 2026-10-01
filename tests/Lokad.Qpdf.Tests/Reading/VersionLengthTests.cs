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
}
