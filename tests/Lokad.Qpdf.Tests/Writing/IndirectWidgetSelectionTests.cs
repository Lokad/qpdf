using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class IndirectWidgetSelectionTests
{
    private static byte[] Fixture(bool merged, bool indirect)
    {
        string subtype = indirect ? "9 0 R" : "/Widget";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 6 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> /Annots [5 0 R] >>", null),
            (4, merged ? "<< /Fields [5 0 R 8 0 R] /CO [5 0 R 8 0 R] >>" : "<< /Fields [7 0 R] /CO [7 0 R] >>", null),
            (5, "<< /Subtype " + subtype + " /Rect [0 0 20 10] /P 3 0 R "
                + (merged ? "/FT /Tx /T (First)" : "/Parent 7 0 R") + " >>", null),
            (6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> /Annots [8 0 R] >>", null),
            (7, "<< /FT /Tx /T (Shared) /Kids [5 0 R 8 0 R] >>", null),
            (8, "<< /Subtype " + subtype + " /Rect [0 0 20 10] /P 6 0 R "
                + (merged ? "/FT /Tx /T (Second)" : "/Parent 7 0 R") + " >>", null),
            (9, "/Widget", null),
        ], 1);
    }

    private static QpdfObject Get(QpdfObject value, string key)
    {
        Assert.True(Assert.IsType<PdfDictionary>(value).TryGet(PdfName.FromAscii(key), out QpdfObject result));
        return result;
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
    {
        while (value is PdfReference reference) { value = document.GetObject(reference.ObjectNumber); }
        return value;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool merged in new[] { false, true })
        foreach (bool indirect in new[] { false, true })
        foreach (string selection in new[] { "one", "reverse", "duplicate" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [merged, indirect, selection, route]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SelectionKeepsResolvedWidgetsAndTheirFieldClosure(bool merged, bool indirect, string selection, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(merged, indirect), QpdfInputOwnership.Transfer);
        int[] pages = selection switch { "one" => [1], "reverse" => [2, 1], _ => [1, 1] };
        if (route == "typed")
        {
            await Qpdf.SelectPagesAsync(host, [new PdfPageSource(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages(pages))],
                QpdfPath.From("output"), CancellationToken.None);
        }
        else
        {
            string range = string.Join(",", pages);
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", range, "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output",
                    pages = new[] { new { file = ".", range } } }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject catalog = Resolve(document, Get(document.Engine.Trailer, "Root"));
        QpdfObject form = Resolve(document, Get(catalog, "AcroForm"));
        PdfArray fields = Assert.IsType<PdfArray>(Resolve(document, Get(form, "Fields")));
        Assert.Equal(merged || selection == "duplicate" ? pages.Length : 1, fields.Count);
        HashSet<int> fieldNumbers = [];
        for (int index = 0; index < fields.Count; index++)
        { fieldNumbers.Add(Assert.IsType<PdfReference>(fields[index]).ObjectNumber); }
        Assert.Equal(fields.Count, fieldNumbers.Count);
        PdfArray order = Assert.IsType<PdfArray>(Resolve(document, Get(form, "CO")));
        Assert.Equal(fields.Count, order.Count);
        for (int index = 0; index < order.Count; index++)
        { Assert.Contains(Assert.IsType<PdfReference>(order[index]).ObjectNumber, fieldNumbers); }
        Assert.Equal(pages.Length, document.PageCount);
        for (int index = 0; index < document.PageCount; index++)
        {
            QpdfObject page = document.PageReferences[index].Page;
            PdfArray annotations = Assert.IsType<PdfArray>(Resolve(document, Get(page, "Annots")));
            Assert.Equal(1, annotations.Count);
            PdfReference widgetReference = Assert.IsType<PdfReference>(annotations[0]);
            QpdfObject widget = Resolve(document, widgetReference);
            Assert.True(Assert.IsType<PdfName>(Resolve(document, Get(widget, "Subtype"))).RawSpan.SequenceEqual("Widget"u8));
            int fieldNumber = merged ? widgetReference.ObjectNumber : Assert.IsType<PdfReference>(Get(widget, "Parent")).ObjectNumber;
            Assert.Contains(fieldNumber, fieldNumbers);
            if (!merged)
            {
                PdfArray kids = Assert.IsType<PdfArray>(Resolve(document, Get(Resolve(document, new PdfReference(fieldNumber, 0)), "Kids")));
                bool found = false;
                for (int child = 0; child < kids.Count; child++)
                { found |= kids[child] is PdfReference reference && reference.ObjectNumber == widgetReference.ObjectNumber; }
                Assert.True(found);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PoolPruningResolvesRootAndDescendantWidgetSubtypes(bool merged, bool indirect)
    {
        PdfDictionary widget = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Subtype")] = indirect ? new PdfReference(9, 0) : PdfName.FromAscii("Widget") });
        PdfDictionary field = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Kids")] = new PdfArray([new PdfReference(2, 0)]) });
        Dictionary<int, QpdfObject> pool = new() { [1] = merged ? widget : field, [2] = widget, [9] = PdfName.FromAscii("Widget") };
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            (List<PdfReference> kept, HashSet<int> closure) = AcroFormCopy.CollectKeptClosure(pool, [new(1, 0)],
                [merged ? 1 : 2], true, QpdfLimits.Default, governor, memory, CancellationToken.None);
            Assert.Equal(1, Assert.Single(kept).ObjectNumber); Assert.Contains(1, closure);
            if (!merged) { Assert.Contains(2, closure); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
