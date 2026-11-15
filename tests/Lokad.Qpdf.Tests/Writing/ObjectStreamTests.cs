using System.Text;
using Lokad.Qpdf.Hosting;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamTests
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

    private static byte[] BuildOnePage(string headerVersion, byte[] streamData)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + streamData.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        return PdfTestDocument.AssembleBodies(bodies, number => number == 4 ? streamData : null,
            headerVersion, "");
    }

    private static byte[] BuildManyResourcePdf(int extraCount)
    {
        StringBuilder resources = new();
        resources.Append("<< /Extra << ");
        for (int i = 0; i < extraCount; i++)
        {
            if (i > 0)
            {
                resources.Append(' ');
            }

            resources.Append("/X" + i.ToString() + " " + (6 + i).ToString() + " 0 R");
        }

        resources.Append(" >> >>");
        byte[] streamData = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Many) Tj ET");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources " + resources.ToString() + " >>",
            "<< /Length " + streamData.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (int i = 0; i < extraCount; i++)
        {
            bodies.Add("<< /V " + i.ToString() + " >>");
        }
        return PdfTestDocument.AssembleBodies(bodies, number => number == 4 ? streamData : null,
            "1.4", "");
    }

    private static string HeaderOf(byte[] pdf)
    {
        return Encoding.ASCII.GetString(pdf, 0, Math.Min(8, pdf.Length));
    }

    private static int CountOccurrences(string text, string marker)
    {
        int count = 0;
        int at = 0;
        while (true)
        {
            int found = text.IndexOf(marker, at, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count += 1;
            at = found + marker.Length;
        }
    }

    private static async Task<byte[]> SelectAsync(InMemoryQpdfHost host, string input, string output, params string[] flags)
    {
        List<string> argv = new() { input };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", output]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] pdf));
        return pdf;
    }

    private static async Task<int> RereadPageCountAsync(InMemoryQpdfHost host, byte[] pdf)
    {
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        return document.PageCount;
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task DisableExplicitlyWritesClassic()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=disable");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("\nxref\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /ObjStm", text, StringComparison.Ordinal);
        Assert.StartsWith("%PDF-1.4", HeaderOf(pdf));
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task DefaultPreservesClassicAsClassic()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("\nxref\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /ObjStm", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateWritesObjectAndXrefStreams()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Type /ObjStm", text, StringComparison.Ordinal);
        Assert.Contains("/Type /XRef", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nxref\n", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
        using MemoryStream buffer = new(pdf);
        using PdfDocument reread = PdfDocument.Open(buffer);
        Assert.Equal(1, reread.NumberOfPages);
    }

    [Fact]
    public async Task GenerateBumpsVersionTo1_5()
    {
        byte[] bytes = BuildOnePage("1.4", Encoding.ASCII.GetBytes("hello"));
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate");
        Assert.StartsWith("%PDF-1.5", HeaderOf(pdf));
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task ForceBelow15DisablesStreams()
    {
        byte[] bytes = BuildOnePage("1.4", Encoding.ASCII.GetBytes("hello"));
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate", "--force-version=1.4");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("\nxref\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /ObjStm", text, StringComparison.Ordinal);
        Assert.StartsWith("%PDF-1.4", HeaderOf(pdf));
    }

    [Fact]
    public async Task PreserveKeepsCompressedObjects()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "objstm-1page.pdf");
        await using (QpdfDocument source = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf");
            string text = Encoding.ASCII.GetString(pdf);
            Assert.Contains("/Type /ObjStm", text, StringComparison.Ordinal);
            Assert.Contains("/Type /XRef", text, StringComparison.Ordinal);
            Assert.Equal(source.PageCount, await RereadPageCountAsync(host, pdf));
        }
    }

    [Fact]
    public async Task DisableExpandsCompressedObjects()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "objstm-1page.pdf");
        await using (QpdfDocument source = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=disable");
            string text = Encoding.ASCII.GetString(pdf);
            Assert.Contains("\nxref\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/Type /ObjStm", text, StringComparison.Ordinal);
            Assert.Equal(source.PageCount, await RereadPageCountAsync(host, pdf));
            using MemoryStream buffer = new(pdf);
            using PdfDocument reread = PdfDocument.Open(buffer);
            Assert.Equal(source.PageCount, reread.NumberOfPages);
        }
    }

    [Fact]
    public async Task GenerateIsDeterministicAcrossRuns()
    {
        InMemoryQpdfHost firstHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] a = await SelectAsync(firstHost, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate");
        InMemoryQpdfHost secondHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] b = await SelectAsync(secondHost, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate");
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task SpaceSeparatedModeFormSucceeds()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams", "generate");
        Assert.Contains("/Type /ObjStm", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyObjectsChunkIntoMultipleStreams()
    {
        byte[] bytes = BuildManyResourcePdf(210);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate");
        Assert.Equal(3, CountOccurrences(Encoding.ASCII.GetString(pdf), "/Type /ObjStm"));
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
        using MemoryStream buffer = new(pdf);
        using PdfDocument reread = PdfDocument.Open(buffer);
        Assert.Equal(1, reread.NumberOfPages);
    }

    [Fact]
    public async Task JobJsonParityHolds()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] a = await SelectAsync(argvHost, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate");
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"objectStreams\": \"generate\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task GenerateWithCompressFlatesContainers()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate", "--compress-streams=y");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Type /ObjStm", text, StringComparison.Ordinal);
        Assert.Contains("/Type /XRef", text, StringComparison.Ordinal);
        Assert.Contains("/Filter /FlateDecode", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
        using MemoryStream buffer = new(pdf);
        using PdfDocument reread = PdfDocument.Open(buffer);
        Assert.Equal(1, reread.NumberOfPages);
    }

    [Fact]
    public async Task GenerateWithoutCompressLeavesContainersRaw()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate");
        Assert.Contains("/Filter /FlateDecode", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateWithCompressIsDeterministic()
    {
        InMemoryQpdfHost firstHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] a = await SelectAsync(firstHost, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate", "--compress-streams=y");
        InMemoryQpdfHost secondHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] b = await SelectAsync(secondHost, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate", "--compress-streams=y");
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("9")]
    public async Task CompressLevelsApplyToContainers(string level)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate", "--compress-streams=y", "--compression-level=" + level);
        Assert.Contains("/Filter /FlateDecode", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveWithCompressFlatesContainers()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "objstm-1page.pdf");
        await using (QpdfDocument source = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=preserve", "--compress-streams=y");
            string text = Encoding.ASCII.GetString(pdf);
            Assert.Contains("/Type /ObjStm", text, StringComparison.Ordinal);
            Assert.Contains("/Filter /FlateDecode", text, StringComparison.Ordinal);
            Assert.Equal(source.PageCount, await RereadPageCountAsync(host, pdf));
        }
    }

    [Fact]
    public async Task CompressJobParityHolds()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        byte[] a = await SelectAsync(argvHost, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate", "--compress-streams=y", "--compression-level=5");
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"objectStreams\": \"generate\", \"compressStreams\": \"y\", \"compressionLevel\": \"5\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("--object-streams=bogus")]
    [InlineData("--object-streams=")]
    [InlineData("--object-streams")]
    [InlineData("--object-streams=Disable")]
    public async Task InvalidModesFail(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobJsonInvalidModeFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"objectStreams\": \"bogus\"}",
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task GenerateYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--object-streams=generate", "--pages", ".", "1", ".", "2", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
