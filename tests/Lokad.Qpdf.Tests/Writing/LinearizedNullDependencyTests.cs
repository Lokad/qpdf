using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedNullDependencyTests
{
    [Theory]
    [InlineData("page", false)]
    [InlineData("page", true)]
    [InlineData("stream", false)]
    [InlineData("stream", true)]
    [InlineData("open", false)]
    [InlineData("open", true)]
    [InlineData("thumbnail", false)]
    [InlineData("thumbnail", true)]
    public async Task IndirectNullsStayOutsidePageAndDocumentHintDependencies(string role, bool streamed)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfDictionary Dictionary(string text) => Assert.IsType<PdfDictionary>(parser.ParseObjectValue(
            new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, source));
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        if (role == "open") { model[0] = new(1, Dictionary("<< /Type /Catalog /Pages 2 0 R /OpenAction 6 0 R >>")); }
        else if (role == "stream")
        { model[3] = new(4, new PdfStream(Dictionary("<< /F 6 0 R >>"), "q Q\n"u8.ToArray())); }
        else
        {
            string key = role == "thumbnail" ? "Thumb" : "Private";
            model[2] = new(3, Dictionary("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> /" + key + " 6 0 R >>"));
        }
        model.Add(new(6, PdfNull.Instance));
        using (ParserMemoryScope classification = ParserMemoryScope.Create(governor))
        using (LinearizedParts parts = LinearizedParts.Classify(model, classification, governor, CancellationToken.None))
        {
            Assert.DoesNotContain(6, parts.Part4); Assert.DoesNotContain(6, parts.Part6); Assert.Contains(6, parts.Part9);
        }
        LinearizedWriter writer = new(QpdfLimits.Default, governor);
        byte[] output;
        using (BufferedSink sink = BufferedSink.Create(QpdfLimits.Default, governor, CancellationToken.None))
        {
            if (streamed)
            { await writer.WriteLinearizedStreamedAsync(new(1, 7), model, [new[] { 5 }], null, new byte[16], new byte[16], true, false, null, sink, null, CancellationToken.None); }
            else
            { await writer.WriteLinearizedAsync(new(1, 7), model, null, new byte[16], new byte[16], true, false, null, sink, null, CancellationToken.None); }
            output = sink.WrittenMemory.ToArray();
        }
        source.Dispose(); Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
        using InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("linearized.pdf");
        host.AddInput(path, output, QpdfInputOwnership.Transfer);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, path, new QpdfCheckOptions(), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, item => item.Name == "linearization").Status);
        Assert.Equal(0, host.OpenLeaseCount);
    }
}
