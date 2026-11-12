using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Security;

public sealed class OutputDecryptionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Cases()
    {
        foreach (string file in new[] { "security-r4-ascii.pdf", "security-r6-empty.pdf", "meta-1page.pdf" })
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "copy", "select", "split", "linear", "version" }) { yield return [file, job, mode]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExplicitDecryptionWritesUsablePlaintextAcrossWriterPaths(string file, bool job, string mode)
    {
        using InMemoryQpdfHost host = new(); Add(host, file);
        string password = file.Contains("empty", StringComparison.Ordinal) ? "" : "reader";
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["password"] = password, ["decrypt"] = "" };
        List<string> argv = [Input.Value, Output.Value, "--password=" + password, "--decrypt"];
        switch (mode)
        {
            case "select":
                fields["pages"] = new[] { new { file = ".", range = "1" } }; argv.AddRange(["--pages", ".", "1", "--"]); break;
            case "split":
                fields["pages"] = new[] { new { file = ".", range = "1,1" } }; fields["splitPages"] = "1";
                argv.AddRange(["--split-pages=1", "--pages", ".", "1,1", "--"]); break;
            case "linear":
                fields["linearize"] = ""; fields["objectStreams"] = "generate";
                argv.AddRange(["--linearize", "--object-streams=generate"]); break;
            case "version":
                fields["forceVersion"] = "1.4"; argv.Add("--force-version=1.4"); break;
        }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        QpdfPath[] outputs = mode == "split" ? [QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf")] : [Output];
        foreach (QpdfPath path in outputs)
        {
            Assert.True(host.TryGetOutput(path, out byte[] bytes));
            QpdfPath copy = QpdfPath.From("copy-" + path.Value); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, CancellationToken.None))
            {
                Assert.Equal(1, document.PageCount); Assert.Null(document.Encryption);
                Assert.False(document.Engine.Trailer.TryGet(PdfName.FromAscii("Encrypt"), out _));
                if (mode == "version") { Assert.Equal("1.4", document.Version.ToString()); }
                Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject info));
                PdfDictionary dictionary = Assert.IsType<PdfDictionary>(info is PdfReference reference ? document.GetObject(reference.ObjectNumber) : info);
                Assert.True(dictionary.TryGet(PdfName.FromAscii("Title"), out QpdfObject title));
                Assert.Equal("TitleText"u8.ToArray(), Assert.IsType<PdfByteString>(title).Value);
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, CancellationToken.None);
            Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecryptionDoesNotBypassPasswordsOrCanceledPublication(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior output"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        ValueTask<QpdfExecutionResult> Execute(string password, CancellationToken token) => job
            ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, string>
                { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value, ["password"] = password, ["decrypt"] = "" }), token)
            : Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password=" + password, "--decrypt"]), token);
        QpdfExecutionResult failed = await Execute("private-wrong", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, failed.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(failed.Diagnostics).Code);
        Assert.DoesNotContain("private-wrong", failed.Diagnostics[0].Message, StringComparison.Ordinal);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Equal(QpdfExecutionStatus.Canceled, (await Execute("reader", cancellation.Token)).Status);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior output"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputFreeInspectionIgnoresTheOutputPolicy(bool job)
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","password":"reader","decrypt":"","showNpages":""}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([Input.Value, "--password=reader", "--decrypt", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageCount);
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task AFalseDecryptionSetterRestoresPreservation()
    {
        using InMemoryQpdfHost host = new(); Add(host, "security-r6-ascii.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","outputFile":"output.pdf","password":"reader","decrypt":"","decrypt":false}""", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); host.AddInput(QpdfPath.From("copy.pdf"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy.pdf"), QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(6, document.Encryption?.Revision);
    }

    [Fact]
    public async Task MalformedDecryptionFlagAndJsonInputRejectBeforeHostAccess()
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--decrypt=yes"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(argv.Diagnostics).Code); Assert.Equal(0, argv.Counters.HostOperations);
        QpdfExecutionResult json = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.json","outputFile":"output.pdf","decrypt":"","jsonInput":""}""", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(json.Diagnostics).Code); Assert.Equal(0, json.Counters.HostOperations);
    }

    private static void Add(InMemoryQpdfHost host, string file)
        => host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
}
