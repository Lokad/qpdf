using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LabelTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildDoc(int pages)
    {
        int fontNumber = 3 + (pages * 2);
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(3, pages).Select(n => n.ToString() + " 0 R")) + "] /Count " + pages.ToString() + " >>",
        };
        List<byte[]> streams = new();
        for (int i = 1; i <= pages; i++)
        {
            int contentsNumber = 3 + pages + (i - 1);
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents " + contentsNumber.ToString() + " 0 R /Resources << /Font << /F1 " + fontNumber.ToString() + " 0 R >> >> >>");
        }

        for (int i = 1; i <= pages; i++)
        {
            streams.Add(Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Page" + i.ToString() + ") Tj ET"));
        }

        bodies.AddRange(streams.Select(data => "<< /Length " + data.Length.ToString() + " >>"));
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            int streamIndex = number - 3 - pages;
            byte[]? streamData = streamIndex >= 0 && streamIndex < streams.Count ? streams[streamIndex] : null;
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

    private static PdfDictionary ReadLabels(QpdfDocument document)
    {
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject raw));
        return Assert.IsType<PdfDictionary>(raw);
    }

    private static (List<long> Keys, List<PdfDictionary> Dicts) ReadNums(PdfDictionary labels)
    {
        Assert.True(labels.TryGet(PdfName.FromAscii("Nums"), out QpdfObject raw));
        PdfArray numbers = Assert.IsType<PdfArray>(raw);
        Assert.True(numbers.Count % 2 == 0);
        List<long> keys = new();
        List<PdfDictionary> dicts = new();
        for (int i = 0; i < numbers.Count; i += 2)
        {
            keys.Add(Assert.IsType<PdfInteger>(numbers[i]).Value);
            dicts.Add(Assert.IsType<PdfDictionary>(numbers[i + 1]));
        }

        return (keys, dicts);
    }

    private static string LabelStyle(PdfDictionary dict)
    {
        if (!dict.TryGet(PdfName.FromAscii("S"), out QpdfObject raw))
        {
            return "none";
        }

        return Assert.IsType<PdfName>(raw).DisplayText;
    }

    [Fact]
    public async Task TypedLabelsEmitNumberTree()
    {
        byte[] bytes = BuildDoc(4);
        InMemoryQpdfHost host = HostWith("inputs/labels.pdf", bytes);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/labels.pdf"), PdfPageSelectionOptions.FromPages([1, 2, 3, 4]))],
            QpdfPath.From("outputs/labels.pdf"),
            new PdfPageMergeOptions
            {
                Labels = [new PdfPageLabel(1, PdfPageLabelStyle.Decimal, null, null),
                          new PdfPageLabel(3, PdfPageLabelStyle.LowerRoman, null, null)],
            },
            CancellationToken.None);
        Assert.Equal(4, result.PageCount);
        await using QpdfDocument output = await Reopen(host, "outputs/labels.pdf");
        (List<long> keys, List<PdfDictionary> dicts) = ReadNums(ReadLabels(output));
        Assert.Equal([0L, 2L], keys);
        Assert.Equal("D", LabelStyle(dicts[0]));
        Assert.Equal("r", LabelStyle(dicts[1]));
    }

    [Fact]
    public async Task PrefixAndStartRoundTrip()
    {
        byte[] bytes = BuildDoc(2);
        InMemoryQpdfHost host = HostWith("inputs/labels.pdf", bytes);
        await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/labels.pdf"), PdfPageSelectionOptions.FromPages([1, 2]))],
            QpdfPath.From("outputs/labels.pdf"),
            new PdfPageMergeOptions
            {
                Labels = [new PdfPageLabel(1, PdfPageLabelStyle.Decimal, 5, "A-"),
                          new PdfPageLabel(2, null, null, "x")],
            },
            CancellationToken.None);
        await using QpdfDocument output = await Reopen(host, "outputs/labels.pdf");
        (List<long> keys, List<PdfDictionary> dicts) = ReadNums(ReadLabels(output));
        Assert.Equal([0L, 1L], keys);
        Assert.True(dicts[0].TryGet(PdfName.FromAscii("St"), out QpdfObject startRaw));
        Assert.Equal(5L, Assert.IsType<PdfInteger>(startRaw).Value);
        Assert.True(dicts[0].TryGet(PdfName.FromAscii("P"), out QpdfObject prefixRaw));
        Assert.Equal([0x41, 0x2D], Assert.IsType<PdfByteString>(prefixRaw).Value);
        Assert.Equal("none", LabelStyle(dicts[1]));
        Assert.True(dicts[1].TryGet(PdfName.FromAscii("P"), out QpdfObject prefix2));
        Assert.Equal([0x78], Assert.IsType<PdfByteString>(prefix2).Value);
    }

    [Fact]
    public async Task LabelOrderRulesFailCleanly()
    {
        byte[] bytes = BuildDoc(4);
        InMemoryQpdfHost host = HostWith("inputs/labels.pdf", bytes);
        PdfPageSource source = new(QpdfPath.From("inputs/labels.pdf"), PdfPageSelectionOptions.FromPages([1, 2, 3, 4]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPagesAsync(
            host, [source], QpdfPath.From("outputs/x.pdf"),
            new PdfPageMergeOptions { Labels = [new PdfPageLabel(2, PdfPageLabelStyle.Decimal, null, null)] },
            CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPagesAsync(
            host, [source], QpdfPath.From("outputs/x.pdf"),
            new PdfPageMergeOptions { Labels = [new PdfPageLabel(1, PdfPageLabelStyle.Decimal, null, null),
                                                new PdfPageLabel(1, PdfPageLabelStyle.UpperAlpha, null, null)] },
            CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPagesAsync(
            host, [source], QpdfPath.From("outputs/x.pdf"),
            new PdfPageMergeOptions { Labels = [new PdfPageLabel(9, PdfPageLabelStyle.Decimal, null, null)] },
            CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void LabelConstructionValidates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageLabel(0, PdfPageLabelStyle.Decimal, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageLabel(1, PdfPageLabelStyle.Decimal, 0, null));
    }

    [Fact]
    public async Task ArgvSetPageLabelsMatchesTyped()
    {
        byte[] bytes = BuildDoc(4);
        InMemoryQpdfHost host = HostWith("inputs/labels.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/labels.pdf", "--pages", ".", "1", "--set-page-labels", "1:r", "--", "outputs/argv.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument labeled = await Reopen(host, "outputs/argv.pdf");
        (List<long> keys, List<PdfDictionary> dicts) = ReadNums(ReadLabels(labeled));
        Assert.Equal([0L], keys);
        Assert.Equal("r", LabelStyle(dicts[0]));
    }

    [Fact]
    public async Task ArgvLabelFormsFailExplicitly()
    {
        byte[] bytes = BuildDoc(2);
        InMemoryQpdfHost host = HostWith("inputs/labels.pdf", bytes);
        QpdfExecutionResult empty = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/labels.pdf", "--pages", ".", "1", "--set-page-labels", "--", "outputs/x.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(empty.Diagnostics).Code);

        QpdfExecutionResult badStyle = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/labels.pdf", "--pages", ".", "1", "--set-page-labels", "1:X", "--", "outputs/x.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(badStyle.Diagnostics).Code);

        QpdfExecutionResult conflict = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/labels.pdf", "--pages", ".", "1", "--set-page-labels", "1:D", "--remove-page-labels", "--", "outputs/x.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(conflict.Diagnostics).Code);
    }

    [Fact]
    public async Task RemovePageLabelsIsAcceptedNoOp()
    {
        byte[] bytes = BuildDoc(2);
        InMemoryQpdfHost host = HostWith("inputs/labels.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/labels.pdf", "--pages", ".", "1", "--remove-page-labels", "--", "outputs/r.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Reopen(host, "outputs/r.pdf");
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.GetObject(1));
        Assert.False(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject _));
    }

    [Fact]
    public async Task JobSetPageLabelsMatchesArgv()
    {
        byte[] bytes = BuildDoc(4);
        InMemoryQpdfHost first = HostWith("inputs/labels.pdf", bytes);
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            first,
            new QpdfCommandInvocation(["inputs/labels.pdf", "--pages", ".", "1", "--set-page-labels", "1:r", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(first.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));

        InMemoryQpdfHost second = HostWith("inputs/labels.pdf", bytes);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            second,
            """{"inputFile": "inputs/labels.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "1"}], "setPageLabels": ["1:r"]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(second.TryGetOutput(QpdfPath.From("outputs/j.pdf"), out byte[] j));
        Assert.Equal(a, j);
    }

    [Fact]
    public async Task JobRemovePageLabelsBehavesLikeArgv()
    {
        byte[] bytes = BuildDoc(2);
        InMemoryQpdfHost host = HostWith("inputs/labels.pdf", bytes);
        QpdfExecutionResult removed = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/labels.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "1"}], "removePageLabels": true}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, removed.ExitCode);

        InMemoryQpdfHost conflicted = HostWith("inputs/labels.pdf", bytes);
        QpdfExecutionResult conflict = await Qpdf.ExecuteJobAsync(
            conflicted,
            """{"inputFile": "inputs/labels.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "1"}], "setPageLabels": ["1:D"], "removePageLabels": true}""",
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, conflict.Status);

        InMemoryQpdfHost mistyped = HostWith("inputs/labels.pdf", bytes);
        QpdfExecutionResult badType = await Qpdf.ExecuteJobAsync(
            mistyped,
            """{"inputFile": "inputs/labels.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "1"}], "removePageLabels": "yes"}""",
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, badType.Status);
    }

    [Theory]
    [InlineData("--set-page-labels", "1:r")]
    [InlineData("--remove-page-labels")]
    public async Task LabelsYieldToExhaustedBudget(params string[] flags)
    {
        byte[] bytes = BuildDoc(2);
        InMemoryQpdfHost host = HostWith("inputs/labels.pdf", bytes);
        List<string> argv = new() { "inputs/labels.pdf", "--pages", ".", "1" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(argv),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
