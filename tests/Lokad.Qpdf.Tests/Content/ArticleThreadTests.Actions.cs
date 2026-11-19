using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed partial class ArticleThreadTests
{
    [Theory]
    [InlineData("1", "1")]
    [InlineData("(Target)", "10 2 R")]
    [InlineData("8 0 R", "1")]
    [InlineData("1", "")]
    public async Task ThreadActionsFindTheFirstRetainedOccurrence(string destination, string bead)
    {
        string selector = bead.Length == 0 ? string.Empty : " /B " + bead;
        using InMemoryQpdfHost host = ActionHost($"<< /S /Thread /D {destination}{selector} >>", "(First)");
        Assert.Equal(QpdfExitCode.Success, (await Run(host, "input.pdf", "--pages", ".", "3,3", "--", "selected.pdf")).ExitCode);
        await using QpdfDocument output = await Open(host, "selected.pdf");
        PdfDictionary catalog = Catalog(output);
        QpdfObject active = Value(catalog, "OpenAction");
        QpdfObject privateAction = Value(catalog, "PrivateAction");
        Assert.NotEqual(privateAction, active);
        Assert.Equal(Value(Dict(output, privateAction), "D"), Value(Dict(output, Value(output.PageReferences[0].Page, "PrivateAction")), "D"));
        PdfDictionary action = Dict(output, active);
        AssertThreadAction(output, action, 0, bead.Length > 0);
        Assert.Equal(Value(action, "D"), Value(PageOpening(output, 1), "D"));
        Assert.NotEqual(
            Assert.Single(Array(output, Value(output.PageReferences[0].Page, "B")).BorrowedItems),
            Assert.Single(Array(output, Value(output.PageReferences[1].Page, "B")).BorrowedItems));
    }

    [Fact]
    public async Task OmittingAThreadPromotesTheRemappedActionAndKeepsTheRemoteTail()
    {
        using InMemoryQpdfHost host = ActionHost(
            "<< /S /Thread /D 0 /Next [<< /S /Thread /D 1 /B 1 >> << /S /Thread /F (remote.pdf) /D 23 /B 17 >>] >>", "(First)");
        Assert.Equal(QpdfExitCode.Success, (await Run(host, "input.pdf", "--pages", ".", "3", "--", "selected.pdf")).ExitCode);
        await using QpdfDocument output = await Open(host, "selected.pdf");
        PdfDictionary catalog = Catalog(output);
        PdfDictionary action = Dict(output, Value(catalog, "OpenAction"));
        AssertThreadAction(output, action, 0, true);
        PdfDictionary remote = Dict(output, Assert.Single(Array(output, Value(action, "Next")).BorrowedItems));
        Assert.Equal("remote.pdf"u8.ToArray(), Assert.IsType<PdfByteString>(Value(remote, "F")).Value);
        Assert.Equal(23L, Assert.IsType<PdfInteger>(Value(remote, "D")).Value);
        Assert.Equal(17L, Assert.IsType<PdfInteger>(Value(remote, "B")).Value);
        Assert.Equal(0L, Assert.IsType<PdfInteger>(Value(Dict(output, Value(catalog, "PrivateAction")), "D")).Value);
    }

    [Theory]
    [InlineData("(Target)", "", "(Target)")]
    [InlineData("1", "9 0 R", "(First)")]
    [InlineData("1", "10 0 R", "(First)")]
    [InlineData("1", "7 0 R", "(First)")]
    public async Task OmittedOrMismatchedSelectorsDoNotRetarget(string thread, string bead, string firstTitle)
    {
        string beadEntry = bead.Length == 0 ? string.Empty : " /B " + bead;
        using InMemoryQpdfHost host = ActionHost($"<< /S /Thread /D {thread}{beadEntry} >>", firstTitle);
        QpdfExecutionResult result = await Run(host, "input.pdf", "--pages", ".", "3", "--", "selected.pdf");
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        await using QpdfDocument output = await Open(host, "selected.pdf");
        Assert.False(Catalog(output).TryGet(PdfName.FromAscii("OpenAction"), out _));
        Assert.False(Dict(output, Value(output.PageReferences[0].Page, "AA")).TryGet(PdfName.FromAscii("O"), out _));
        Assert.Single(Array(output, Value(Catalog(output), "Threads")).BorrowedItems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachSplitPartRebindsItsOwnArticleActions(bool bound)
    {
        using InMemoryQpdfHost host = ActionHost("<< /S /Thread /D 1 /B 1 >>", "(First)");
        host.AddInput(QpdfPath.From("patch.json"), """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = bound
            ? await Run(host, "input.pdf", "--update-from-json=patch.json", "--split-pages=1", "part.pdf")
            : await Run(host, "input.pdf", "--split-pages=1", "part.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument first = await Open(host, "part-1.pdf");
        await using QpdfDocument second = await Open(host, "part-2.pdf");
        await using QpdfDocument third = await Open(host, "part-3.pdf");
        Assert.False(Catalog(first).TryGet(PdfName.FromAscii("OpenAction"), out _));
        Assert.False(Catalog(second).TryGet(PdfName.FromAscii("OpenAction"), out _));
        AssertThreadAction(third, PageOpening(third, 0), 0, true);
        if (bound) { AssertThreadAction(third, Dict(third, Value(Catalog(third), "OpenAction")), 0, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondaryThreadActionsDoNotLaunchThePrimaryArticle(bool createFromReport)
    {
        using InMemoryQpdfHost host = Host();
        host.AddInput(QpdfPath.From("secondary.pdf"),
            ActionFixture("<< /S /Thread /D 0 /Next << /S /Named /N /NextPage >> >>", "(Secondary)"),
            QpdfInputOwnership.Transfer);
        if (createFromReport)
        {
            QpdfExecutionResult report = await Run(host, "input.pdf", "--pages", ".", "1", "secondary.pdf", "3,3", "--", "--json-output");
            Assert.Equal(QpdfExitCode.Success, report.ExitCode);
            host.AddInput(QpdfPath.From("report.json"), Encoding.UTF8.GetBytes(Assert.IsType<string>(report.OutputText)),
                QpdfInputOwnership.Transfer);
            Assert.Equal(QpdfExitCode.Success, (await Run(host, "--json-input", "report.json", "selected.pdf")).ExitCode);
        }
        else
        {
            Assert.Equal(QpdfExitCode.Success, (await Run(host, "input.pdf", "--pages", ".", "1", "secondary.pdf", "3,3", "--", "selected.pdf")).ExitCode);
        }
        await using QpdfDocument output = await Open(host, "selected.pdf");
        Assert.Single(Array(output, Value(Catalog(output), "Threads")).BorrowedItems);
        for (int index = 1; index <= 2; index++)
        {
            PdfDictionary opening = PageOpening(output, index);
            Assert.Equal(PdfName.FromAscii("Named"), Value(opening, "S"));
            Assert.Equal(PdfName.FromAscii("NextPage"), Value(opening, "N"));
            PdfDictionary original = Dict(output, Value(output.PageReferences[index].Page, "PrivateAction"));
            Assert.Equal(PdfName.FromAscii("Thread"), Value(original, "S"));
            Assert.Equal(0L, Assert.IsType<PdfInteger>(Value(original, "D")).Value);
        }
    }

    [Fact]
    public async Task AssembledJsonRecreatesAnActiveThreadAction()
    {
        using InMemoryQpdfHost host = ActionHost("<< /S /Thread /D (Target) /B 10 2 R >>", "(First)");
        QpdfExecutionResult report = await Run(host, "input.pdf", "--pages", ".", "3", "--", "--json-output");
        Assert.Equal(QpdfExitCode.Success, report.ExitCode);
        host.AddInput(QpdfPath.From("report.json"), Encoding.UTF8.GetBytes(Assert.IsType<string>(report.OutputText)),
            QpdfInputOwnership.Transfer);
        Assert.Equal(QpdfExitCode.Success, (await Run(host, "--json-input", "report.json", "selected.pdf")).ExitCode);
        await using QpdfDocument output = await Open(host, "selected.pdf");
        AssertThreadAction(output, Dict(output, Value(Catalog(output), "OpenAction")), 0, true);
    }

    [Fact]
    public async Task PlacementKeepsTheContinuationInItsSourceContext()
    {
        using InMemoryQpdfHost host = Host();
        host.AddInput(QpdfPath.From("secondary.pdf"),
            ActionFixture("<< /S /Thread /D 0 /Next << /S /URI /URI (../trail.html) >> >>", "(Secondary)"),
            QpdfInputOwnership.Transfer);
        var result = await Run(host, "input.pdf", "--overlay", "secondary.pdf", "--from=3", "--to=1", "--", "placed.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Open(host, "placed.pdf");
        PdfDictionary page = output.PageReferences[0].Page;
        PdfDictionary annotation = Dict(output, Assert.Single(Array(output, Value(page, "Annots")).BorrowedItems));
        PdfDictionary action = Dict(output, Value(annotation, "A"));
        Assert.Equal(PdfName.FromAscii("URI"), Value(action, "S"));
        Assert.Equal("https://example.test/trail.html"u8.ToArray(), Assert.IsType<PdfByteString>(Value(action, "URI")).Value);
        Assert.Single(Array(output, Value(Catalog(output), "Threads")).BorrowedItems);
    }

    private static InMemoryQpdfHost ActionHost(string action, string firstTitle)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Input, ActionFixture(action, firstTitle), QpdfInputOwnership.Transfer);
        return host;
    }

    private static byte[] ActionFixture(string action, string firstTitle)
        => PdfTestDocument.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /Threads [6 0 R 8 0 R] /OpenAction 11 0 R /PrivateAction 11 0 R /URI << /Base (https://example.test/survey/) >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 101] /Resources << >> /B [9 0 R] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 102] /Resources << >> /B [7 0 R] >>", null),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 103] /Resources << >> /B [10 2 R] /AA << /O 11 0 R >> /PrivateAction 11 0 R /Annots [12 0 R] >>", null),
            (6, $"<< /Type /Thread /F 7 0 R /I << /Title {firstTitle} >> >>", null),
            (7, "<< /Type /Bead /T 6 0 R /N 7 0 R /V 7 0 R /P 4 0 R /R [10 10 20 20] >>", null),
            (8, "<< /Type /Thread /F 9 0 R /I << /Title <FEFF005400610072006700650074> >> >>", null),
            (9, "<< /Type /Bead /T 8 0 R /N 10 2 R /V 10 2 R /P 3 0 R /R [10 20 30 40] >>", null),
            (10, "<< /Type /Bead /N 9 0 R /V 9 0 R /P 5 0 R /R [20 30 50 60] >>", null),
            (11, action, null),
            (12, "<< /Type /Annot /Subtype /Link /Rect [10 10 30 40] /A 11 0 R >>", null),
        ], 1, new Dictionary<int, int> { [10] = 2 });

    private static PdfDictionary PageOpening(QpdfDocument document, int index)
        => Dict(document, Value(Dict(document, Value(document.PageReferences[index].Page, "AA")), "O"));

    private static void AssertThreadAction(QpdfDocument document, PdfDictionary action, int page, bool withBead)
    {
        Assert.Equal(PdfName.FromAscii("Thread"), Value(action, "S"));
        QpdfObject thread = Assert.Single(Array(document, Value(Catalog(document), "Threads")).BorrowedItems);
        Assert.Equal(thread, Value(action, "D"));
        if (!withBead)
        {
            Assert.False(action.TryGet(PdfName.FromAscii("B"), out _));
            return;
        }
        QpdfObject bead = Assert.Single(Array(document, Value(document.PageReferences[page].Page, "B")).BorrowedItems);
        Assert.Equal(bead, Value(action, "B"));
        Assert.Equal(bead, Value(Dict(document, thread), "F"));
    }
}
