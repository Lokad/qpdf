using System.Text;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class PagesBlockGrammarTests
{
    public static IEnumerable<object[]> InvalidOptions()
    {
        foreach (string token in new[] { "--deterministic-id", "--static-id", "--progress", "--verbose",
            "--collate", "--rotate=+90:1", "--remove-page-labels", "--set-page-labels",
            "--object-streams=generate", "--global", "--job-json-file=settings.json", "--normalize-content=y",
            "--parser-max-errors=10", "--bogus" })
        {
            yield return [token, false];
            yield return [token, true];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task MainOptionInsidePagesRejectsBeforePdfAccessAndPreservesTarget(string token, bool mixed)
    {
        InMemoryQpdfHost host = await Host();
        byte[] prior = "prior target"u8.ToArray();
        await using (IQpdfOutputTransaction target = await host.CreateOutputAsync(QpdfPath.From("outputs/a.pdf"), CancellationToken.None))
        {
            await target.WriteAsync(prior, CancellationToken.None);
            await target.CommitAsync(CancellationToken.None);
        }
        List<string> argv = ["missing.pdf"];
        if (mixed) { argv.Add("--job-json-file=settings.json"); }
        argv.AddRange(["--pages", ".", "1", token, "--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.Contains(token, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("pages options must be terminated with --", diagnostic.Message, StringComparison.Ordinal);
        if (!mixed) { Assert.Equal(0L, result.Counters.HostOperations); }
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] after));
        Assert.Equal(prior, after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedOptionWinsOverMissingTerminator(bool mixed)
    {
        InMemoryQpdfHost host = await Host();
        List<string> argv = ["missing.pdf"];
        if (mixed) { argv.Add("--job-json-file=settings.json"); }
        argv.AddRange(["--pages", ".", "1", "--deterministic-id"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Contains("--deterministic-id (pages options", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task MainOptionsAtLegalPositionsMatchJobOutput(int position, bool mixed)
    {
        InMemoryQpdfHost host = await Host();
        List<string> argv = ["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"];
        int insertion = position switch { 0 => 0, 1 => 1, 2 => 5, _ => 6 };
        argv.InsertRange(insertion, ["--deterministic-id", "--object-streams=disable", "--rotate=+90:1", "--collate"]);
        if (mixed) { argv.Insert(0, "--job-json-file=settings.json"); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        InMemoryQpdfHost expectedHost = await Host();
        QpdfExecutionResult expected = await Qpdf.ExecuteJobAsync(expectedHost,
            """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","pages":[{"file":".","range":"1"}],"deterministicId":"","objectStreams":"disable","rotate":["+90:1"],"collate":"1"}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, expected.Status);
        Assert.True(expectedHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] reference));
        Assert.Equal(reference, output);
        using PdfDocument independent = PdfDocument.Open(output);
        Assert.Equal(1, independent.NumberOfPages);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task FileAndRangeSpellingsRemainValid(int shape, bool mixed)
    {
        InMemoryQpdfHost host = await Host();
        List<string> argv = ["inputs/a.pdf", "--deterministic-id"];
        if (mixed) { argv.Add("--job-json-file=settings.json"); }
        argv.Add("--pages");
        argv.AddRange(shape switch
        {
            0 => [".", "2"],
            1 => ["--file=.", "--range=2"],
            2 => [".", "--range=2"],
            _ => ["--file=inputs/a.pdf", "--range=2"],
        });
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        using PdfDocument independent = PdfDocument.Open(output);
        Assert.Equal(1, independent.NumberOfPages);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task NamedOptionsRequireEqualsBeforePdfAccess(bool file, bool mixed)
    {
        InMemoryQpdfHost host = await Host();
        List<string> argv = ["missing.pdf"];
        if (mixed) { argv.Add("--job-json-file=settings.json"); }
        argv.Add("--pages");
        argv.AddRange(file ? ["--file", "."] : [".", "--range", "2"]);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Contains(file ? "--file must be given as --file=file" : "--range must be given as --range=page-range", result.Diagnostics[0].Message, StringComparison.Ordinal);
        if (!mixed) { Assert.Equal(0L, result.Counters.HostOperations); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionalArgumentAfterNamedFileStartsAnotherFile(bool mixed)
    {
        InMemoryQpdfHost host = await Host();
        List<string> argv = ["inputs/a.pdf"];
        if (mixed) { argv.Add("--job-json-file=settings.json"); }
        argv.AddRange(["--pages", "--file=.", "2", "--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.HostOperationFailed, Assert.Single(result.Diagnostics).Code);
        Assert.Contains("'2'", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("mixed")]
    [InlineData("job")]
    public async Task EmptyLabelBlockAtMainContextIsANoOp(string route)
    {
        InMemoryQpdfHost host = await Host();
        QpdfExecutionResult result;
        if (route == "job")
        {
            result = await Qpdf.ExecuteJobAsync(host,
                """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","pages":[{"file":".","range":"1"}],"deterministicId":"","setPageLabels":[]}""", CancellationToken.None);
        }
        else
        {
            List<string> argv = ["inputs/a.pdf", "--deterministic-id", "--set-page-labels", "--", "--pages", ".", "1", "--", "outputs/a.pdf"];
            if (route == "mixed") { argv.Insert(0, "--job-json-file=settings.json"); }
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        InMemoryQpdfHost plain = await Host();
        QpdfExecutionResult expected = await Qpdf.ExecuteAsync(plain,
            new QpdfCommandInvocation(["inputs/a.pdf", "--deterministic-id", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, expected.Status);
        Assert.True(plain.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] reference));
        Assert.Equal(reference, output);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("overlay", false)]
    [InlineData("overlay", true)]
    [InlineData("underlay", false)]
    [InlineData("underlay", true)]
    [InlineData("add-attachment", false)]
    [InlineData("add-attachment", true)]
    public async Task BlockAfterCompletedPagesReturnsToMainOutputContext(string block, bool mixed)
    {
        InMemoryQpdfHost host = await Host();
        List<string> argv = ["inputs/a.pdf", "--deterministic-id"];
        if (mixed) { argv.Add("--job-json-file=settings.json"); }
        argv.AddRange(["--pages", ".", "1", "--", "--" + block, "inputs/a.pdf", "--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        using PdfDocument independent = PdfDocument.Open(output);
        Assert.Equal(1, independent.NumberOfPages);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReplacementAlsoRequiresPagesTerminator(bool terminator, bool mixed)
    {
        InMemoryQpdfHost host = await Host();
        List<string> argv = ["inputs/a.pdf", "--deterministic-id", "--replace-input"];
        if (mixed) { argv.Add("--job-json-file=settings.json"); }
        argv.AddRange(["--pages", ".", "1"]);
        if (terminator) { argv.Add("--"); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(terminator ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
        if (!terminator) { Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code); }
        Assert.Equal(0L, host.OpenLeaseCount);
        QpdfPath readPath = QpdfPath.From("inputs/a.pdf");
        if (terminator)
        {
            Assert.True(host.TryGetOutput(readPath, out byte[] replaced));
            readPath = QpdfPath.From("inputs/replaced.pdf");
            host.AddInput(readPath, replaced, QpdfInputOwnership.Copy);
        }
        else { Assert.False(host.TryGetOutput(readPath, out byte[] _)); }
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, readPath, CancellationToken.None);
        Assert.Equal(terminator ? 1 : 2, document.PageCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task JobSettingsBeforeOrAfterPagesKeepSetterOrder(bool job, bool later)
    {
        InMemoryQpdfHost host = await Host();
        host.AddInput(QpdfPath.From("version.json"), "{\"forceVersion\":\"1.5\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result;
        if (job)
        {
            string settings = later ? "\"forceVersion\":\"1.6\",\"jobJsonFile\":\"version.json\"" : "\"jobJsonFile\":\"version.json\",\"forceVersion\":\"1.6\"";
            result = await Qpdf.ExecuteJobAsync(host,
                "{\"inputFile\":\"inputs/a.pdf\",\"outputFile\":\"outputs/a.pdf\",\"pages\":[{\"file\":\".\",\"range\":\"1\"}]," + settings + "}", CancellationToken.None);
        }
        else
        {
            List<string> argv = ["inputs/a.pdf", "--force-version=1.6", "--pages", ".", "1", "--", "outputs/a.pdf"];
            argv.Insert(later ? 6 : 1, "--job-json-file=version.json");
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        Assert.StartsWith(job || later ? "%PDF-1.5" : "%PDF-1.6", Encoding.ASCII.GetString(output), StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConfigurationFailureBeforePagesRemainsFirstButLaterFileIsNotOpened(bool before, bool missing)
    {
        InMemoryQpdfHost host = await Host();
        host.AddInput(QpdfPath.From("bad.json"), "{"u8.ToArray(), QpdfInputOwnership.Copy);
        string file = missing ? "missing.json" : "bad.json";
        List<string> argv = ["missing.pdf", "--pages", ".", "1", "--deterministic-id", "--", "outputs/a.pdf"];
        argv.Insert(before ? 1 : argv.Count, "--job-json-file=" + file);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        if (before)
        {
            Assert.DoesNotContain("pages options", diagnostic.Message, StringComparison.Ordinal);
            Assert.True(result.Counters.HostOperations > 0);
        }
        else
        {
            Assert.Contains("pages options", diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal(0L, result.Counters.HostOperations);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Fact]
    public void ScanObservesCancellationBeforeReadingArguments()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => PagesArgumentBlock.Validate(["--pages", ".", "1", "--"], governor));
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Theory]
    [InlineData("--overlay")]
    [InlineData("--underlay")]
    [InlineData("--add-attachment")]
    [InlineData("--copy-attachments-from")]
    [InlineData("--set-page-labels")]
    [InlineData("--global")]
    public void OtherBlockContentsCannotOpenPagesContext(string block)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PagesArgumentBlock.Validate([block, "--pages", "--deterministic-id", "--", "inputs/a.pdf"], governor);
    }

    private static async Task<InMemoryQpdfHost> Host()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        return host;
    }
}
