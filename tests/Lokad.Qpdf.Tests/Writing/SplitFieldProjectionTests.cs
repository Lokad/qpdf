using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SplitFieldProjectionTests
{
    private static byte[] CreateSource() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /CO [6 0 R 7 0 R] >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 80 80] /Resources << >> >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /Annots [8 0 R] /AuditField 5 0 R >>", null),
        (4, "<< /Type /Page /Parent 2 0 R /Annots [9 0 R] /AuditField 5 0 R >>", null),
        (5, "<< /T (Root) /Kids [6 0 R 7 0 R] >>", null),
        (6, "<< /Parent 5 0 R /FT /Tx /T (Left) /V (left value) /DV (Left default) /Kids [8 0 R] >>", null),
        (7, "<< /Parent 5 0 R /FT /Tx /T (Right) /V (right value) /DV (Right default) /Kids [9 0 R] >>", null),
        (8, "<< /Subtype /Widget /Rect [10 10 30 30] /Parent 6 0 R /P 3 0 R >>", null),
        (9, "<< /Subtype /Widget /Rect [10 10 30 30] /Parent 7 0 R >>", null)], 1);

    [Fact]
    public async Task TypedSelectionProjectsTheKeptBranchAndItsCalculationField()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("selected");
        host.AddInput(input, CreateSource(), QpdfInputOwnership.Transfer);
        await Qpdf.SelectPageAsync(host, input, output, new PdfPageSelectionOptions(2), CancellationToken.None);
        await CheckPart(host, output.Value, "Right", "right value", false);
    }

    [Fact]
    public async Task SelectedPdfReportProjectsTheKeptBranch()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), CreateSource(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host,
            new(["input", "--pages", ".", "2", "--", "--json-output=2", "graph"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, report.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("graph"), out byte[] json));
        host.AddInput(QpdfPath.From("bound"), json, QpdfInputOwnership.Transfer);
        QpdfExecutionResult written = await Qpdf.ExecuteAsync(host,
            new(["bound", "--json-input", "selected"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, written.ExitCode);
        await CheckPart(host, "selected", "Right", "right value", false);
    }

    [Fact]
    public async Task PdfSplitBuildsIndependentActiveFieldsAndLeavesPrivateChildren()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), CreateSource(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--split-pages=1", "part.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await CheckPart(host, "part-1.pdf", "Left", "left value", false);
        await CheckPart(host, "part-2.pdf", "Right", "right value", false);
    }

    [Fact]
    public async Task BoundJsonSplitUsesTheSameProjectedWidgetAndCalculationField()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("input"), graph = QpdfPath.From("graph");
        host.AddInput(input, CreateSource(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult exported = await Qpdf.ExecuteAsync(host,
            new(["input", "--json-output=2", "export"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, exported.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("export"), out byte[] json));
        host.AddInput(graph, json, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--split-pages=1", "--object-streams=generate", "part.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await CheckPart(host, "part-1.pdf", "Left", "left value", true);
        await CheckPart(host, "part-2.pdf", "Right", "right value", true);
    }

    private static async Task CheckPart(InMemoryQpdfHost host, string target, string name, string value, bool packed)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(target), out byte[] bytes));
        QpdfPath read = QpdfPath.From("read-" + target);
        host.AddInput(read, bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
        QpdfObject Resolve(QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference) : raw;
        PdfDictionary Dictionary(QpdfObject raw) => Assert.IsType<PdfDictionary>(Resolve(raw));
        QpdfObject[] Array(QpdfObject raw) => Assert.IsType<PdfArray>(Resolve(raw)).Items;
        static string Text(QpdfObject raw) => System.Text.Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(raw).ValueSpan);

        var location = Assert.Single(document.PageReferences);
        PdfDictionary catalog = Dictionary(Get(document.Engine.Trailer, "Root"));
        PdfDictionary form = Dictionary(Get(catalog, "AcroForm"));
        PdfReference root = Assert.IsType<PdfReference>(Assert.Single(Array(Get(form, "Fields"))));
        PdfReference leaf = Assert.IsType<PdfReference>(Assert.Single(Array(Get(Dictionary(root), "Kids"))));
        Assert.Equal(leaf, Assert.Single(Array(Get(form, "CO"))));
        Assert.Equal(root, Get(Dictionary(leaf), "Parent"));
        Assert.Equal(name, Text(Get(Dictionary(leaf), "T")));
        Assert.Equal(value, Text(Get(Dictionary(leaf), "V")));
        Assert.Equal(name + " default", Text(Get(Dictionary(leaf), "DV")));
        PdfReference widget = Assert.IsType<PdfReference>(Assert.Single(Array(Get(Dictionary(leaf), "Kids"))));
        if (packed)
        {
            Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[leaf.ObjectNumber].Kind);
            Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[widget.ObjectNumber].Kind);
        }
        Assert.Equal(widget, Assert.Single(Array(Get(location.Page, "Annots"))));
        Assert.Equal(leaf, Get(Dictionary(widget), "Parent"));
        Assert.Equal(location.Source, Get(Dictionary(widget), "P"));

        QpdfObject original = Get(location.Page, "AuditField");
        Assert.NotEqual(root, original);
        QpdfObject[] originals = Array(Get(Dictionary(original), "Kids"));
        Assert.Equal(new[] { "Left", "Right" }, originals.Select(child => Text(Get(Dictionary(child), "T"))));
        foreach (QpdfObject child in originals)
        {
            Assert.Equal(original, Get(Dictionary(child), "Parent"));
            QpdfObject originalWidget = Assert.Single(Array(Get(Dictionary(child), "Kids")));
            Assert.Equal(child, Get(Dictionary(originalWidget), "Parent"));
        }
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }
}
