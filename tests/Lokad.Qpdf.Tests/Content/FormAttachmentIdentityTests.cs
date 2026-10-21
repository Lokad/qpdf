using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormAttachmentIdentityTests
{
    public static TheoryData<bool, bool, bool, bool> Cases
    {
        get
        {
            TheoryData<bool, bool, bool, bool> cases = new();
            foreach (bool job in new[] { false, true })
            foreach (bool selection in new[] { false, true })
            foreach (bool attachment in new[] { false, true })
            foreach (bool overlay in new[] { false, true })
            { cases.Add(job, selection, attachment, overlay); }
            return cases;
        }
    }

    internal static byte[] Fixture()
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA 9 0 R /DR << /Font << /Helv 8 0 R >> >> >> /PageLabels << /Nums [0 << /P 10 0 R >>] >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [5 0 R] >>", null),
            (4, "null", null),
            (5, "<< /Subtype /Widget /FT /Tx /T (Field) /Rect [0 0 10 10] >>", null),
            (6, "null", null), (7, "null", null),
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (9, "(/Helv 12 Tf)", null), (10, "(Prefix)", null),
        ], 1);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ColdCatalogReferencesStayDistinctFromAttachmentsAndOverlays(bool job, bool selection, bool attachment, bool overlay)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), "abc"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("overlay"), CheckTests.Assemble(CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1), QpdfInputOwnership.Transfer);
        List<string> command = ["input"];
        if (selection) { command.AddRange(["--pages", ".", "1", "--"]); }
        if (attachment) { command.AddRange(["--add-attachment", "payload", "--"]); }
        if (overlay) { command.AddRange(["--overlay", "overlay", "--"]); }
        command.Add("output");
        string json = "{\"inputFile\":\"input\",\"outputFile\":\"output\""
            + (selection ? ",\"pages\":[{\"file\":\".\",\"range\":\"1\"}]" : "")
            + (attachment ? ",\"addAttachment\":[{\"file\":\"payload\"}]" : "")
            + (overlay ? ",\"overlay\":[{\"file\":\"overlay\"}]" : "") + "}";
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(command), CancellationToken.None);
        Assert.Equal(0, (int)result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("inspection"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inspection"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
            QpdfObject Get(PdfDictionary dictionary, string key)
            { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            PdfDictionary form = Assert.IsType<PdfDictionary>(Get(catalog, "AcroForm"));
            PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(form, "DR"));
            PdfDictionary fonts = Assert.IsType<PdfDictionary>(Get(resources, "Font"));
            PdfDictionary font = Assert.IsType<PdfDictionary>(Get(fonts, "Helv"));
            Assert.Equal("Font", Assert.IsType<PdfName>(Get(font, "Type")).DisplayText);
            Assert.Equal("Helvetica", Assert.IsType<PdfName>(Get(font, "BaseFont")).DisplayText);
            Assert.Equal("/Helv 12 Tf"u8.ToArray(), Assert.IsType<PdfByteString>(Get(form, "DA")).Value);
            PdfDictionary labels = Assert.IsType<PdfDictionary>(Get(catalog, "PageLabels"));
            PdfArray nums = Assert.IsType<PdfArray>(Get(labels, "Nums"));
            PdfDictionary label = Assert.IsType<PdfDictionary>(Resolve(nums[1]));
            Assert.Equal("Prefix"u8.ToArray(), Assert.IsType<PdfByteString>(Get(label, "P")).Value);
        }
        if (attachment)
        {
            QpdfAttachmentExtraction extracted = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inspection"), "payload", QpdfPath.From("extracted"), CancellationToken.None);
            Assert.Equal(3, extracted.ByteCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("extracted"), out byte[] bytes)); Assert.Equal("abc"u8.ToArray(), bytes);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
}
