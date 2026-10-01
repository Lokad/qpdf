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

    private static byte[] Assemble(List<(int Number, string Dict, byte[]? StreamData)> objects, int rootNumber)
    {
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        Dictionary<int, long> offsets = new();
        long position = header.Length;
        foreach ((int number, string dict, byte[]? streamData) in objects)
        {
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + dict + "\n");
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

            xref.Append(offset.ToString("D10") + " 00000 n \n");
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

    private static List<(int, string, byte[]?)> SinglePage(string contentsDict, byte[] contentsData)
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
        Assert.Equal(8, result.Checks.Count);
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

    [Fact]
    public async Task CorruptStreamDecodeFailsTheCheck()
    {
        byte[] bytes = Assemble(SinglePage("<< /Filter /FlateDecode /Length 14 >>", Encoding.ASCII.GetBytes("not-deflate!!!")), 1);
        InMemoryQpdfHost host = HostWith("inputs/bad.pdf", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/bad.pdf"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, SingleOutcome(result, "object-graph").Status);
        QpdfCheckOutcome streams = SingleOutcome(result, "streams");
        Assert.Equal(QpdfCheckStatus.Failed, streams.Status);
        Assert.Contains("stream 4", streams.Detail, StringComparison.Ordinal);
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed && diagnostic.Severity == QpdfDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task UnavailableFilterSkipsStreamsWithWarning()
    {
        byte[] bytes = Assemble(SinglePage("<< /Filter /DCTDecode /Length 4 >>", [0xFF, 0xD8, 0xFF, 0xE0]), 1);
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
    public async Task LinearizedFileSkipsLinearization()
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
        Assert.Equal(QpdfCheckStatus.Skipped, linearization.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckSkipped && diagnostic.Severity == QpdfDiagnosticSeverity.Warning);
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
    public async Task ArgvCheckWarnsOnCountMismatch()
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
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCountMismatch);
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
