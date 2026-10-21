using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayResourceNamespaceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PlacementNamesUseTheFreshPageNamespace(bool job, bool largeName)
    {
        string name = largeName ? new string('A', 262144) : "Existing";
        byte[] content = "/Fx1 Do /Fx2 Do"u8.ToArray();
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Fx1 5 0 R /Fx2 5 0 R /" + name + " 5 0 R >> >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + content.Length + " >>", content),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 3 >>", "q Q"u8.ToArray())], 1);
        byte[] stamp = CheckTests.Assemble(CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1);
        PdfStream escaped;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer); host.AddInput(QpdfPath.From("stamp"), stamp, QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","compressStreams":"n","underlay":[{"file":"stamp"}],"overlay":[{"file":"stamp"}]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--underlay", "stamp", "--", "--overlay", "stamp", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
            PdfDictionary page = document.PageReferences[0].Page;
            PdfDictionary top = Assert.IsType<PdfDictionary>(Get(document, Assert.IsType<PdfDictionary>(Get(document, page, "Resources")), "XObject"));
            Assert.Equal(3, top.Count); Assert.IsType<PdfStream>(Get(document, top, "Fx1")); Assert.IsType<PdfStream>(Get(document, top, "Fx2"));
            escaped = Assert.IsType<PdfStream>(Get(document, top, "Fx0"));
            PdfDictionary nested = Assert.IsType<PdfDictionary>(Get(document, Assert.IsType<PdfDictionary>(Get(document, escaped.Dictionary, "Resources")), "XObject"));
            Assert.Equal(3, nested.Count); Assert.IsType<PdfStream>(Get(document, nested, name));
            Assert.Same(Get(document, nested, "Fx1"), Get(document, nested, "Fx2"));
            string stacked = Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Get(document, page, "Contents")).RawMemory.Span);
            Assert.True(stacked.IndexOf("/Fx1 Do", StringComparison.Ordinal) < stacked.IndexOf("/Fx0 Do", StringComparison.Ordinal));
            Assert.True(stacked.IndexOf("/Fx0 Do", StringComparison.Ordinal) < stacked.IndexOf("/Fx2 Do", StringComparison.Ordinal));
        }
        Assert.Equal(content, escaped.RawMemory.ToArray());
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }
}
