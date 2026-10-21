using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonNameTests
{
    [Theory]
    [InlineData("C3A9", true)]
    [InlineData("ED9FBF", true)]
    [InlineData("EE8080", true)]
    [InlineData("F48FBFBF", true)]
    [InlineData("EDA080", false)]
    [InlineData("EDBFBF", false)]
    [InlineData("F4908080", false)]
    [InlineData("F5808080", false)]
    [InlineData("F7808080", false)]
    [InlineData("F0808080", false)]
    [InlineData("C0AF", false)]
    [InlineData("E282", false)]
    public async Task ExportCreateAndUpdatePreserveRawNameBytes(string hex, bool unicode)
    {
        byte[] raw = Convert.FromHexString(hex);
        string name = "/" + string.Concat(raw.Select(value => "#" + value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + name + " 42 /Audit " + name + " >>", null),
            (2, "<< /Type /Pages /Kids [] /Count 0 >>", null)], 1);
        using InMemoryQpdfHost source = new(); source.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
        QpdfJsonResult exported = await Qpdf.GetDocumentJsonAsync(source, QpdfPath.From("input"), QpdfPath.From("json"), new QpdfJsonOptions(2, ["qpdf"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(exported.Json);
        JsonProperty property = json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:1 0 R").GetProperty("value").EnumerateObject().Single(p => p.Value.ValueKind == JsonValueKind.Number);
        Assert.StartsWith(unicode ? "/" : "n:/", property.Name);
        Assert.Equal(property.Name, json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:1 0 R").GetProperty("value").GetProperty("/Audit").GetString());
        foreach (bool update in new[] { false, true })
        {
            using InMemoryQpdfHost host = new();
            host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
            host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(exported.Json), QpdfInputOwnership.Transfer);
            if (update) { await Qpdf.UpdatePdfFromJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
            else { await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
            Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] pdf));
            host.AddInput(QpdfPath.From("again"), pdf, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            PdfName key = Assert.Single(catalog.Keys, k => catalog.TryGet(k, out QpdfObject value) && value is PdfInteger);
            Assert.Equal(raw, key.RawBytes);
            Assert.True(catalog.TryGet(new PdfName("Audit"u8.ToArray()), out QpdfObject audit));
            Assert.Equal(raw, Assert.IsType<PdfName>(audit).RawBytes);
        }
    }
}
