using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedStreamedPartsTests
{
    private static PdfName N(string text)
    {
        return PdfName.FromAscii(text);
    }

    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
    {
        Dictionary<PdfName, QpdfObject> map = new();
        foreach ((string key, QpdfObject value) in entries)
        {
            map[N(key)] = value;
        }

        return new PdfDictionary(map);
    }

    private static PdfReference Ref(int number)
    {
        return new PdfReference(number, 0);
    }

    internal static List<NumberedObject> OnePageModel()
    {
        byte[] content = System.Text.Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
        return new List<NumberedObject>
        {
            new(1, Dict(("Type", N("Catalog")), ("Pages", Ref(2)))),
            new(2, Dict(("Type", N("Pages")), ("Kids", new PdfArray([Ref(3)])), ("Count", new PdfInteger("1", 1)))),
            new(3, Dict(("Type", N("Page")), ("Parent", Ref(2)), ("Contents", Ref(4)), ("Resources", Dict(("Font", Dict(("F1", Ref(5)))))))),
            new(4, new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), content)),
            new(5, Dict(("Type", N("Font")), ("BaseFont", N("Helvetica")))),
        };
    }

    private static ResourceGovernor Governor()
    {
        return new ResourceGovernor(QpdfLimits.Default, CancellationToken.None);
    }

    [Fact]
    public void ContainerLandsInFirstPagePart()
    {
        ResourceGovernor governor = Governor();
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using StreamedParts parts = LinearizedParts.ClassifyStreamed(
            OnePageModel(),
            new List<IReadOnlyList<int>> { new List<int> { 5 } },
            memory,
            governor,
            CancellationToken.None);
        int container = Assert.Single(parts.Containers);
        Assert.Equal(new List<int> { 3, 4, container }, parts.Part6);
        Assert.Equal(new List<int> { 2 }, parts.Part9);
        Assert.Equal(7, parts.Renumber[3]);
        Assert.Equal(8, parts.Renumber[4]);
        Assert.Equal(9, parts.Renumber[container]);
        Assert.Equal(10, parts.Renumber[5]);
        Assert.Equal(9, parts.Members[5].Container);
        Assert.Equal(0, parts.Members[5].Index);
        Assert.Equal(2, parts.XrefMainNumber);
        Assert.Equal(3, parts.LindictNumber);
        Assert.Equal(4, parts.XrefFirstNumber);
        Assert.Equal(6, parts.HintNumber);
        Assert.Equal(3, parts.MainSize);
        Assert.Equal(11, parts.FirstSize);
        Assert.Equal(10, parts.MaxNumber);
        Assert.Equal(0, parts.MaxMemberIndex);
        Assert.Empty(parts.Outlines);
    }

    [Fact]
    public void PagesAndCatalogStayDirect()
    {
        ResourceGovernor governor = Governor();
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using StreamedParts parts = LinearizedParts.ClassifyStreamed(
            OnePageModel(),
            new List<IReadOnlyList<int>> { new List<int> { 1, 2, 3, 5 } },
            memory,
            governor,
            CancellationToken.None);
        int container = Assert.Single(parts.Containers);
        Assert.Equal(new List<int> { 5 }, parts.ContainerMembers[container]);
        Assert.Contains(3, parts.Part6);
        Assert.Contains(container, parts.Part6);
        Assert.DoesNotContain(1, parts.ContainerMembers[container]);
    }

    [Fact]
    public void EmptyGroupsYieldNoContainers()
    {
        ResourceGovernor governor = Governor();
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using StreamedParts parts = LinearizedParts.ClassifyStreamed(
            OnePageModel(),
            new List<IReadOnlyList<int>>(),
            memory,
            governor,
            CancellationToken.None);
        Assert.Empty(parts.Containers);
        Assert.Empty(parts.Members);
        Assert.Equal(new List<int> { 3, 4, 5 }, parts.Part6);
    }

    [Fact]
    public void ThumbnailRunsKeepUniqueAndSharedDependenciesInPhysicalOrder()
    {
        List<NumberedObject> model =
        [
            new(1, Dict(("Type", N("Catalog")), ("Pages", Ref(2)), ("ViewerPreferences", Ref(13)), ("Outlines", Ref(14)))),
            new(2, Dict(("Type", N("Pages")), ("Kids", new PdfArray([Ref(3), Ref(4), Ref(5), Ref(6), Ref(7), Ref(8)])))),
            new(3, Dict(("Type", N("Page")), ("Parent", Ref(2)), ("Resources", Dict(("Private", Ref(12)))))),
            new(4, Dict(("Type", N("Page")), ("Parent", Ref(2)), ("Thumb", Ref(16)))),
            new(5, Dict(("Type", N("Page")), ("Parent", Ref(2)), ("Thumb", Ref(17)))),
            new(6, Dict(("Type", N("Page")), ("Parent", Ref(2)), ("Thumb", Ref(17)))),
            new(7, Dict(("Type", N("Page")), ("Parent", Ref(2)), ("Thumb", Ref(19)))),
            new(8, Dict(("Type", N("Page")), ("Parent", Ref(2)), ("Thumb", Ref(20)))),
            new(11, Dict()),
            new(12, Dict()),
            new(13, Dict()),
            new(14, Dict(("Type", N("Outlines")))),
            new(16, new PdfStream(Dict(("Private", new PdfArray([Ref(11), Ref(12), Ref(13), Ref(2), Ref(14), Ref(18)]))), [10])),
            new(17, new PdfStream(Dict(("Private", Ref(18))), [11])),
            new(18, Dict()),
            new(19, PdfNull.Instance),
        ];
        ResourceGovernor governor = Governor();
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using LinearizedParts classic = LinearizedParts.Classify(model, memory, governor, CancellationToken.None);
        Assert.Equal(new[] { 1, 13 }, classic.Part4);
        Assert.Equal(new[] { 3, 12 }, classic.Part6);
        Assert.Equal(new[] { 2, 16, 11, 17, 18, 14, 19 }, classic.Part9);

        using StreamedParts streamed = LinearizedParts.ClassifyStreamed(
            model, [new[] { 11 }], memory, governor, CancellationToken.None);
        int container = Assert.Single(streamed.Containers);
        Assert.Equal(streamed.Renumber[container], streamed.Members[11].Container);
        Assert.Equal(classic.Part4, streamed.Part4);
        Assert.Equal(classic.Part6, streamed.Part6);
        Assert.Equal(new[] { 2, 16, container, 17, 18, 14, 19 }, streamed.Part9);
    }

    [Fact]
    public void PagesWithoutThumbnailsScaleWithPageTreeSize()
    {
        long small = WorkForPageTree(128);
        long large = WorkForPageTree(1024);
        Assert.True(large < 12 * small, $"Work notifications for 128 pages: {small}. For 1,024 pages: {large}.");
    }

    private static long WorkForPageTree(int count)
    {
        List<NumberedObject> model = [new(1, Dict(("Type", N("Catalog")), ("Pages", Ref(2))))];
        QpdfObject[] kids = new QpdfObject[count];
        for (int index = 0; index < count; index++)
        {
            int branch = 3 + 2 * index;
            int page = branch + 1;
            kids[index] = Ref(branch);
            model.Add(new(branch, Dict(("Type", N("Pages")), ("Parent", Ref(2)), ("Kids", new PdfArray([Ref(page)])))));
            model.Add(new(page, Dict(("Type", N("Page")), ("Parent", Ref(branch)))));
        }
        model.Add(new(2, Dict(("Type", N("Pages")), ("Kids", new PdfArray(kids)))));

        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = long.MaxValue };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using LinearizedParts parts = LinearizedParts.Classify(model, memory, governor, CancellationToken.None);
        Assert.Equal(count, parts.Pages.Count);
        Assert.Equal(Enumerable.Range(0, count).Select(index => 4 + 2 * index), parts.Pages);
        Assert.Equal(new[] { 2 }.Concat(Enumerable.Range(0, count).Select(index => 3 + 2 * index)), parts.Part9);
        FieldInfo work = typeof(ResourceGovernor).GetField("_operationsSinceCheck", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing work observation.");
        return Assert.IsType<long>(work.GetValue(governor));
    }
}
