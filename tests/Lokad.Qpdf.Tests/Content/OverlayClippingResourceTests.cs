using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayClippingResourceTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string mode in new[] { "absent", "null", "direct", "indirect" })
        foreach (bool inside in new[] { false, true }) { yield return [mode, inside]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task WrapperNamesCannotSupplyMissingSourceResources(string mode, bool inside)
    {
        byte[] program = "/Original Do"u8.ToArray(); byte[] leaf = "1 0 0 rg 0 0 40 40 re f"u8.ToArray();
        string resources = "<< /XObject << /Original 6 0 R >> >>";
        string pageResources = mode switch { "absent" => "", "null" => "/Resources null", "direct" => "/Resources " + resources, _ => "/Resources 7 0 R" };
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " + pageResources + " /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect " + (inside ? "[20 20 60 60]" : "[-20 20 20 60]") + " /AP << /N 5 0 R >> /Private 5 0 R >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 40 40] /Length " + program.Length + " >>", program),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 40 40] /Resources << >> /Length " + leaf.Length + " >>", leaf),
            (7, resources, null)], 1);
        RecordingMemoryBudget budget = new(8L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
            host.AddInput(QpdfPath.From("input"), OverlayAnnotationClippingTests.Target(), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--", "--compress-streams=n", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
            {
                PdfArray annotations = Assert.IsType<PdfArray>(Value(document, document.PageReferences[0].Page, "Annots"));
                PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
                PdfDictionary appearances = Assert.IsType<PdfDictionary>(Value(document, annotation, "AP"));
                PdfStream original = Assert.IsType<PdfStream>(Value(document, appearances, "N"));
                if (!inside)
                {
                    PdfDictionary wrapperResources = Assert.IsType<PdfDictionary>(Value(document, original.Dictionary, "Resources"));
                    PdfDictionary wrapperObjects = Assert.IsType<PdfDictionary>(Value(document, wrapperResources, "XObject"));
                    original = Assert.IsType<PdfStream>(Value(document, wrapperObjects, "Original"));
                }
                Assert.Equal(program, original.CopyData(owner, CancellationToken.None));
                PdfDictionary originalResources = Assert.IsType<PdfDictionary>(Value(document, original.Dictionary, "Resources"));
                if (mode is "absent" or "null") { Assert.Equal(0, originalResources.Count); }
                else
                {
                    PdfDictionary sourceObjects = Assert.IsType<PdfDictionary>(Value(document, originalResources, "XObject"));
                    PdfStream sourceLeaf = Assert.IsType<PdfStream>(Value(document, sourceObjects, "Original"));
                    Assert.NotSame(original, sourceLeaf); Assert.Equal(leaf, sourceLeaf.CopyData(owner, CancellationToken.None));
                }
                PdfStream privateOriginal = Assert.IsType<PdfStream>(Value(document, annotation, "Private"));
                Assert.Equal(program, privateOriginal.CopyData(owner, CancellationToken.None));
                Assert.IsType<PdfNull>(Value(document, privateOriginal.Dictionary, "Resources"));
            }
            Assert.Equal(0, host.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
    private static QpdfObject Value(QpdfDocument document, PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? Resolve(document, value) : PdfNull.Instance;
}
