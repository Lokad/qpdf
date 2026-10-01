using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class CollationTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildDoc(string marker)
    {
        byte[] contents = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (" + marker + ") Tj ET");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + contents.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        return Assemble(bodies, new Dictionary<int, byte[]> { [4] = contents });
    }

    private static byte[] BuildTwoPages(string first, string second)
    {
        byte[] data1 = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (" + first + ") Tj ET");
        byte[] data2 = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (" + second + ") Tj ET");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Length " + data1.Length.ToString() + " >>",
            "<< /Length " + data2.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        return Assemble(bodies, new Dictionary<int, byte[]> { [5] = data1, [6] = data2 });
    }

    private static byte[] Assemble(List<string> bodies, Dictionary<int, byte[]>? streams)
    {
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            byte[]? streamData = streams is not null && streams.TryGetValue(number, out byte[]? data) ? data : null;
            if (streamData is null)
            {
                byte[] close = Encoding.ASCII.GetBytes("endobj\n");
                offsets.Add(position);
                position += open.Length + close.Length;
                byte[] plain = new byte[open.Length + close.Length];
                Buffer.BlockCopy(open, 0, plain, 0, open.Length);
                Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
                parts.Add(plain);
                continue;
            }

            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            offsets.Add(position);
            position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
            byte[] payload = new byte[open.Length + streamOpen.Length + streamData.Length + streamClose.Length];
            int at = 0;
            Buffer.BlockCopy(open, 0, payload, at, open.Length);
            at += open.Length;
            Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
            at += streamOpen.Length;
            Buffer.BlockCopy(streamData, 0, payload, at, streamData.Length);
            at += streamData.Length;
            Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
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

    private static List<string> MarkersOf(QpdfDocument document)
    {
        List<string> markers = new();
        for (int i = 0; i < document.PageCount; i++)
        {
            PdfDictionary page = document.PageReferences[i].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
            PdfReference reference = Assert.IsType<PdfReference>(raw);
            PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(reference.ObjectNumber));
            markers.Add(Encoding.ASCII.GetString(stream.Data));
        }

        return markers;
    }

    private static async Task<List<string>> SelectMarkers(
        InMemoryQpdfHost host,
        List<PdfPageSource> sources,
        PdfPageMergeOptions options,
        string output)
    {
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host, sources, QpdfPath.From(output), options, CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(result.PageCount, document.PageCount);
        return MarkersOf(document);
    }

    [Fact]
    public async Task UniformCollationInterleaves()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildTwoPages("A1", "A2"));
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildTwoPages("B1", "B2"), QpdfInputOwnership.Copy);
        List<string> markers = await SelectMarkers(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1, 2])),
             new PdfPageSource(QpdfPath.From("inputs/b.pdf"), PdfPageSelectionOptions.FromPages([1, 2]))],
            new PdfPageMergeOptions { Collation = PdfPageCollation.Interleave(1) },
            "outputs/collated.pdf");
        Assert.Equal(4, markers.Count);
        Assert.Contains("A1", markers[0], StringComparison.Ordinal);
        Assert.Contains("B1", markers[1], StringComparison.Ordinal);
        Assert.Contains("A2", markers[2], StringComparison.Ordinal);
        Assert.Contains("B2", markers[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerSourceGroupSizesApply()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDoc("A1"));
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildDoc("B1"), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/c.pdf"), BuildDoc("C1"), QpdfInputOwnership.Copy);
        List<PdfPageSource> sources = new()
        {
            new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1, 1, 1])),
            new PdfPageSource(QpdfPath.From("inputs/b.pdf"), PdfPageSelectionOptions.FromPages([1])),
            new PdfPageSource(QpdfPath.From("inputs/c.pdf"), PdfPageSelectionOptions.FromPages([1, 1])),
        };
        PdfPageMergeOptions options = new() { Collation = new PdfPageCollation([2, 1, 1]) };
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host, sources, QpdfPath.From("outputs/c.pdf"), options, CancellationToken.None);
        Assert.Equal([0, 0, 1, 2, 0, 2], result.Mapping.Select(mapping => mapping.SourceIndex).ToList());
        List<string> markers = await SelectMarkers(host, sources, options, "outputs/c2.pdf");
        Assert.Equal(["A1", "A1", "B1", "C1", "A1", "C1"], markers.ConvertAll(marker => marker.Contains("A1", StringComparison.Ordinal) ? "A1" : marker.Contains("B1", StringComparison.Ordinal) ? "B1" : "C1"));
    }

    [Fact]
    public async Task SingleGroupSizeAppliesToEverySource()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDoc("A1"));
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildDoc("B1"), QpdfInputOwnership.Copy);
        List<PdfPageSource> sources = new()
        {
            new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1, 1])),
            new PdfPageSource(QpdfPath.From("inputs/b.pdf"), PdfPageSelectionOptions.FromPages([1, 1])),
        };
        List<string> markers = await SelectMarkers(
            host, sources, new PdfPageMergeOptions { Collation = new PdfPageCollation([2]) }, "outputs/c.pdf");
        Assert.Equal(4, markers.Count);
        Assert.Contains("A1", markers[0], StringComparison.Ordinal);
        Assert.Contains("A1", markers[1], StringComparison.Ordinal);
        Assert.Contains("B1", markers[2], StringComparison.Ordinal);
        Assert.Contains("B1", markers[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollationWithRotationUsesFinalOrder()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDoc("A1"));
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildDoc("B1"), QpdfInputOwnership.Copy);
        List<PdfPageSource> sources = new()
        {
            new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1])),
            new PdfPageSource(QpdfPath.From("inputs/b.pdf"), PdfPageSelectionOptions.FromPages([1])),
        };
        PdfPageMergeOptions options = new()
        {
            Collation = PdfPageCollation.Interleave(1),
            Rotations = [new PdfPageRotation(PdfRotationMode.Add, 90, [1])],
        };
        await Qpdf.SelectPagesAsync(host, sources, QpdfPath.From("outputs/c.pdf"), options, CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/c.pdf"), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.True(output.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject first));
        Assert.Equal(90L, Assert.IsType<PdfInteger>(first).Value);
        Assert.False(output.PageReferences[1].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject _));
    }

    [Fact]
    public void CollationConstructionValidates()
    {
#pragma warning disable CS8625 // Intentional null literal for negative tests.
        Assert.Throws<ArgumentNullException>(() => new PdfPageCollation(null!));
#pragma warning restore CS8625
        Assert.Throws<ArgumentException>(() => new PdfPageCollation([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageCollation([1, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageCollation.Interleave(0));
    }

    [Fact]
    public async Task MismatchedGroupCountFailsCleanly()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDoc("A1"));
        host.AddInput(QpdfPath.From("inputs/b.pdf"), BuildDoc("B1"), QpdfInputOwnership.Copy);
        List<PdfPageSource> sources = new()
        {
            new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1])),
            new PdfPageSource(QpdfPath.From("inputs/b.pdf"), PdfPageSelectionOptions.FromPages([1])),
        };
        await Assert.ThrowsAsync<ArgumentException>(() => Qpdf.SelectPagesAsync(
            host, sources, QpdfPath.From("outputs/x.pdf"),
            new PdfPageMergeOptions { Collation = new PdfPageCollation([1, 1, 1]) },
            CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
