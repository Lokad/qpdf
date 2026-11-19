using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SplitNavigationTests
{
    private static byte[] Fixture() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 12 0 R /OpenAction 14 0 R >>", null),
        (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] /MediaBox [0 0 80 80] /Resources << >> >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /Annots [5 0 R 6 0 R 7 0 R 8 0 R] /Audit [10 0 R 11 0 R 13 0 R 14 0 R] >>", null),
        (4, "<< /Type /Page /Parent 2 0 R >>", null),
        (5, "<< /Subtype /Link /Rect [0 0 10 10] /Dest 10 0 R >>", null),
        (6, "<< /Subtype /Link /Rect [10 0 20 10] /A 11 0 R >>", null),
        (7, "<< /Subtype /Link /Rect [20 0 30 10] /Dest [3 0 R /Fit] >>", null),
        (8, "<< /Subtype /Link /Rect [30 0 40 10] /A 9 0 R >>", null),
        (9, "<< /S /GoToR /F (remote.pdf) /D [0 /Fit] >>", null),
        (10, "[4 0 R /Fit]", null),
        (11, "<< /S /GoTo /D 10 0 R >>", null),
        (12, "<< /Type /Outlines /First 13 0 R /Last 13 0 R /Count 1 >>", null),
        (13, "<< /Parent 12 0 R /Title (Second) /Dest 10 0 R >>", null),
        (14, "<< /S /GoTo /D 10 0 R >>", null)], 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitProjectsLocalDestinationsAndPreservesPrivateAndRemoteRoles(bool bound)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(), QpdfInputOwnership.Transfer);
        string[] split = ["input", "--split-pages=1", "part.pdf"];
        if (bound)
        {
            await BindAsync(host);
            split = ["bound", "--json-input", "--split-pages=1", "part.pdf"];
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(split), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        for (int part = 1; part <= 2; part++)
        {
            QpdfPath path = QpdfPath.From("part-" + part + ".pdf");
            Assert.True(host.TryGetOutput(path, out byte[] bytes));
            host.AddInput(path, bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
            QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference) : value;
            PdfDictionary Dictionary(QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(value));
            PdfArray Array(QpdfObject value) => Assert.IsType<PdfArray>(Resolve(value));
            var page = Assert.Single(document.PageReferences);
            PdfDictionary catalog = Dictionary(Get(document.Engine.Trailer, "Root"));
            PdfDictionary outlines = Dictionary(Get(catalog, "Outlines"));
            QpdfObject bookmarkRef = Get(outlines, "First");
            PdfDictionary bookmark = Dictionary(bookmarkRef);
            Assert.Equal(Get(catalog, "Outlines"), Get(bookmark, "Parent"));
            if (part == 2)
            {
                Assert.Equal(page.Source, Array(Get(bookmark, "Dest"))[0]);
                if (bound) { Assert.Equal(page.Source, Array(Get(Dictionary(Get(catalog, "OpenAction")), "D"))[0]); }
                else { Assert.IsType<PdfNull>(Get(catalog, "OpenAction")); }
                continue;
            }

            PdfArray annots = Array(Get(page.Page, "Annots"));
            Assert.IsType<PdfNull>(Get(Dictionary(annots[0]), "Dest"));
            Assert.IsType<PdfNull>(Get(Dictionary(annots[1]), "A"));
            Assert.Equal(page.Source, Array(Get(Dictionary(annots[2]), "Dest"))[0]);
            PdfDictionary remote = Dictionary(Get(Dictionary(annots[3]), "A"));
            Assert.Equal(PdfName.FromAscii("GoToR"), Get(remote, "S"));
            Assert.Equal(0, Assert.IsType<PdfInteger>(Array(Get(remote, "D"))[0]).Value);
            Assert.IsType<PdfNull>(Get(catalog, "OpenAction"));
            Assert.IsType<PdfNull>(Get(bookmark, "Dest"));
            PdfArray audit = Array(Get(page.Page, "Audit"));
            Assert.NotEqual(bookmarkRef, audit[2]);
            Assert.Equal(audit[0], Get(Dictionary(audit[1]), "D"));
            Assert.Equal(audit[0], Get(Dictionary(audit[2]), "Dest"));
            Assert.Equal(audit[0], Get(Dictionary(audit[3]), "D"));
            Assert.IsType<PdfNull>(Array(audit[0])[0]);
        }
    }

    [Fact]
    public async Task SplitPromotesLiveNextActionsInOrderAndKeepsPrivateChain()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OpenAction 8 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] /MediaBox [0 0 80 80] /Resources << >> >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Annots [5 0 R 6 0 R] /Audit 8 0 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R >>", null),
            (5, "<< /Subtype /Link /Rect [0 0 10 10] /A 8 0 R >>", null),
            (6, "<< /Subtype /Link /Rect [10 0 20 10] /A 9 0 R >>", null),
            (7, "null", null),
            (8, "<< /S /GoTo /D [4 0 R /Fit] /Next 12 0 R >>", null),
            (9, "<< /S /GoToR /D [0 /Fit] /F (first) /Next [14 0 R] >>", null),
            (10, "<< /S /GoToR /D [1 /Fit] /F (second) >>", null),
            (11, "<< /S /GoToR /D [2 /Fit] /F (third) >>", null),
            (12, "[13 0 R 10 0 R 11 0 R]", null),
            (13, "<< /S /GoTo /D [4 0 R /Fit] /Next 9 0 R >>", null),
            (14, "<< /S /GoToR /D [9 /Fit] /F (nested) >>", null)], 1), QpdfInputOwnership.Transfer);
        await BindAsync(host);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["bound", "--json-input", "--split-pages=1", "part.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfPath path = QpdfPath.From("part-1.pdf");
        Assert.True(host.TryGetOutput(path, out byte[] bytes));
        host.AddInput(path, bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference) : value;
        PdfDictionary Dictionary(QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(value));
        PdfArray Array(QpdfObject value) => Assert.IsType<PdfArray>(Resolve(value));
        long Ordinal(PdfDictionary action)
        {
            Assert.Equal(PdfName.FromAscii("GoToR"), Get(action, "S"));
            return Assert.IsType<PdfInteger>(Array(Get(action, "D"))[0]).Value;
        }

        PdfDictionary page = Assert.Single(document.PageReferences).Page;
        PdfDictionary catalog = Dictionary(Get(document.Engine.Trailer, "Root"));
        PdfArray annots = Array(Get(page, "Annots"));
        QpdfObject promoted = Get(Dictionary(annots[0]), "A"), stillFirst = Get(Dictionary(annots[1]), "A");
        Assert.NotEqual(promoted, stillFirst);
        Assert.Equal(promoted, Get(catalog, "OpenAction"));
        PdfDictionary action = Dictionary(promoted);
        Assert.Equal(0, Ordinal(action));
        Assert.Equal(new long[] { 9, 1, 2 }, Array(Get(action, "Next")).Items.Select(item => Ordinal(Dictionary(item))));
        Assert.Equal(9, Ordinal(Dictionary(Assert.Single(Array(Get(Dictionary(stillFirst), "Next")).Items))));

        PdfDictionary privateAction = Dictionary(Get(page, "Audit"));
        Assert.Equal(PdfName.FromAscii("GoTo"), Get(privateAction, "S"));
        Assert.IsType<PdfNull>(Array(Get(privateAction, "D"))[0]);
        PdfArray originalNext = Array(Get(privateAction, "Next"));
        Assert.Equal(3, originalNext.Count);
        Assert.Equal(PdfName.FromAscii("GoTo"), Get(Dictionary(originalNext[0]), "S"));
        Assert.Equal(stillFirst, Get(Dictionary(originalNext[0]), "Next"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitSkipsStaleActionCyclesAndKeepsLiveExit(bool liveExit)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] /MediaBox [0 0 80 80] /Resources << >> >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Annots [5 0 R] /Audit 6 0 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R >>", null),
            (5, "<< /Subtype /Link /Rect [0 0 10 10] /A 6 0 R >>", null),
            (6, "<< /S /GoTo /D [4 0 R /Fit] /Next 7 0 R >>", null),
            (7, "<< /S /GoTo /D [4 0 R /Fit] /Next " + (liveExit ? "[6 0 R 8 0 R]" : "6 0 R") + " >>", null),
            (8, "<< /S /GoToR /D [0 /Fit] /F (live.pdf) >>", null)], 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--split-pages=1", "part.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfPath path = QpdfPath.From("part-1.pdf");
        Assert.True(host.TryGetOutput(path, out byte[] bytes));
        host.AddInput(path, bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference) : value;
        PdfDictionary Dictionary(QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(value));
        PdfDictionary page = Assert.Single(document.PageReferences).Page;
        PdfArray annots = Assert.IsType<PdfArray>(Resolve(Get(page, "Annots")));
        QpdfObject action = Get(Dictionary(Assert.Single(annots.Items)), "A");
        if (liveExit)
        {
            PdfDictionary promoted = Dictionary(action);
            Assert.Equal(PdfName.FromAscii("GoToR"), Get(promoted, "S"));
            Assert.IsType<PdfNull>(Get(promoted, "Next"));
        }
        else { Assert.IsType<PdfNull>(action); }
        PdfDictionary privateNext = Dictionary(Get(Dictionary(Get(page, "Audit")), "Next"));
        QpdfObject back = Get(privateNext, "Next");
        if (liveExit) { back = Assert.IsType<PdfArray>(Resolve(back))[0]; }
        Assert.Equal(Get(page, "Audit"), back);
    }

    private static async Task BindAsync(InMemoryQpdfHost host)
    {
        QpdfExecutionResult exported = await Qpdf.ExecuteAsync(host,
            new(["input", "--json-output=2", "graph"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, exported.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("graph"), out byte[] json));
        host.AddInput(QpdfPath.From("bound"), json, QpdfInputOwnership.Transfer);
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;
}
