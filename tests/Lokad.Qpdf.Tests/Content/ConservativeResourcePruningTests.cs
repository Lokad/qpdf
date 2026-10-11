using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ConservativeResourcePruningTests
{
    [Theory]
    [InlineData("argv", "yes", false)]
    [InlineData("job", "yes", false)]
    [InlineData("mixed", "yes", false)]
    [InlineData("argv", "default", false)]
    [InlineData("job", "no", false)]
    [InlineData("argv", "yes", true)]
    public async Task ExplicitUnresolvedFormScopePreservesPageResources(string route, string policy, bool declared)
    {
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "undefined-form-resources.pdf"));
        if (declared)
        {
            input = Lokad.Qpdf.Tests.Inspection.CheckTests.Assemble(
            [
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 4 0 R /Contents [8 0 R 9 0 R] >>", null),
                (4, "<< /Font << /F1 5 0 R /Unused 6 0 R >> /XObject << /Form 7 0 R >> >>", null),
                (5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
                (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
                (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Font << /F1 5 0 R >> >> /Length 9 >>", "/F1 12 Tf"u8.ToArray()),
                (8, "<< /Length 5 >>", "/Form"u8.ToArray()),
                (9, "<< /Length 2 >>", "Do"u8.ToArray()),
            ], 1);
        }
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), input, QpdfInputOwnership.Copy);
        string settings = "\"outputFile\":\"out.pdf\",\"pages\":[{\"file\":\".\",\"range\":\"1\"}]"
            + (policy == "default" ? string.Empty : ",\"removeUnreferencedResources\":\"" + policy + "\"");
        QpdfExecutionResult result;
        if (route == "job")
        {
            result = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"a.pdf\"," + settings + "}", CancellationToken.None);
        }
        else if (route == "mixed")
        {
            host.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes("{" + settings + "}"), QpdfInputOwnership.Copy);
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["a.pdf", "--job-json-file=settings.json"]), CancellationToken.None);
        }
        else
        {
            List<string> arguments = ["a.pdf"];
            if (policy != "default") { arguments.Add("--remove-unreferenced-resources=" + policy); }
            arguments.AddRange(["--pages", ".", "1", "--", "out.pdf"]);
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(arguments), CancellationToken.None);
        }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            PdfDictionary page = Assert.IsType<PdfDictionary>(document.GetObject(3));
            Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject raw));
            PdfDictionary resources = ResolveDictionary(document, raw);
            Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject fonts));
            PdfDictionary fontDictionary = ResolveDictionary(document, fonts);
            if (declared) { Assert.Empty(fontDictionary.Keys); }
            else
            {
                Assert.True(fontDictionary.TryGet(PdfName.FromAscii("F1"), out _));
                Assert.True(fontDictionary.TryGet(PdfName.FromAscii("Unused"), out _));
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    private static PdfDictionary ResolveDictionary(QpdfDocument document, QpdfObject value)
        => Assert.IsType<PdfDictionary>(value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value);
}
