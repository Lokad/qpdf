using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class EmptyDecodeParmsTests
{
    public static IEnumerable<object[]> Samples()
    {
        // Independently framed samples all decode to the one byte 0x41.
        yield return ["/ASCIIHexDecode", "34313E", false];
        yield return ["/ASCII85Decode", "356C7E3E", false];
        yield return ["/FlateDecode", "789C73040000420042", true];
        yield return ["/LZWDecode", "80106020", false];
        yield return ["/RunLengthDecode", "004180", true];
        yield return ["[/ASCIIHexDecode /RunLengthDecode]", "3030343138303E", true];
    }

    private static byte[] Image(string filter, string hex, string parameters, bool indirect)
    {
        byte[] data = Convert.FromHexString(hex);
        byte[] content = "/Im Do\n"u8.ToArray();
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + content.Length + " >>", content),
            (5, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter " + filter
                + " /DecodeParms " + (indirect ? "6 0 R" : parameters) + " /Length " + data.Length + " >>", data)];
        if (indirect) { objects.Add((6, parameters, null)); }
        return PdfTestDocument.Assemble(objects, 1);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task FilteredInspectionUsesDefaultsForEmptyParameterArrays(string filter, string hex, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Image(filter, hex, "[]", indirect), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--show-object=5", "--filtered-stream-data"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(result.OutputBytes is ReadOnlyMemory<byte> bytes && bytes.Span.SequenceEqual("A"u8));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task ImageJsonAndCheckingShareDefaultParameters(bool job, int version)
    {
        string filter = job ? "/RunLengthDecode" : "/FlateDecode";
        string hex = job ? "004180" : "789C73040000420042";
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Image(filter, hex, "[]", !job), QpdfInputOwnership.Transfer);
        string text;
        if (job)
        {
            QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
                """{"inputFile":"input","json":"1","jsonKey":["pages","objects"],"decodeLevel":"all"}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            text = result.OutputText ?? throw new InvalidOperationException();
        }
        else
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(version, ["pages", "qpdf"]), CancellationToken.None);
            text = result.Json;
        }
        using JsonDocument report = JsonDocument.Parse(text);
        JsonElement image = Assert.Single(report.RootElement.GetProperty("pages")[0].GetProperty("images").EnumerateArray());
        Assert.True(image.GetProperty("filterable").GetBoolean());
        JsonElement dictionary = version == 2 ? report.RootElement.GetProperty("qpdf")[1].GetProperty("obj:5 0 R").GetProperty("stream").GetProperty("dict")
            : report.RootElement.GetProperty("objects").GetProperty("5 0 R");
        JsonElement parameters = dictionary.GetProperty("/DecodeParms");
        if (job) { Assert.Empty(parameters.EnumerateArray()); }
        else { Assert.Equal("6 0 R", parameters.GetString()); }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.True(check.Passed);
        Assert.Equal(1, check.PageCount);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NonemptyParameterCountMismatchStillPreservesTheReport()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Image("/FlateDecode", "789C73040000420042", "[null null]", false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("report"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--", "report"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task DefaultParametersDoNotRelaxDecodedBudgets()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Image("[/ASCIIHexDecode /RunLengthDecode]", "3030343138303E", "[]", true), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("report"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxDecodedBytes = 1 });
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--json=2", "--json-stream-data=inline", "--decode-level=all", "--", "report"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded && d.Message.Contains("MaxDecodedBytes", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BootstrapXrefDecodingUsesTheSameDefaultParameterPolicy()
    {
        byte[] source = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "xstream-1page.pdf"));
        string text = Encoding.Latin1.GetString(source);
        const string marker = "/Type /XRef /Size 7 /Root 1 0 R /W [1 4 2] /Filter /FlateDecode";
        Assert.Equal(1, text.Split(marker, StringSplitOptions.None).Length - 1);
        // The dictionary is in the final xref object. Inserting here preserves
        // every recorded object offset, including that object's own startxref.
        byte[] input = Encoding.Latin1.GetBytes(text.Replace(marker, marker + " /DecodeParms []", StringComparison.Ordinal));
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        { Assert.Equal(1, document.PageCount); Assert.Empty(document.Warnings); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
