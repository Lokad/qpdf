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
        Assert.True(parts.OutlinesDirect);
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
}
