using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class PageResourcePolicyTests
{
    private static readonly QpdfPath Source = QpdfPath.From("source"), Output = QpdfPath.From("output.pdf");
    private static readonly PdfName ResourceKey = PdfName.FromAscii("Resources");
    private static readonly byte[] Drawing = "q 0 0 20 20 re f Q\n"u8.ToArray();

    [Theory]
    [InlineData("")]
    [InlineData("/Resources null")]
    [InlineData("/Resources 5 0 R")]
    public async Task CheckWarnsAndSelectionRepairsOnlyTheActiveResourceRole(string resources)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Source, Fixture(resources, "", Drawing), QpdfInputOwnership.Transfer);
        QpdfExecutionResult check = await Qpdf.ExecuteAsync(host, new([Source.Value, "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, check.ExitCode);
        Assert.Contains(check.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageStructureWarning);
        QpdfExecutionResult quiet = await Qpdf.ExecuteAsync(host,
            new([Source.Value, "--check", "--no-warn", "--warning-exit-0"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, quiet.ExitCode);
        Assert.DoesNotContain(quiet.Diagnostics, item => item.Severity == QpdfDiagnosticSeverity.Warning);
        QpdfExecutionResult selection = await Qpdf.ExecuteAsync(host,
            new([Source.Value, "--pages", ".", "1", "--", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, selection.ExitCode);
        Assert.Contains(selection.Diagnostics, item => item.Code == QpdfDiagnosticCode.RepairedObject);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(Output, bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument retained = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None))
        {
            PdfDictionary page = Assert.Single(retained.PageReferences).Page;
            Assert.Equal(0, Assert.IsType<PdfDictionary>(Read(retained, page, ResourceKey)).Count);
            Assert.Equal(17L, Assert.IsType<PdfInteger>(Read(retained, page, PdfName.FromAscii("Private"))).Value);
            AssertProgram(retained, page, Drawing);
        }
        QpdfExecutionResult after = await Qpdf.ExecuteAsync(host, new([Output.Value, "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, after.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("/Resources << /Sentinel 31 >>", "")]
    [InlineData("/Resources 7 0 R", "/Resources 6 2 R")]
    public async Task SelectionRetainsDirectAndInheritedResourceBindings(string leaf, string ancestor)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Source, Fixture(leaf, ancestor, Drawing), QpdfInputOwnership.Transfer);
        QpdfExecutionResult check = await Qpdf.ExecuteAsync(host,
            new([Source.Value, "--pages", ".", "1", "--", "--check", "--show-pages"]), CancellationToken.None);
        Assert.True(check.ExitCode == QpdfExitCode.Success, string.Join("\n", check.Diagnostics.Select(item => item.Message)));
        QpdfExecutionResult selection = await Qpdf.ExecuteAsync(host,
            new([Source.Value, "--pages", ".", "1", "--", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, selection.ExitCode);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(Output, bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument result = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None);
        PdfDictionary page = Assert.Single(result.PageReferences).Page;
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Read(result, page, ResourceKey));
        Assert.Equal(31L, Assert.IsType<PdfInteger>(Read(result, resources, PdfName.FromAscii("Sentinel"))).Value);
        AssertProgram(result, page, Drawing);
    }

    [Fact]
    public async Task UnknownResourceBindingsStayUnresolvedAndWholeRewritePreservesTheSourceRole()
    {
        byte[] use = "/Picture Do\n"u8.ToArray();
        using InMemoryQpdfHost host = new();
        host.AddInput(Source, Fixture("/Resources 5 0 R", "", use), QpdfInputOwnership.Transfer);
        QpdfPath whole = QpdfPath.From("whole.pdf");
        QpdfExecutionResult rewrite = await Qpdf.ExecuteAsync(host, new([Source.Value, whole.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, rewrite.ExitCode);
        QpdfExecutionResult selection = await Qpdf.ExecuteAsync(host,
            new([Source.Value, "--pages", ".", "1", "--", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, selection.ExitCode);
        Assert.Contains(selection.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageStructureWarning);
        foreach (QpdfPath path in new[] { whole, Output })
        {
            Assert.True(host.TryGetOutput(path, out byte[] bytes));
            host.AddInput(path, bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument result = await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
            PdfDictionary page = Assert.Single(result.PageReferences).Page;
            Assert.Equal(17L, Assert.IsType<PdfInteger>(Read(result, page, ResourceKey)).Value);
            AssertProgram(result, page, use);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundPageSplitRepairsAResourceFreeProgramBeforePublication()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Source, """
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
            "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},
            "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},
            "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,20,20],"/Contents":"4 0 R","/Resources":17}},
            "obj:4 0 R":{"stream":{"dict":{},"data":"cSAwIDAgMjAgMjAgcmUgZiBRCg=="}},
            "trailer":{"value":{"/Root":"1 0 R","/Size":5}}}]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"source","jsonInput":"","splitPages":"1","outputFile":"part-%d.pdf"}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.RepairedObject);
        QpdfPath part = QpdfPath.From("part-1.pdf");
        Assert.True(host.TryGetOutput(part, out byte[] bytes));
        host.AddInput(part, bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, part, CancellationToken.None))
        {
            PdfDictionary page = Assert.Single(document.PageReferences).Page;
            Assert.Equal(0, Assert.IsType<PdfDictionary>(Read(document, page, ResourceKey)).Count);
            AssertProgram(document, page, Drawing);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task MissingResourcesCheckDoesNotDecodeAndUnadmittedRepairKeepsTheTarget()
    {
        byte[] flateDrawing = Convert.FromHexString("789C2B5430004223302A4A55485308E402002804040E");
        List<(int, string, byte[]?)> objects = PdfTestDocument.SinglePage(
            $"<< /Length {flateDrawing.Length} /Filter /FlateDecode >>", flateDrawing);
        objects[2] = (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents 4 0 R >>", null);
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(PdfTestDocument.Assemble(objects, 1), Output);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 4 };
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("input"),
            new QpdfCheckOptions { DecodeLevel = QpdfStreamDecodeLevel.None }, limits, CancellationToken.None);
        Assert.True(check.Passed);
        Assert.Contains(check.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageStructureWarning);
        Assert.Equal(0L, check.Counters.TotalDecodedBytes);

        QpdfExecutionResult selection = await Qpdf.ExecuteAsync(host,
            new(["input", "--pages", ".", "1", "--", "--stream-data=preserve", Output.Value]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, selection.ExitCode);
        Assert.Contains(selection.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(Output, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static byte[] Fixture(string leaf, string ancestor, byte[] bytes) => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] " + ancestor + " >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents 4 0 R /Private 5 0 R " + leaf + " >>", null),
        (4, $"<< /Length {bytes.Length} >>", bytes),
        (5, "17", null), (6, "<< /Sentinel 31 >>", null), (7, "null", null)], 1, new Dictionary<int, int> { [6] = 2 });

    private static QpdfObject Read(QpdfDocument document, PdfDictionary values, PdfName key)
    {
        Assert.True(values.TryGet(key, out QpdfObject value));
        return value is PdfReference reference ? document.GetObject(reference) : value;
    }

    private static void AssertProgram(QpdfDocument document, PdfDictionary page, byte[] expected)
    {
        PdfStream stream = Assert.IsType<PdfStream>(Read(document, page, PdfName.FromAscii("Contents")));
        Assert.Equal(expected, StreamObservation.Decode(stream));
    }
}
