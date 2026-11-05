using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayClippingProfileTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "absent", "null", "malformed", "missing-normal", "empty-states", "missing-state", "bad-state", "missing-box", "bad-box", "opaque-matrix", "zero-box", "zero-matrix", "hidden", "popup" })
        foreach (bool inside in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [shape, inside, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ClippingRequiresRepresentableStaticArtwork(string shape, bool inside, bool job)
    {
        RecordingMemoryBudget budget = new(8L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
            host.AddInput(QpdfPath.From("input"), Target(), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("stamp"), Source(shape, inside), QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", overlay = new { file = "stamp" }, compressStreams = "n" }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--", "--compress-streams=n", "output"]), CancellationToken.None);
            bool supported = inside || shape is "zero-box" or "zero-matrix" or "hidden" or "popup";
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            if (!supported)
            {
                Assert.Equal(QpdfExitCode.Error, result.ExitCode);
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature && d.Message.Contains("clipping", StringComparison.Ordinal));
                Assert.Equal("prior"u8.ToArray(), output);
            }
            else
            {
                Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
                Assert.NotEqual("prior"u8.ToArray(), output);
                if (!inside && shape is "zero-box" or "zero-matrix")
                {
                    host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
                    await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
                    PdfArray annotations = Assert.IsType<PdfArray>(Value(document, document.PageReferences[0].Page, "Annots"));
                    PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
                    PdfDictionary ap = Assert.IsType<PdfDictionary>(Value(document, annotation, "AP"));
                    PdfStream appearance = Assert.IsType<PdfStream>(Value(document, ap, "N"));
                    Assert.DoesNotContain(" Do", System.Text.Encoding.ASCII.GetString(appearance.CopyData(owner, CancellationToken.None)), StringComparison.Ordinal);
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static byte[] Source(string shape, bool inside)
    {
        string ap = shape switch
        {
            "absent" or "hidden" or "popup" => "", "null" => "/AP null", "malformed" => "/AP (opaque)",
            "missing-normal" => "/AP << /R 5 0 R >>", "empty-states" => "/AP << /N << >> >>",
            "missing-state" => "/AP << /N << /Off 5 0 R >> >> /AS /On",
            "bad-state" => "/AP << /N 5 0 R /R << /Off 17 >> >>",
            _ => "/AP << /N 5 0 R >>",
        };
        string box = shape switch { "missing-box" => "", "bad-box" => "/BBox [0 0 40]", "zero-box" => "/BBox [0 0 0 40]", _ => "/BBox [0 0 40 40]" };
        string matrix = shape switch { "opaque-matrix" => "/Matrix (opaque)", "zero-matrix" => "/Matrix [0 0 0 0 0 0]", _ => "" };
        string subtype = shape == "popup" ? "Popup" : "Stamp";
        string rect = inside ? "[20 20 60 60]" : "[-20 20 20 60]";
        byte[] body = "1 0 0 rg 0 0 40 40 re f"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /" + subtype + " /F " + (shape == "hidden" ? "2" : "4") + " /Rect " + rect + " " + ap + " >>", null),
            (5, "<< /Type /XObject /Subtype /Form " + box + " " + matrix + " /Resources << >> /Length " + body.Length + " >>", body)], 1);
    }

    private static byte[] Target() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 500] /Resources << >> >>", null)], 1);
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
    private static QpdfObject Value(QpdfDocument document, PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? Resolve(document, value) : PdfNull.Instance;
}
