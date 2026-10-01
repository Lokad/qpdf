using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PageSelectionTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<(InMemoryQpdfHost Host, byte[] Bytes)> LoadFixture(string file)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), file));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/" + file), bytes, QpdfInputOwnership.Copy);
        return (host, bytes);
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildNPages(int count)
    {
        return BuildNPages(count, string.Empty);
    }

    private static byte[] BuildNPages(int count, string fontExtra)
    {
        int fontNumber = 3 + (count * 2);
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(3, count).Select(n => n.ToString() + " 0 R")) + "] /Count " + count.ToString() + " >>",
        };
        List<byte[]> streams = new();
        for (int i = 1; i <= count; i++)
        {
            int contentsNumber = 3 + count + (i - 1);
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents " + contentsNumber.ToString() + " 0 R /Resources << /Font << /F1 " + fontNumber.ToString() + " 0 R >> >> >>");
        }

        for (int i = 1; i <= count; i++)
        {
            streams.Add(Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Page" + i.ToString() + ") Tj ET"));
        }

        bodies.AddRange(streams.Select(data => "<< /Length " + data.Length.ToString() + " >>"));
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica" + fontExtra + " >>");

        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            int streamIndex = number - 3 - count;
            if (streamIndex >= 0 && streamIndex < streams.Count)
            {
                byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
                byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
                byte[] data = streams[streamIndex];
                offsets.Add(position);
                position += open.Length + streamOpen.Length + data.Length + streamClose.Length;
                byte[] payload = new byte[open.Length + streamOpen.Length + data.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, payload, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(data, 0, payload, at, data.Length);
                at += data.Length;
                Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
                parts.Add(payload);
                continue;
            }

            byte[] close = Encoding.ASCII.GetBytes("endobj\n");
            offsets.Add(position);
            position += open.Length + close.Length;
            byte[] plain = new byte[open.Length + close.Length];
            Buffer.BlockCopy(open, 0, plain, 0, open.Length);
            Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
            parts.Add(plain);
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

    private static async Task<List<string>> OutputMarkers(InMemoryQpdfHost host, string output)
    {
        QpdfPath rereadPath = QpdfPath.From("inputs/reread.pdf");
        if (!host.TryGetOutput(QpdfPath.From(output), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException("Output was not published: " + output + ".");
        }

        host.AddInput(rereadPath, published, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, rereadPath, CancellationToken.None);
        List<string> markers = new();
        foreach (PageLocation location in document.PageReferences)
        {
            if (!location.Page.TryGet(new PdfName(Encoding.ASCII.GetBytes("Contents")), out QpdfObject raw))
            {
                throw new Xunit.Sdk.XunitException("Output page has no /Contents.");
            }

            PdfStream stream;
            if (raw is PdfReference reference)
            {
                stream = Assert.IsType<PdfStream>(document.GetObject(reference.ObjectNumber));
            }
            else
            {
                stream = Assert.IsType<PdfStream>(raw);
            }

            markers.Add(Encoding.ASCII.GetString(stream.Data));
        }

        return markers;
    }

    [Fact]
    public async Task OrderedSelectionPreservesOrderAndDuplicates()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-2page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/ordered.pdf"),
            PdfPageSelectionOptions.FromPages([2, 1, 2]),
            CancellationToken.None);
        Assert.Equal(2, result.SourcePageCount);
        Assert.Equal([2, 1, 2], result.SourcePages);
        Assert.Equal(3, result.PageCount);
        List<string> markers = await OutputMarkers(host, "outputs/ordered.pdf");
        Assert.Equal(3, markers.Count);
        Assert.Contains("Second", markers[0], StringComparison.Ordinal);
        Assert.Contains("First", markers[1], StringComparison.Ordinal);
        Assert.Contains("Second", markers[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicatePagesAreDistinctObjects()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-2page.pdf");
        await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/dup.pdf"),
            PdfPageSelectionOptions.FromPages([1, 1]),
            CancellationToken.None);
        if (!host.TryGetOutput(QpdfPath.From("outputs/dup.pdf"), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException("Output was not published.");
        }

        QpdfPath rereadPath = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(rereadPath, published, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, rereadPath, CancellationToken.None);
        Assert.Equal(2, document.PageCount);
        int? first = document.PageReferences[0].Source?.ObjectNumber;
        int? second = document.PageReferences[1].Source?.ObjectNumber;
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task FirstAndLastReturnsWindowsInOrder()
    {
        byte[] bytes = BuildNPages(4);
        InMemoryQpdfHost host = HostWith("inputs/four.pdf", bytes);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/four.pdf"), QpdfPath.From("outputs/fl.pdf"),
            PdfPageSelectionOptions.FirstAndLast(1), CancellationToken.None);
        Assert.Equal(4, result.SourcePageCount);
        Assert.Equal([1, 4], result.SourcePages);
        List<string> markers = await OutputMarkers(host, "outputs/fl.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("Page1", markers[0], StringComparison.Ordinal);
        Assert.Contains("Page4", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstAndLastOverlapReturnsAllPagesOnce()
    {
        byte[] bytes = BuildNPages(4);
        InMemoryQpdfHost host = HostWith("inputs/four.pdf", bytes);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/four.pdf"), QpdfPath.From("outputs/fl.pdf"),
            PdfPageSelectionOptions.FirstAndLast(3), CancellationToken.None);
        Assert.Equal([1, 2, 3, 4], result.SourcePages);
        Assert.Equal(4, result.PageCount);
    }

    [Fact]
    public async Task FirstAndLastMeetsExactly()
    {
        byte[] bytes = BuildNPages(4);
        InMemoryQpdfHost host = HostWith("inputs/four.pdf", bytes);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/four.pdf"), QpdfPath.From("outputs/fl.pdf"),
            PdfPageSelectionOptions.FirstAndLast(2), CancellationToken.None);
        Assert.Equal([1, 2, 3, 4], result.SourcePages);
    }

    [Fact]
    public async Task SelectingAllPagesRewritesRatherThanEchoes()
    {
        (InMemoryQpdfHost host, byte[] input) = await LoadFixture("minimal-2page.pdf");
        await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/all.pdf"),
            PdfPageSelectionOptions.FromPages([1, 2]),
            CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/all.pdf"), out byte[] published));
        Assert.NotEqual(input, published);
        List<string> markers = await OutputMarkers(host, "outputs/all.pdf");
        Assert.Equal(2, markers.Count);
    }

    [Fact]
    public async Task OutOfRangePagesFailExplicitly()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-2page.pdf");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/x.pdf"),
            PdfPageSelectionOptions.FromPages([1, 3]),
            CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
    }

    [Fact]
    public void EmptyAndInvalidSelectionsFailFast()
    {
        Assert.Throws<ArgumentException>(() => PdfPageSelectionOptions.FromPages([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageSelectionOptions.FromPages([1, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageSelectionOptions.FirstAndLast(0));
        Assert.Throws<ArgumentNullException>(() => PdfPageSelectionOptions.FromPages(null!));
    }

    [Fact]
    public void NormalizedSelectionSortsAndDedups()
    {
        PdfPageSelectionOptions options = PdfPageSelectionOptions.Normalized([3, 1, 2, 1]);
        Assert.Equal([1, 2, 3], options.Pages);
    }

    [Fact]
    public async Task EmptyDocumentFirstAndLastYieldsEmpty()
    {
        byte[] bytes = BuildNPages(0);
        InMemoryQpdfHost host = HostWith("inputs/empty.pdf", bytes);
        await using QpdfDocument source = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/empty.pdf"), CancellationToken.None);
        Assert.Equal(0, source.PageCount);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/empty.pdf"), QpdfPath.From("outputs/empty.pdf"),
            PdfPageSelectionOptions.FirstAndLast(2), CancellationToken.None);
        Assert.Equal(0, result.SourcePageCount);
        Assert.Empty(result.SourcePages);
        Assert.Equal(0, result.PageCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/empty.pdf"), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(0, output.PageCount);
    }

    [Fact]
    public async Task SharedResourcesCopiedOnceAcrossDuplicates()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("minimal-2page.pdf");
        await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/minimal-2page.pdf"),
            QpdfPath.From("outputs/dup.pdf"),
            PdfPageSelectionOptions.FromPages([1, 1]),
            CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/dup.pdf"), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(2, output.PageCount);
        int firstFont = FontObject(output, 0);
        int secondFont = FontObject(output, 1);
        Assert.Equal(firstFont, secondFont);
    }

    [Fact]
    public async Task EscapedNamesAndStringsPreserved()
    {
        byte[] bytes = BuildNPages(1, " /BaseFontX /Helv#E9tica /FontName (A\\(B)");
        InMemoryQpdfHost host = HostWith("inputs/esc.pdf", bytes);
        await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/esc.pdf"), QpdfPath.From("outputs/esc.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/esc.pdf"), out byte[] published));
        bool hasEscape = false;
        byte[] marker = Encoding.ASCII.GetBytes("#E9");
        for (int i = 0; i + marker.Length <= published.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < marker.Length; j++)
            {
                if (published[i + j] != marker[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                hasEscape = true;
                break;
            }
        }

        Assert.True(hasEscape);
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        PdfDictionary font = FindFont(output, 0);
        Assert.True(font.TryGet(PdfName.FromAscii("BaseFontX"), out QpdfObject nameRaw));
        Assert.Equal([0x48, 0x65, 0x6C, 0x76, 0xE9, 0x74, 0x69, 0x63, 0x61], Assert.IsType<PdfName>(nameRaw).RawBytes);
        Assert.True(font.TryGet(PdfName.FromAscii("FontName"), out QpdfObject textRaw));
        Assert.Equal([0x41, 0x28, 0x42], Assert.IsType<PdfByteString>(textRaw).Value);
    }

    [Fact]
    public async Task CyclicResourcesTerminate()
    {
        byte[] bytes = BuildNPages(1, " /Self 5 0 R");
        InMemoryQpdfHost host = HostWith("inputs/cyclic.pdf", bytes);
        await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/cyclic.pdf"), QpdfPath.From("outputs/cyclic.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/cyclic.pdf"), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(1, output.PageCount);
    }

    private static int FontObject(QpdfDocument document, int index)
    {
        PdfDictionary page = document.PageReferences[index].Page;
        Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resourcesRaw));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(resourcesRaw);
        Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject fontRaw));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(fontRaw);
        Assert.True(fonts.TryGet(PdfName.FromAscii("F1"), out QpdfObject f1Raw));
        PdfReference reference = Assert.IsType<PdfReference>(f1Raw);
        return reference.ObjectNumber;
    }

    private static PdfDictionary FindFont(QpdfDocument document, int index)
    {
        PdfDictionary page = document.PageReferences[index].Page;
        Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resourcesRaw));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(resourcesRaw);
        Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject fontRaw));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(fontRaw);
        Assert.True(fonts.TryGet(PdfName.FromAscii("F1"), out QpdfObject f1Raw));
        PdfReference reference = Assert.IsType<PdfReference>(f1Raw);
        return Assert.IsType<PdfDictionary>(document.GetObject(reference.ObjectNumber));
    }}
