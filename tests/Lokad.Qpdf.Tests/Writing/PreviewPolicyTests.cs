using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PreviewPolicyTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildPolicyPdf(
        string catalogExtra,
        List<(string Dict, byte[]? StreamData)> extras,
        string ancestorExtra,
        string annots1,
        bool omitMedia1,
        bool nullMedia1)
    {
        string media1 = omitMedia1 || nullMedia1 ? string.Empty : " /MediaBox [0 0 612 792]";
        if (nullMedia1)
        {
            media1 = " /MediaBox null";
        }

        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2" + ancestorExtra + " >>",
            "<< /Type /Page /Parent 2 0 R" + media1 + " /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >>" + annots1 + " >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Length 37 >>",
            "<< /Length 38 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach ((string dict, byte[]? _) in extras)
        {
            bodies.Add(dict);
        }

        byte[] data1 = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (First) Tj ET");
        byte[] data2 = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Second) Tj ET");
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = null;
            if (number == 5)
            {
                streamData = data1;
            }
            else if (number == 6)
            {
                streamData = data2;
            }
            else if (number > 7)
            {
                streamData = extras[number - 8].StreamData;
            }

            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
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

    private static byte[] Basic(string annots1)
    {
        return BuildPolicyPdf(string.Empty, new List<(string, byte[]?)>(), string.Empty, annots1, false, false);
    }

    private static PdfDictionary OutputPage(QpdfDocument document, int index)
    {
        PageLocation location = document.PageReferences[index];
        return location.Page;
    }

    private static PdfDictionary AnnotAt(PdfDictionary page, int index)
    {
        Assert.True(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw));
        PdfArray annots = Assert.IsType<PdfArray>(raw);
        return Assert.IsType<PdfDictionary>(annots[index]);
    }

    [Fact]
    public async Task OmittedPageDestinationPrunesToNull()
    {
        string annots = " /Annots [<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [4 0 R /XYZ null null null] >>]";
        byte[] bytes = Basic(annots);
        InMemoryQpdfHost host = HostWith("inputs/link.pdf", bytes);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/link.pdf"), QpdfPath.From("outputs/link.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Equal([1], result.SourcePages);
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), host.TryGetOutput(QpdfPath.From("outputs/link.pdf"), out byte[] pub) ? pub : throw new Xunit.Sdk.XunitException("missing"), QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(1, output.PageCount);
        PdfDictionary annot = AnnotAt(OutputPage(output, 0), 0);
        Assert.True(annot.TryGet(PdfName.FromAscii("Dest"), out QpdfObject destRaw));
        PdfArray dest = Assert.IsType<PdfArray>(destRaw);
        Assert.IsType<PdfNull>(dest[0]);
    }

    [Fact]
    public async Task SelectedPageDestinationRemapsBackward()
    {
        string annots = " /Annots [<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [4 0 R /XYZ null null null] >>]";
        byte[] bytes = Basic(annots);
        InMemoryQpdfHost host = HostWith("inputs/link.pdf", bytes);
        await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/link.pdf"), QpdfPath.From("outputs/link.pdf"),
            PdfPageSelectionOptions.FromPages([2, 1]), CancellationToken.None);
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), host.TryGetOutput(QpdfPath.From("outputs/link.pdf"), out byte[] pub) ? pub : throw new Xunit.Sdk.XunitException("missing"), QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(2, output.PageCount);
        PdfDictionary annot = AnnotAt(OutputPage(output, 1), 0);
        Assert.True(annot.TryGet(PdfName.FromAscii("Dest"), out QpdfObject destRaw));
        PdfArray dest = Assert.IsType<PdfArray>(destRaw);
        PdfReference reference = Assert.IsType<PdfReference>(dest[0]);
        Assert.NotEqual(4, reference.ObjectNumber);
    }

    [Fact]
    public async Task CatalogPolicyKeepsMetadataDropsOutlines()
    {
        List<(string, byte[]?)> extras = new() { ("<< /Foo (bar) >>", null), ("<< /NamesMap 1 >>", null) };
        byte[] bytes = BuildPolicyPdf(" /Outlines 8 0 R /Metadata 9 0 R", extras, string.Empty, string.Empty, false, false);
        InMemoryQpdfHost host = HostWith("inputs/cat.pdf", bytes);
        await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/cat.pdf"), QpdfPath.From("outputs/cat.pdf"),
            PdfPageSelectionOptions.FromPages([1, 2]), CancellationToken.None);
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), host.TryGetOutput(QpdfPath.From("outputs/cat.pdf"), out byte[] pub) ? pub : throw new Xunit.Sdk.XunitException("missing"), QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(2, output.PageCount);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.GetObject(1));
        HashSet<string> keys = new();
        foreach (PdfName key in catalog.Keys)
        {
            keys.Add(key.DisplayText);
        }

        Assert.Equal(new HashSet<string>(["Type", "Pages", "Metadata"]), keys);
        Assert.False(catalog.TryGet(PdfName.FromAscii("Outlines"), out QpdfObject _));
    }

    [Fact]
    public async Task PageOnlyBoxesDoNotInherit()
    {
        byte[] bytes = BuildPolicyPdf(string.Empty, new List<(string, byte[]?)>(), " /BleedBox [0 0 100 100] /UserUnit 2.0", string.Empty, false, false);
        InMemoryQpdfHost host = HostWith("inputs/box.pdf", bytes);
        await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/box.pdf"), QpdfPath.From("outputs/box.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), host.TryGetOutput(QpdfPath.From("outputs/box.pdf"), out byte[] pub) ? pub : throw new Xunit.Sdk.XunitException("missing"), QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        PdfDictionary page = OutputPage(output, 0);
        Assert.False(page.TryGet(PdfName.FromAscii("BleedBox"), out QpdfObject _));
        Assert.False(page.TryGet(PdfName.FromAscii("UserUnit"), out QpdfObject _));
        Assert.True(page.TryGet(PdfName.FromAscii("MediaBox"), out QpdfObject _));
    }

    [Fact]
    public async Task NullMediaBoxFallsBackToAncestor()
    {
        byte[] bytes = BuildPolicyPdf(string.Empty, new List<(string, byte[]?)>(), " /MediaBox [0 0 111 222]", string.Empty, false, true);
        InMemoryQpdfHost host = HostWith("inputs/nullbox.pdf", bytes);
        await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/nullbox.pdf"), QpdfPath.From("outputs/nullbox.pdf"),
            PdfPageSelectionOptions.FromPages([1, 2, 1]), CancellationToken.None);
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), host.TryGetOutput(QpdfPath.From("outputs/nullbox.pdf"), out byte[] pub) ? pub : throw new Xunit.Sdk.XunitException("missing"), QpdfInputOwnership.Copy);
        await using (QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None))
        {
            Assert.Equal(3, output.PageCount);
            for (int index = 0; index < output.PageCount; index++)
            {
                PdfDictionary page = OutputPage(output, index);
                Assert.True(page.TryGet(PdfName.FromAscii("MediaBox"), out QpdfObject boxRaw));
                PdfArray box = Assert.IsType<PdfArray>(boxRaw);
                Assert.Equal(4, box.Count);
                Assert.Equal(index == 1 ? 612L : 111L, Assert.IsType<PdfInteger>(box[2]).Value);
                Assert.Equal(index == 1 ? 792L : 222L, Assert.IsType<PdfInteger>(box[3]).Value);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NullMediaBoxWithoutFallbackFails()
    {
        byte[] bytes = BuildPolicyPdf(string.Empty, new List<(string, byte[]?)>(), string.Empty, string.Empty, false, true);
        InMemoryQpdfHost host = HostWith("inputs/nullbox.pdf", bytes);
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/nullbox.pdf"), QpdfPath.From("outputs/x.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
    }

    [Fact]
    public async Task MissingMediaBoxEverywhereFails()
    {
        byte[] bytes = BuildPolicyPdf(string.Empty, new List<(string, byte[]?)>(), string.Empty, string.Empty, true, false);
        InMemoryQpdfHost host = HostWith("inputs/nobox.pdf", bytes);
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/nobox.pdf"), QpdfPath.From("outputs/x.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
    }

    [Fact]
    public async Task WidgetAnnotationRetainsAppearance()
    {
        byte[] appearance = Encoding.ASCII.GetBytes("APPEARANCE-BYTES");
        List<(string, byte[]?)> extras = new() { ("<< /Length " + appearance.Length.ToString() + " >>", appearance) };
        string annots = " /Annots [<< /Type /Annot /Subtype /Widget /Rect [0 0 20 20] /AP << /N 8 0 R >> >>]";
        byte[] bytes = BuildPolicyPdf(string.Empty, extras, string.Empty, annots, false, false);
        InMemoryQpdfHost host = HostWith("inputs/widget.pdf", bytes);
        await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/widget.pdf"), QpdfPath.From("outputs/widget.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), host.TryGetOutput(QpdfPath.From("outputs/widget.pdf"), out byte[] pub) ? pub : throw new Xunit.Sdk.XunitException("missing"), QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        PdfDictionary annot = AnnotAt(OutputPage(output, 0), 0);
        Assert.True(annot.TryGet(PdfName.FromAscii("AP"), out QpdfObject apRaw));
        PdfDictionary ap = Assert.IsType<PdfDictionary>(apRaw);
        Assert.True(ap.TryGet(PdfName.FromAscii("N"), out QpdfObject nRaw));
        PdfReference appearanceRef = Assert.IsType<PdfReference>(nRaw);
        PdfStream stream = Assert.IsType<PdfStream>(output.GetObject(appearanceRef.ObjectNumber));
        Assert.Equal(appearance, stream.Data);
    }
}
