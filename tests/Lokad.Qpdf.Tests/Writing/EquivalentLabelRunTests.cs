using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class EquivalentLabelRunTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string shape in new[] { "direct", "shared", "distinct", "bytes", "prefix-different", "style-different", "restart", "prefix-encoding", "malformed" })
        { yield return [route, shape]; }
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task CompareAcrossClosedSourcesUsingCopiedPoolValues(string route, bool equal)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), LabelIndirectionTests.Build(2,
            "<< /Nums [0 << /S 5 0 R /P 6 0 R /St 10 >>] >>", (5, "/D"), (6, "(P-)")), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("other"), LabelIndirectionTests.Build(2,
            "<< /Nums [0 << /S 5 0 R /P 6 0 R /St 12 >>] >>", (5, "/#44"), (6, equal ? "<502d>" : "(Other)")), QpdfInputOwnership.Transfer);
        if (route is "typed")
        {
            await Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages([1, 2])), new(QpdfPath.From("other"), PdfPageSelectionOptions.FromPages([1, 2]))],
                QpdfPath.From("output"), CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route is "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1-2", "other", "1-2", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1-2"},{"file":"other","range":"1-2"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        Assert.Equal(4, document.PageReferences.Count);
        Assert.Equal(equal ? 2 : 4, LabelIndirectionTests.Numbers(document).Count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CompressResolvedRunsWithoutChangingReferencesOrOpaqueValues(string route, string shape)
    {
        string secondStyle = shape is "direct" ? "/D" : shape is "shared" ? "5 0 R" : "7 0 R";
        string secondPrefix = shape is "direct" ? "(P-)" : shape is "shared" ? "6 0 R" : "8 0 R";
        string styleBody = shape is "style-different" ? "/R" : shape is "malformed" ? "(D)" : "/#44";
        string prefixBody = shape is "prefix-different" ? "(Other)" : shape is "prefix-encoding" ? "<feff0050002d>" : "<502d>";
        string start = shape is "restart" ? "1" : "11";
        byte[] source = Inspection.CheckTests.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S 5 0 R /P 6 0 R /St 10 >> 1 << /S "
                + secondStyle + " /P " + secondPrefix + " /St " + start + " >>] >> >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /PrivateStyle 5 0 R /PrivatePrefix 6 0 R /OtherPrefix 8 0 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> >>", null),
            (5, "/D", null), (6, "(P-)", null), (7, styleBody, null), (8, prefixBody, null),
        ], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        await LabelIndirectionTests.Select(host, route, true);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        PdfByteString escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfArray numbers = LabelIndirectionTests.Numbers(document);
            bool same = shape is "direct" or "shared" or "distinct" or "bytes";
            Assert.Equal(same ? 2 : 4, numbers.Count);
            PdfDictionary label = Assert.IsType<PdfDictionary>(numbers[1]);
            PdfDictionary page = document.PageReferences[0].Page;
            Assert.True(label.TryGet(PdfName.FromAscii("S"), out QpdfObject style));
            Assert.True(page.TryGet(PdfName.FromAscii("PrivateStyle"), out QpdfObject privateStyle));
            Assert.Equal(Assert.IsType<PdfReference>(privateStyle), Assert.IsType<PdfReference>(style));
            Assert.True(label.TryGet(PdfName.FromAscii("P"), out QpdfObject prefix));
            Assert.True(page.TryGet(PdfName.FromAscii("PrivatePrefix"), out QpdfObject privatePrefix));
            Assert.Equal(Assert.IsType<PdfReference>(privatePrefix), Assert.IsType<PdfReference>(prefix));
            escaped = Assert.IsType<PdfByteString>(document.GetObject(Assert.IsType<PdfReference>(prefix).ObjectNumber));
            Assert.True(page.TryGet(PdfName.FromAscii("OtherPrefix"), out QpdfObject other));
            PdfByteString otherValue = Assert.IsType<PdfByteString>(document.GetObject(Assert.IsType<PdfReference>(other).ObjectNumber));
            Assert.Equal(shape is "prefix-encoding" ? new byte[] { 254, 255, 0, 80, 0, 45 }
                : shape is "prefix-different" ? "Other"u8.ToArray() : "P-"u8.ToArray(), otherValue.ValueSpan.ToArray());
        }
        host.Dispose();
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal("P-"u8.ToArray(), escaped.ValueSpan.ToArray());
    }
}
