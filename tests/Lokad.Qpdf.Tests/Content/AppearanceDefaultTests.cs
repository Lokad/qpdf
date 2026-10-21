using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceDefaultTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/Resources << >>")]
    [InlineData("/Resources 11 0 R")]
    [InlineData("/Resources 8 0 R")]
    public async Task CatalogDefaultSuppliesTheProgramAndMissingFont(string resources)
    {
        foreach (bool job in new[] { false, true })
        foreach (bool selection in new[] { false, true })
        foreach (string appearance in new[] { "/DA (/H#65lv 17 Tf)", "/DA 7 0 R" })
        {
            byte[] input = AppearanceProgramTests.Fixture("/Tx BMC 0 0 10 10 re f EMC", "", appearance, resources);
            var generated = await AppearanceProgramTests.Generate(input, job, selection); Assert.Equal(QpdfExitCode.Success, generated.Result.ExitCode);
            PdfStream stream = await AppearanceProgramTests.ReadAppearance(generated.Pdf);
            Assert.Contains("/H#65lv 17 Tf", Encoding.ASCII.GetString(stream.RawMemory.Span)); Assert.Equal("Helvetica", await FontName(generated.Pdf));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldProgramAndLocalFontOverrideCatalogDefaults(bool job)
    {
        byte[] input = AppearanceProgramTests.Fixture("/Tx BMC 0 0 10 10 re f EMC", "/DA (/Helv 11 Tf)", "/DA (/Helv 17 Tf)", "/Resources << /Font << /Helv 10 0 R >> >>");
        var generated = await AppearanceProgramTests.Generate(input, job, false); Assert.Equal(QpdfExitCode.Success, generated.Result.ExitCode);
        string text = Encoding.ASCII.GetString((await AppearanceProgramTests.ReadAppearance(generated.Pdf)).RawMemory.Span);
        Assert.Contains("/Helv 11 Tf", text); Assert.DoesNotContain("17 Tf", text); Assert.Equal("Courier", await FontName(generated.Pdf));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/DA (0 g)")]
    [InlineData("/DA 11 0 R")]
    public async Task UnusableDefaultWarnsAndPreservesTheAppearance(string defaults)
    {
        const string original = "/Tx BMC 0 0 10 10 re f EMC";
        foreach (bool job in new[] { false, true })
        {
            var generated = await AppearanceProgramTests.Generate(AppearanceProgramTests.Fixture(original, "", defaults, ""), job, false);
            Assert.Equal(QpdfExitCode.Warning, generated.Result.ExitCode);
            Assert.Contains(generated.Result.Diagnostics, d => d.Message.Contains("usable default appearance", StringComparison.Ordinal));
            Assert.Equal(Encoding.ASCII.GetBytes(original), (await AppearanceProgramTests.ReadAppearance(generated.Pdf)).RawMemory.ToArray());
        }
    }

    [Theory]
    [InlineData("/Resources 42")]
    [InlineData("/Resources 7 0 R")]
    public async Task UnusableResourcesWarnAndKeepTheOriginal(string resources)
    {
        const string original = "/Tx BMC 0 0 10 10 re f EMC";
        var generated = await AppearanceProgramTests.Generate(AppearanceProgramTests.Fixture(original, "", "/DA (/Helv 17 Tf)", resources), false, false);
        Assert.Equal(QpdfExitCode.Warning, generated.Result.ExitCode);
        Assert.Contains(generated.Result.Diagnostics, d => d.Message.Contains("appearance resources", StringComparison.Ordinal));
        Assert.Equal(Encoding.ASCII.GetBytes(original), (await AppearanceProgramTests.ReadAppearance(generated.Pdf)).RawMemory.ToArray());
    }

    private static async Task<string> FontName(byte[] pdf)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfObject Get(PdfDictionary dictionary, string key)
        {
            Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
            return value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
        }
        PdfArray annotations = Assert.IsType<PdfArray>(Get(document.PageReferences[0].Page, "Annots"));
        QpdfObject raw = annotations[0]; PdfDictionary widget = Assert.IsType<PdfDictionary>(raw is PdfReference link ? document.GetObject(link.ObjectNumber) : raw);
        PdfStream stream = Assert.IsType<PdfStream>(Get(Assert.IsType<PdfDictionary>(Get(widget, "AP")), "N"));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(stream.Dictionary, "Resources"));
        PdfDictionary font = Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(resources, "Font")), "Helv"));
        return Assert.IsType<PdfName>(Get(font, "BaseFont")).DisplayText;
    }
}
