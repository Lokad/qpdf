using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class ShowObjectTests
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

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static List<string> ObjectLines(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> lines = new();
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Assert.Equal(QpdfDiagnosticCode.ObjectListing, diagnostic.Code);
            lines.Add(diagnostic.Message);
        }

        return lines;
    }

    [Fact]
    public async Task ReportsDictionaryDefinition()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=3"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Equal(
            ["<< /Contents 4 0 R /MediaBox [ 0 0 612 792 ] /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> >> /Type /Page >>"],
            lines);
    }

    [Fact]
    public async Task ReportsStreamDictionary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Equal(["Object is stream.  Dictionary:", "<< /Length 37 >>"], lines);
    }

    [Fact]
    public async Task ReportsTrailer()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=trailer"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Equal(["<< /Root 1 0 R /Size 6 >>"], lines);
    }

    [Fact]
    public async Task ReportsNullForMissingObject()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=99"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Equal(["null"], lines);
    }

    [Fact]
    public async Task ReportsCompressedMember()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "objstm-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=1"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Single(lines);
        Assert.Contains("/Type /Catalog", lines[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--show-object", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=nope", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=0", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=1,2,3", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--show-object=4")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--show-npages")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--show-pages")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--check")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--", "outputs/a.pdf")]
    public async Task RejectsMalformedAndCombinedForms(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task AbsentGenerationShowsNull()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=3,1"]), CancellationToken.None);
        // The reference resolves object-generation pairs exactly: a pair that was
        // never defined displays null with a success exit instead of failing.
        Assert.Equal(["null"], ObjectLines(result));
    }

    [Fact]
    public async Task JobShowObjectMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=3"]), CancellationToken.None);
        List<string> argvLines = ObjectLines(argv);
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"3\"}",
            CancellationToken.None);
        Assert.Equal(argvLines, ObjectLines(job));
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"showObject\": \"3\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"showObject\": \"3\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"nope\"}")]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"showObject\":\"3\",\"showObject\":[]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"3\", \"showNpages\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"3\", \"showPages\": true}")]
    public async Task JobShowObjectRejectsCombinations(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("o.pdf"), out byte[] _));
    }

    [Fact]
    public async Task EncryptedInputFailsWithCryptoCode()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/e.pdf", "encrypted-decl.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/e.pdf", "--show-object=1"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, diagnostic.Code);
    }

    [Fact]
    public async Task FailsOnExhaustedInputBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=3"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
    }

    private static byte[] BuildFlatePage()
    {
        byte[] raw = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Hello) Tj ET");
        byte[] encoded;
        using (MemoryStream encodedStream = new())
        {
            using (System.IO.Compression.ZLibStream compressor = new(encodedStream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                compressor.Write(raw, 0, raw.Length);
            }

            encoded = encodedStream.ToArray();
        }

        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + encoded.Length.ToString() + " /Filter /FlateDecode >>",
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
            byte[] streamData = number == 4 ? encoded : Array.Empty<byte>();
            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            if (number == 4)
            {
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
                continue;
            }

            byte[] close = Encoding.ASCII.GetBytes("endobj\n");
            offsets.Add(position);
            position += open.Length + close.Length;
            parts.Add(open);
            parts.Add(close);
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
        parts.Add(Encoding.ASCII.GetBytes(xref.ToString()));
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

    [Fact]
    public async Task PublishesRawStreamBytes()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4", "--raw-stream-data", "--", "outputs/r.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/r.bin"), out byte[] published));
        Assert.Equal(Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Hello) Tj ET"), published);
    }

    [Fact]
    public async Task PublishesFilteredStreamBytes()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildFlatePage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4", "--filtered-stream-data", "--", "outputs/f.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/f.bin"), out byte[] published));
        Assert.Equal(Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Hello) Tj ET"), published);
    }

    [Fact]
    public async Task FilteredBeatsRaw()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildFlatePage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4", "--raw-stream-data", "--filtered-stream-data", "--", "outputs/f.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/f.bin"), out byte[] published));
        Assert.Equal(Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Hello) Tj ET"), published);
    }

    [Fact]
    public async Task IgnoresStreamFlagsForTrailer()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=trailer", "--raw-stream-data", "--filtered-stream-data"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Contains("<< /Root 1 0 R /Size 6 >>", lines);
    }

    [Fact]
    public async Task IgnoresStreamFlagsForMissingObject()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=99", "--filtered-stream-data"]), CancellationToken.None);
        Assert.Equal(["null"], ObjectLines(result));
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--show-object=4", "--raw-stream-data")]
    [InlineData("inputs/a.pdf", "--show-object=trailer", "--raw-stream-data", "--", "outputs/r.bin")]
    [InlineData("inputs/a.pdf", "--show-object=4", "--raw-stream-data", "--", "outputs/r.bin", "--", "outputs/s.bin")]
    public async Task StreamDataNeedsExactlyOneOutput(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/r.bin"), out byte[] _));
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--show-object=4", "--raw-stream-data=x")]
    [InlineData("inputs/a.pdf", "--show-object=4", "--filtered-stream-data=x")]
    [InlineData("inputs/a.pdf", "--show-object=4", "--raw-stream-data", "--raw-stream-data", "--", "outputs/r.bin")]
    [InlineData("inputs/a.pdf", "--show-object=4", "--filtered-stream-data", "--filtered-stream-data", "--", "outputs/r.bin")]
    [InlineData("inputs/a.pdf", "--show-object=4", "--normalize-content=y", "--filtered-stream-data", "--", "outputs/f.bin")]
    public async Task RejectsMalformedStreamForms(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/r.bin"), out byte[] _));
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/f.bin"), out byte[] _));
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--raw-stream-data")]
    [InlineData("inputs/a.pdf", "--filtered-stream-data")]
    [InlineData("inputs/a.pdf", "--check", "--raw-stream-data")]
    [InlineData("inputs/a.pdf", "--show-npages", "--filtered-stream-data")]
    public async Task StreamDataRequiresShowObject(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task FailsOnUndecodableFiltered()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "bogus-filter.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4", "--filtered-stream-data", "--", "outputs/f.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/f.bin"), out byte[] _));
    }

    [Fact]
    public async Task JobStreamDataMatchesArgv()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", BuildFlatePage());
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4", "--filtered-stream-data", "--", "outputs/f.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/f.bin"), out byte[] a));
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", BuildFlatePage());
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(jobHost, "{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"4\", \"filteredStreamData\": true, \"outputFile\": \"outputs/f.bin\"}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/f.bin"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"4\", \"rawStreamData\": \"yes\"}")]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"showObject\":\"4\",\"rawStreamData\":true,\"rawStreamData\":[]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"rawStreamData\": true}")]
    public async Task JobStreamDataRejectsCombinations(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task StreamDataYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4", "--raw-stream-data", "--", "outputs/r.bin"]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/r.bin"), out byte[] _));
    }
    [Fact]
    public async Task SameGenRevisionShowsLatest()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildSameGenUpdate());
        QpdfExecutionResult bare = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4"]), CancellationToken.None);
        Assert.Equal(
            ["Object is stream.  Dictionary:", "<< /Length 13 >>"],
            ObjectLines(bare));
        QpdfExecutionResult explicitGen = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4,0"]), CancellationToken.None);
        Assert.Equal(
            ["Object is stream.  Dictionary:", "<< /Length 13 >>"],
            ObjectLines(explicitGen));
        QpdfExecutionResult raw = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4", "--raw-stream-data", "--", "outputs/r.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, raw.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/r.bin"), out byte[] published));
        Assert.Equal(Encoding.ASCII.GetBytes("BT (V2) Tj ET"), published);
    }
    [Fact]
    public async Task FreeReuseResolvesPairs()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildFreeReuse());
        QpdfExecutionResult bare = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4"]), CancellationToken.None);
        Assert.Equal(["null"], ObjectLines(bare));
        QpdfExecutionResult freed = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4,0"]), CancellationToken.None);
        Assert.Equal(["null"], ObjectLines(freed));
        QpdfExecutionResult live = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4,1"]), CancellationToken.None);
        Assert.Equal(
            ["Object is stream.  Dictionary:", "<< /Length 13 >>"],
            ObjectLines(live));
        QpdfExecutionResult raw = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4,1", "--raw-stream-data", "--", "outputs/r.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, raw.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/r.bin"), out byte[] published));
        Assert.Equal(Encoding.ASCII.GetBytes("BT (V2) Tj ET"), published);
    }
    private static byte[] BuildSameGenUpdate()
    {
        byte[] first = Encoding.ASCII.GetBytes("BT (V1) Tj ET");
        byte[] second = Encoding.ASCII.GetBytes("BT (V2) Tj ET");
        (byte[] Base, long Xref) baseDocument = AssembleClassic(first);
        return AppendRevision(baseDocument.Base, baseDocument.Xref, 4, 0, "<< /Length " + second.Length.ToString() + " >>", second).Bytes;
    }

    private static byte[] BuildFreeReuse()
    {
        byte[] first = Encoding.ASCII.GetBytes("BT (V1) Tj ET");
        byte[] second = Encoding.ASCII.GetBytes("BT (V2) Tj ET");
        (byte[] Base, long Xref) baseDocument = AssembleClassic(first);
        (byte[] Bytes, long Xref) freed = AppendRevision(baseDocument.Base, baseDocument.Xref, 4, 1, null, null);
        return AppendRevision(freed.Bytes, freed.Xref, 4, 1, "<< /Length " + second.Length.ToString() + " >>", second).Bytes;
    }
    private static (byte[] Base, long Xref) AssembleClassic(byte[] contents)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>",
            "<< /Length " + contents.Length.ToString() + " >>",
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
            offsets.Add(position);
            if (number == 4)
            {
                byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
                byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
                position += open.Length + streamOpen.Length + contents.Length + streamClose.Length;
                byte[] payload = new byte[open.Length + streamOpen.Length + contents.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, payload, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(contents, 0, payload, at, contents.Length);
                at += contents.Length;
                Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
                parts.Add(payload);
                continue;
            }

            byte[] close = Encoding.ASCII.GetBytes("endobj\n");
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
        long xrefPosition = position;
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        parts.Add(Encoding.ASCII.GetBytes(xref.ToString()));
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

        return (output, xrefPosition);
    }

    private static (byte[] Bytes, long Xref) AppendRevision(byte[] prior, long prevXref, int number, int generation, string? dict, byte[]? streamData)
    {
        List<byte> output = new(prior);
        string entry;
        if (dict is null)
        {
            entry = "0000000000 " + generation.ToString("D5") + " f ";
        }
        else
        {
            long offset = output.Count;
            StringBuilder body = new();
            body.Append(number.ToString() + " " + generation.ToString() + " obj\n" + dict + "\n");
            if (streamData is not null)
            {
                body.Append("stream\n");
            }

            output.AddRange(Encoding.ASCII.GetBytes(body.ToString()));
            if (streamData is not null)
            {
                output.AddRange(streamData);
                output.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
            }
            else
            {
                output.AddRange(Encoding.ASCII.GetBytes("endobj\n"));
            }

            entry = offset.ToString("D10") + " " + generation.ToString("D5") + " n ";
        }

        long xrefPosition = output.Count;
        StringBuilder xref = new();
        xref.Append("xref\n" + number.ToString() + " 1\n" + entry + "\n");
        xref.Append("trailer\n<< /Size 5 /Root 1 0 R /Prev " + prevXref.ToString() + " >>\n");
        xref.Append("startxref\n" + xrefPosition.ToString() + "\n%%EOF\n");
        output.AddRange(Encoding.ASCII.GetBytes(xref.ToString()));
        return (output.ToArray(), xrefPosition);
    }
}
