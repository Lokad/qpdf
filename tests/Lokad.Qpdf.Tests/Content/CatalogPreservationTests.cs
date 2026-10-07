using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Content;

public sealed class CatalogPreservationTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<InMemoryQpdfHost> HostWithFixture(string logical, string fixture)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(logical), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<QpdfDocument> RereadAsync(InMemoryQpdfHost host, string output, string alias)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] pdf));
        host.AddInput(QpdfPath.From(alias), pdf, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, QpdfPath.From(alias), CancellationToken.None);
    }

    private static PdfDictionary RequireCatalog(QpdfDocument output)
    {
        return Assert.IsType<PdfDictionary>(output.GetObject(1));
    }

    [Fact]
    public async Task TypedSelectionDropsOutlinesForContainedPreview()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Equal([1], result.SourcePages);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        Assert.Equal(1, output.PageCount);
        PdfDictionary catalog = RequireCatalog(output);
        Assert.False(catalog.TryGet(PdfName.FromAscii("Outlines"), out QpdfObject _));
    }

    [Fact]
    public async Task ArgvSelectionPreservesOutlinesForOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        Assert.Equal(1, output.PageCount);
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("Outlines"), out QpdfObject? raw));
        PdfReference link = Assert.IsType<PdfReference>(raw);
        PdfDictionary outlines = Assert.IsType<PdfDictionary>(output.GetObject(link.ObjectNumber));
        Assert.True(outlines.TryGet(PdfName.FromAscii("First"), out QpdfObject _));
    }

    [Fact]
    public async Task JobSelectionPreservesOutlinesForOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        string job = "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("Outlines"), out QpdfObject _));
    }

    [Fact]
    public async Task TypedSelectionDropsStructureForContainedPreview()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-preserve-1page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.False(catalog.TryGet(PdfName.FromAscii("StructTreeRoot"), out QpdfObject _));
        Assert.False(catalog.TryGet(PdfName.FromAscii("MarkInfo"), out QpdfObject _));
    }

    [Fact]
    public async Task ArgvSelectionPreservesStructureForOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-preserve-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("StructTreeRoot"), out QpdfObject? treeRaw));
        Assert.IsType<PdfReference>(treeRaw);
        Assert.True(catalog.TryGet(PdfName.FromAscii("MarkInfo"), out QpdfObject? markRaw));
        Assert.IsType<PdfDictionary>(markRaw);
    }

    [Fact]
    public async Task JobSelectionPreservesStructureForOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-preserve-1page.pdf");
        string job = "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("StructTreeRoot"), out QpdfObject _));
        Assert.True(catalog.TryGet(PdfName.FromAscii("MarkInfo"), out QpdfObject _));
    }

    [Fact]
    public async Task DamagedStructureDropsWithoutFailingOrdinary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        Assert.Equal(1, output.PageCount);
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildTwoPageExtrasPdf(string catalogExtra)
    {
        byte[] first = Encoding.ASCII.GetBytes("BT (One) Tj ET");
        byte[] second = Encoding.ASCII.GetBytes("BT (Two) Tj ET");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Length " + first.Length.ToString() + " >>",
            "<< /Length " + second.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = number == 5 ? first : number == 6 ? second : null;
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

    private static void AssertExtras(QpdfDocument output, PdfDictionary catalog)
    {
        Assert.True(catalog.TryGet(PdfName.FromAscii("NeedsRendering"), out QpdfObject? needsRendering));
        Assert.True(Assert.IsType<PdfBoolean>(needsRendering).Value);
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageLayout"), out QpdfObject? layout));
        Assert.Equal("TwoColumnLeft", Assert.IsType<PdfName>(layout).DisplayText);
        Assert.True(catalog.TryGet(PdfName.FromAscii("Lang"), out QpdfObject? lang));
        Assert.Equal("en-US", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(lang).Value));
        Assert.True(catalog.TryGet(PdfName.FromAscii("ViewerPreferences"), out QpdfObject? preferences));
        Assert.IsType<PdfDictionary>(preferences);
        Assert.True(catalog.TryGet(PdfName.FromAscii("OpenAction"), out QpdfObject? openAction));
        PdfArray action = Assert.IsType<PdfArray>(openAction);
        PdfReference target = Assert.IsType<PdfReference>(action[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(output.GetObject(target.ObjectNumber));
        Assert.True(page.TryGet(PdfName.FromAscii("Type"), out QpdfObject? pageType));
        Assert.Equal("Page", Assert.IsType<PdfName>(pageType).DisplayText);
    }

    [Fact]
    public async Task ArgvSubsetPreservesExtras()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "catalog-extras-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        AssertExtras(output, RequireCatalog(output));
    }

    [Fact]
    public async Task JobSubsetPreservesExtras()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "catalog-extras-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host, """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        AssertExtras(output, RequireCatalog(output));
    }

    [Fact]
    public async Task WholeFilePreservesExtras()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "catalog-extras-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host, """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "coalesceContents": true}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        AssertExtras(output, RequireCatalog(output));
    }

    [Fact]
    public async Task TypedPreviewDropsExtras()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "catalog-extras-1page.pdf");
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Equal([1], result.SourcePages);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        Assert.False(RequireCatalog(output).TryGet(PdfName.FromAscii("NeedsRendering"), out QpdfObject _));
    }

    [Fact]
    public async Task MergeKeepsPrimaryExtras()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "catalog-extras-1page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf", "inputs/reread.pdf");
        Assert.Equal(2, output.PageCount);
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("Lang"), out QpdfObject? lang));
        Assert.Equal("en-US", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(lang).Value));
    }

    [Fact]
    public async Task MergeDropsSecondaryExtras()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "catalog-extras-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf", "inputs/reread.pdf");
        Assert.Equal(2, output.PageCount);
        Assert.False(RequireCatalog(output).TryGet(PdfName.FromAscii("Lang"), out QpdfObject _));
    }

    [Fact]
    public async Task EmptyBaseMergeDropsExtras()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "catalog-extras-1page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/m.pdf", "inputs/reread.pdf");
        Assert.Equal(2, output.PageCount);
        Assert.False(RequireCatalog(output).TryGet(PdfName.FromAscii("NeedsRendering"), out QpdfObject _));
    }

    [Fact]
    public async Task SplitDropsExtras()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "catalog-extras-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--split-pages", "--pages", ".", "1", "--", "outputs/s.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/s-1.pdf", "inputs/reread.pdf");
        Assert.Equal(1, output.PageCount);
        Assert.False(RequireCatalog(output).TryGet(PdfName.FromAscii("NeedsRendering"), out QpdfObject _));
    }

    [Fact]
    public async Task DanglingOpenActionNulls()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildTwoPageExtrasPdf(" /NeedsRendering true /OpenAction [4 0 R /Fit]"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("NeedsRendering"), out QpdfObject _));
        Assert.True(catalog.TryGet(PdfName.FromAscii("OpenAction"), out QpdfObject? openAction));
        Assert.IsType<PdfNull>(Assert.IsType<PdfArray>(openAction)[0]);
    }

    [Fact]
    public async Task DamagedExtraDropsSilently()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildTwoPageExtrasPdf(" /NeedsRendering true /BrokenExtra 99 0 R"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("NeedsRendering"), out QpdfObject _));
        Assert.False(catalog.TryGet(PdfName.FromAscii("BrokenExtra"), out QpdfObject _));
    }

    [Fact]
    public async Task StructuralRefExtraDrops()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildTwoPageExtrasPdf(" /NeedsRendering true /TreeRef 2 0 R"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("NeedsRendering"), out QpdfObject _));
        Assert.False(catalog.TryGet(PdfName.FromAscii("TreeRef"), out QpdfObject _));
    }

    [Fact]
    public async Task ExplicitLabelsCoexistWithExtras()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "catalog-extras-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--set-page-labels", "1:D", "--", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await RereadAsync(host, "outputs/a.pdf", "inputs/reread.pdf");
        PdfDictionary catalog = RequireCatalog(output);
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject _));
        Assert.True(catalog.TryGet(PdfName.FromAscii("NeedsRendering"), out QpdfObject _));
    }
}
