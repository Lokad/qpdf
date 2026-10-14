using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class DictionaryPrefixTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task PublicRewritePreservesDictionaryAcrossScratchBoundaryAndCleanup(int count)
    {
        StringBuilder body = new("<< /A#30 (discard) /A0 [7 /Tag]");
        for (int index = 1; index < count; index++) { body.Append($" /K{index} {index}"); }
        body.Append(" /A0 (kept\\012) >>");
        List<(int Number, string Body, byte[]? Data)> objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Fixture 6 0 R >>", null);
        objects.Add((6, body.ToString(), null));
        QpdfPath input = QpdfPath.From("in.pdf"), output = QpdfPath.From("out.pdf");
        InMemoryQpdfHost host = new();
        host.AddInput(input, CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        PdfDictionary original;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, input, CancellationToken.None))
        {
            original = Assert.IsType<PdfDictionary>(document.GetObject(6));
            Assert.Equal(2, document.Warnings.Count(item => item.Code == QpdfDiagnosticCode.DuplicateKey));
        }
        IEnumerable<PdfName> keys = original.Keys;
        Validate(original, count);

        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["in.pdf", "--static-id", "--stream-data=preserve", "out.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(2, result.Diagnostics.Count(item => item.Code == QpdfDiagnosticCode.DuplicateKey));
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        using (UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes))
        { Assert.Equal(1, independent.NumberOfPages); }
        host.AddInput(QpdfPath.From("written.pdf"), bytes, QpdfInputOwnership.Copy);
        PdfDictionary rewritten;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("written.pdf"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("Fixture"), out QpdfObject value));
            PdfReference reference = Assert.IsType<PdfReference>(value);
            rewritten = Assert.IsType<PdfDictionary>(document.GetObject(reference.ObjectNumber));
        }
        Validate(rewritten, count);
        Validate(original, count);
        Assert.Equal(new[] { "A0" }.Concat(Enumerable.Range(1, count - 1).Select(index => "K" + index)), keys.Select(key => key.DisplayText));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static void Validate(PdfDictionary dictionary, int count)
    {
        Assert.Equal(count, dictionary.Count);
        Assert.True(dictionary.TryGet(PdfName.FromAscii("A0"), out QpdfObject value));
        PdfByteString text = Assert.IsType<PdfByteString>(value);
        Assert.Equal("kept\n"u8.ToArray(), text.Value);
        byte[] copy = text.Value; Array.Clear(copy);
        Assert.Equal("kept\n"u8.ToArray(), text.Value);
        for (int index = 1; index < count; index++)
        {
            Assert.True(dictionary.TryGet(PdfName.FromAscii("K" + index), out value));
            Assert.Equal(index, Assert.IsType<PdfInteger>(value).Value);
        }
    }
}
