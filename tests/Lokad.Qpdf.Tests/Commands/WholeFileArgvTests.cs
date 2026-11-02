using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class WholeFileArgvTests
{
    [Theory]
    [InlineData("--rotate=+90:1", false)]
    [InlineData("--rotate=+90:1", true)]
    [InlineData("--rotate=+90", false)]
    [InlineData("--rotate=+90", true)]
    [InlineData("--collate=2", false)]
    [InlineData("--collate=2", true)]
    public async Task WholeFilePageControlsMatchEmptyJobFile(string flag, bool addAttachment)
    {
        async Task<byte[]> Run(bool mixed)
        {
            using InMemoryQpdfHost host = await Host();
            host.AddInput(QpdfPath.From("empty.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
            host.AddInput(QpdfPath.From("payload.bin"), "payload"u8.ToArray(), QpdfInputOwnership.Copy);
            string[] edits = addAttachment ? ["--add-attachment", "payload.bin", "--"] : [];
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([
                "inputs/a.pdf", .. mixed ? new[] { "--job-json-file=empty.json" } : [], flag, .. edits, "outputs/a.pdf"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bytes));
            host.AddInput(QpdfPath.From("result.pdf"), bytes, QpdfInputOwnership.Copy);
            await using (QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("result.pdf"), CancellationToken.None))
            {
                Assert.Equal(2, output.PageCount);
                for (int index = 0; index < 2; index++)
                {
                    PdfDictionary page = output.PageReferences[index].Page;
                    long rotation = page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject raw) ? Assert.IsType<PdfInteger>(raw).Value : 0L;
                    long expected = flag.StartsWith("--rotate", StringComparison.Ordinal) && (index == 0 || !flag.Contains(':')) ? 90L : 0L;
                    Assert.Equal(expected, rotation);
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.Equal(0L, result.Counters.OpenHandles);
            return bytes;
        }
        Assert.Equal(await Run(false), await Run(true));
    }

    [Theory]
    [InlineData("--rotate=+90:3", false)]
    [InlineData("--rotate=+90:3", true)]
    [InlineData("--rotate=+45:1", false)]
    [InlineData("--rotate=+45:1", true)]
    [InlineData("--rotate=+90:0", false)]
    [InlineData("--rotate=+90:0", true)]
    public async Task InvalidWholeFileRotationPreservesOutput(string flag, bool mixed)
    {
        using InMemoryQpdfHost host = await Host();
        host.AddInput(QpdfPath.From("empty.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("outputs/a.pdf"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([
            "inputs/a.pdf", .. mixed ? new[] { "--job-json-file=empty.json" } : [], flag, "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("--rotate=+90")]
    [InlineData("--collate=2")]
    public async Task WholeFilePageControlsAcceptSyntheticEmptyDocument(string flag)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--empty", flag, "out.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] bytes));
        host.AddInput(QpdfPath.From("result.pdf"), bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("result.pdf"), CancellationToken.None))
        { Assert.Equal(0, output.PageCount); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task<InMemoryQpdfHost> Host()
    {
        InMemoryQpdfHost host = new();
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf"));
        host.AddInput(QpdfPath.From("inputs/a.pdf"), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    [Theory]
    [InlineData(false, "disable")]
    [InlineData(true, "disable")]
    [InlineData(false, "preserve")]
    [InlineData(true, "preserve")]
    [InlineData(false, "generate")]
    [InlineData(true, "generate")]
    public async Task BareLinearizedRewriteMatchesTheSharedJobWriter(bool terminator, string mode)
    {
        InMemoryQpdfHost argvHost = await Host();
        List<string> argv = new() { "inputs/a.pdf", "--linearize", "--object-streams=" + mode, "--deterministic-id" };
        if (terminator) { argv.Add("--"); }
        argv.Add("outputs/a.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        Assert.Contains("/Linearized", Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        using (PdfDocument independent = PdfDocument.Open(output)) { Assert.Equal(2, independent.NumberOfPages); }
        InMemoryQpdfHost jobHost = await Host();
        string job = "{\"inputFile\":\"inputs/a.pdf\",\"outputFile\":\"outputs/a.pdf\",\"linearize\":\"\",\"deterministicId\":\"\",\"objectStreams\":\"" + mode + "\"}";
        QpdfExecutionResult expected = await Qpdf.ExecuteJobAsync(jobHost, job, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, expected.Status);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] reference));
        Assert.Equal(reference, output);
        Assert.Equal(0L, argvHost.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainRewriteNeedsNoSelectionOption(bool terminator)
    {
        InMemoryQpdfHost host = await Host();
        string[] argv = terminator ? ["inputs/a.pdf", "--", "outputs/a.pdf"] : ["inputs/a.pdf", "outputs/a.pdf"];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        using PdfDocument independent = PdfDocument.Open(output);
        Assert.Equal(2, independent.NumberOfPages);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("no-input")]
    public async Task InvalidBareRewriteArgumentsPreservePriorTargetsBeforeInputAccess(string shape)
    {
        InMemoryQpdfHost host = await Host();
        QpdfPath outputPath = QpdfPath.From("outputs/a.pdf");
        byte[] prior = "prior target"u8.ToArray();
        await using (IQpdfOutputTransaction output = await host.CreateOutputAsync(outputPath, CancellationToken.None))
        {
            await output.WriteAsync(prior, CancellationToken.None);
            await output.CommitAsync(CancellationToken.None);
        }
        string[] argv = shape switch
        {
            "missing" => ["inputs/a.pdf", "--linearize"],
            "no-input" => ["--linearize"],
            _ => ["inputs/a.pdf", "--linearize", "outputs/a.pdf", "outputs/extra.pdf"],
        };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.True(host.TryGetOutput(outputPath, out byte[] after));
        Assert.Equal(prior, after);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
