using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class TaggedPageSetTests
{
    private static readonly byte[] First = "/P <</MCID 0>> BDC q Q EMC\n"u8.ToArray();
    private static readonly byte[] Second = "/P <</MCID 0>> BDC 0 0 2 2 re f EMC\n"u8.ToArray();

    [Theory]
    [InlineData("whole", true, 1, 2)]
    [InlineData("same", true, 1, 2)]
    [InlineData("subset", false, 2)]
    [InlineData("repeat", false, 2, 2)]
    [InlineData("reverse", false, 2, 1)]
    [InlineData("flatten", false, 1, 2)]
    [InlineData("remove", false, 1, 2)]
    public async Task TagActivationFollowsTheRetainedPageSet(string operation, bool active, params int[] pages)
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("input.pdf"), output = QpdfPath.From("output.pdf");
        host.AddInput(input, Fixture(), QpdfInputOwnership.Transfer);
        string[] flags = operation switch
        {
            "same" => ["--pages", ".", "1,2", "--"],
            "subset" => ["--pages", ".", "2", "--"],
            "repeat" => ["--pages", ".", "2,2", "--"],
            "reverse" => ["--pages", ".", "2,1", "--"],
            "flatten" => ["--flatten-annotations=all"],
            "remove" => ["--remove-structure"],
            _ => [],
        };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([input.Value, .. flags, output.Value]), CancellationToken.None);
        bool dropped = operation is "subset" or "repeat" or "reverse" or "flatten";
        Assert.Equal(dropped ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(dropped, result.Diagnostics.Any(diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageStructureWarning));
        await Verify(host, output, active, true, pages);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task SplitPartsOnlyAdvertiseCompleteTagRelationships(bool bound, int groupSize)
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("input.pdf"), graph = QpdfPath.From("graph.json");
        host.AddInput(input, Fixture(), QpdfInputOwnership.Transfer);
        if (bound)
        {
            QpdfJsonResult model = await Qpdf.GetDocumentJsonAsync(host, input,
                new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], true), CancellationToken.None);
            Assert.Contains("\"/StructTreeRoot\"", model.Json, StringComparison.Ordinal);
            host.AddInput(graph, System.Text.Encoding.UTF8.GetBytes(model.Json), QpdfInputOwnership.Transfer);
        }
        string split = $"--split-pages={groupSize}";
        string[] argv = bound ? [graph.Value, "--json-input", split, "part-%d.pdf"]
            : [input.Value, split, "part-%d.pdf"];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        QpdfExitCode expected = groupSize == 1 ? QpdfExitCode.Warning : QpdfExitCode.Success;
        Assert.True(expected == result.ExitCode, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
        Assert.Equal(groupSize == 1 ? 1 : 0, result.Diagnostics.Count(diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageStructureWarning));
        if (groupSize == 1)
        {
            await Verify(host, QpdfPath.From("part-1.pdf"), false, bound, [1]);
            await Verify(host, QpdfPath.From("part-2.pdf"), false, bound, [2]);
        }
        else { await Verify(host, QpdfPath.From("part-1-2.pdf"), true, bound, [1, 2]); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NonTaggingMarkInformationSurvivesASubset()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("input.pdf"), output = QpdfPath.From("output.pdf");
        host.AddInput(input, Fixture("/MarkInfo << /Marked false /Suspects true >>"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([input.Value, "--pages", ".", "2", "--", output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        host.AddInput(output, bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, output, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Read(document, document.Engine.Trailer, "Root"));
        PdfDictionary mark = Assert.IsType<PdfDictionary>(Read(document, catalog, "MarkInfo"));
        Assert.False(Assert.IsType<PdfBoolean>(Read(document, mark, "Marked")).Value);
        Assert.True(Assert.IsType<PdfBoolean>(Read(document, mark, "Suspects")).Value);
    }

    [Theory]
    [InlineData("1,2", true, false, 1, 2)]
    [InlineData("2,1", false, false, 2, 1)]
    [InlineData("2", false, false, 2)]
    [InlineData("1,2", true, true, 1, 2)]
    [InlineData("2", false, true, 2)]
    public async Task RecreatedPageReportsKeepTagsOnlyForTheOriginalSequence(string range, bool tagged, bool bound, params int[] pages)
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("input.pdf"), report = QpdfPath.From("report.json"), output = QpdfPath.From("output.pdf");
        host.AddInput(input, Fixture(), QpdfInputOwnership.Transfer);
        string[] source = [input.Value];
        if (bound)
        {
            QpdfExecutionResult seed = await Qpdf.ExecuteAsync(host,
                new([input.Value, "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, seed.ExitCode);
            host.AddInput(QpdfPath.From("source.json"), System.Text.Encoding.UTF8.GetBytes(Assert.IsType<string>(seed.OutputText)),
                QpdfInputOwnership.Transfer);
            source = ["source.json", "--json-input"];
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([.. source, "--pages", ".", range, "--", "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
        Assert.Equal(tagged ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(tagged ? 0 : 1, result.Diagnostics.Count(item => item.Code == QpdfDiagnosticCode.PageStructureWarning));
        host.AddInput(report, System.Text.Encoding.UTF8.GetBytes(result.OutputText ?? throw new InvalidOperationException()), QpdfInputOwnership.Transfer);
        await Qpdf.CreatePdfFromJsonAsync(host, report, output, CancellationToken.None);
        await Verify(host, output, tagged, true, pages);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task Verify(InMemoryQpdfHost host, QpdfPath output, bool active, bool hasPrivateRole, int[] pages)
    {
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        host.AddInput(output, bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, output, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Read(document, document.Engine.Trailer, "Root"));
        Assert.Equal(active, catalog.TryGet(PdfName.FromAscii("StructTreeRoot"), out _));
        Assert.Equal(active, catalog.TryGet(PdfName.FromAscii("MarkInfo"), out _));
        if (hasPrivateRole)
        {
            PdfDictionary audit = Assert.IsType<PdfDictionary>(Read(document, catalog, "Audit"));
            PdfDictionary originalTree = Assert.IsType<PdfDictionary>(Read(document, audit, "Backup"));
            Assert.Equal("StructTreeRoot", Assert.IsType<PdfName>(Read(document, originalTree, "Type")).DisplayText);
            Assert.Equal(3, Assert.IsType<PdfArray>(Read(document, originalTree, "K")).Count);
        }
        Assert.Equal(pages.Length, document.PageCount);
        for (int j = 0; j < pages.Length; j++)
        {
            PdfDictionary page = document.PageReferences[j].Page;
            PdfStream contents = Assert.IsType<PdfStream>(Read(document, page, "Contents"));
            Assert.Equal(pages[j] == 1 ? First : Second, StreamObservation.Decode(contents));
            PdfDictionary backup = Assert.IsType<PdfDictionary>(Read(document, page, "TagBackup"));
            Assert.Equal("StructTreeRoot", Assert.IsType<PdfName>(Read(document, backup, "Type")).DisplayText);
            if (active)
            {
                PdfDictionary tree = Assert.IsType<PdfDictionary>(Read(document, catalog, "StructTreeRoot"));
                PdfArray children = Assert.IsType<PdfArray>(Read(document, tree, "K"));
                PdfDictionary child = Assert.IsType<PdfDictionary>(Resolve(document, children[j]));
                Assert.Equal(document.PageReferences[j].Source, Get(child, "Pg"));
                Assert.Equal(j == 0 ? 0L : 7L, Assert.IsType<PdfInteger>(Read(document, page, "StructParents")).Value);
                PdfDictionary parentTree = Assert.IsType<PdfDictionary>(Read(document, tree, "ParentTree"));
                PdfArray entries = Assert.IsType<PdfArray>(Read(document, parentTree, "Nums"));
                Assert.Equal(j == 0 ? 0L : 7L, Assert.IsType<PdfInteger>(entries[2 * j]).Value);
                Assert.Equal(children[j], Assert.IsType<PdfArray>(entries[2 * j + 1])[0]);
                if (j == 1)
                {
                    QpdfObject annotation = Assert.IsType<PdfArray>(Read(document, page, "Annots"))[0];
                    PdfDictionary taggedAnnotation = Assert.IsType<PdfDictionary>(Resolve(document, annotation));
                    long parent = Assert.IsType<PdfInteger>(Read(document, taggedAnnotation, "StructParent")).Value;
                    Assert.Equal(parent, Assert.IsType<PdfInteger>(entries[4]).Value);
                    Assert.Equal(children[2], entries[5]);
                    PdfDictionary element = Assert.IsType<PdfDictionary>(Resolve(document, children[2]));
                    PdfDictionary objectRef = Assert.IsType<PdfDictionary>(Read(document, element, "K"));
                    Assert.Equal(annotation, Get(objectRef, "Obj"));
                }
            }
        }
    }

    private static byte[] Fixture() => Fixture("/StructTreeRoot 5 0 R /MarkInfo << /Marked true >>");

    private static byte[] Fixture(string roles) => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R " + roles + " /Audit << /Backup 5 0 R >> >>", null),
        (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /StructParents 0 /Contents 9 0 R /TagBackup 5 0 R >>", null),
        (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /StructParents 7 /Contents 10 0 R /TagBackup 5 0 R /Annots [11 0 R] >>", null),
        (5, "<< /Type /StructTreeRoot /K [6 0 R 7 0 R 12 0 R] /ParentTree 8 0 R /ParentTreeNextKey 10 >>", null),
        (6, "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K 0 >>", null),
        (7, "<< /Type /StructElem /S /P /P 5 0 R /Pg 4 0 R /K 0 >>", null),
        (8, "<< /Nums [0 [6 0 R] 7 [7 0 R] 9 12 0 R] >>", null),
        (9, $"<< /Length {First.Length} >>", First),
        (10, $"<< /Length {Second.Length} >>", Second),
        (11, "<< /Type /Annot /Subtype /Square /Rect [0 0 2 2] /P 4 0 R /StructParent 9 >>", null),
        (12, "<< /Type /StructElem /S /Annot /P 5 0 R /Pg 4 0 R /K << /Type /OBJR /Pg 4 0 R /Obj 11 0 R >> >>", null)], 1);

    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject raw)
        => raw is PdfReference reference ? document.GetObject(reference) : raw;
    private static QpdfObject Read(QpdfDocument document, PdfDictionary dictionary, string name)
        => Resolve(document, Get(dictionary, name));
}
