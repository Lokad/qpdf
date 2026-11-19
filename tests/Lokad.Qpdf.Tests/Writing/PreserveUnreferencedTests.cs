using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PreserveUnreferencedTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildOrphanPdf(string catalogExtra, List<(string Dict, byte[]? StreamData)> extras)
    {
        byte[] pageData = Encoding.ASCII.GetBytes("BT (Page) Tj ET");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + pageData.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach ((string dict, byte[]? _) in extras)
        {
            bodies.Add(dict);
        }
        return PdfTestDocument.AssembleBodies(bodies, number => number == 4 ? pageData : number > 5 ? extras[number - 6].StreamData : null,
            "1.4", "");
    }

    private static byte[] OrphanFixture()
    {
        return BuildOrphanPdf(string.Empty, new List<(string Dict, byte[]? StreamData)>
        {
            ("<< /Marker (OrphanOne) >>", null),
            ("<< /Length 12 >>", Encoding.ASCII.GetBytes("HELLO-ORPHAN")),
        });
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
    public async Task DefaultDropsUnreferencedObjects()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("OrphanOne", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HELLO-ORPHAN", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveKeepsUnreferencedObjects()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--preserve-unreferenced");
        string text = await StreamObservation.PdfText(pdf);
        Assert.Contains("OrphanOne", text, StringComparison.Ordinal);
        Assert.Contains("HELLO-ORPHAN", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveSkipsRebuiltRoots()
    {
        byte[] bytes = BuildOrphanPdf(
            " /NeedsRendering true",
            new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--preserve-unreferenced");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Equal(1, text.Split("NeedsRendering", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveKeepsDetachedPages()
    {
        byte[] bytes = BuildOrphanPdf(
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1 1] /Contents 4 0 R /StructParents 424242 >>", null),
            });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--preserve-unreferenced");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("424242", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
        host.AddInput(QpdfPath.From("inputs/tree.pdf"), pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/tree.pdf"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Pages"), out QpdfObject? pagesRaw));
        PdfReference pagesRef = Assert.IsType<PdfReference>(pagesRaw);
        PdfDictionary tree = Assert.IsType<PdfDictionary>(document.GetObject(pagesRef.ObjectNumber));
        Assert.True(tree.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw));
        Assert.Equal(1, Assert.IsType<PdfArray>(kidsRaw).Count);
    }

    [Fact]
    public async Task PreserveSkipsUnpreservableWithWarning()
    {
        byte[] bytes = BuildOrphanPdf(
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /BackRef 1 0 R >>", null),
                ("<< /Length 12 >>", Encoding.ASCII.GetBytes("HELLO-ORPHAN")),
            });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        List<string> argv = new() { "inputs/a.pdf", "--preserve-unreferenced", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        QpdfDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code);
        Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Skipped 1", warning.Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.Contains("HELLO-ORPHAN", await StreamObservation.PdfText(pdf), StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveAcrossMergeKeepsPrimaryOrphans()
    {
        byte[] aBytes = BuildOrphanPdf(
            string.Empty,
            new List<(string Dict, byte[]? StreamData)> { ("<< /Marker (AlphaOrphan) >>", null) });
        byte[] bBytes = BuildOrphanPdf(
            string.Empty,
            new List<(string Dict, byte[]? StreamData)> { ("<< /Marker (BetaOrphan) >>", null) });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", aBytes);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), bBytes, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--preserve-unreferenced", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("AlphaOrphan", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BetaOrphan", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreserveEmptyMergeDropsOrphans()
    {
        byte[] aBytes = BuildOrphanPdf(
            string.Empty,
            new List<(string Dict, byte[]? StreamData)> { ("<< /Marker (AlphaOrphan) >>", null) });
        byte[] bBytes = BuildOrphanPdf(
            string.Empty,
            new List<(string Dict, byte[]? StreamData)> { ("<< /Marker (BetaOrphan) >>", null) });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", aBytes);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), bBytes, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--empty", "--preserve-unreferenced", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("AlphaOrphan", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BetaOrphan", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreserveAcrossSplitKeepsOrphansPerGroup()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--preserve-unreferenced", "--split-pages=9", "--pages", ".", "1", "--", "outputs/s.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/s-1.pdf"), out byte[] pdf));
        string text = await StreamObservation.PdfText(pdf);
        Assert.Contains("OrphanOne", text, StringComparison.Ordinal);
        Assert.Contains("HELLO-ORPHAN", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitKeepsOnlyLocalContentAndRequestedDetachedGraphs(bool preserve)
    {
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Contents [5 0 R 7 0 R 10 0 R] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /Contents [6 0 R 7 0 R] >>", null),
            (5, "<< /Length 6 >>", "%LEFT\n"u8.ToArray()),
            (6, "<< /Length 7 >>", "%RIGHT\n"u8.ToArray()),
            (7, "<< /Length 8 >>", "%SHARED\n"u8.ToArray()),
            (8, "<< /Orphan true /Body 9 0 R /Pinned 10 0 R /Left 3 0 R /Right 4 0 R >>", null),
            (9, "<< /Length 10 >>", "%DETACHED\n"u8.ToArray()),
            (10, "<< /Length 8 >>", "%PINNED\n"u8.ToArray()),
        ], 1);
        using InMemoryQpdfHost host = HostWith("input.pdf", source);
        string[] flags = preserve ? ["--preserve-unreferenced"] : [];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--split-pages=1", .. flags, "part.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);

        for (int part = 1; part <= 2; part++)
        {
            Assert.True(host.TryGetOutput(QpdfPath.From($"part-{part}.pdf"), out byte[] bytes));
            QpdfPath read = QpdfPath.From($"check-{part}.pdf");
            host.AddInput(read, bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
            PageLocation page = Assert.Single(document.PageReferences);
            QpdfObject[] objects = document.Engine.XRefEntries
                .Where(entry => entry.Value.Kind != XrefEntryKind.Free)
                .Select(entry => document.GetObject(entry.Key)).ToArray();
            string[] expected = preserve
                ? ["%DETACHED\n", part == 1 ? "%LEFT\n" : "%RIGHT\n", "%PINNED\n", "%SHARED\n"]
                : part == 1 ? ["%LEFT\n", "%PINNED\n", "%SHARED\n"] : ["%RIGHT\n", "%SHARED\n"];
            string[] actual = objects.OfType<PdfStream>()
                .Select(stream => Encoding.ASCII.GetString(StreamObservation.Decode(stream))).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual);

            IEnumerable<PdfDictionary> orphans = objects.OfType<PdfDictionary>()
                .Where(dictionary => dictionary.TryGet(PdfName.FromAscii("Orphan"), out _));
            if (!preserve)
            {
                Assert.Empty(orphans);
                continue;
            }
            PdfDictionary orphan = Assert.Single(orphans);
            Assert.True(orphan.TryGet(PdfName.FromAscii(part == 1 ? "Left" : "Right"), out QpdfObject local));
            Assert.Equal(page.Source, Assert.IsType<PdfReference>(local));
            Assert.True(orphan.TryGet(PdfName.FromAscii(part == 1 ? "Right" : "Left"), out QpdfObject foreign));
            Assert.IsType<PdfNull>(foreign);
            Assert.True(orphan.TryGet(PdfName.FromAscii("Body"), out QpdfObject body));
            PdfStream detached = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(body)));
            Assert.Equal("%DETACHED\n"u8.ToArray(), StreamObservation.Decode(detached));
            Assert.True(orphan.TryGet(PdfName.FromAscii("Pinned"), out QpdfObject pinned));
            PdfStream privateStream = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(pinned)));
            Assert.Equal("%PINNED\n"u8.ToArray(), StreamObservation.Decode(privateStream));
        }
    }

    [Fact]
    public async Task PreserveWithGenerateCompressesOrphans()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--preserve-unreferenced", "--object-streams=generate");
        string text = await StreamObservation.PdfText(pdf);
        Assert.Contains("/Type /ObjStm", text, StringComparison.Ordinal);
        Assert.Contains("OrphanOne", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveFailsOnExhaustedOutputBudget()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--preserve-unreferenced", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobJsonParityHolds()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", OrphanFixture());
        byte[] a = await SelectAsync(argvHost, "inputs/a.pdf", "outputs/a.pdf", "--preserve-unreferenced");
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"preserveUnreferenced\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("--preserve-unreferenced=x")]
    [InlineData("--preserve-unreferenced=")]
    public async Task ValuedFlagFails(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobJsonInvalidValueFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"preserveUnreferenced\": 42}",
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
}
