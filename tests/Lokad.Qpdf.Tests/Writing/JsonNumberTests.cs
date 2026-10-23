using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonNumberTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Json = QpdfPath.From("input.json");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [17, 23, 41];

    private static byte[] Fixture(string number) => Encoding.UTF8.GetBytes(
        """{"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Probe":NUMBER}},"obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R"],"/Count":1e0}},"obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{}}},"trailer":{"value":{"/Root":"1 0 R","/Size":4}}}]}"""
            .Replace("NUMBER", number, StringComparison.Ordinal));

    private static async Task<InMemoryQpdfHost> Host(string number, bool update)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Json, Fixture(number), QpdfInputOwnership.Transfer);
        if (update)
        {
            host.AddInput(Input, CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", null)
            ], 1), QpdfInputOwnership.Transfer);
        }
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync(Prior, CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        return host;
    }

    private static async Task<QpdfExecutionResult?> Run(IQpdfHost host, string route, bool update)
    {
        if (route == "typed")
        {
            QpdfJsonImportResult result = update
                ? await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None)
                : await Qpdf.CreatePdfFromJsonAsync(host, Json, Output, CancellationToken.None);
            Assert.Equal(1, result.PageCount); Assert.Equal(0L, result.Counters.OpenHandles);
            return null;
        }
        return route == "job"
            ? await Qpdf.ExecuteJobAsync(host, update
                ? """{"inputFile":"input.pdf","outputFile":"output.pdf","updateFromJson":"input.json"}"""
                : """{"inputFile":"input.json","outputFile":"output.pdf","jsonInput":true}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(update
                ? [Input.Value, "--update-from-json=" + Json.Value, "--", Output.Value]
                : [Json.Value, "--json-input", "--", Output.Value]), CancellationToken.None);
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task NumericFormsRoundTripThroughCreateAndUpdate(string route, bool update)
    {
        using InMemoryQpdfHost host = await Host(
            "[1e3,-1.25e-2,1.234567890123456789e-2,1e19,1e-400,0e0,-0e0,0.125e2,1000.0,-0.0,9223372036854775807,-9223372036854775808,-0.125e2,0e1000000,-0e-1000000,1e000000003]", update);
        QpdfExecutionResult? result = await Run(host, route, update);
        if (result is not null) { Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, result.Counters.OpenHandles); }
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        PdfArray values;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            Assert.True(Assert.IsType<PdfDictionary>(document.GetObject(1)).TryGet(PdfName.FromAscii("Probe"), out QpdfObject probe));
            values = Assert.IsType<PdfArray>(probe);
        }
        // Escaped immutable values remain readable after the document closes.
        Assert.Equal(1000L, Assert.IsType<PdfInteger>(values[0]).Value);
        Assert.Equal("-0.0125", Assert.IsType<PdfReal>(values[1]).Text);
        Assert.Equal("0.01234567890123456789", Assert.IsType<PdfReal>(values[2]).Text);
        Assert.Equal("10000000000000000000.0", Assert.IsType<PdfReal>(values[3]).Text);
        Assert.Equal("0." + new string('0', 399) + "1", Assert.IsType<PdfReal>(values[4]).Text);
        Assert.Equal("0", Assert.IsType<PdfInteger>(values[5]).Text);
        Assert.Equal("-0", Assert.IsType<PdfInteger>(values[6]).Text);
        Assert.Equal(12.5, Assert.IsType<PdfReal>(values[7]).Value);
        Assert.Equal("1000.0", Assert.IsType<PdfReal>(values[8]).Text);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(Assert.IsType<PdfReal>(values[9]).Value));
        Assert.Equal(long.MaxValue, Assert.IsType<PdfInteger>(values[10]).Value);
        Assert.Equal(long.MinValue, Assert.IsType<PdfInteger>(values[11]).Value);
        Assert.Equal("-12.5", Assert.IsType<PdfReal>(values[12]).Text);
        Assert.Equal("0", Assert.IsType<PdfInteger>(values[13]).Text);
        Assert.Equal("-0", Assert.IsType<PdfInteger>(values[14]).Text);
        Assert.Equal(1000L, Assert.IsType<PdfInteger>(values[15]).Value);
        QpdfJsonResult exported = await Qpdf.GetDocumentJsonAsync(host, Output, QpdfPath.From("export.json"), new QpdfJsonOptions(2, ["qpdf"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(exported.Json);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:1 0 R").GetProperty("value").GetProperty("/Probe").ValueKind);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed", false, "1e309")]
    [InlineData("argv", false, "1e309")]
    [InlineData("job", false, "1e309")]
    [InlineData("typed", true, "1e309")]
    [InlineData("argv", true, "1e309")]
    [InlineData("job", true, "1e309")]
    [InlineData("argv", false, "9223372036854775808")]
    [InlineData("argv", true, "-9223372036854775809")]
    [InlineData("argv", false, "1e2147483648")]
    [InlineData("argv", true, "1e-2147483649")]
    [InlineData("argv", false, "1e-2147483648")]
    [InlineData("argv", true, "1e2147483647")]
    [InlineData("argv", false, "1.8e308")]
    public async Task UnsupportedNumbersPreservePriorOutput(string route, bool update, string number)
    {
        using InMemoryQpdfHost host = await Host(number, update);
        if (route == "typed") { await Assert.ThrowsAsync<QpdfParseException>(() => Run(host, route, update)); }
        else
        {
            QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await Run(host, route, update));
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments
                && item.Message.Contains("qpdf JSON", StringComparison.Ordinal));
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.True(host.TryGetOutput(Output, out byte[] unchanged)); Assert.Equal(Prior, unchanged);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ExpansionRefusalReleasesSharedAdmissionAndPreservesOutput()
    {
        using InMemoryQpdfHost storage = await Host("1e-1000000", false);
        RecordingMemoryBudget root = new(524288L);
        using IMemoryScope owner = root.CreateScope();
        MemoryHost host = new(storage, owner);
        await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Qpdf.CreatePdfFromJsonAsync(host, Json, Output, CancellationToken.None).AsTask());
        Assert.Equal(0, host.OutputCalls); Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.InRange(root.ReservedBytes, 0L, 4096L);
        owner.Dispose(); Assert.Equal(0L, root.ReservedBytes);
        Assert.True(storage.TryGetOutput(Output, out byte[] unchanged)); Assert.Equal(Prior, unchanged);
    }

    private sealed class MemoryHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        internal int OutputCalls;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { OutputCalls++; return storage.CreateOutputAsync(path, token); }
    }
}
