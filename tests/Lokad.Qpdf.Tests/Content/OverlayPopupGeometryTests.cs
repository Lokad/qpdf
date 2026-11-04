using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayPopupGeometryTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool listed in new[] { false, true })
        foreach (bool repeat in new[] { false, true })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool job in new[] { false, true }) { yield return [listed, repeat, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task LinkedOnlyAndListedPopupsUseTheSameFrameOncePerPlacement(
        bool listed, bool repeat, string mode, bool job)
    {
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("source"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Rotate 90 /Resources << >> /Annots "
                + (listed ? "[4 0 R 5 0 R]" : "[4 0 R]") + " >>", null),
            (4, "<< /Type /Annot /Subtype /Text /F 4 /Rect [10 20 40 40] /Popup 5 0 R >>", null),
            (5, "<< /Type /Annot /Subtype /Popup /F 4 /Rect 6 0 R /PrivateRect 6 0 R /Parent 4 0 R /Open true >>", null),
            (6, "[30 40 60 80]", null)], 1), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        List<string> arguments = ["input", "--" + mode, "source", "--"];
        if (repeat) { arguments.AddRange(["--" + mode, "source", "--"]); }
        arguments.Add("output");
        Dictionary<string, object> settings = new()
            { ["inputFile"] = "input", ["outputFile"] = "output", [mode] = repeat
                ? new object[] { new { file = "source" }, new { file = "source" } } : new { file = "source" } };
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        PdfArray escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            PdfArray annotations = Assert.IsType<PdfArray>(Get(document, document.PageReferences[0].Page, "Annots"));
            int stride = listed ? 2 : 1;
            Assert.Equal(stride * (repeat ? 2 : 1), annotations.Count);
            List<QpdfObject> identities = [];
            for (int offset = 0; offset < annotations.Count; offset += stride)
            {
                PdfDictionary parent = Assert.IsType<PdfDictionary>(Resolve(document, annotations[offset]));
                PdfDictionary popup = Assert.IsType<PdfDictionary>(Get(document, parent, "Popup"));
                Assert.DoesNotContain(popup, identities); identities.Add(popup);
                Assert.Same(parent, Get(document, popup, "Parent"));
                Assert.Equal(new double[] { 120, 210, 140, 240 }, Numbers(Assert.IsType<PdfArray>(Get(document, parent, "Rect"))));
                Assert.Equal(new double[] { 140, 190, 180, 220 }, Numbers(Assert.IsType<PdfArray>(Get(document, popup, "Rect"))));
                escaped = Assert.IsType<PdfArray>(Get(document, popup, "PrivateRect"));
                Assert.Equal(new double[] { 30, 40, 60, 80 }, Numbers(escaped));
                if (listed)
                {
                    Assert.Same(popup, Resolve(document, annotations[offset + 1]));
                }
            }
            PdfDictionary first = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
            escaped = Assert.IsType<PdfArray>(Get(document, Assert.IsType<PdfDictionary>(Get(document, first, "Popup")), "PrivateRect"));
        }
        host.Dispose();
        Assert.Equal(new double[] { 30, 40, 60, 80 }, Numbers(escaped));
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static QpdfObject Get(QpdfDocument document, PdfDictionary owner, string key)
    { Assert.True(owner.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }

    private static double[] Numbers(PdfArray array)
        => array.Items.Select(item => item is PdfInteger integer ? (double)integer.Value : Assert.IsType<PdfReal>(item).Value).ToArray();
}
