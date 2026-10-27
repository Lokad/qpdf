using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JobFieldOrderingTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool file in new[] { false, true })
        {
            yield return ["empty", "", "{}", true, file];
            yield return ["include-only", "\"jobJsonFile\":\"child\"", "{}", true, file];
            yield return ["prefix-only", "\"compressStreams\":\"n\"", "{}", false, file];
            yield return ["suffix-only", "\"qdf\":\"\"", "{}", false, file];
            yield return ["last-property", "\"compressStreams\":\"y\",\"compressStreams\":\"n\"", "{}", false, file];
            yield return ["prefix-before-child", "\"compressStreams\":\"n\",\"jobJsonFile\":\"child\"", """{"compressStreams":"y"}""", true, file];
            yield return ["suffix-after-child", "\"jobJsonFile\":\"child\",\"qdf\":\"\"", """{"streamData":"uncompress"}""", false, file];
            yield return ["nested-order", "\"compressStreams\":\"y\",\"jobJsonFile\":\"child\",\"streamData\":\"compress\"",
                """{"jobJsonFile":"grandchild","streamData":"uncompress"}""", true, file];
            yield return ["duplicate-include", "\"jobJsonFile\":\"missing\",\"jobJsonFile\":\"child\"", """{"compressStreams":"n"}""", false, file];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PartitionsPreserveSetterOrderAndLastValues(string name, string properties, string child, bool compressed, bool file)
    {
        byte[] payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q Q\n", 64)));
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble(CheckTests.SinglePage($"<< /Length {payload.Length} >>", payload), 1), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("child"), Encoding.UTF8.GetBytes(child), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("grandchild"), """{"compressStreams":"n"}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result;
        if (file)
        {
            host.AddInput(QpdfPath.From("config"), Encoding.UTF8.GetBytes("{" + properties + "}"), QpdfInputOwnership.Transfer);
            result = await Qpdf.ExecuteAsync(host, new(["input", "--job-json-file=config", "--", "output"]), CancellationToken.None);
        }
        else
        {
            string json = "{\"inputFile\":\"input\",\"outputFile\":\"output\"" + (properties.Length == 0 ? "" : "," + properties) + "}";
            result = await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None);
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output), name);
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        if (contents is PdfArray array) { contents = Assert.Single(array.Items); }
        PdfReference reference = Assert.IsType<PdfReference>(contents);
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(reference.ObjectNumber));
        Assert.Equal(compressed, stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
    }
}
