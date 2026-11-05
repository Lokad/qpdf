using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayFieldRegistrationTests
{
    private static readonly byte[] Original = "/Tx BMC BT /F1 10 Tf 2 5 Td (OLD) Tj ET EMC"u8.ToArray();

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool parent in new[] { false, true })
        foreach (bool privateFirst in new[] { false, true })
        foreach (bool need in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [parent, privateFirst, need, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrivateCopyDoesNotSuppressFieldRegistration(bool parent, bool privateFirst, bool need, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Target(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), Source(parent, privateFirst, need), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
            { inputFile = "input", outputFile = "output", generateAppearances = true, compressStreams = "n", overlay = new { file = "stamp" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--", "--generate-appearances", "--compress-streams=n", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Value(document, document.Engine.Trailer, "Root"));
            PdfDictionary form = Assert.IsType<PdfDictionary>(Value(document, catalog, "AcroForm"));
            PdfArray fields = Assert.IsType<PdfArray>(Value(document, form, "Fields"));
            Assert.Equal(1, fields.Count);
            PdfArray annotations = Assert.IsType<PdfArray>(Value(document, Assert.Single(document.PageReferences).Page, "Annots"));
            Assert.Equal(2, annotations.Count);
            QpdfObject widgetLink = annotations[privateFirst ? 1 : 0];
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, widgetLink));
            PdfDictionary ordinary = Assert.IsType<PdfDictionary>(Resolve(document, annotations[privateFirst ? 0 : 1]));
            Assert.True(ordinary.TryGet(PdfName.FromAscii("Private"), out QpdfObject privateRoot));
            Assert.Equal(fields[0], privateRoot);
            if (parent)
            {
                Assert.True(widget.TryGet(PdfName.FromAscii("Parent"), out QpdfObject root));
                Assert.Equal(fields[0], root);
            }
            else { Assert.Equal(fields[0], widgetLink); }
            PdfStream appearance = Assert.IsType<PdfStream>(Value(document, Assert.IsType<PdfDictionary>(Value(document, widget, "AP")), "N"));
            string body = System.Text.Encoding.ASCII.GetString(appearance.RawMemory.Span);
            Assert.Contains(need ? "(NEW) Tj" : "(OLD) Tj", body);
            PdfStream original = Assert.IsType<PdfStream>(Value(document, ordinary, "OriginalAP"));
            Assert.Equal(Original, original.RawMemory.ToArray());
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    private static byte[] Source(bool parent, bool privateFirst, bool need)
    {
        string root = parent ? "8 0 R" : "4 0 R";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots " + (privateFirst ? "[9 0 R 4 0 R]" : "[4 0 R 9 0 R]") + " >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /Rect [20 40 80 60] /F 4 /AP << /N 5 0 R >> " + (parent ? "/Parent 8 0 R" : "/FT /Tx /T (field) /V (NEW)") + " >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 60 20] /Resources << /Font << /F1 7 0 R >> >> /Length " + Original.Length + " >>", Original),
            (6, "<< /Fields [" + root + "] /NeedAppearances " + (need ? "true" : "false") + " /DA (/F1 10 Tf 0 g) /DR << /Font << /F1 7 0 R >> >> >>", null),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
            (8, "<< /FT /Tx /T (field) /V (NEW) /Kids [4 0 R] >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /Border [0 0 0] /Private " + root + " /OriginalAP 5 0 R /A << /S /URI /URI (https://example.test) >> >>", null)], 1);
    }

    private static byte[] Target() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1);

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject? Value(QpdfDocument document, PdfDictionary dictionary, string name)
        => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) ? Resolve(document, value) : null;
}
