using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonImportContentTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");
    private static readonly byte[] First = "q 0 0 10 12 re f"u8.ToArray();
    private static readonly byte[] Changed = "q 0 0 24 29 re f"u8.ToArray();
    private static readonly byte[] Last = "Q"u8.ToArray();
    private static readonly byte[] Encoded = Encode(First);

    [Fact]
    public async Task CreationJoinsVisiblePagesWhileKeepingThePrivateArrayAndDirectKid()
    {
        using InMemoryQpdfHost host = new();
        AddGraph(host, "/FlateDecode");
        QpdfExecutionResult run = await Qpdf.ExecuteAsync(host,
            new([Input.Value, "--json-input", "--stream-data=preserve", "baseline"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, run.ExitCode);
        await using (QpdfDocument baseline = await OpenOutput(host, "baseline"))
        {
            foreach (var page in baseline.PageReferences)
            { Assert.Equal(2, Assert.IsType<PdfArray>(Resolve(baseline, Read(page.Page, "Contents"))).Count); }
            Assert.Null(baseline.PageReferences[1].Source);
        }

        run = await Qpdf.ExecuteAsync(host, new([Input.Value, "--json-input", "--coalesce-contents",
            "--stream-data=preserve", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, run.ExitCode);
        Assert.Equal(First.Length, run.Counters.TotalDecodedBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        await using QpdfDocument document = await OpenOutput(host, Output.Value);
        Assert.Equal(2, document.PageCount);
        QpdfObject first = Read(document.PageReferences[0].Page, "Contents");
        Assert.Equal(first, Read(document.PageReferences[1].Page, "Contents"));
        Assert.NotNull(document.PageReferences[1].Source);
        AssertJoined(document, first, First);
        PdfDictionary catalog = Catalog(document);
        QpdfObject archive = Read(catalog, "Archive");
        Assert.NotEqual(first, archive);
        AssertPrivateContents(document, archive, First);
        PdfArray kids = Assert.IsType<PdfArray>(Resolve(document, Read(catalog, "PrivateKids")));
        PdfDictionary original = Assert.IsType<PdfDictionary>(kids[1]);
        Assert.Equal(archive, Read(original, "Contents"));
    }

    [Fact]
    public async Task JobUpdateCoalescesThePatchedPayloadBeforeQdfPublication()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 6 0 R >>", null),
            (4, $"<< /Length {First.Length} >>", First),
            (5, $"<< /Length {Last.Length} >>", Last),
            (6, "[4 0 R 5 0 R]", null),
            (7, "<< /Unrelated 29 >>", null)], 1), QpdfInputOwnership.Transfer);
        string payload = Convert.ToBase64String(Encode(Changed));
        host.AddInput(QpdfPath.From("patch"), Encoding.UTF8.GetBytes($$$$"""
            {"qpdf":[{"jsonversion":2},{"obj:4 0 R":{"stream":{"dict":{"/Filter":"/FlateDecode"},"data":"{{{{payload}}}}"}}}]}
            """), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"input","updateFromJson":"patch","outputFile":"output","coalesceContents":"",
             "qdf":"","normalizeContent":"n","streamData":"preserve"}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        string qdf = Encoding.ASCII.GetString(bytes);
        Assert.Contains("%QDF-1.0", qdf, StringComparison.Ordinal);
        Assert.DoesNotContain("%% Original object ID: 4 0\n", qdf, StringComparison.Ordinal);
        Assert.DoesNotContain("%% Original object ID: 6 0\n", qdf, StringComparison.Ordinal);
        Assert.Contains("%% Original object ID: 7 0\n", qdf, StringComparison.Ordinal);
        await using QpdfDocument document = await OpenOutput(host, Output.Value);
        AssertJoined(document, Read(document.PageReferences[0].Page, "Contents"), Changed);
        Assert.Single(document.Engine.XRefEntries.Where(entry => entry.Key > 0
            && entry.Value.Kind != XrefEntryKind.Free).Select(entry => document.GetObject(entry.Key)).OfType<PdfStream>());
    }

    [Fact]
    public async Task UnsupportedStreamKeepsTheWholeArrayOpaque()
    {
        using InMemoryQpdfHost host = new();
        AddGraph(host, "/DCTDecode");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, "--json-input", "--coalesce-contents",
            "--stream-data=preserve", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.UnsupportedFeature
            && item.Message.Contains("Left page contents uncoalesced", StringComparison.Ordinal));
        await using QpdfDocument document = await OpenOutput(host, Output.Value);
        PdfArray array = Assert.IsType<PdfArray>(Resolve(document, Read(document.PageReferences[0].Page, "Contents")));
        Assert.Equal(2, array.Count);
        Assert.Equal(Encoded, Assert.IsType<PdfStream>(Resolve(document, array[0])).Data);
        Assert.Equal(Last, Assert.IsType<PdfStream>(Resolve(document, array[1])).Data);
    }

    [Fact]
    public async Task DecodeRefusalLeavesThePriorTargetAndOpensNoTransaction()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        AddGraph(host.Inner, "/FlateDecode");
        byte[] sentinel = [17, 23, 41];
        await using (IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Output, CancellationToken.None))
        {
            await prior.WriteAsync(sentinel, CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxDecodedBytes = First.Length - 1 });
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Input.Value, "--json-input",
            "--coalesce-contents", "--stream-data=preserve", Output.Value]), options, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded
            && item.Message.Contains(nameof(QpdfLimits.MaxDecodedBytes), StringComparison.Ordinal));
        Assert.Empty(host.CreatedPaths);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] output));
        Assert.Equal(sentinel, output);
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Fact]
    public async Task CancellationDuringPublicationKeepsThePriorTarget()
    {
        using RecordingJobHost host = new("write", Output, "success", true);
        AddGraph(host.Inner, "/FlateDecode");
        byte[] sentinel = [47, 53, 59];
        await using (IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Output, CancellationToken.None))
        {
            await prior.WriteAsync(sentinel, CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        using CancellationTokenSource stop = new();
        Task<QpdfExecutionResult> running = Qpdf.ExecuteAsync(host, new([Input.Value, "--json-input", "--coalesce-contents",
            "--stream-data=preserve", Output.Value]), stop.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(host.PendingWrite.IsEmpty);
            await stop.CancelAsync();
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
            Assert.Equal(First.Length, result.Counters.TotalDecodedBytes);
            Assert.Empty(host.Committed);
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] output));
            Assert.Equal(sentinel, output);
            ExecutionJobFixtures.AssertClosed(host, result);
        }
        finally { host.Resume(); await running; }
    }

    private static void AddGraph(InMemoryQpdfHost host, string filter)
    {
        string graph = $$$$"""
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Archive":"11 0 R","/PrivateKids":"12 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":"12 0 R","/Count":2}},
              "obj:12 0 R":{"value":["3 0 R",{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{},"/Contents":"11 0 R"}]},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{},"/Contents":"11 0 R"}},
              "obj:7 0 R":{"stream":{"dict":{"/Filter":"{{{{filter}}}}"},"datafile":"sidecar"}},
              "obj:8 0 R":{"stream":{"dict":{},"data":"UQ=="}},
              "obj:11 0 R":{"value":["7 0 R","8 0 R"]},
              "trailer":{"value":{"/Root":"1 0 R","/Size":13}}
            }] }
            """;
        host.AddInput(Input, Encoding.UTF8.GetBytes(graph), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("sidecar"), Encoded, QpdfInputOwnership.Copy);
    }

    private static ValueTask<QpdfDocument> OpenOutput(InMemoryQpdfHost host, string output)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] bytes));
        QpdfPath readback = QpdfPath.From("read-" + output);
        host.AddInput(readback, bytes, QpdfInputOwnership.Transfer);
        return QpdfDocument.OpenAsync(host, readback, CancellationToken.None);
    }

    private static PdfDictionary Catalog(QpdfDocument document)
        => Assert.IsType<PdfDictionary>(Resolve(document, Read(document.Engine.Trailer, "Root")));

    private static QpdfObject Read(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference) : value;

    private static void AssertJoined(QpdfDocument document, QpdfObject value, byte[] first)
    {
        PdfStream stream = Assert.IsType<PdfStream>(Resolve(document, Assert.IsType<PdfReference>(value)));
        Assert.Equal([.. first, (byte)'\n', .. Last], StreamObservation.Decode(stream));
    }

    private static void AssertPrivateContents(QpdfDocument document, QpdfObject value, byte[] first)
    {
        PdfArray array = Assert.IsType<PdfArray>(Resolve(document, value));
        Assert.Equal(2, array.Count);
        Assert.Equal(first, StreamObservation.Decode(Assert.IsType<PdfStream>(Resolve(document, array[0]))));
        Assert.Equal(Last, StreamObservation.Decode(Assert.IsType<PdfStream>(Resolve(document, array[1]))));
    }

    private static byte[] Encode(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream encoder = new(output, CompressionLevel.Fastest, true)) { encoder.Write(data); }
        return output.ToArray();
    }
}
