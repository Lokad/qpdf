using System.Globalization;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedThumbnailWorkTests
{
    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private static long Classify(int pages)
    {
        int font = 3 + 2 * pages;
        List<NumberedObject> model =
        [
            new(1, Dict(("Type", PdfName.FromAscii("Catalog")), ("Pages", new PdfReference(2, 0)))),
            new(2, Dict(("Type", PdfName.FromAscii("Pages")),
                ("Kids", new PdfArray(Enumerable.Range(0, pages).Select(i => (QpdfObject)new PdfReference(3 + 2 * i, 0)).ToArray())),
                ("Count", new PdfInteger(pages.ToString(CultureInfo.InvariantCulture), pages)))),
            new(font, Dict(("Type", PdfName.FromAscii("Font")), ("BaseFont", PdfName.FromAscii("Helvetica")))),
        ];
        for (int i = 0; i < pages; i++)
        {
            model.Add(new(3 + 2 * i, Dict(("Type", PdfName.FromAscii("Page")),
                ("Parent", new PdfReference(2, 0)), ("Contents", new PdfReference(4 + 2 * i, 0)),
                ("Resources", Dict(("Font", Dict(("F1", new PdfReference(font, 0)))))))));
            model.Add(new(4 + 2 * i, new PdfStream(Dict(), "BT (Hi) Tj ET"u8.ToArray())));
        }
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using ClassifiedGraph graph = LinearizedParts.ClassifyGraph(
            model.ToDictionary(item => item.Number, item => item.Value), 1, memory, governor, CancellationToken.None);
        Assert.Equal(pages, graph.Pages.Count);
        Assert.Equal(new[] { 2 }, graph.Part9);
        Assert.Equal(pages - 1, graph.Part7.Count);
        return governor.Snapshot().TotalWork;
    }

    [Fact]
    public void PagesWithoutThumbnailsDoNotMultiplyAllPageResourceVisits()
    {
        long smaller = Classify(250);
        long larger = Classify(500);
        // Broad growth guard for the former page-by-page thumbnail scan. This
        // concerns work, not wall-clock speed or exact accounting geometry.
        Assert.True(larger < 3 * smaller, $"Doubling pages grew classification work from {smaller} to {larger}.");
    }
}
