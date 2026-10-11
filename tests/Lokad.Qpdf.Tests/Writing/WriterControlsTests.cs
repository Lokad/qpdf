using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class WriterControlsTests
{
    private sealed class SingleSourceHost(InMemoryQpdfHost inner) : IQpdfHost
    {
        internal int Opens { get; private set; }
        private int _activeInputs;

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            Assert.Equal(1, ++Opens);
            IQpdfInput input = await inner.OpenInputAsync(path, cancellationToken);
            _activeInputs++;
            return new Input(this, input);
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            Assert.Equal(0, _activeInputs);
            return inner.CreateOutputAsync(path, cancellationToken);
        }

        private sealed class Input(SingleSourceHost host, IQpdfInput inner) : IQpdfInput
        {
            private bool _closed;
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
                => inner.ReadAsync(offset, count, cancellationToken);
            public async ValueTask DisposeAsync()
            {
                if (_closed) { return; }
                _closed = true;
                try { await inner.DisposeAsync(); }
                finally { host._activeInputs--; }
            }
        }
    }

    [Theory]
    [InlineData("argv", "rewrite")]
    [InlineData("job", "rewrite")]
    [InlineData("argv", "replace")]
    [InlineData("job", "linearize")]
    [InlineData("argv", "split")]
    [InlineData("job", "pass1")]
    public async Task WholeFileUsesOneStableSourceAndClosesItBeforePublication(string route, string operation)
    {
        InMemoryQpdfHost inner = await HostWithFixture("input.pdf", "minimal-2page.pdf");
        SingleSourceHost host = new(inner);
        QpdfExecutionResult result;
        if (route == "argv")
        {
            List<string> args = ["input.pdf", "--static-id"];
            if (operation == "replace") { args.AddRange(["--replace-input", "--pages", ".", "1-2", "--"]); }
            else
            {
                if (operation == "split") { args.Add("--split-pages=1"); }
                args.Add("output.pdf");
            }
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(args), CancellationToken.None);
        }
        else
        {
            string extra = operation == "linearize" ? ",\"linearize\":\"\""
                : operation == "pass1" ? ",\"linearize\":\"\",\"linearizePass1\":\"pass1.pdf\"" : string.Empty;
            result = await Qpdf.ExecuteJobAsync(host,
                "{\"inputFile\":\"input.pdf\",\"outputFile\":\"output.pdf\",\"staticId\":\"\"" + extra + "}",
                CancellationToken.None);
        }
        Assert.True(result.Status == QpdfExecutionStatus.Completed,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(1, host.Opens);
        Assert.Equal(0L, inner.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
        string[] paths = operation == "split" ? ["output-1.pdf", "output-2.pdf"]
            : operation == "replace" ? ["input.pdf"] : ["output.pdf"];
        List<string> actual = [];
        foreach (string path in paths)
        {
            Assert.True(inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
            using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(bytes);
            foreach (UglyToad.PdfPig.Content.Page page in document.GetPages()) { actual.Add(page.Text); }
        }
        using UglyToad.PdfPig.PdfDocument original = UglyToad.PdfPig.PdfDocument.Open(
            await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-2page.pdf")));
        Assert.Equal(original.GetPages().Select(page => page.Text), actual);
        if (operation == "pass1") { Assert.True(inner.TryGetOutput(QpdfPath.From("pass1.pdf"), out byte[] _)); }
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

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildOnePage(string headerVersion, byte[] streamData, string catalogExtra, string trailerExtra)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + streamData.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
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
            if (number == 4)
            {
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

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R" + trailerExtra + " >>\n");
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

    private static string HeaderOf(byte[] pdf)
    {
        return Encoding.ASCII.GetString(pdf, 0, Math.Min(8, pdf.Length));
    }

    private static (string Id1, string Id2) IdsOf(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        string marker = "/ID [<";
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "Output trailer must carry /ID.");
        int firstStart = at + marker.Length;
        int firstEnd = text.IndexOf('>', firstStart);
        Assert.True(firstEnd > firstStart, "First ID must close.");
        string first = text.Substring(firstStart, firstEnd - firstStart);
        Assert.True(firstEnd + 1 < text.Length && text[firstEnd + 1] == '<', "Second ID must follow the first.");
        int secondStart = firstEnd + 2;
        int secondEnd = text.IndexOf('>', secondStart);
        Assert.True(secondEnd > secondStart, "Second ID must close.");
        string second = text.Substring(secondStart, secondEnd - secondStart);
        return (first, second);
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task MinVersionRaisesLowerInput()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--min-version=1.5", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.StartsWith("%PDF-1.5", HeaderOf(pdf));
    }

    [Fact]
    public async Task MinVersionKeepsHigherInput()
    {
        byte[] bytes = BuildOnePage("1.7", Encoding.ASCII.GetBytes("hello"), string.Empty, string.Empty);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--min-version=1.5", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.StartsWith("%PDF-1.7", HeaderOf(pdf));
    }

    [Fact]
    public async Task ForceVersionOverridesBothDirections()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--force-version=1.7", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.StartsWith("%PDF-1.7", HeaderOf(pdf));
    }

    [Fact]
    public async Task ForceVersionWinsOverMinVersion()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--min-version=1.7", "--force-version=1.5", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.StartsWith("%PDF-1.5", HeaderOf(pdf));
    }

    [Fact]
    public async Task ForceVersionWithExtensionAddsCatalogExtensions()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--force-version=1.7.8", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.StartsWith("%PDF-1.7", HeaderOf(pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.True(text.Contains("/Extensions", StringComparison.Ordinal));
        Assert.True(text.Contains("/ADBE", StringComparison.Ordinal));
        Assert.True(text.Contains("/ExtensionLevel 8", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--min-version=1.8")]
    [InlineData("--min-version=2.1")]
    [InlineData("--min-version=banana")]
    [InlineData("--min-version=")]
    [InlineData("--force-version=0.9")]
    [InlineData("--force-version=1.7.x")]
    public async Task InvalidVersionsFail(params string[] flag)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flag);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task StaticIdEmitsTestConstant()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--static-id", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        (string id1, string id2) = IdsOf(pdf);
        Assert.Equal("31415926535897932384626433832795", id1);
        Assert.Equal("31415926535897932384626433832795", id2);
    }

    [Fact]
    public async Task StaticIdPreservesOriginalFirstWord()
    {
        byte[] bytes = BuildOnePage(
            "1.4",
            Encoding.ASCII.GetBytes("hello"),
            string.Empty,
            " /ID [<00112233445566778899AABBCCDDEEFF><FFEEDDCCBBAA99887766554433221100>]");
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--static-id", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        (string id1, string id2) = IdsOf(pdf);
        Assert.Equal("00112233445566778899AABBCCDDEEFF", id1);
        Assert.Equal("31415926535897932384626433832795", id2);
    }

    [Fact]
    public async Task DefaultIdsAreDeterministicAcrossRuns()
    {
        InMemoryQpdfHost firstHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult first = await Qpdf.ExecuteAsync(
            firstHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, first.ExitCode);
        Assert.True(firstHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));

        InMemoryQpdfHost secondHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult second = await Qpdf.ExecuteAsync(
            secondHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, second.ExitCode);
        Assert.True(secondHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
        (string id1a, string id2a) = IdsOf(a);
        (string id1b, string id2b) = IdsOf(b);
        Assert.Equal(id1a, id1b);
        Assert.Equal(id2a, id2b);
    }

    [Fact]
    public async Task DeterministicFlagMatchesDefaultForSameContent()
    {
        InMemoryQpdfHost plainHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult plain = await Qpdf.ExecuteAsync(
            plainHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, plain.ExitCode);
        Assert.True(plainHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));

        InMemoryQpdfHost flagHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult flagged = await Qpdf.ExecuteAsync(
            flagHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--deterministic-id", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, flagged.ExitCode);
        Assert.True(flagHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    private static async Task AssertEncodedBoundary(byte[] pdf, byte[] expected, bool newline)
    {
        InMemoryQpdfHost host = HostWith("input.pdf", pdf);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input.pdf"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(contents).ObjectNumber));
        Assert.Equal(expected, StreamObservation.Decode(stream));
        Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(filter).DisplayText);
        byte[] boundary = stream.Data.Concat(newline ? new byte[] { 10 } : Array.Empty<byte>()).Concat("endstream"u8.ToArray()).ToArray();
        Assert.True(pdf.AsSpan().IndexOf(boundary) >= 0);
        byte[] extra = stream.Data.Concat(newline ? new byte[] { 10, 10 } : new byte[] { 10 }).Concat("endstream"u8.ToArray()).ToArray();
        Assert.True(pdf.AsSpan().IndexOf(extra) < 0);
    }

    [Fact]
    public async Task DefaultStreamHasNoExtraNewline()
    {
        byte[] bytes = BuildOnePage("1.4", Encoding.ASCII.GetBytes("hello"), string.Empty, string.Empty);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        await AssertEncodedBoundary(pdf, "hello"u8.ToArray(), false);
    }

    [Fact]
    public async Task NewlineFlagAddsExtraNewline()
    {
        byte[] bytes = BuildOnePage("1.4", Encoding.ASCII.GetBytes("hello"), string.Empty, string.Empty);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--newline-before-endstream", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        await AssertEncodedBoundary(pdf, "hello"u8.ToArray(), true);
    }

    [Fact]
    public async Task NewlineFlagDoublesTrailingNewline()
    {
        byte[] bytes = BuildOnePage("1.4", Encoding.ASCII.GetBytes("hello\n"), string.Empty, string.Empty);
        InMemoryQpdfHost plainHost = HostWith("inputs/a.pdf", bytes);
        QpdfExecutionResult plain = await Qpdf.ExecuteAsync(
            plainHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, plain.ExitCode);
        Assert.True(plainHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        await AssertEncodedBoundary(a, "hello\n"u8.ToArray(), false);

        InMemoryQpdfHost flagHost = HostWith("inputs/b.pdf", bytes);
        QpdfExecutionResult flagged = await Qpdf.ExecuteAsync(
            flagHost,
            new QpdfCommandInvocation(["inputs/b.pdf", "--newline-before-endstream", "--pages", ".", "1", "--", "outputs/b.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, flagged.ExitCode);
        Assert.True(flagHost.TryGetOutput(QpdfPath.From("outputs/b.pdf"), out byte[] b));
        await AssertEncodedBoundary(b, "hello\n"u8.ToArray(), true);
    }

    [Fact]
    public async Task JobJsonWriterControlsParityHolds()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--min-version=1.6", "--static-id", "--newline-before-endstream", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));

        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "minVersion": "1.6", "staticId": "", "newlineBeforeEndstream": ""}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
        Assert.StartsWith("%PDF-1.6", HeaderOf(b));
    }

    [Fact]
    public async Task SpaceSeparatedVersionFormsSucceed()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--min-version", "1.6", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.StartsWith("%PDF-1.6", HeaderOf(pdf));
    }

    [Theory]
    [InlineData("--min-version=1.5", "--min-version=1.6")]
    [InlineData("--force-version=1.5", "--force-version=1.6")]
    [InlineData("--static-id", "--static-id")]
    [InlineData("--deterministic-id", "--deterministic-id")]
    [InlineData("--newline-before-endstream", "--newline-before-endstream")]
    public async Task DuplicateWriterFlagsFail(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Theory]
    [InlineData("--deterministic-id=1")]
    [InlineData("--static-id=yes")]
    [InlineData("--newline-before-endstream=n")]
    [InlineData("--min-version")]
    [InlineData("--force-version")]
    public async Task MalformedWriterFlagsFail(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobJsonInvalidVersionFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "minVersion": "9.9"}""",
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Theory]
    [InlineData("--min-version=1.7")]
    [InlineData("--force-version=1.1")]
    [InlineData("--deterministic-id")]
    [InlineData("--static-id")]
    [InlineData("--newline-before-endstream")]
    public async Task WriterControlsYieldToExhaustedBudget(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(argv),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
