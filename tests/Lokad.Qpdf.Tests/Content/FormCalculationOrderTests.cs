using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormCalculationOrderTests
{
    private static byte[] Fixture(bool indirect, string order)
    {
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] /CO " + (indirect ? "9 0 R" : order) + " >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [5 0 R] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [6 0 R] >>", null),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (A) /V (1) /P 3 0 R /Rect [10 10 20 20] >>", null),
            (6, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (B) /V (2) /P 4 0 R /Rect [10 10 20 20] >>", null),
            (7, "null", null), (8, "null", null), (9, order, null)], 1);
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value; }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool primary in new[] { false, true })
        foreach (bool secondary in new[] { false, true })
        foreach (bool empty in new[] { false, true })
        foreach (string operation in new[] { "whole", "select", "merge", "duplicate", "split" })
        foreach (bool job in new[] { false, true }) { yield return [primary, secondary, empty, operation, job]; }
        foreach (bool primary in new[] { false, true })
        foreach (bool secondary in new[] { false, true })
        foreach (string operation in new[] { "merge-primary-empty", "merge-secondary-empty" })
        foreach (bool job in new[] { false, true }) { yield return [primary, secondary, false, operation, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CalculationOrderUsesTheSameMergeAndClosureForDirectAndIndirectArrays(bool primary, bool secondary, bool empty, string operation, bool job)
    {
        List<PdfArray> escaped = [];
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(primary, empty || operation == "merge-primary-empty" ? "[]" : "[6 0 R 5 0 R]"), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("other"), Fixture(secondary, empty || operation == "merge-secondary-empty" ? "[]" : "[6 0 R 5 0 R]"), QpdfInputOwnership.Transfer);
            List<string> argv = ["input"];
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
            bool merging = operation.StartsWith("merge", StringComparison.Ordinal);
            if (merging || operation is "select" or "duplicate")
            {
                string range = operation == "select" ? "1" : operation == "duplicate" ? "1,1,2" : "1-z";
                argv.AddRange(["--pages", ".", range]);
                List<Dictionary<string, string>> pages = [new() { ["file"] = ".", ["range"] = range }];
                if (merging) { argv.AddRange(["other", "1-z"]); pages.Add(new() { ["file"] = "other", ["range"] = "1-z" }); }
                argv.Add("--"); settings["pages"] = pages;
            }
            if (operation == "duplicate") { argv.AddRange(["--rotate=90:1", "--flatten-rotation"]); settings["rotate"] = new[] { "90:1" }; settings["flattenRotation"] = true; }
            if (operation == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
            argv.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
            string[] paths = operation == "split" ? ["output-1", "output-2"] : ["output"];
            for (int index = 0; index < paths.Length; index++)
            {
                QpdfPath read = QpdfPath.From("read-" + index);
                Assert.True(host.TryGetOutput(QpdfPath.From(paths[index]), out byte[] bytes)); host.AddInput(read, bytes, QpdfInputOwnership.Transfer);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document, document.Engine.Trailer, "Root")), form = Assert.IsType<PdfDictionary>(Get(document, catalog, "AcroForm"));
                PdfArray fields = Assert.IsType<PdfArray>(Get(document, form, "Fields")), order = Assert.IsType<PdfArray>(Get(document, form, "CO"));
                List<string> names = [];
                for (int entry = 0; entry < order.Count; entry++)
                {
                    PdfReference reference = Assert.IsType<PdfReference>(order[entry]);
                    Assert.Contains(reference, fields.BorrowedItems);
                    PdfDictionary field = Assert.IsType<PdfDictionary>(document.GetObject(reference.ObjectNumber));
                    names.Add(Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(document, field, "T")).Value));
                }
                string[] expected = empty ? [] : operation switch
                {
                    "select" => ["A"], "split" => [index == 0 ? "A" : "B"],
                    "merge" => ["B", "A", "B+1", "A+1"], "merge-primary-empty" => ["B+1", "A+1"],
                    "duplicate" => ["B", "A", "A+1"], _ => ["B", "A"],
                };
                Assert.Equal(expected, names); escaped.Add(order);
            }
        }
        foreach (PdfArray array in escaped)
        { for (int index = 0; index < array.Count; index++) { Assert.True(Assert.IsType<PdfReference>(array[index]).ObjectNumber > 0); } }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task TypedMergeFiltersBothOrdersThroughTheSurvivingFields(bool primary, bool secondary, bool empty)
    {
        using InMemoryQpdfHost host = new(); string sourceOrder = empty ? "[]" : "[6 0 R 5 0 R]";
        host.AddInput(QpdfPath.From("input"), Fixture(primary, sourceOrder), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("other"), Fixture(secondary, sourceOrder), QpdfInputOwnership.Transfer);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
            [new(QpdfPath.From("input"), new(1)), new(QpdfPath.From("other"), new(1))], QpdfPath.From("output"), CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document, document.Engine.Trailer, "Root")), form = Assert.IsType<PdfDictionary>(Get(document, catalog, "AcroForm"));
        PdfArray order = Assert.IsType<PdfArray>(Get(document, form, "CO")); Assert.Equal(empty ? 0 : 2, order.Count);
        if (!empty)
        {
            for (int index = 0; index < 2; index++)
            {
                PdfDictionary field = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(order[index]).ObjectNumber));
                Assert.Equal(index == 0 ? "A" : "A+1", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(document, field, "T")).Value));
            }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task NonArrayCalculationOrderKeepsItsOpaqueValue(bool indirect, bool dictionary, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(indirect, dictionary ? "<< /Marker (opaque) >>" : "17"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document, document.Engine.Trailer, "Root")), form = Assert.IsType<PdfDictionary>(Get(document, catalog, "AcroForm"));
        QpdfObject order = Get(document, form, "CO");
        if (dictionary)
        { Assert.Equal("opaque", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(document, Assert.IsType<PdfDictionary>(order), "Marker")).Value)); }
        else { Assert.Equal(17, Assert.IsType<PdfInteger>(order).Value); }
    }
}
