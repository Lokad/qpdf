using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayIdentityTests
{
    internal static byte[] LinkedAnnotations(bool reverse)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 50 50] /Annots " + (reverse ? "[6 0 R 5 0 R 4 0 R]" : "[4 0 R 5 0 R 6 0 R]") + " >>", null),
            (4, "<< /Type /Annot /Subtype /Text /Contents (Note) /Rect [1 2 11 12] /Popup 5 0 R /AP << /N 7 0 R >> >>", null),
            (5, "<< /Type /Annot /Subtype /Popup /Rect [20 21 30 31] /Parent 4 0 R >>", null),
            (6, "<< /Type /Annot /Subtype /Text /Contents (Reply) /Rect [3 4 13 14] /IRT 4 0 R /AP << /N 7 0 R >> >>", null),
            (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Matrix [1 0 0 1 0 0] /Length 3 >>", "q Q"u8.ToArray())], 1);

    private static byte[] Primary() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", null),
        (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", null)], 1);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task LinkedAnnotationsShareTheirPlacementCopiesAndTransformOnce(bool job, bool underlay, bool reverse)
    {
        PdfStream? escaped = null;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Primary(), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("source"), LinkedAnnotations(reverse), QpdfInputOwnership.Transfer);
            string kind = underlay ? "underlay" : "overlay";
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"compressStreams\":\"n\",\"" + kind + "\":{\"file\":\"source\",\"repeat\":\"1\"}}", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--" + kind, "source", "--repeat=1", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("inspection"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inspection"), CancellationToken.None))
            {
                QpdfObject Get(PdfDictionary dictionary, string key)
                { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
                QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
                double[] Numbers(QpdfObject value) => Enumerable.Range(0, Assert.IsType<PdfArray>(value).Count)
                    .Select(i => ((PdfArray)value)[i] is PdfReal real ? real.Value : ((PdfInteger)((PdfArray)value)[i]).Value).ToArray();
                HashSet<int> previous = new();
                for (int page = 0; page < 2; page++)
                {
                    PdfArray annotations = Assert.IsType<PdfArray>(Resolve(Get(document.PageReferences[page].Page, "Annots")));
                    Assert.Equal(3, annotations.Count);
                    int[] ids = Enumerable.Range(0, 3).Select(i => Assert.IsType<PdfReference>(annotations[i]).ObjectNumber).ToArray();
                    int noteId = ids[reverse ? 2 : 0], popupId = ids[1], replyId = ids[reverse ? 0 : 2];
                    Assert.All(ids, id => Assert.DoesNotContain(id, previous)); previous.UnionWith(ids);
                    PdfDictionary note = Assert.IsType<PdfDictionary>(document.GetObject(noteId));
                    PdfDictionary popup = Assert.IsType<PdfDictionary>(document.GetObject(popupId));
                    PdfDictionary reply = Assert.IsType<PdfDictionary>(document.GetObject(replyId));
                    Assert.Equal(popupId, Assert.IsType<PdfReference>(Get(note, "Popup")).ObjectNumber);
                    Assert.Equal(noteId, Assert.IsType<PdfReference>(Get(popup, "Parent")).ObjectNumber);
                    Assert.Equal(noteId, Assert.IsType<PdfReference>(Get(reply, "IRT")).ObjectNumber);
                    Assert.Equal(new double[] { 26, 27, 36, 37 }, Numbers(Get(note, "Rect")));
                    Assert.Equal(new double[] { 45, 46, 55, 56 }, Numbers(Get(popup, "Rect")));
                    PdfReference Appearance(PdfDictionary annotation) => Assert.IsType<PdfReference>(Get(Assert.IsType<PdfDictionary>(Resolve(Get(annotation, "AP"))), "N"));
                    PdfReference appearance = Appearance(note); Assert.Equal(appearance.ObjectNumber, Appearance(reply).ObjectNumber);
                    Assert.DoesNotContain(appearance.ObjectNumber, previous); previous.Add(appearance.ObjectNumber);
                    escaped = Assert.IsType<PdfStream>(document.GetObject(appearance.ObjectNumber));
                    Assert.Equal(new double[] { 1, 0, 0, 1, 25, 25 }, Numbers(Get(escaped.Dictionary, "Matrix")));
                }
                Assert.NotNull(escaped);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.NotNull(escaped); Assert.Equal("q Q"u8.ToArray(), escaped.RawMemory.ToArray());
    }
}
