using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class RotationTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildDoc(string ancestorRotate, string pageRotate, string marker)
    {
        byte[] contents = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (" + marker + ") Tj ET");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1" + ancestorRotate + " >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" + pageRotate + " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + contents.Length.ToString() + " >>",
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

    private static async Task<QpdfDocument> Reopen(InMemoryQpdfHost host, string output)
    {
        if (!host.TryGetOutput(QpdfPath.From(output), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException("Output was not published: " + output + ".");
        }

        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, published, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
    }

    private static long PageRotation(QpdfDocument document, int index)
    {
        PdfDictionary page = document.PageReferences[index].Page;
        Assert.True(page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject raw));
        return Assert.IsType<PdfInteger>(raw).Value;
    }

    [Fact]
    public async Task AddRotatesFromInheritedBase()
    {
        byte[] bytes = BuildDoc(" /Rotate 90", string.Empty, "Hi");
        InMemoryQpdfHost host = HostWith("inputs/rot.pdf", bytes);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/rot.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/rot.pdf"),
            [new PdfPageRotation(PdfRotationMode.Add, 90, null)],
            CancellationToken.None);
        Assert.Equal([new PdfPageSelectionMapping(0, 1)], result.Mapping);
        await using QpdfDocument output = await Reopen(host, "outputs/rot.pdf");
        Assert.Equal(180L, PageRotation(output, 0));
    }

    [Fact]
    public async Task SubtractWrapsBelowZero()
    {
        byte[] bytes = BuildDoc(string.Empty, string.Empty, "Hi");
        InMemoryQpdfHost host = HostWith("inputs/rot.pdf", bytes);
        await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/rot.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/rot.pdf"),
            [new PdfPageRotation(PdfRotationMode.Subtract, 180, null)],
            CancellationToken.None);
        await using QpdfDocument output = await Reopen(host, "outputs/rot.pdf");
        Assert.Equal(180L, PageRotation(output, 0));
    }

    [Fact]
    public async Task SetIgnoresBaseAndLaterSpecsWin()
    {
        byte[] bytes = BuildDoc(" /Rotate 90", " /Rotate 270", "Hi");
        InMemoryQpdfHost host = HostWith("inputs/rot.pdf", bytes);
        await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/rot.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/rot.pdf"),
            [new PdfPageRotation(PdfRotationMode.Add, 90, null),
             new PdfPageRotation(PdfRotationMode.Set, 180, [1])],
            CancellationToken.None);
        await using QpdfDocument output = await Reopen(host, "outputs/rot.pdf");
        Assert.Equal(180L, PageRotation(output, 0));
    }

    [Fact]
    public async Task ExplicitPageBaseWinsOverAncestor()
    {
        byte[] bytes = BuildDoc(" /Rotate 90", " /Rotate 270", "Hi");
        InMemoryQpdfHost host = HostWith("inputs/rot.pdf", bytes);
        await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/rot.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/rot.pdf"),
            [new PdfPageRotation(PdfRotationMode.Add, 90, null)],
            CancellationToken.None);
        await using QpdfDocument output = await Reopen(host, "outputs/rot.pdf");
        Assert.Equal(0L, PageRotation(output, 0));
    }

    [Fact]
    public async Task OutOfRangePositionsFailCleanly()
    {
        byte[] bytes = BuildDoc(string.Empty, string.Empty, "Hi");
        InMemoryQpdfHost host = HostWith("inputs/rot.pdf", bytes);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/rot.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/x.pdf"),
            [new PdfPageRotation(PdfRotationMode.Add, 90, [5])],
            CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void InvalidRotationsFailFast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageRotation(PdfRotationMode.Add, 45, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageRotation(PdfRotationMode.Set, 90, [0]));
    }

    [Fact]
    public async Task DuplicatesRotateIndependently()
    {
        byte[] bytes = BuildDoc(string.Empty, string.Empty, "Hi");
        InMemoryQpdfHost host = HostWith("inputs/rot.pdf", bytes);
        await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/rot.pdf"), PdfPageSelectionOptions.FromPages([1, 1]))],
            QpdfPath.From("outputs/rot.pdf"),
            [new PdfPageRotation(PdfRotationMode.Add, 90, [2])],
            CancellationToken.None);
        await using QpdfDocument output = await Reopen(host, "outputs/rot.pdf");
        Assert.Equal(2, output.PageCount);
        Assert.False(output.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject _));
        Assert.Equal(90L, PageRotation(output, 1));
    }

    [Fact]
    public async Task GarbageBaseFailsOnlyWhenRotated()
    {
        byte[] bytes = BuildDoc(string.Empty, " /Rotate (nope)", "Hi");
        InMemoryQpdfHost preservedHost = HostWith("inputs/garbage.pdf", bytes);
        await Qpdf.SelectPagesAsync(
            preservedHost,
            [new PdfPageSource(QpdfPath.From("inputs/garbage.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/kept.pdf"),
            [],
            CancellationToken.None);
        await using QpdfDocument preserved = await Reopen(preservedHost, "outputs/kept.pdf");
        Assert.True(preserved.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject kept));
        Assert.Equal([0x6E, 0x6F, 0x70, 0x65], Assert.IsType<PdfByteString>(kept).Value);

        InMemoryQpdfHost host = HostWith("inputs/garbage.pdf", bytes);
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/garbage.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/x.pdf"),
            [new PdfPageRotation(PdfRotationMode.Add, 90, null)],
            CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ArgvRotateCoversAllPagesByDefault()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--rotate=+90", "--", "outputs/r.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Reopen(host, "outputs/r.pdf");
        Assert.Equal(90L, PageRotation(output, 0));
    }

    [Fact]
    public async Task ArgvRotateRangeOutOfRangeFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--rotate=+90:2", "--", "outputs/r.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/r.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--rotate=45")]
    [InlineData("--rotate=+90:odd")]
    [InlineData("--rotate=")]
    [InlineData("--rotate")]
    [InlineData("--rotate=+90:2-1")]
    public async Task ArgvRotateInvalidSpecsFail(string flag)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = ["inputs/a.pdf", "--pages", ".", "1", flag, "--", "outputs/r.pdf"];
        if (flag == "--rotate")
        {
            argv = ["inputs/a.pdf", "--pages", ".", "1", "--rotate", "--", "outputs/r.pdf"];
        }

        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task JobRotateKeyMatchesArgv()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "1"}], "rotate": ["+90"]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Reopen(host, "outputs/j.pdf");
        Assert.Equal(90L, PageRotation(output, 0));

        InMemoryQpdfHost bad = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult rejected = await Qpdf.ExecuteJobAsync(
            bad,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "1"}], "rotate": ["sideways"]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, rejected.Status);
    }

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
}
