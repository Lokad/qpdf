using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class FixQdfTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<InMemoryQpdfHost> HostWithFixture(string logical, string fixture)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
        return HostWith(logical, bytes);
    }

    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<(QpdfExecutionResult Result, byte[] Pdf)> RunAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        return (result, pdf);
    }

    private static async Task<(QpdfExecutionResult Result, byte[]? Pdf)> FixAsync(byte[] qdf)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", qdf);
        QpdfExecutionResult result = await Qpdf.FixQdfAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/fixed.pdf"), CancellationToken.None);
        bool published = host.TryGetOutput(QpdfPath.From("outputs/fixed.pdf"), out byte[] pdf);
        return (result, published ? pdf : null);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (int i = start; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int k = 0; k < needle.Length; k++)
            {
                if (haystack[i + k] != needle[k])
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

    private static byte[] ReplaceFirst(byte[] input, string needle, string replacement)
    {
        byte[] needleBytes = Encoding.ASCII.GetBytes(needle);
        byte[] replacementBytes = Encoding.ASCII.GetBytes(replacement);
        int at = IndexOf(input, needleBytes);
        Assert.NotEqual(-1, at);
        Assert.Equal(-1, IndexOf(input, needleBytes, at + 1));
        byte[] output = new byte[checked(input.Length - needleBytes.Length + replacementBytes.Length)];
        Buffer.BlockCopy(input, 0, output, 0, at);
        Buffer.BlockCopy(replacementBytes, 0, output, at, replacementBytes.Length);
        Buffer.BlockCopy(input, at + needleBytes.Length, output, at + replacementBytes.Length, input.Length - at - needleBytes.Length);
        return output;
    }

    private static async Task<byte[]> ReadContentsAsync(byte[] pdf)
    {
        InMemoryQpdfHost host = HostWith("inputs/r.pdf", pdf);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        for (int number = 3; number <= 9; number++)
        {
            QpdfObject candidate;
            try
            {
                candidate = document.GetObject(number);
            }
            catch (QpdfParseException)
            {
                continue;
            }

            if (candidate is PdfDictionary page
                && page.TryGet(PdfName.FromAscii("Type"), out QpdfObject? type)
                && type is PdfName name
                && name.DisplayText == "Page"
                && page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents)
                && contents is PdfReference reference
                && document.GetObject(reference.ObjectNumber) is PdfStream stream)
            {
                return stream.Data;
            }
        }

        Assert.Fail("Page contents not found in repaired output.");
        return [];
    }

    [Fact]
    public async Task PristineClassicQdfRoundTripsIdentically()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        (QpdfExecutionResult result, byte[]? repaired) = await FixAsync(qdf);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.NotNull(repaired);
        Assert.Equal(qdf, repaired);
    }

    [Fact]
    public async Task LengthenedContentStreamGetsNewLengthAndOffsets()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        byte[] edited = ReplaceFirst(qdf, "(Hello)", "(HelloHello)");
        Assert.NotEqual(qdf.Length, edited.Length);
        (QpdfExecutionResult result, byte[]? repaired) = await FixAsync(edited);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.NotNull(repaired);
        byte[] contents = await ReadContentsAsync(repaired);
        Assert.Equal("BT /F1 24 Tf 100 700 Td (HelloHello) Tj ET", Encoding.ASCII.GetString(contents));
        using PdfDocument reread = PdfDocument.Open(new MemoryStream(repaired));
        Assert.Equal(1, reread.NumberOfPages);
    }

    [Fact]
    public async Task ShortenedContentStreamShrinksDownstreamObjects()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        byte[] edited = ReplaceFirst(qdf, "(Hello)", "(Hi)");
        (QpdfExecutionResult result, byte[]? repaired) = await FixAsync(edited);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.NotNull(repaired);
        Assert.True(repaired.Length < qdf.Length);
        byte[] contents = await ReadContentsAsync(repaired);
        Assert.Equal("BT /F1 24 Tf 100 700 Td (Hi) Tj ET", Encoding.ASCII.GetString(contents));
        string text = Encoding.Latin1.GetString(repaired);
        int lengthAt = text.IndexOf(" 0 obj\n34\nendobj", StringComparison.Ordinal);
        Assert.NotEqual(-1, lengthAt);
    }

    [Fact]
    public async Task StreamedMemberEditRefixesTableAndLengths()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--object-streams=generate", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        byte[] edited = ReplaceFirst(qdf, "/Helvetica", "/Helvetica-Bold");
        (QpdfExecutionResult result, byte[]? repaired) = await FixAsync(edited);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.NotNull(repaired);
        InMemoryQpdfHost reread = HostWith("inputs/r.pdf", repaired);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(reread, QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        bool sawBold = false;
        for (int number = 1; number <= 9; number++)
        {
            QpdfObject candidate;
            try
            {
                candidate = document.GetObject(number);
            }
            catch (QpdfParseException)
            {
                continue;
            }

            if (candidate is PdfDictionary font
                && font.TryGet(PdfName.FromAscii("BaseFont"), out QpdfObject? baseFont)
                && baseFont is PdfName name
                && name.DisplayText == "Helvetica-Bold")
            {
                sawBold = true;
                break;
            }
        }

        Assert.True(sawBold);
        using PdfDocument pig = PdfDocument.Open(new MemoryStream(repaired));
        Assert.Equal(1, pig.NumberOfPages);
    }

    [Fact]
    public async Task RenumberedObjectFailsWithInvalidArguments()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        byte[] edited = ReplaceFirst(qdf, "\n2 0 obj\n", "\n9 0 obj\n");
        (QpdfExecutionResult result, byte[]? repaired) = await FixAsync(edited);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Null(repaired);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
    }

    [Fact]
    public async Task CorruptLengthLineFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        byte[] edited = ReplaceFirst(qdf, "\n37\nendobj", "\nxyz\nendobj");
        (QpdfExecutionResult result, byte[]? repaired) = await FixAsync(edited);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Null(repaired);
    }

    [Fact]
    public async Task CrLfOuterSyntaxFailsExplicitly()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        byte[] crlf = ReplaceBytes(qdf, new byte[] { 10 }, new byte[] { 13, 10 });
        (QpdfExecutionResult result, byte[]? repaired) = await FixAsync(crlf);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Null(repaired);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
    }

    [Fact]
    public async Task TruncatedInputFailsWithoutPublishing()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        byte[] cut = new byte[qdf.Length * 3 / 4];
        Buffer.BlockCopy(qdf, 0, cut, 0, cut.Length);
        (QpdfExecutionResult result, byte[]? repaired) = await FixAsync(cut);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Null(repaired);
    }

    [Fact]
    public async Task EmptyInputFails()
    {
        (QpdfExecutionResult result, byte[]? repaired) = await FixAsync([]);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Null(repaired);
    }

    [Fact]
    public async Task MissingInputFailsAsHostError()
    {
        InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.FixQdfAsync(host, QpdfPath.From("inputs/missing.pdf"), QpdfPath.From("outputs/fixed.pdf"), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.HostOperationFailed);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/fixed.pdf"), out byte[] _));
    }

    [Fact]
    public async Task TinyOutputBudgetFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        InMemoryQpdfHost fixHost = HostWith("inputs/a.pdf", qdf);
        QpdfLimits limits = new QpdfLimits { MaxOutputBytes = 64L };
        QpdfExecutionResult result = await Qpdf.FixQdfAsync(fixHost, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/fixed.pdf"), limits, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
    }

    [Fact]
    public async Task CancelledTokenReportsCanceled()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        InMemoryQpdfHost fixHost = HostWith("inputs/a.pdf", qdf);
        using CancellationTokenSource source = new();
        await source.CancelAsync();
        QpdfExecutionResult result = await Qpdf.FixQdfAsync(fixHost, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/fixed.pdf"), source.Token);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
    }

    [Fact]
    public async Task SameInputOutputPathPublishesRepairedBytesWithoutChangingRegistration()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult emit, byte[] qdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, emit.ExitCode);
        byte[] edited = ReplaceFirst(qdf, "(Hello)", "(HelloHello)");
        InMemoryQpdfHost fixHost = HostWith("inputs/a.pdf", edited);
        QpdfExecutionResult result = await Qpdf.FixQdfAsync(fixHost, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("inputs/a.pdf"),
            QpdfLimits.Default with { MaxOpenHandles = 1L }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(fixHost.TryGetOutput(QpdfPath.From("inputs/a.pdf"), out byte[] repaired));
        byte[] contents = await ReadContentsAsync(repaired);
        Assert.Equal("BT /F1 24 Tf 100 700 Td (HelloHello) Tj ET", Encoding.ASCII.GetString(contents));
        await using (IQpdfInput original = await fixHost.OpenInputAsync(QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            byte[] read = new byte[edited.Length];
            await QpdfInputReader.ReadExactlyAsync(original, 0L, read, CancellationToken.None);
            Assert.Equal(edited, read);
        }
        Assert.Equal(0L, fixHost.OpenLeaseCount);
    }

    private static byte[] ReplaceBytes(byte[] input, byte[] needle, byte[] replacement)
    {
        List<byte> output = new(input.Length);
        int cursor = 0;
        while (cursor < input.Length)
        {
            bool match = cursor + needle.Length <= input.Length;
            for (int k = 0; match && k < needle.Length; k++)
            {
                if (input[cursor + k] != needle[k])
                {
                    match = false;
                }
            }

            if (match)
            {
                output.AddRange(replacement);
                cursor += needle.Length;
            }
            else
            {
                output.Add(input[cursor]);
                cursor++;
            }
        }

        return [.. output];
    }
}
