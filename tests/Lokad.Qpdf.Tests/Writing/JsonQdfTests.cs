using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonQdfTests
{
    private static readonly QpdfPath Json = QpdfPath.From("graph"), Source = QpdfPath.From("source"), Output = QpdfPath.From("output");
    private static readonly byte[] Payload = "q\r\n0 0 30 40 re f\rQ\r\n"u8.ToArray();
    private static readonly byte[] Replacement = "q\n0 0 47 29 re f\nQ\n"u8.ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedQdfRetainsOriginalPairsAndRepairsAnEditedStream(bool update)
    {
        using InMemoryQpdfHost host = Storage(update);
        QpdfExecutionResult result = update
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"source","updateFromJson":"graph","outputFile":"output","qdf":"","normalizeContent":"n"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["graph", "--json-input", "--qdf", "--normalize-content=n", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        string qdf = Encoding.Latin1.GetString(output);
        Assert.Contains("%QDF-1.0", qdf, StringComparison.Ordinal);
        foreach (string pair in new[] { "1 0", "2 7", "3 12", "4 4" })
        { Assert.Contains($"%% Original object ID: {pair}\n", qdf, StringComparison.Ordinal); }

        (byte[] bytes, bool filtered, int generation, bool packed) = await ReadContentsAsync(host, "published", output);
        Assert.Equal(update ? Replacement : Payload, bytes);
        Assert.False(filtered); Assert.Equal(0, generation); Assert.False(packed);
        string rectangle = update ? "0 0 47 29 re" : "0 0 30 40 re";
        QpdfPath edited = QpdfPath.From("edited"), fixedPdf = QpdfPath.From("repaired");
        host.AddInput(edited, Encoding.Latin1.GetBytes(qdf.Replace(rectangle, "0 0 130 440 re", StringComparison.Ordinal)), QpdfInputOwnership.Transfer);
        Assert.Equal(QpdfExitCode.Success, (await Qpdf.FixQdfAsync(host, edited, fixedPdf, CancellationToken.None)).ExitCode);
        Assert.True(host.TryGetOutput(fixedPdf, out byte[] repaired));
        (byte[] editedPayload, _, _, _) = await ReadContentsAsync(host, "fixed", repaired);
        Assert.Contains("0 0 130 440 re", Encoding.Latin1.GetString(editedPayload), StringComparison.Ordinal);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task QdfSettingsControlNormalizationCompressionAndPhysicalLayout(bool preserve, bool linearize)
    {
        using InMemoryQpdfHost host = Storage(false);
        List<string> args = ["graph", "--json-input", "--qdf", "--no-original-object-ids", "--object-streams=generate", "--compress-streams=y"];
        if (preserve) { args.Add("--normalize-content=n"); }
        if (linearize) { args.Add("--linearize"); }
        args.Add("output");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Output, out byte[] pdf));
        string physical = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("original object ID:", physical, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(!linearize, physical.Contains("%QDF-1.0", StringComparison.Ordinal));
        Assert.Equal(linearize, physical.Contains("/Linearized", StringComparison.Ordinal));
        (byte[] bytes, bool filtered, _, bool packed) = await ReadContentsAsync(host, "published", pdf);
        Assert.True(packed);
        Assert.Equal(preserve, filtered);
        if (preserve) { Assert.Equal(Payload, bytes); }
        else
        {
            Assert.DoesNotContain('\r', Encoding.Latin1.GetString(bytes));
            Assert.Contains("0 0 30 40 re", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedQdfKeepsRequestedOutputProtection(bool update)
    {
        using InMemoryQpdfHost host = Storage(false);
        List<string> args = ["graph", "--json-input", "--encrypt", "reader", "owner", "256", "--", "output"];
        if (!update) { args.Insert(2, "--qdf"); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        if (update)
        {
            host.AddInput(Source, output, QpdfInputOwnership.Transfer);
            host.AddInput(Json, """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
            result = await Qpdf.ExecuteAsync(host, new(["source", "--password=reader", "--update-from-json=graph", "--qdf", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.True(host.TryGetOutput(Output, out output));
        }
        Assert.Contains("%QDF-1.0", Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        Assert.DoesNotContain("0 0 30 40 re", Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        QpdfPath path = QpdfPath.From("protected");
        host.AddInput(path, output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(6, document.Encryption?.Revision);
        Assert.Contains("0 0 30 40 re", Encoding.Latin1.GetString(StreamObservation.Decode(Content(document).Stream)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("source")]
    public async Task ImportedQdfRejectsAnAliasedPrimaryOrPatchTarget(string target)
    {
        using InMemoryQpdfHost host = Storage(true);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["source", "--update-from-json=graph", "--qdf", target]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.False(host.TryGetOutput(QpdfPath.From(target), out _));
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public Task QdfPreparationRefundsAdmissionAndPreservesNamedPriorOutput()
        => PreparationAdmissionChecks.VerifyAsync(SeededStorageAsync,
            (host, token) => Qpdf.ExecuteAsync(host, new(["graph", "--json-input", "--qdf", "output"]), token),
            Output, 512, ["write", "cancel"]);

    private static async Task<InMemoryQpdfHost> SeededStorageAsync()
    {
        InMemoryQpdfHost host = Storage(false);
        try
        {
            await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None);
            await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
            return host;
        }
        catch { host.Dispose(); throw; }
    }

    private static async Task<(byte[] Bytes, bool Filtered, int Generation, bool Packed)> ReadContentsAsync(
        InMemoryQpdfHost host, string path, byte[] output)
    {
        QpdfPath read = QpdfPath.From(path);
        host.AddInput(read, output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        (PdfReference reference, PdfStream stream) = Content(document);
        Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(root)));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Alias"), out QpdfObject alias));
        Assert.Equal(reference, Assert.IsType<PdfReference>(alias));
        return (StreamObservation.Decode(stream), stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _),
            reference.Generation, document.Engine.XRefEntries.Values.Any(entry => entry.Kind == XrefEntryKind.Compressed));
    }

    private static (PdfReference Reference, PdfStream Stream) Content(QpdfDocument document)
    {
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        PdfReference reference = Assert.IsType<PdfReference>(contents);
        return (reference, Assert.IsType<PdfStream>(document.GetObject(reference)));
    }

    private static InMemoryQpdfHost Storage(bool update)
    {
        Dictionary<string, object> entries = new()
        {
            ["obj:4 4 R"] = new { stream = new { dict = new Dictionary<string, object>(), data = Convert.ToBase64String(update ? Replacement : Payload) } }
        };
        if (!update)
        {
            entries["obj:1 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Catalog", ["/Pages"] = "2 7 R", ["/Alias"] = "4 4 R", ["/Extra"] = "5 0 R" } };
            entries["obj:2 7 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Count"] = 1, ["/Kids"] = new[] { "3 12 R" } } };
            entries["obj:3 12 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Page", ["/Parent"] = "2 7 R", ["/Contents"] = "4 4 R", ["/Resources"] = new Dictionary<string, object>(), ["/MediaBox"] = new[] { 0, 0, 100, 100 } } };
            entries["obj:5 0 R"] = new { value = "u:extra" };
            entries["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = "1 0 R", ["/Size"] = 6 } };
        }
        InMemoryQpdfHost host = new();
        host.AddInput(Json, JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.4" }, entries } }), QpdfInputOwnership.Transfer);
        if (update)
        {
            host.AddInput(Source, PdfTestDocument.Assemble([
                (1, "<< /Type /Catalog /Pages 2 7 R /Alias 4 4 R >>", null),
                (2, "<< /Type /Pages /Count 1 /Kids [3 12 R] >>", null),
                (3, "<< /Type /Page /Parent 2 7 R /Contents 4 4 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
                (4, $"<< /Length {Payload.Length} >>", Payload)], 1, new Dictionary<int, int> { [2] = 7, [3] = 12, [4] = 4 }), QpdfInputOwnership.Transfer);
        }
        return host;
    }
}
