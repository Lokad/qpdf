using Lokad.Qpdf.Image;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class CheckTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<InMemoryQpdfHost> HostWithFixture(string logical, string fixture)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
        InMemoryQpdfHost host = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        host.AddInput(QpdfPath.From(logical), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    internal static byte[] Assemble(List<(int Number, string Dict, byte[]? StreamData)> objects, int rootNumber)
        => Assemble(objects, rootNumber, null);

    internal static byte[] Assemble(List<(int Number, string Dict, byte[]? StreamData)> objects, int rootNumber,
        IReadOnlyDictionary<int, int>? generations)
    {
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        Dictionary<int, long> offsets = new();
        long position = header.Length;
        foreach ((int number, string dict, byte[]? streamData) in objects)
        {
            int generation = generations is not null && generations.TryGetValue(number, out int active) ? active : 0;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " " + generation.ToString() + " obj\n" + dict + "\n");
            if (streamData is null)
            {
                byte[] close = Encoding.ASCII.GetBytes("endobj\n");
                offsets[number] = position;
                position += open.Length + close.Length;
                byte[] plain = new byte[open.Length + close.Length];
                Buffer.BlockCopy(open, 0, plain, 0, open.Length);
                Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
                parts.Add(plain);
                continue;
            }

            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            offsets[number] = position;
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

        int size = 0;
        foreach ((int number, string _, byte[]? _) in objects)
        {
            if (number > size)
            {
                size = number;
            }
        }

        size += 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        for (int number = 1; number < size; number++)
        {
            if (!offsets.TryGetValue(number, out long offset))
            {
                throw new Xunit.Sdk.XunitException("Builder is missing object " + number.ToString() + ".");
            }

            int generation = generations is not null && generations.TryGetValue(number, out int active) ? active : 0;
            xref.Append(offset.ToString("D10") + " " + generation.ToString("D5") + " n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root " + rootNumber.ToString() + " 0 R >>\n");
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

    private static byte[] HelloContents()
    {
        return Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Hello) Tj ET");
    }

    internal static List<(int, string, byte[]?)> SinglePage(string contentsDict, byte[] contentsData)
    {
        return new List<(int, string, byte[]?)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>", null),
            (4, contentsDict, contentsData),
            (5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
        };
    }

    private static byte[] CmykJpeg()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cmyk-8x8.jpg"));

    private static QpdfCheckOutcome SingleOutcome(QpdfCheckResult result, string name)
    {
        return Assert.Single(result.Checks, check => check.Name == name);
    }

    [Fact]
    public async Task ValidDocumentReportsExpectedOutcomes()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        Assert.Equal("1.4", result.Version.ToString());
        Assert.Equal(9, result.Checks.Count);
        Assert.Equal(QpdfCheckStatus.Passed, SingleOutcome(result, "header").Status);
        Assert.Equal(QpdfCheckStatus.Passed, SingleOutcome(result, "cross-reference").Status);
        Assert.Equal(QpdfCheckStatus.Passed, SingleOutcome(result, "trailer").Status);
        Assert.Equal(QpdfCheckStatus.Passed, SingleOutcome(result, "page-tree").Status);
        Assert.Equal(QpdfCheckStatus.Passed, SingleOutcome(result, "object-graph").Status);
        QpdfCheckOutcome streams = SingleOutcome(result, "streams");
        Assert.Equal(QpdfCheckStatus.Passed, streams.Status);
        Assert.Contains("unfiltered", streams.Detail, StringComparison.Ordinal);
        QpdfCheckOutcome pageContent = SingleOutcome(result, "page-content");
        Assert.Equal(QpdfCheckStatus.Passed, pageContent.Status);
        Assert.Contains("tokenized", pageContent.Detail, StringComparison.Ordinal);
        Assert.Equal(QpdfCheckStatus.Passed, SingleOutcome(result, "linearization").Status);
        QpdfCheckOutcome forms = SingleOutcome(result, "forms");
        Assert.Equal(QpdfCheckStatus.Passed, forms.Status);
        Assert.Contains("No interactive form", forms.Detail, StringComparison.Ordinal);
        Assert.True(result.Passed);
        Assert.False(result.HasSkipped);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckSkipped);
    }

    [Fact]
    public async Task NoneLevelSkipsStreams()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfCheckResult result = await Qpdf.CheckAsync(
            host, QpdfPath.From("inputs/a.pdf"),
            new QpdfCheckOptions { DecodeLevel = QpdfStreamDecodeLevel.None },
            QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Skipped, SingleOutcome(result, "streams").Status);
        Assert.True(result.Passed);
        Assert.True(result.HasSkipped);
    }

    private sealed class InaccessibleHost : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget? MemoryBudget => throw new InvalidOperationException("Unexpected memory host access.");
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("Unexpected input access.");
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("Unexpected output access.");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(999)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task InvalidDecodeLevelsFailBeforeHostAccess(int level)
    {
        ArgumentOutOfRangeException failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Qpdf.CheckAsync(new InaccessibleHost(), QpdfPath.From("unopened.pdf"),
                new QpdfCheckOptions { DecodeLevel = (QpdfStreamDecodeLevel)level }, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal("options", failure.ParamName);
        Assert.Equal((QpdfStreamDecodeLevel)level, failure.ActualValue);
    }

    [Theory]
    [InlineData(QpdfStreamDecodeLevel.None)]
    [InlineData(QpdfStreamDecodeLevel.Generalized)]
    [InlineData(QpdfStreamDecodeLevel.Specialized)]
    [InlineData(QpdfStreamDecodeLevel.All)]
    public async Task SupportedDecodeLevelsKeepTheirCorruptStreamCoverage(QpdfStreamDecodeLevel level)
    {
        byte[] bytes = Assemble(SinglePage("<< /Filter /FlateDecode /Length 14 >>", Encoding.ASCII.GetBytes("not-deflate!!!")), 1);
        using InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/bad.pdf"),
            new QpdfCheckOptions { DecodeLevel = level }, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, SingleOutcome(result, "object-graph").Status);
        QpdfCheckOutcome streams = SingleOutcome(result, "streams");
        Assert.Equal(0L, host.OpenLeaseCount);
        if (level == QpdfStreamDecodeLevel.None)
        {
            Assert.Equal(QpdfCheckStatus.Skipped, streams.Status);
            Assert.True(result.Passed);
            Assert.True(result.HasSkipped);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed);
            return;
        }
        Assert.Equal(QpdfCheckStatus.Failed, streams.Status);
        Assert.Contains("stream 4", streams.Detail, StringComparison.Ordinal);
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed && diagnostic.Severity == QpdfDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("789C000300FCFF71205101E700E3")]
    [InlineData("789C0300000001")]
    public async Task IncompleteFlateFramesFailTypedAndCommandChecks(string hex)
    {
        byte[] frame = Convert.FromHexString(hex);
        using InMemoryQpdfHost host = HostWith("inputs/incomplete.pdf",
            Assemble(SinglePage($"<< /Filter /FlateDecode /Length {frame.Length} >>", frame), 1));
        QpdfPath input = QpdfPath.From("inputs/incomplete.pdf");
        QpdfCheckResult check = await Qpdf.CheckAsync(host, input, CancellationToken.None);
        Assert.False(check.Passed);
        Assert.Equal(QpdfCheckStatus.Failed, SingleOutcome(check, "streams").Status);
        Assert.Contains(check.Diagnostics, item => item.Code == QpdfDiagnosticCode.CheckFailed);
        QpdfExecutionResult execution = await Qpdf.ExecuteAsync(host,
            new([input.Value, "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, execution.Status);
        Assert.Equal(QpdfExitCode.Error, execution.ExitCode);
        Assert.Contains(execution.Diagnostics, item => item.Code == QpdfDiagnosticCode.CheckFailed);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task UnavailableFilterSkipsStreamsWithWarning()
    {
        // Four-component JPEGs have no raw-sample path through the approved codec, so DCTDecode
        // stays genuinely unavailable for this input and the check skips instead of failing.
        byte[] jpeg = CmykJpeg();
        List<(int, string, byte[]?)> objects = SinglePage("<< /Length 37 >>", HelloContents());
        objects.Add((6, "<< /Filter /DCTDecode /Length " + jpeg.Length.ToString() + " >>", jpeg));
        byte[] bytes = Assemble(objects, 1);
        InMemoryQpdfHost host = HostWith("inputs/dct.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/dct.pdf"), CancellationToken.None);
        QpdfCheckOutcome streams = SingleOutcome(result, "streams");
        Assert.Equal(QpdfCheckStatus.Skipped, streams.Status);
        Assert.Contains("DCTDecode", streams.Detail, StringComparison.Ordinal);
        Assert.Contains("grayscale/RGB", streams.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, item => item.Message.Contains("milestone", StringComparison.Ordinal));
        Assert.True(result.Passed);
        Assert.True(result.HasSkipped);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckSkipped && diagnostic.Severity == QpdfDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task UnavailableFilterReportsItsNameWithoutARoadmapPromise()
    {
        List<(int, string, byte[]?)> objects = SinglePage("<< /Length 0 >>", []);
        objects.Add((6, "<< /Filter /JPXDecode /Length 1 >>", new byte[] { 0 }));
        using InMemoryQpdfHost host = HostWith("inputs/unavailable.pdf", Assemble(objects, 1));
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/unavailable.pdf"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Skipped, SingleOutcome(result, "streams").Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.CheckSkipped
            && item.Message.Contains("JPXDecode unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, item => item.Message.Contains("milestone", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task TrueCmykSkipsStreamsWithWarning()
    {
        // A genuine four-component JPEG (known DC-only samples): the streams check
        // skips DCTDecode for it exactly like the labeled variant, since no CMYK
        // pixel path exists in the approved codec.
        byte[] jpeg = Lokad.Qpdf.Tests.Filters.CmykJpegBuilder.Build(new[] { 10, -20, 30, -40 }, null);
        List<(int, string, byte[]?)> objects = SinglePage("<< /Length 37 >>", HelloContents());
        objects.Add((6, "<< /Filter /DCTDecode /Length " + jpeg.Length.ToString() + " >>", jpeg));
        byte[] bytes = Assemble(objects, 1);
        InMemoryQpdfHost host = HostWith("inputs/dct.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/dct.pdf"), CancellationToken.None);
        QpdfCheckOutcome streams = SingleOutcome(result, "streams");
        Assert.Equal(QpdfCheckStatus.Skipped, streams.Status);
        Assert.Contains("DCTDecode", streams.Detail, StringComparison.Ordinal);
        Assert.True(result.Passed);
        Assert.True(result.HasSkipped);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckSkipped && diagnostic.Severity == QpdfDiagnosticSeverity.Warning);
    }
    [Fact]
    public async Task ValidDctPassesStreamsCheck()
    {
        byte[] jpeg = Lokad.Qpdf.Tests.Filters.JpegFixtures.Encode(8, 8, 3, 75, (x, y, c) => c == 0 ? (byte)(x * 32) : c == 1 ? (byte)(y * 32) : (byte)128);

        List<(int, string, byte[]?)> objects = SinglePage("<< /Length 37 >>", HelloContents());
        objects.Add((6, "<< /Filter /DCTDecode /Length " + jpeg.Length.ToString() + " >>", jpeg));
        byte[] bytes = Assemble(objects, 1);
        InMemoryQpdfHost host = HostWith("inputs/dct.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/dct.pdf"), CancellationToken.None);
        QpdfCheckOutcome streams = SingleOutcome(result, "streams");
        Assert.Equal(QpdfCheckStatus.Passed, streams.Status);
        Assert.Contains("decoded", streams.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed);
    }

    [Fact]
    public async Task FileWithoutFileSizeReadsAsNotLinearized()
    {
        List<(int, string, byte[]?)> objects = new()
        {
            (1, "<< /Linearized 1 >>", null),
            (2, "<< /Type /Catalog /Pages 3 0 R >>", null),
            (3, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>", null),
            (4, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << >> >>", null),
            (5, "<< /Length 37 >>", HelloContents()),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
        };
        byte[] bytes = Assemble(objects, 2);
        InMemoryQpdfHost host = HostWith("inputs/lin.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/lin.pdf"), CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        QpdfCheckOutcome linearization = SingleOutcome(result, "linearization");
        Assert.Equal(QpdfCheckStatus.Passed, linearization.Status);
        Assert.Contains("not linearized", linearization.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckSkipped);
    }

    [Fact]
    public async Task CheckFailsOnExhaustedInputBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--check"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
    }

    [Fact]
    public async Task ArgvCheckSucceedsWithoutOutput()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ArgvCheckSilentlyUsesTraversalOnCountMismatch()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf"));
        byte[] marker = Encoding.ASCII.GetBytes("/Count 1");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        bytes[at + 7] = (byte)'9';
        InMemoryQpdfHost host = HostWith("inputs/mismatch.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/mismatch.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCountMismatch);
    }

    [Fact]
    public async Task ArgvCheckRejectsCombinedAndTrailingForms()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult combined = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, combined.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(combined.Diagnostics).Code);
        QpdfExecutionResult trailing = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, trailing.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(trailing.Diagnostics).Code);
    }

    [Fact]
    public async Task ArgvCheckFailsOnTruncatedInput()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "truncated-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
    }

    private static List<(int, string, byte[]?)> FormPage(
        string annots,
        string acroFields,
        string fieldDict,
        string widgetInForm,
        string? widgetOrphan)
    {
        List<(int, string, byte[]?)> objects = new()
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields " + acroFields + " >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots " + annots + " >>", null),
            (4, fieldDict, null),
            (5, widgetInForm, null),
        };
        if (widgetOrphan is not null)
        {
            objects.Add((6, widgetOrphan, null));
        }

        return objects;
    }

    [Fact]
    public async Task UnreachableWidgetWarnsOnCheck()
    {
        List<(int, string, byte[]?)> objects = FormPage(
            "[5 0 R 6 0 R]",
            "[4 0 R]",
            "<< /FT /Tx /T (F) /Kids [5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /Parent 4 0 R /T (F) >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /T (G) /FT /Tx >>");
        byte[] bytes = Assemble(objects, 1);
        InMemoryQpdfHost host = HostWith("inputs/unreach.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/unreach.pdf"), CancellationToken.None);
        QpdfCheckOutcome forms = SingleOutcome(result, "forms");
        Assert.Equal(QpdfCheckStatus.Passed, forms.Status);
        Assert.Contains("field nodes traversed", forms.Detail, StringComparison.Ordinal);
        Assert.Contains("1 warnings recorded", forms.Detail, StringComparison.Ordinal);
        QpdfDiagnostic warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.FormStructureWarning);
        Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("not reachable from /AcroForm", warning.Message, StringComparison.Ordinal);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ArgvCheckExitsWarningOnUnreachableWidget()
    {
        List<(int, string, byte[]?)> objects = FormPage(
            "[5 0 R 6 0 R]",
            "[4 0 R]",
            "<< /FT /Tx /T (F) /Kids [5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /Parent 4 0 R /T (F) >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /T (G) /FT /Tx >>");
        byte[] bytes = Assemble(objects, 1);
        InMemoryQpdfHost host = HostWith("inputs/unreach.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/unreach.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
    }

    [Fact]
    public async Task MalformedParentLinkWarnsOnCheck()
    {
        List<(int, string, byte[]?)> objects = FormPage(
            "[5 0 R]",
            "[4 0 R]",
            "<< /FT /Tx /T (F) /Kids [5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /T (F) >>",
            null);
        byte[] bytes = Assemble(objects, 1);
        InMemoryQpdfHost host = HostWith("inputs/malparent.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/malparent.pdf"), CancellationToken.None);
        QpdfCheckOutcome forms = SingleOutcome(result, "forms");
        Assert.Equal(QpdfCheckStatus.Passed, forms.Status);
        Assert.Contains("1 warnings recorded", forms.Detail, StringComparison.Ordinal);
        QpdfDiagnostic warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.FormStructureWarning);
        Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("invalid /Parent", warning.Message, StringComparison.Ordinal);
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/malparent.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, argv.Status);
        Assert.Equal(QpdfExitCode.Warning, argv.ExitCode);
    }

    [Fact]
    public async Task WellFormedParentStaysSilentOnCheck()
    {
        List<(int, string, byte[]?)> objects = FormPage(
            "[5 0 R]",
            "[4 0 R]",
            "<< /FT /Tx /T (F) /Kids [5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /Parent 4 0 R /T (F) >>",
            null);
        byte[] bytes = Assemble(objects, 1);
        InMemoryQpdfHost host = HostWith("inputs/cleanform.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/cleanform.pdf"), CancellationToken.None);
        QpdfCheckOutcome forms = SingleOutcome(result, "forms");
        Assert.Equal(QpdfCheckStatus.Passed, forms.Status);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.FormStructureWarning);
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/cleanform.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, argv.Status);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
    }
    [Fact]
    public async Task DirectFieldNodeWarnsAndStaysIgnoredOnCheck()
    {
        List<(int, string, byte[]?)> objects = FormPage(
            "[5 0 R]",
            "[4 0 R]",
            "<< /FT /Tx /T (F) /Kids [<< /FT /Tx /T (D) >> 5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /Parent 4 0 R /T (F) >>",
            null);
        byte[] bytes = Assemble(objects, 1);
        InMemoryQpdfHost host = HostWith("inputs/directform.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/directform.pdf"), CancellationToken.None);
        QpdfCheckOutcome forms = SingleOutcome(result, "forms");
        Assert.Equal(QpdfCheckStatus.Passed, forms.Status);
        Assert.Contains("1 warnings recorded", forms.Detail, StringComparison.Ordinal);
        QpdfDiagnostic warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.FormStructureWarning);
        Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("direct object as a field or annotation", warning.Message, StringComparison.Ordinal);
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/directform.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, argv.Status);
        Assert.Equal(QpdfExitCode.Warning, argv.ExitCode);
    }
    [Fact]
    public async Task CancelledTokenReportsCanceledOnCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "parent-held-1page.pdf");
        using CancellationTokenSource source = new();
        source.Cancel();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check"]), source.Token);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
    }
    [Fact]
    public async Task WarningModifiersApplyToFormFindings()
    {
        InMemoryQpdfHost zeroHost = await HostWithFixture("inputs/a.pdf", "parent-held-1page.pdf");
        QpdfExecutionResult zero = await Qpdf.ExecuteAsync(
            zeroHost, new QpdfCommandInvocation(["inputs/a.pdf", "--warning-exit-0", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, zero.Status);
        Assert.Equal(QpdfExitCode.Success, zero.ExitCode);
        Assert.Contains(zero.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.FormStructureWarning);
        InMemoryQpdfHost quietHost = await HostWithFixture("inputs/a.pdf", "parent-held-1page.pdf");
        QpdfExecutionResult quiet = await Qpdf.ExecuteAsync(
            quietHost, new QpdfCommandInvocation(["inputs/a.pdf", "--no-warn", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, quiet.Status);
        Assert.Equal(QpdfExitCode.Warning, quiet.ExitCode);
        Assert.DoesNotContain(quiet.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.FormStructureWarning);
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
}
