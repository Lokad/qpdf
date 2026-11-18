using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonSidecarReuseTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 257).Select(i => (byte)(i % 251)).ToArray();
    private static readonly QpdfPath Json = QpdfPath.From("json"), Source = QpdfPath.From("source");
    private static readonly QpdfPath Output = QpdfPath.From("output");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPathsBorrowOnePayloadWhileIndependentPathsStillRead(bool update)
    {
        using InMemoryQpdfHost storage = Storage(["p0", "p0", "p1"]);
        SidecarHost host = new(storage);
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        DiagnosticBag diagnostics = new(governor);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using JsonDocument json = await JsonImport.ReadJsonAsync(host, Json, limits, governor, memory, CancellationToken.None);
        ImportedModel model;
        if (update)
        {
            await using QpdfDocument source = await QpdfDocument.OpenInspectionCore(host, Source, null,
                limits, governor, diagnostics, QpdfRecoveryMode.Strict, CancellationToken.None, false);
            model = await JsonImport.BuildUpdateModelAsync(host, json, source.Engine, source.HeaderVersionParts,
                false, false, false, limits, governor, memory, CancellationToken.None);
        }
        else
        {
            model = await JsonImport.BuildCreateModelAsync(host, json, false, limits, governor, memory, CancellationToken.None);
        }
        PdfStream[] streams = model.Objects.Values.OfType<PdfStream>().ToArray();
        Assert.Equal(3, streams.Length);
        Assert.Equal(new[] { "p0", "p1" }, host.Opens);
        Assert.Equal(2 * Payload.Length, host.BytesRead);
        Assert.True(host.PayloadReads > host.Opens.Count);
        Assert.NotSame(streams[0], streams[1]);
        Assert.NotSame(streams[0].Dictionary, streams[1].Dictionary);
        Assert.Same(streams[0].AllocationCarrier, streams[1].AllocationCarrier);
        Assert.NotSame(streams[0].AllocationCarrier, streams[2].AllocationCarrier);
        Assert.All(streams, stream => Assert.Equal(Payload, stream.Data));
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task EvictionReadsAgainWithoutReplacingPriorStreamBacking()
    {
        string[] names = ["p0", "p0", .. Enumerable.Range(1, 8).Select(i => $"p{i}"), "p0"];
        using InMemoryQpdfHost storage = Storage(names);
        SidecarHost host = new(storage);
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using JsonDocument json = await JsonImport.ReadJsonAsync(host, Json, limits, governor, memory, CancellationToken.None);
        ImportedModel model = await JsonImport.BuildCreateModelAsync(host, json, false, limits, governor, memory, CancellationToken.None);
        PdfStream[] streams = model.Objects.Values.OfType<PdfStream>().ToArray();
        Assert.Equal(names.Length, streams.Length);
        Assert.Equal(new[] { "p0", "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p0" }, host.Opens);
        Assert.Same(streams[0].AllocationCarrier, streams[1].AllocationCarrier);
        Assert.NotSame(streams[0].AllocationCarrier, streams[^1].AllocationCarrier);
        Assert.Equal(Payload, streams[0].Data);
        Assert.Equal(Payload, streams[^1].Data);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task JobPublishesSeparateStreamsForAReusedPayload()
    {
        using InMemoryQpdfHost storage = Storage(["p0", "p0"]);
        SidecarHost host = new(storage);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"json","jsonInput":true,"outputFile":"output"}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(new[] { "p0" }, host.Opens);
        Assert.True(storage.TryGetOutput(Output, out byte[] pdf));
        QpdfPath read = QpdfPath.From("read");
        storage.AddInput(read, pdf, QpdfInputOwnership.Transfer);
        await using QpdfDocument doc = await QpdfDocument.OpenAsync(storage, read, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(doc.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Payloads"), out QpdfObject payloads));
        PdfArray references = Assert.IsType<PdfArray>(payloads);
        Assert.Equal(2, references.Count);
        Assert.NotEqual(references[0], references[1]);
        for (int i = 0; i < references.Count; ++i)
        {
            PdfStream stream = Assert.IsType<PdfStream>(doc.GetObject(Assert.IsType<PdfReference>(references[i])));
            Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Marker"), out QpdfObject marker));
            Assert.Equal(i, Assert.IsType<PdfInteger>(marker).Value);
            Assert.Equal(Payload, StreamObservation.Decode(stream));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSecondAcquisitionClosesLeasesAndKeepsTheNamedOutput(bool cancel)
    {
        using InMemoryQpdfHost storage = Storage(["p0", "p0", "p1"]);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        using CancellationTokenSource stop = new();
        SidecarHost host = new(storage)
        {
            AfterRead = path =>
            {
                if (path != "p1") { return; }
                if (cancel) { stop.Cancel(); }
                else { throw new QpdfHostException(QpdfPath.From(path), QpdfHostFailureReason.ReadFailed); }
            }
        };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["json", "--json-input", Output.Value]), stop.Token);
        Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        if (!cancel) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.HostOperationFailed); }
        Assert.Equal(new[] { "p0", "p1" }, host.Opens);
        Assert.True(host.BytesRead > Payload.Length);
        Assert.True(host.BytesRead < 2 * Payload.Length);
        Assert.True(storage.TryGetOutput(Output, out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    private static InMemoryQpdfHost Storage(IReadOnlyList<string> names)
    {
        using JsonDocument skeleton = JsonDocument.Parse("""
            {"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},
             "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R"],"/Count":1}},
             "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,80,40],"/Resources":{}}},
             "trailer":{"value":{"/Root":"1 0 R"}}}
            """);
        Dictionary<string, object> objects = skeleton.RootElement.EnumerateObject().ToDictionary(
            entry => entry.Name, entry => (object)entry.Value, StringComparer.Ordinal);
        objects["obj:1 0 R"] = new { value = new Dictionary<string, object>
        {
            ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R",
            ["/Payloads"] = Enumerable.Range(0, names.Count).Select(i => $"{4 + i} 0 R").ToArray()
        } };
        for (int i = 0; i < names.Count; ++i)
        {
            objects[$"obj:{4 + i} 0 R"] = new { stream = new { dict = new Dictionary<string, int> { ["/Marker"] = i }, datafile = names[i] } };
        }
        InMemoryQpdfHost host = new();
        host.AddInput(Json, JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.4" }, objects } }), QpdfInputOwnership.Transfer);
        host.AddInput(Source, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        foreach (string name in names.Distinct(StringComparer.Ordinal)) { host.AddInput(QpdfPath.From(name), Payload, QpdfInputOwnership.Copy); }
        return host;
    }

    [Fact]
    public Task SharedSidecarAdmissionRefusalsRefundStorageAndKeepTheNamedOutput()
        => PreparationAdmissionChecks.VerifyAsync(
            SeededStorageAsync,
            (host, token) => Qpdf.ExecuteAsync(host, new(["json", "--json-input", "--stream-data=preserve", Output.Value]), token),
            Output, 512, ["write", "cancel"]);

    private static async Task<InMemoryQpdfHost> SeededStorageAsync()
    {
        InMemoryQpdfHost storage = Storage(["p0", "p0", "p1"]);
        try
        {
            await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None);
            await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
            return storage;
        }
        catch { storage.Dispose(); throw; }
    }

    private sealed class SidecarHost(InMemoryQpdfHost storage) : IQpdfHost
    {
        internal List<string> Opens { get; } = [];
        internal int BytesRead { get; private set; }
        internal int PayloadReads { get; private set; }
        internal Action<string>? AfterRead { get; init; }

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfInput input = await storage.OpenInputAsync(path, token);
            if (!path.Value.StartsWith("p", StringComparison.Ordinal)) { return input; }
            Opens.Add(path.Value);
            return new ShortSidecar(input, this);
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => storage.CreateOutputAsync(path, token);

        private sealed class ShortSidecar(IQpdfInput input, SidecarHost host) : IQpdfInput
        {
            public QpdfPath Path => input.Path;
            public long Length => input.Length;
            public string Identity => input.Identity;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                int count = await input.ReadAsync(offset, destination[..Math.Min(17, destination.Length)], token);
                host.BytesRead += count;
                host.PayloadReads++;
                host.AfterRead?.Invoke(input.Path.Value);
                return count;
            }
            public ValueTask DisposeAsync() => input.DisposeAsync();
        }
    }
}
