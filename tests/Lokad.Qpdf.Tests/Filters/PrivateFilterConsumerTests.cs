using System.Text.Json;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class PrivateFilterConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedPrivateConsumersRefundUnknownNames(bool image)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        byte[] raw = [1, 2, 3];
        PdfStream stream = PdfStream.FromOwnedData(new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Width")] = new PdfInteger("128", 128),
            [PdfName.FromAscii("Height")] = new PdfInteger("128", 128),
            [PdfName.FromAscii("BitsPerComponent")] = new PdfInteger("8", 8),
            [PdfName.FromAscii("ColorSpace")] = PdfName.FromAscii("DeviceGray"),
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii(new string('Z', 8192)),
        }), raw);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        for (int index = 0; index < 32; index++)
        {
            if (image)
            { Assert.Null(ImageOptimizer.TryOptimize(stream, new(true, 0, 0, 0, false, null), new(new Dictionary<int, QpdfObject>()), limits, governor, diagnostics, CancellationToken.None)); }
            else
            {
                using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
                ReadOnlyMemory<byte> result = EmbeddedFiles.ExtractData(stream, 5, document.Engine, limits, governor, diagnostics, memory, CancellationToken.None);
                Assert.Equal(raw, result.ToArray());
            }
        }
        Assert.Same(raw, stream.AllocationCarrier);
        if (image) { Assert.Empty(diagnostics.Entries); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); }
        else
        {
            Assert.Equal(32, diagnostics.Entries.Count);
            Assert.All(diagnostics.Entries, entry => Assert.Contains("extracted raw", entry.Message, StringComparison.Ordinal));
            Assert.InRange(governor.Snapshot().LiveMemoryBytes, 1L, 16384L);
        }
        governor.CompleteStorage();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DescriptorKeepsCopiedFullNameAfterThePrivatePlanAndReaderClose(int version)
    {
        string name = new('Z', 8192); byte[] json;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), ImageInspectionTests.Fixture("/Image", "/Image", "/Im0 5 0 R",
                "/Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /" + name), QpdfInputOwnership.Transfer);
            await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(version, ["pages"]), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out json)); Assert.Equal(0L, host.OpenLeaseCount);
        }
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement image = parsed.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        Assert.Equal("/" + name, image.GetProperty("filter")[0].GetString());
        Assert.False(image.GetProperty("filterable").GetBoolean());
    }
}
