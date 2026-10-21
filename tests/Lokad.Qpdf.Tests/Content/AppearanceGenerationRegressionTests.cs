using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceGenerationRegressionTests
{
    [Theory]
    [InlineData(32768, "/Yes")]
    [InlineData(65536, "/Yes")]
    [InlineData(0, "null")]
    public async Task SkippedButtonsSurviveBesideUpdatedWidgets(int flags, string value)
    {
        foreach (bool indirect in new[] { false, true })
        foreach (bool job in new[] { false, true })
        {
            byte[] input = CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] /NeedAppearances true >> >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots " + (indirect ? "9 0 R" : "[5 0 R 6 0 R]") + " >>", null),
                (4, "null", null),
                (5, "<< /Subtype /Widget /FT /Btn /Ff " + flags + " /T (Skipped) /V " + value + " /AS /Off /Rect [0 0 100 20] /AP << /N << /Off 7 0 R /Yes 8 0 R >> >> >>", null),
                (6, "<< /Subtype /Widget /FT /Btn /T (Updated) /V /Yes /AS /Off /Rect [0 30 100 50] /AP << /N << /Off 7 0 R /Yes 8 0 R >> >> >>", null),
                (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Length 1 >>", "q"u8.ToArray()),
                (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Length 1 >>", "Q"u8.ToArray()),
                (9, "[5 0 R 6 0 R]", null)], 1);
            using InMemoryQpdfHost host = new();
            byte[] output = await Generate(host, input, job, QpdfExitCode.Warning);
            host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
            PdfDictionary page = document.PageReferences[0].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw));
            PdfArray annotations = Assert.IsType<PdfArray>(Resolve(document, raw)); Assert.Equal(2, annotations.Count);
            PdfDictionary skipped = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
            PdfDictionary updated = Assert.IsType<PdfDictionary>(Resolve(document, annotations[1]));
            Assert.True(skipped.TryGet(PdfName.FromAscii("AS"), out QpdfObject oldState)); Assert.Equal(PdfName.FromAscii("Off"), oldState);
            Assert.True(updated.TryGet(PdfName.FromAscii("AS"), out QpdfObject newState)); Assert.Equal(PdfName.FromAscii("Yes"), newState);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InheritedTypeDoesNotChangeSiblingValues(bool merged)
    {
        foreach (bool terminalType in new[] { false, true })
        foreach (bool job in new[] { false, true })
        {
            string widgetA = "/Subtype /Widget /NM (WidgetA) /AS /Off /Rect [0 0 100 20] /AP << /N << /Off 10 0 R /OnA 11 0 R >> >>";
            string widgetB = "/Subtype /Widget /NM (WidgetB) /AS /Off /Rect [0 30 100 50] /AP << /N << /Off 10 0 R /OnB 12 0 R >> >>";
            string ft = terminalType ? " /FT /Btn" : "";
            byte[] input = CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true >> >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots " + (merged ? "[6 0 R 8 0 R]" : "[7 0 R 9 0 R]") + " >>", null),
                (4, "null", null), (5, "<< /T (Group) /FT /Btn /V /Off /Kids [6 0 R 8 0 R] >>", null),
                (6, "<< /T (A) /V /OnA /Parent 5 0 R" + ft + (merged ? " " + widgetA : " /Kids [7 0 R]") + " >>", null),
                (7, merged ? "null" : "<< /Parent 6 0 R " + widgetA + " >>", null),
                (8, "<< /T (B) /Parent 5 0 R" + ft + (merged ? " " + widgetB : " /Kids [9 0 R]") + " >>", null),
                (9, merged ? "null" : "<< /Parent 8 0 R " + widgetB + " >>", null),
                (10, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Length 1 >>", "q"u8.ToArray()),
                (11, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Length 1 >>", "Q"u8.ToArray()),
                (12, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Length 1 >>", "Q"u8.ToArray())], 1);
            using InMemoryQpdfHost host = new(); byte[] output = await Generate(host, input, job, QpdfExitCode.Success);
            host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
            Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotations));
            PdfArray array = Assert.IsType<PdfArray>(Resolve(document, annotations));
            PdfDictionary a = Assert.IsType<PdfDictionary>(Resolve(document, array[0]));
            PdfDictionary b = Assert.IsType<PdfDictionary>(Resolve(document, array[1]));
            Assert.True(a.TryGet(PdfName.FromAscii("AS"), out QpdfObject aState)); Assert.Equal(PdfName.FromAscii("OnA"), aState);
            Assert.True(b.TryGet(PdfName.FromAscii("AS"), out QpdfObject bState)); Assert.Equal(PdfName.FromAscii("Off"), bState);
            PdfDictionary field = a;
            if (!merged) { Assert.True(a.TryGet(PdfName.FromAscii("Parent"), out QpdfObject owner)); field = Assert.IsType<PdfDictionary>(Resolve(document, owner)); }
            Assert.True(field.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parent));
            PdfDictionary group = Assert.IsType<PdfDictionary>(Resolve(document, parent));
            Assert.True(group.TryGet(PdfName.FromAscii("V"), out QpdfObject groupValue)); Assert.Equal(PdfName.FromAscii("Off"), groupValue);
        }
    }

    [Theory]
    [InlineData("FT")]
    [InlineData("V")]
    [InlineData("DA")]
    public async Task IndirectTextAttributesMatchDirectValues(string attribute)
    {
        foreach (bool indirect in new[] { false, true })
        foreach (bool job in new[] { false, true })
        {
            byte[] old = "/Tx BMC\nBT /Helv 17 Tf (Old) Tj ET\nEMC"u8.ToArray();
            string ft = indirect && attribute == "FT" ? "10 0 R" : "/Tx";
            string value = indirect && attribute == "V" ? "8 0 R" : "(Hello)";
            string da = indirect && attribute == "DA" ? "11 0 R" : "(/Helv 17 Tf)";
            byte[] input = CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true /DR << /Font << /Helv 9 0 R >> >> >> >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [5 0 R] >>", null),
                (4, "null", null),
                (5, "<< /Subtype /Widget /FT " + ft + " /T (Text) /V " + value + " /DA " + da + " /Rect [0 0 100 20] /AP << /N 6 0 R >> >>", null),
                (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /Helv 9 0 R >> >> /Length " + old.Length + " >>", old),
                (7, "null", null), (8, "(Hello)", null),
                (9, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
                (10, "/Tx", null), (11, "(/Helv 17 Tf)", null)], 1);
            using InMemoryQpdfHost host = new(); byte[] output = await Generate(host, input, job, QpdfExitCode.Success);
            host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
            Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotations));
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, Assert.IsType<PdfArray>(Resolve(document, annotations))[0]));
            Assert.True(widget.TryGet(PdfName.FromAscii("AP"), out QpdfObject ap));
            Assert.True(Assert.IsType<PdfDictionary>(Resolve(document, ap)).TryGet(PdfName.FromAscii("N"), out QpdfObject normal));
            PdfStream stream = Assert.IsType<PdfStream>(Resolve(document, normal));
            Assert.False(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
            string text = Encoding.ASCII.GetString(stream.RawMemory.Span); Assert.Contains("(Hello) Tj", text); Assert.Contains("/Helv 17 Tf", text);
        }
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static async Task<byte[]> Generate(InMemoryQpdfHost host, byte[] input, bool job, QpdfExitCode expected)
    {
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"}],"generateAppearances":true,"compressStreams":"n"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--generate-appearances", "--compress-streams=n", "--pages", ".", "1", "--", "output"]), CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(d => d.Message))); Assert.Equal(expected, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); return output;
    }
}

