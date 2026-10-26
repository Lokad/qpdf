using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class XRefStreamTests
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

    private static async Task<QpdfDocument> ReopenOutput(InMemoryQpdfHost host, string output)
    {
        if (!host.TryGetOutput(QpdfPath.From(output), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException($"Output was not published: {output}.");
        }

        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, published, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
    }

    [Fact]
    public async Task OpensXRefStreamFile()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("xstream-1page.pdf");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/xstream-1page.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        Assert.Equal("1.4", document.Version.ToString());
        Assert.Equal(PdfNull.Instance, document.GetObject(0));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(new PdfName(Encoding.ASCII.GetBytes("Type")), out QpdfObject type));
        Assert.Equal(new PdfName(Encoding.ASCII.GetBytes("Catalog")), type);
        PdfStream contents = Assert.IsType<PdfStream>(document.GetObject(4));
        Assert.Equal("BT /F1 24 Tf 100 700 Td (Hello) Tj ET", Encoding.ASCII.GetString(contents.Data));
    }

    [Fact]
    public async Task OpensTypelessEntryForm()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("xstream-nowtype.pdf");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/xstream-nowtype.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public async Task ResolvesCompressedObjects()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("objstm-1page.pdf");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/objstm-1page.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        PdfDictionary font = Assert.IsType<PdfDictionary>(document.GetObject(6));
        Assert.True(font.TryGet(new PdfName(Encoding.ASCII.GetBytes("BaseFont")), out QpdfObject baseFont));
        Assert.Equal(new PdfName(Encoding.ASCII.GetBytes("Helvetica")), baseFont);
        PdfInteger length = Assert.IsType<PdfInteger>(document.GetObject(7));
        Assert.Equal(37L, length.Value);
        PdfStream contents = Assert.IsType<PdfStream>(document.GetObject(4));
        Assert.Equal(37, contents.Data.Length);
    }

    [Fact]
    public async Task SelectsFromCompressedStructures()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("objstm-1page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host,
            QpdfPath.From("inputs/objstm-1page.pdf"),
            QpdfPath.From("outputs/objstm.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        await using QpdfDocument output = await ReopenOutput(host, "outputs/objstm.pdf");
        Assert.Equal(1, output.PageCount);

        (InMemoryQpdfHost cross, byte[] _) = await LoadFixture("xstream-1page.pdf");
        await Qpdf.SelectPageAsync(
            cross,
            QpdfPath.From("inputs/xstream-1page.pdf"),
            QpdfPath.From("outputs/xstream.pdf"),
            new PdfPageSelectionOptions(1),
            CancellationToken.None);
        await using QpdfDocument xoutput = await ReopenOutput(cross, "outputs/xstream.pdf");
        Assert.Equal(1, xoutput.PageCount);
        PdfStream stream = Assert.IsType<PdfStream>(xoutput.GetObject(4));
        Assert.Equal("BT /F1 24 Tf 100 700 Td (Hello) Tj ET", Encoding.ASCII.GetString(stream.Data));
    }

    [Fact]
    public async Task AcceptsLzwXRefStream()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("xstream-lzw.pdf");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/xstream-lzw.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public async Task UnknownEntryTypeHasNullSemantics()
    {
        (InMemoryQpdfHost host, byte[] data) = await LoadFixture("xstream-badtype.pdf");
        using (host)
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using (XRefTable table = XRefTable.Parse(data, QpdfLimits.Default, governor, new(governor), QpdfRecoveryMode.Strict, false))
            {
                // This fixture marks the catalog itself as future type 5.
                Assert.Equal(XrefEntryKind.Free, table.Entries[1].Kind);
                Assert.Equal(0L, table.Entries[1].First); Assert.Equal(0, table.Entries[1].Second);
            }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Fact]
    public async Task RejectsChainedPrevStreams()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("prev-chain.pdf");
        QpdfParseException ex = await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/prev-chain.pdf"), CancellationToken.None).AsTask());
        Assert.Contains("/Prev", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsHybridXRefStm()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("xstm-classic.pdf");
        QpdfParseException ex = await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/xstm-classic.pdf"), CancellationToken.None).AsTask());
        Assert.Contains("/XRefStm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtendedObjectStreamMemberReadsThroughOwnPairs()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("objstm-extends.pdf");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/objstm-extends.pdf"), CancellationToken.None);
        PdfDictionary font = Assert.IsType<PdfDictionary>(document.GetObject(6));
        Assert.True(font.TryGet(PdfName.FromAscii("BaseFont"), out QpdfObject? baseFont));
        Assert.Equal("Helvetica", Assert.IsType<PdfName>(baseFont).DisplayText);
    }

    [Fact]
    public async Task ExtendedObjectStreamMemberShowsThroughArgv()
    {
        (InMemoryQpdfHost host, byte[] _) = await LoadFixture("objstm-extends.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/objstm-extends.pdf", "--show-object=6"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
    }

    [Fact]
    public async Task RejectsEntryOffsetsOutsideInput()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf"));
        byte[] marker = Encoding.ASCII.GetBytes("0000000058 00000 n ");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        for (int i = 0; i < 10; i++)
        {
            bytes[at + i] = (byte)'9';
        }

        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/bad.pdf"), bytes, QpdfInputOwnership.Copy);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static int FindMarker(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
    [Fact]
    public void InflatedObjectCountCannotAllocateBeyondBudget()
    {
        byte[] data = Encoding.ASCII.GetBytes("5 0 obj\n<< /Type /ObjStm /N 1000000 /First 4 /Length 8 >>\nstream\n6 0 null\nendstream\nendobj\n");
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 65536L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        var entries = new System.Collections.Generic.Dictionary<int, XrefEntry>
        {
            [5] = XrefEntry.Uncompressed(0L, 0),
            [6] = XrefEntry.Compressed(5, 0),
        };
        PdfDictionary trailer = new(new System.Collections.Generic.Dictionary<PdfName, QpdfObject>());
        var table = new XRefTable(0L, entries, trailer, [trailer]);
        var engine = new DocumentEngine(data, table, limits, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict);
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { engine.Load(6); } catch (QpdfException) { }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1048576L);
    }
}
