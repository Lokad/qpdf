using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class MergeTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildDoc(string headerVersion, string marker, string baseFont, string annots)
    {
        byte[] contents = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (" + marker + ") Tj ET");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >>" + annots + " >>",
            "<< /Length " + contents.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /" + baseFont + " >>",
        };
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-" + headerVersion + "\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            byte[]? streamData = number == 4 ? contents : null;
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

    private static string MarkerOf(QpdfDocument document, int index)
    {
        PdfDictionary page = document.PageReferences[index].Page;
        Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
        PdfReference reference = Assert.IsType<PdfReference>(raw);
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(reference.ObjectNumber));
        return Encoding.ASCII.GetString(stream.Data);
    }

    private static string FontOf(QpdfDocument document, int index)
    {
        PdfDictionary page = document.PageReferences[index].Page;
        Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resourcesRaw));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(resourcesRaw);
        Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject fontRaw));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(fontRaw);
        Assert.True(fonts.TryGet(PdfName.FromAscii("F1"), out QpdfObject f1Raw));
        PdfReference reference = Assert.IsType<PdfReference>(f1Raw);
        PdfDictionary font = Assert.IsType<PdfDictionary>(document.GetObject(reference.ObjectNumber));
        Assert.True(font.TryGet(PdfName.FromAscii("BaseFont"), out QpdfObject baseRaw));
        return Assert.IsType<PdfName>(baseRaw).DisplayText;
    }

    [Fact]
    public async Task SameNumbersAcrossDocumentsNeverCollide()
    {
        byte[] first = BuildDoc("1.4", "AAA", "Helvetica", string.Empty);
        byte[] second = BuildDoc("1.4", "BBB", "Courier", string.Empty);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", first);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), second, QpdfInputOwnership.Copy);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("inputs/b.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/merged.pdf"),
            CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        Assert.Equal([1, 1], result.SourcePageCounts);
        Assert.Equal(new PdfPageSelectionMapping(0, 1), result.Mapping[0]);
        Assert.Equal(new PdfPageSelectionMapping(1, 1), result.Mapping[1]);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/merged.pdf"), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(2, output.PageCount);
        Assert.Contains("AAA", MarkerOf(output, 0), StringComparison.Ordinal);
        Assert.Contains("BBB", MarkerOf(output, 1), StringComparison.Ordinal);
        Assert.Equal("Helvetica", FontOf(output, 0));
        Assert.Equal("Courier", FontOf(output, 1));
    }

    [Fact]
    public async Task SameFieldNamesStayInertWithoutAcroForm()
    {
        string widget = " /Annots [<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /T (Name) >>]";
        byte[] first = BuildDoc("1.4", "AAA", "Helvetica", widget);
        byte[] second = BuildDoc("1.4", "BBB", "Courier", widget);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", first);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), second, QpdfInputOwnership.Copy);
        await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("inputs/b.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/merged.pdf"),
            CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/merged.pdf"), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(2, output.PageCount);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.GetObject(1));
        Assert.False(catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject _));
        foreach (int index in new[] { 0, 1 })
        {
            PdfDictionary page = output.PageReferences[index].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotsRaw));
            PdfDictionary annot = Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(annotsRaw)[0]);
            Assert.True(annot.TryGet(PdfName.FromAscii("T"), out QpdfObject nameRaw));
            Assert.Equal([0x4E, 0x61, 0x6D, 0x65], Assert.IsType<PdfByteString>(nameRaw).Value);
        }
    }

    [Fact]
    public async Task DuplicateDocumentImportKeepsOccurrencesDistinct()
    {
        byte[] doc = BuildDoc("1.4", "AAA", "Helvetica", string.Empty);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", doc);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("inputs/a.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/merged.pdf"),
            CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(new PdfPageSelectionMapping(0, 1), result.Mapping[0]);
        Assert.Equal(new PdfPageSelectionMapping(1, 1), result.Mapping[1]);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/merged.pdf"), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        int? first = output.PageReferences[0].Source?.ObjectNumber;
        int? second = output.PageReferences[1].Source?.ObjectNumber;
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task HighestSourceVersionWins()
    {
        byte[] old = BuildDoc("1.4", "AAA", "Helvetica", string.Empty);
        byte[] recent = BuildDoc("1.7", "BBB", "Helvetica", string.Empty);
        InMemoryQpdfHost host = HostWith("inputs/old.pdf", old);
        host.AddInput(QpdfPath.From("inputs/new.pdf"), recent, QpdfInputOwnership.Copy);
        await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/old.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("inputs/new.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/merged.pdf"),
            CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/merged.pdf"), out byte[] published));
        Assert.Equal("%PDF-1.7", Encoding.ASCII.GetString(published, 0, 8));
    }

    [Fact]
    public async Task OutOfRangeInLaterSourceFailsCleanly()
    {
        byte[] first = BuildDoc("1.4", "AAA", "Helvetica", string.Empty);
        byte[] second = BuildDoc("1.4", "BBB", "Courier", string.Empty);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", first);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), second, QpdfInputOwnership.Copy);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("inputs/b.pdf"), new PdfPageSelectionOptions(9))],
            QpdfPath.From("outputs/x.pdf"),
            CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ValidationsFailFast()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDoc("1.4", "AAA", "Helvetica", string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => Qpdf.SelectPagesAsync(
            host, [], QpdfPath.From("outputs/x.pdf"), CancellationToken.None).AsTask());
#pragma warning disable CS8625 // Intentional null literals for negative tests.
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.SelectPagesAsync(
            null!, [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/x.pdf"), CancellationToken.None).AsTask());
        Assert.Throws<ArgumentNullException>(() => new PdfPageSource(QpdfPath.From("inputs/a.pdf"), null!));
#pragma warning restore CS8625
    }
}
